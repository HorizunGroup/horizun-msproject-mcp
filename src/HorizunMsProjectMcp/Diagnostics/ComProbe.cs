using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Horizun.ProjectMcp.Diagnostics;

/// <summary>
/// Result of probing the Microsoft Project COM server.
/// <para>
/// The point of this type is that "COM is unavailable" is never enough. Every failure
/// carries the HRESULT, a plain-language diagnosis, and a concrete repair path — because
/// the failure that actually happens in the field (0x80080005 with a perfectly valid
/// registration) is indistinguishable from "not installed" unless you say so.
/// </para>
/// </summary>
public sealed record ComProbeResult
{
    public required bool Available { get; init; }
    public required string Status { get; init; }

    /// <summary>Set when the probe was registry-only (see <see cref="ComProbe.Inspect"/>).</summary>
    public bool Launched { get; init; }

    public string? ProductVersion { get; init; }
    public string? ExecutablePath { get; init; }
    public string? HResult { get; init; }
    public string? Diagnosis { get; init; }
    public IReadOnlyList<string> Repair { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Detects Microsoft Project and whether its COM automation server can actually start.
/// </summary>
/// <remarks>
/// Two probe depths, because launching Project is not free:
/// <list type="bullet">
/// <item><description><see cref="Inspect"/> — registry + filesystem only. Cheap, no side effects. The default.</description></item>
/// <item><description><see cref="Launch"/> — a real CoCreateInstance. Starts Project, then quits it.</description></item>
/// </list>
/// </remarks>
public static class ComProbe
{
    private const string ProgId = "MSProject.Application";
    private const string ClsId = "{36D27C48-A1E8-11D3-BA55-00C04F72F325}";

    /// <summary>Registry- and filesystem-only inspection. Does not start Microsoft Project.</summary>
    public static ComProbeResult Inspect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ComProbeResult
            {
                Available = false,
                Status = "unsupported_platform",
                Diagnosis = "COM automation only exists on Windows. The MPXJ backend covers this platform.",
            };
        }

        var exePath = ReadLocalServerPath();
        if (exePath is null)
        {
            return new ComProbeResult
            {
                Available = false,
                Status = "not_registered",
                Diagnosis =
                    $"No LocalServer32 registration for CLSID {ClsId}. Microsoft Project is most likely not installed.",
                Repair = new[]
                {
                    "Install Microsoft Project (Standard or Professional).",
                    "If it is installed, run an Office Quick Repair to restore the COM registration.",
                    "Until then the server runs on the MPXJ backend, which needs neither Project nor a licence.",
                },
            };
        }

        if (!File.Exists(exePath))
        {
            return new ComProbeResult
            {
                Available = false,
                Status = "registered_but_missing",
                ExecutablePath = exePath,
                Diagnosis =
                    $"The COM registration points at '{exePath}', but that file does not exist. The registration is stale.",
                Repair = new[]
                {
                    "Run an Office Quick Repair to rewrite the registration.",
                    "If Project was uninstalled, the stale CLSID key can be ignored — use the MPXJ backend.",
                },
            };
        }

        return new ComProbeResult
        {
            Available = true,
            Status = "registered",
            ExecutablePath = exePath,
            ProductVersion = FileVersionInfo.SafeProductVersion(exePath),
            Diagnosis =
                "Registration looks correct. This does NOT prove the COM server starts — "
                + "call project_health with deep=true to actually launch it.",
        };
    }

    /// <summary>
    /// Actually starts the COM server and shuts it down again. Slow (seconds) and visible
    /// to the user, so it is opt-in rather than part of the default health check.
    /// </summary>
    public static ComProbeResult Launch()
    {
        var inspected = Inspect();
        if (!inspected.Available)
        {
            return inspected;
        }

        if (!OperatingSystem.IsWindows())
        {
            return inspected;
        }

        object? app = null;
        try
        {
            var type = Type.GetTypeFromProgID(ProgId, throwOnError: false);
            if (type is null)
            {
                return inspected with
                {
                    Available = false,
                    Status = "progid_unresolved",
                    Diagnosis = $"ProgID '{ProgId}' did not resolve to a type even though the CLSID is registered.",
                };
            }

            app = Activator.CreateInstance(type);
            if (app is null)
            {
                return inspected with
                {
                    Available = false,
                    Status = "activation_returned_null",
                    Diagnosis = "COM activation returned null without raising an error.",
                };
            }

            var version = type.InvokeMember("Version", System.Reflection.BindingFlags.GetProperty, null, app, null);
            var build = type.InvokeMember("Build", System.Reflection.BindingFlags.GetProperty, null, app, null);

            return inspected with
            {
                Available = true,
                Status = "operational",
                Launched = true,
                ProductVersion = $"{version} (build {build})",
                Diagnosis = null,
                Repair = Array.Empty<string>(),
            };
        }
        catch (Exception ex)
        {
            var hr = ex is COMException com ? com.HResult : ex.HResult;
            var (diagnosis, repair) = Explain(hr, inspected.ExecutablePath);

            return inspected with
            {
                Available = false,
                Status = "launch_failed",
                Launched = false,
                HResult = $"0x{hr:X8}",
                Diagnosis = diagnosis,
                Repair = repair,
            };
        }
        finally
        {
            TryQuit(app);
        }
    }

