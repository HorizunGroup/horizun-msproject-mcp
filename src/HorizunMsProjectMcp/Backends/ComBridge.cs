using System.Reflection;
using System.Runtime.InteropServices;

namespace Horizun.ProjectMcp.Backends;

/// <summary>
/// The COM accelerator: drives an installed Microsoft Project to do the things no library can.
/// </summary>
/// <remarks>
/// Only used for capabilities the file backend genuinely cannot serve. Everything else stays on
/// MPXJ, because COM means a licensed Windows install and a visible application — a dependency
/// worth paying only where it buys something real.
/// </remarks>
public static class ComBridge
{
    private const string ProgId = "MSProject.Application";

    /// <summary>
    /// Writes a genuine binary .mpp by having Microsoft Project open an intermediate MSPDI file
    /// and save it in its own format. This is the only way the format can be authored at all.
    /// </summary>
    public static void SaveAsMpp(string sourceMspdiPath, string targetMppPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new McpToolException("Native .mpp can only be written on Windows, through Microsoft Project.");
        }

        object? app = null;
        try
        {
            var type = Type.GetTypeFromProgID(ProgId, throwOnError: false)
                       ?? throw new McpToolException(
                           "Microsoft Project is not registered for COM automation on this machine. "
                           + "Run project_health with deep=true for the diagnosis and the repair steps.");

            app = Activator.CreateInstance(type)
                  ?? throw new McpToolException("Microsoft Project did not start.");

            Set(app, "Visible", false);
            Set(app, "DisplayAlerts", false);

            Call(app, "FileOpen", Path.GetFullPath(sourceMspdiPath));

            if (File.Exists(targetMppPath))
            {
                File.Delete(targetMppPath);
            }

            // No FormatID: .mpp is Project's own default, and passing one is rejected as an
            // invalid argument rather than honoured.
            Call(app, "FileSaveAs", Path.GetFullPath(targetMppPath));
            Call(app, "FileCloseEx", 0); // 0 = pjDoNotSave

            if (!File.Exists(targetMppPath))
            {
                throw new McpToolException(
                    $"Microsoft Project reported no error but '{targetMppPath}' was not created.");
            }
        }
        catch (McpToolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Late binding wraps everything in TargetInvocationException; the useful message is
            // always underneath it, and reporting the wrapper instead is how COM errors become
            // undebuggable.
            var root = ex;
            while (root is TargetInvocationException { InnerException: { } inner })
            {
                root = inner;
            }

            throw new McpToolException(
                $"Microsoft Project could not write the .mpp: {root.Message} "
                + $"({root.GetType().Name}). Write MSPDI instead (format='mspdi' — a .xml Microsoft "
                + "Project opens natively), or run project_health with deep=true to check the COM server.");
        }
        finally
        {
            Quit(app);
        }
    }

    private static void Set(object target, string property, object value)
    {
        try
        {
            target.GetType().InvokeMember(property, BindingFlags.SetProperty, null, target, new[] { value });
        }
        catch
        {
            // These are conveniences; a version that does not expose them is not a failure.
        }
    }

    private static object? Call(object target, string method, params object[] args) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args);

    private static void Quit(object? app)
    {
        if (app is null)
        {
            return;
        }

        try
        {
            Call(app, "Quit", 0);
        }
        catch
        {
            // Best effort.
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
