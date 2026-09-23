using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Horizun.ProjectMcp.Backends;

/// <summary>
/// The only way this server touches a running Microsoft Project. Every COM session goes through
/// <see cref="Run{T}"/>, which exists to enforce three things.
/// </summary>
/// <remarks>
/// <para>
/// <b>Microsoft Project is single-instance.</b> Measured, not assumed: with Project already open,
/// <c>CreateInstance</c>, <c>DispatchEx</c>, and launching <c>WINPROJ.EXE /automation</c> all end
/// up inside the user's own process — a second process forwards to the first and exits. So an
/// automation session cannot assume it owns the application. Before this class existed, the .mpp
/// save and the deep health check both hid the window and called <c>Quit(pjDoNotSave)</c>, which
/// on a machine where someone was working would close their Project and discard their unsaved
/// changes.
/// </para>
/// <para>
/// So a session is either an <b>owner</b> (Project was not running; we started it hidden and we
/// quit it) or a <b>guest</b> (it was running; we never hide it, never quit it, only ever touch the
/// document we opened, and give the user back the window they had active). Documents are found by
/// a unique name token, never by "whatever is active": before every command that acts on the active
/// document, ours is activated and checked, so a user clicking another window mid-operation makes
/// the operation abort rather than land on their file.
/// </para>
/// <para>
/// <b>A modal dialog blocks forever.</b> Project raises them for macros, file-format questions and
/// more, and a hidden instance has nobody to answer. Every session runs under a watchdog; an owned
/// instance that stops answering is killed, a guest one never is — it is the user's.
/// </para>
/// <para>
/// <b>One session at a time — across processes, not just threads.</b> Two sessions would share the
/// single instance and each could decide the other's documents meant the application was not theirs
/// to quit. Claude Desktop and Claude Code each run their own copy of this server, so the lock is a
/// named system mutex rather than an in-process one.
/// </para>
/// </remarks>
public static class ProjectAutomation
{
    private const string ProgId = "MSProject.Application";
    private const string ProcessName = "WINPROJ";
    // Local\ scopes it to this user's logon session, which is where their Project runs.
    private const string LockName = @"Local\horizun-msproject-mcp-com";

    /// <summary>How long one session may take before it is treated as hung. Configurable because
    /// a very large schedule on a slow machine legitimately takes minutes.</summary>
    public static TimeSpan Timeout =>
        int.TryParse(Environment.GetEnvironmentVariable("HORIZUN_MSPROJECT_COM_TIMEOUT_SECONDS"), out var s) && s > 0
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromMinutes(5);