    /// <summary>
    /// Maps the HRESULTs that actually show up when driving Office COM to a diagnosis
    /// a user can act on, rather than re-surfacing the raw interop message.
    /// </summary>
    private static (string Diagnosis, string[] Repair) Explain(int hresult, string? exePath) => unchecked((uint)hresult) switch
    {
        0x80040154 => (
            "REGDB_E_CLASSNOTREG — the class is not registered. Microsoft Project is not installed, "
            + "or its COM registration was removed.",
            new[]
            {
                "Install Microsoft Project, or run an Office Quick Repair.",
                "Meanwhile, use the MPXJ backend — it needs neither Project nor a licence.",
            }),

        // The one that bites in the field: registration is valid, the binary is on disk,
        // and activation still fails because the server process never comes up.
        0x80080005 => (
            "CO_E_SERVER_EXEC_FAILURE — the registration is valid and the executable exists, but the COM "
            + "server process failed to start. Microsoft Project is installed; something is blocking activation. "
            + "The usual causes are an incomplete first run (licence/privacy dialog), an integrity-level mismatch "
            + "between this process and Project, or a Click-to-Run activation issue.",
            new[]
            {
                $"Launch Project interactively once ({exePath ?? "WINPROJ.EXE"}) and complete any first-run, "
                + "licence, or privacy dialog. Then close it and retry.",
                "Check that this process and Project run at the same integrity level — an elevated caller "
                + "cannot activate a non-elevated Office server, and vice versa.",
                "Run an Office Quick Repair (Settings > Apps > Microsoft 365 > Modify > Quick Repair).",
                "Review the DCOM launch permissions for Microsoft Project in dcomcnfg.",
                "None of this blocks you: the MPXJ backend reads and writes schedules without COM.",
            }),

        0x800401E3 => (
            "MK_E_UNAVAILABLE — Microsoft Project is running but is not registered in the Running Object Table, "
            + "so this process cannot attach to the live instance.",
            new[]
            {
                "Check that Project and this process run as the same user, in the same session, "
                + "at the same integrity level.",
                "Close all Project instances and let the server start its own.",
            }),

        0x80070005 => (
            "E_ACCESSDENIED — activation was refused. This is a DCOM permissions problem, not a missing install.",
            new[]
            {
                "Grant local launch and activation rights for Microsoft Project in dcomcnfg.",
                "Verify the account running this server may start Office applications.",
            }),

        _ => (
            "COM activation failed with an HRESULT this server does not have a specific diagnosis for. "
            + "The MPXJ backend remains available.",
            new[]
            {
                "Launch Project interactively once and retry.",
                "Run an Office Quick Repair.",
            }),
    };

    private static string? ReadLocalServerPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{ClsId}\LocalServer32");
            var raw = key?.GetValue(null) as string;
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim('"');
        }
        catch
        {
            // A registry read failing is itself a "cannot determine" answer, not a crash.
            return null;
        }
    }

    private static void TryQuit(object? app)
    {
        if (app is null)
        {
            return;
        }

        try
        {
            // 0 = pjDoNotSave. We opened it to look, not to change anything.
            app.GetType().InvokeMember("Quit", System.Reflection.BindingFlags.InvokeMethod, null, app, new object[] { 0 });
        }
        catch
        {
            // Best effort — a probe must never take the server down.
        }
        finally
        {
            try
            {
                Marshal.ReleaseComObject(app);
            }
            catch
            {
                // Ditto.
            }
        }
    }
}

internal static class FileVersionInfo
{
    public static string? SafeProductVersion(string path)
    {
        try
        {
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion;
        }
        catch
        {
            return null;
        }
    }
}