    public static T Run<T>(Func<ProjectHost, T> work)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new McpToolException("Microsoft Project can only be driven on Windows.");
        }

        using var gate = new Mutex(false, LockName);
        try
        {
            if (!gate.WaitOne(Timeout))
            {
                throw new McpToolException(
                    "Another operation — possibly from another client running this server — is still using "
                    + "Microsoft Project and did not finish in time. Try again.");
            }
        }
        catch (AbandonedMutexException)
        {
            // Its previous holder died mid-session; the lock is ours now, and the watchdog has
            // already dealt with whatever instance it left behind.
        }

        try
        {
            T? result = default;
            Exception? failure = null;
            int? ownedPid = null;

            // Project's object model is apartment-threaded; driving it from a dedicated STA thread
            // keeps every call on one thread and gives the watchdog something it can abandon.
            var thread = new Thread(() =>
            {
                try
                {
                    result = Session(work, pid => ownedPid = pid);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            })
            {
                IsBackground = true,
                Name = "horizun-msproject-com",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!thread.Join(Timeout))
            {
                if (ownedPid is int pid)
                {
                    try
                    {
                        Process.GetProcessById(pid).Kill();
                    }
                    catch
                    {
                        // Already gone.
                    }

                    thread.Join(TimeSpan.FromSeconds(30));
                    throw new McpToolException(
                        $"Microsoft Project stopped responding after {Timeout.TotalSeconds:0} seconds — most likely "
                        + "it was showing a dialog nobody could see. The instance this server started was closed. "
                        + "Nothing was saved. If the schedule is very large, raise "
                        + "HORIZUN_MSPROJECT_COM_TIMEOUT_SECONDS and try again.");
                }

                throw new McpToolException(
                    $"Microsoft Project did not respond within {Timeout.TotalSeconds:0} seconds. It is the copy "
                    + "you have open, so it was left alone: check it for an open dialog and answer it. "
                    + "Your documents were not touched.");
            }

            if (failure is not null)
            {
                throw failure is McpToolException ? failure : Describe(failure);
            }

            return result!;
        }
        finally
        {
            gate.ReleaseMutex();
        }
    }

    private static T Session<T>(Func<ProjectHost, T> work, Action<int> reportOwnedPid)
    {
        var type = Type.GetTypeFromProgID(ProgId, throwOnError: false)
                   ?? throw new McpToolException(
                       "Microsoft Project is not registered for COM automation on this machine. "
                       + "Run project_health with deep=true for the diagnosis and the repair steps.");

        var before = RunningPids();
        var app = Activator.CreateInstance(type) ?? throw new McpToolException("Microsoft Project did not start.");
        var started = RunningPids().Except(before).ToList();
        var owner = before.Count == 0;
        if (owner && started.Count == 1)
        {
            reportOwnedPid(started[0]);
        }

        var host = new ProjectHost(app, owner);
        try
        {
            host.Prepare();
            return work(host);
        }
        finally
        {
            host.Finish();
            try
            {
                Marshal.ReleaseComObject(app);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private static List<int> RunningPids() =>
        Process.GetProcessesByName(ProcessName).Select(p => p.Id).ToList();

    /// <summary>Late binding wraps every failure in TargetInvocationException; the useful message is
    /// underneath, and reporting the wrapper instead is how COM errors become undebuggable.</summary>
    internal static McpToolException Describe(Exception ex)
    {
        var root = ex;
        while (root is TargetInvocationException { InnerException: { } inner })
        {
            root = inner;
        }

        return root as McpToolException
               ?? new McpToolException($"Microsoft Project reported: {root.Message} ({root.GetType().Name}).");
    }
}

/// <summary>
/// One automation session inside Microsoft Project. Every method acts on a document opened by this
/// session and identified by its token — never on "the active document" taken on trust.
/// </summary>
public sealed class ProjectHost
{
    private readonly object _app;
    private readonly bool _owner;
    private object? _previouslyActive;
    private object? _previousAlerts;
    // Our documents, held by reference. A name is not a stable handle: saving as .mpp renames the
    // document to the new file, and a session that looked it up by name afterwards would lose it —
    // and leave it open in the user's window list.
    private readonly Dictionary<string, object> _opened = new();

    internal ProjectHost(object app, bool owner)
    {
        _app = app;
        _owner = owner;
    }

    /// <summary>True when this session started Microsoft Project; false when it is a guest in the
    /// copy the user already had open.</summary>
    public bool Owner => _owner;

    internal void Prepare()
    {
        _previousAlerts = TryGet(_app, "DisplayAlerts");
        TrySet(_app, "DisplayAlerts", false);

        if (_owner)
        {
            TrySet(_app, "Visible", false);
        }
        else
        {
            // Throws when no document is open; that simply means there is nothing to give back.
            _previouslyActive = TryGet(_app, "ActiveProject");
        }
    }

    /// <summary>
    /// Opens a file read-only. <paramref name="token"/> must be unique and part of the file name:
    /// Project imports an XML file as a new, unsaved project named after the file, with no path, so
    /// the name is the only reliable handle on it.
    /// </summary>
    public void Open(string path, string token)
    {
        if (!Path.GetFileNameWithoutExtension(path).Contains(token, StringComparison.Ordinal))
        {
            throw new ArgumentException("The token must be part of the file name.", nameof(token));
        }

        Call(_app, "FileOpenEx", Path.GetFullPath(path), true);
        _opened[token] = FindByName(token) ?? throw new McpToolException(
            "Microsoft Project opened the working copy but it could not be found among its documents.");
        Activate(token);
    }

    /// <summary>Recalculates only our document. <c>CalculateAll</c> would recalculate every open
    /// project — including a user's, which may be in manual calculation on purpose.</summary>
    public void Calculate(string token)
    {
        Activate(token);
        Call(_app, "CalculateProject");
    }

    /// <summary>Saves our document as MSPDI. The format has to be passed positionally: with named
    /// arguments Project silently writes a binary .mpp under the .xml name.</summary>
    public void SaveXml(string token, string path)
    {
        Activate(token);
        DeleteIfPresent(path);
        Call(_app, "FileSaveAs", Path.GetFullPath(path), 0, false, false, false, false, "", "", "", "MSProject.XML");
        Require(path);
    }

    /// <summary>Saves our document in Project's own binary format.</summary>
    public void SaveMpp(string token, string path)
    {
        Activate(token);
        DeleteIfPresent(path);
        // No FormatID: .mpp is Project's own default, and passing one is rejected as an invalid
        // argument rather than honoured.
        Call(_app, "FileSaveAs", Path.GetFullPath(path));
        Require(path);
    }

    public void Close(string token)
    {
        Activate(token);
        Call(_app, "FileCloseEx", 0); // pjDoNotSave: ours are always scratch copies
        _opened.Remove(token);
    }

    public string? Version => TryGet(_app, "Version")?.ToString();

    public string? Build => TryGet(_app, "Build")?.ToString();

    private void Activate(string token)
    {
        if (!_opened.TryGetValue(token, out var document))
        {
            throw new McpToolException("This session has no working copy by that name.");
        }

        var ours = NameOf(document) ?? throw new McpToolException(
            "The working copy this server opened in Microsoft Project is no longer open — it may have "
            + "been closed by hand. Nothing was saved; try again.");

        // Already ours — the normal case, and the only one a hidden instance allows: Project refuses
        // to activate a document while its window is hidden ("unexpected error in the method").
        if (ActiveName() == ours)
        {
            return;
        }

        try
        {
            Call(document, "Activate");
        }
        catch (McpToolException)
        {
            // Checked below: whatever the reason, what matters is whether ours is now active.
        }

        if (ActiveName() != ours)
        {
            throw new McpToolException(
                "Microsoft Project switched to another document in the middle of the operation, so it was "
                + "stopped before it could act on the wrong one. Nothing of yours was changed; try again.");
        }
    }

    private string? ActiveName()
    {
        var active = TryGet(_app, "ActiveProject");
        return active is null ? null : NameOf(active);
    }

    private static string? NameOf(object document) => TryGet(document, "Name")?.ToString();

    private object? FindByName(string token)
    {
        var projects = Get(_app, "Projects");
        var count = Convert.ToInt32(Get(projects, "Count"));
        for (var i = 1; i <= count; i++)
        {
            var document = projects.GetType().InvokeMember(
                "Item", BindingFlags.GetProperty, null, projects, new object[] { i });
            var name = document is null ? null : NameOf(document);
            if (name is not null && name.Contains(token, StringComparison.Ordinal))
            {
                return document;
            }
        }

        return null;
    }

    internal void Finish()
    {
        // Close anything of ours a failure left open, so a guest session never leaves scratch
        // documents in the user's window list.
        foreach (var token in _opened.Keys.ToList())
        {
            try
            {
                Close(token);
            }
            catch
            {
                // Best effort; the watchdog or the owner quit below cleans up what this cannot.
            }
        }

        TrySet(_app, "DisplayAlerts", _previousAlerts ?? true);

        if (!_owner)
        {
            if (_previouslyActive is not null)
            {
                try
                {
                    Call(_previouslyActive, "Activate");
                }
                catch
                {
                    // The user closed it meanwhile; nothing to restore.
                }
            }

            return;
        }

        // We started it hidden. If the user opened a document into it meanwhile — Windows routes a
        // double-clicked .mpp to the running instance, hidden or not — hand it over visible rather
        // than quitting with their document inside.
        var remaining = 0;
        try
        {
            remaining = Convert.ToInt32(Get(Get(_app, "Projects"), "Count"));
        }
        catch
        {
            // Unreadable: assume someone is using it.
            remaining = 1;
        }

        if (remaining > 0)
        {
            TrySet(_app, "Visible", true);
        }
        else
        {
            try
            {
                Call(_app, "Quit", 0);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void Require(string path)
    {
        if (!File.Exists(path))
        {
            throw new McpToolException($"Microsoft Project reported no error but '{path}' was not created.");
        }
    }

    private static object Get(object target, string property) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null)
        ?? throw new McpToolException($"Microsoft Project returned nothing for {property}.");

    private static object? TryGet(object target, string property)
    {
        try
        {
            return target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null);
        }
        catch
        {
            return null;
        }
    }

    private static void TrySet(object target, string property, object value)
    {
        try
        {
            target.GetType().InvokeMember(property, BindingFlags.SetProperty, null, target, new[] { value });
        }
        catch
        {
            // Conveniences; a version that does not expose them is not a failure.
        }
    }

    private static object? Call(object target, string method, params object[] args)
    {
        try
        {
            return target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args);
        }
        catch (Exception ex)
        {
            // Name the method: "unexpected error in the method" is all Project says on its own.
            var root = ProjectAutomation.Describe(ex);
            throw new McpToolException($"Microsoft Project failed in {method}: {root.Message}");
        }
    }
}
