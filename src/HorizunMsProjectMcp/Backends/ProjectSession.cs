using System.Collections.Concurrent;
using System.Security.Cryptography;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Backends;

/// <summary>
/// One open schedule. Carries the fingerprint that lets a write refuse to run against a
/// document somebody changed underneath us.
/// </summary>
public sealed class ProjectSession
{
    public required string Handle { get; init; }
    public required string Path { get; init; }
    public required ProjectFile File { get; set; }
    public required bool ReadOnly { get; init; }
    public required string Fingerprint { get; set; }
    public bool Dirty { get; set; }

    /// <summary>
    /// True when this schedule was created through this server rather than imported.
    /// </summary>
    /// <remarks>
    /// This is the gate on automatic rescheduling. Our critical-path engine reproduces Microsoft
    /// Project exactly on schedules we authored, and measurably does not on real imported ones —
    /// on a client file it can move half the tasks by a week or more. So imported schedules are
    /// never silently rescheduled: their dates stay as Microsoft Project left them, and the caller
    /// asks for a recalculation explicitly if they want ours.
    /// </remarks>
    public bool Authored { get; init; }

    /// <summary>Set once the caller has explicitly asked for our engine on this document.</summary>
    public bool RescheduleAuthorised { get; set; }

    public bool MayReschedule => Authored || RescheduleAuthorised;
    public DateTime OpenedAt { get; } = DateTime.UtcNow;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Runs <paramref name="body"/> with exclusive access to this document.
    /// </summary>
    /// <remarks>
    /// MPXJ's object model is not thread-safe, and an MCP client is free to have several tool calls
    /// in flight at once. Unguarded, sixty concurrent writes to one schedule all failed and left the
    /// document unusable — measured, not theorised. Every tool that touches a document goes through
    /// here. The lock is per document, so unrelated schedules never wait on each other.
    /// </remarks>
    public T Locked<T>(Func<T> body)
    {
        // Reentrant on purpose. Several tools are built on others — schedule_sequence applies its
        // proposals through links_write, project_import through tasks_write — and a plain semaphore
        // would have them wait on a lock their own call already holds, hanging the server outright.
        var me = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref _owner) == me)
        {
            return body();
        }

        _gate.Wait();
        Volatile.Write(ref _owner, me);
        try
        {
            return body();
        }
        finally
        {
            Volatile.Write(ref _owner, 0);
            _gate.Release();
        }
    }

    private int _owner;

    /// <summary>
    /// Identity of the file on disk plus the shape of what we loaded. If either moves, a
    /// write would be pointing at something other than what the agent inspected.
    /// </summary>
    public static string ComputeFingerprint(string path, ProjectFile file)
    {
        var info = new FileInfo(path);
        var seed = string.Join(
            '|',
            info.Exists ? info.LastWriteTimeUtc.Ticks.ToString() : "0",
            info.Exists ? info.Length.ToString() : "0",
            file.Tasks.Count.ToString(),
            file.Resources.Count.ToString(),
            file.ResourceAssignments.Count.ToString());

        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed)))[..16];
    }
}

/// <summary>Process-wide registry of open schedules.</summary>
public static class SessionStore
{
    private static readonly ConcurrentDictionary<string, ProjectSession> Sessions = new();
    private static int _counter;

    /// <summary>
    /// How many schedules may be open at once.
    /// </summary>
    /// <remarks>
    /// A schedule stays in memory until it is closed, and a large one is not cheap: a 170 MB file
    /// with 1,937 tasks costs around 400 MB resident. Nothing evicts them, so an agent that opens
    /// documents and forgets to close them would climb until the process died — with no hint that
    /// the cause was housekeeping. Refusing the twenty-first open, and naming what is holding the
    /// memory, is far kinder than an out-of-memory kill.
    /// </remarks>
    public const int MaxOpenDocuments = 20;

    public static ProjectSession Add(string path, ProjectFile file, bool readOnly, bool authored = false)
    {
        while (Sessions.Count >= MaxOpenDocuments)
        {
            // Retire the oldest document that has nothing unsaved. Refusing outright would punish
            // a caller for reading a lot of schedules, which is a normal thing to do; discarding
            // unsaved work to make room would be far worse. So only clean documents are evicted.
            var evictable = Sessions.Values
                .Where(s => !s.Dirty)
                .OrderBy(s => s.OpenedAt)
                .FirstOrDefault();

            if (evictable is null)
            {
                var unsaved = string.Join(", ", Sessions.Values
                    .OrderBy(s => s.OpenedAt)
                    .Select(s => $"{s.Handle} ({System.IO.Path.GetFileName(s.Path)})"));

                throw new McpToolException(
                    $"All {Sessions.Count} open schedules have unsaved changes, so none can be closed to "
                    + "make room, and none will be discarded to make room either. Save or close one "
                    + $"first with project_save. Open, oldest first: {unsaved}.");
            }

            Sessions.TryRemove(evictable.Handle, out _);
            Evicted[evictable.Handle] = System.IO.Path.GetFileName(evictable.Path);
        }

        var handle = $"d{Interlocked.Increment(ref _counter)}";
        var session = new ProjectSession
        {
            Handle = handle,
            Path = path,
            File = file,
            ReadOnly = readOnly,
            Fingerprint = ProjectSession.ComputeFingerprint(path, file),
            Authored = authored,
        };
        Sessions[handle] = session;
        return session;
    }

    /// <summary>Resolves a handle and runs the body under that document's lock.</summary>
    public static T Use<T>(string handle, Func<ProjectSession, T> body)
    {
        var session = Get(handle);
        return session.Locked(() => body(session));
    }

    /// <summary>As <see cref="Use{T}"/>, but for the tools that modify the document.</summary>
    public static T UseForWrite<T>(string handle, Func<ProjectSession, T> body)
    {
        var session = GetForWrite(handle);
        return session.Locked(() => body(session));
    }

    /// <summary>Handles retired to make room, so their reuse can be explained rather than denied.</summary>
    private static readonly ConcurrentDictionary<string, string> Evicted = new();

    public static ProjectSession Get(string handle)
    {
        if (Sessions.TryGetValue(handle, out var session))
        {
            return session;
        }

        // A handle that was retired to make room is not the same situation as one that never
        // existed, and telling the two apart saves the caller hunting for a bug that is not there.
        if (Evicted.TryGetValue(handle, out var name))
        {
            throw new McpToolException(
                $"Document '{handle}' ({name}) was closed automatically to make room for another "
                + "schedule — it had no unsaved changes, so nothing was lost. Open it again with "
                + "project_open. Close documents you have finished with to stop this happening.");
        }

        throw new McpToolException(
            $"No open document with handle '{handle}'. Call project_open first; " +
            $"currently open: {(Sessions.IsEmpty ? "none" : string.Join(", ", Sessions.Keys))}.");
    }

    /// <summary>
    /// Resolves a handle for a write and refuses if the file changed on disk since it was opened.
    /// This is what stops a stale handle from silently overwriting somebody else's edits.
    /// </summary>
    public static ProjectSession GetForWrite(string handle)
    {
        var session = Get(handle);

        if (session.ReadOnly)
        {
            throw new McpToolException(
                $"Document '{handle}' was opened read-only. Reopen it with mode='readwrite' to modify it.");
        }

        if (!session.Dirty)
        {
            var current = ProjectSession.ComputeFingerprint(session.Path, session.File);
            if (current != session.Fingerprint)
            {
                throw new McpToolException(
                    $"Document '{handle}' changed on disk since it was opened " +
                    $"(fingerprint {session.Fingerprint} -> {current}). Refusing to write over it. " +
                    "Reopen the file and re-check your assumptions before retrying.");
            }
        }

        return session;
    }

    public static bool Remove(string handle) => Sessions.TryRemove(handle, out _);

    public static IReadOnlyList<ProjectSession> All() => Sessions.Values.ToList();
}

/// <summary>Input checks that fail with a message instead of an unexplained exception.</summary>
/// <remarks>
/// An empty path used to reach Path.GetFullPath and throw, which the SDK masks as
/// "An error occurred invoking &lt;tool&gt;" with nothing after it — the one failure mode that
/// leaves a caller with no idea what happened.
/// </remarks>
public static class Guard
{
    public static string Path(string? value, string argument)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new McpToolException($"'{argument}' is required and cannot be empty.");
        }

        try
        {
            return System.IO.Path.GetFullPath(value);
        }
        catch (Exception ex)
        {
            throw new McpToolException(
                $"'{argument}' is not a usable path: {ex.Message} (given '{value}').");
        }
    }

    /// <summary>A path that must already exist as a file, not a directory.</summary>
    public static string ExistingFile(string? value, string argument)
    {
        var full = Path(value, argument);

        if (Directory.Exists(full))
        {
            throw new McpToolException(
                $"'{full}' is a directory, not a schedule file. Give the path of the file itself.");
        }

        if (!File.Exists(full))
        {
            throw new McpToolException($"No file at '{full}'.");
        }

        return full;
    }
}

/// <summary>
/// An error meant for the agent: states what went wrong and what to do instead.
/// </summary>
/// <remarks>
/// Derives from the SDK's <see cref="ModelContextProtocol.McpException"/> on purpose. The SDK masks
/// ordinary exceptions behind "An error occurred invoking &lt;tool&gt;" so server internals cannot leak;
/// an McpException is passed through intact. These messages are the whole point — a refusal that
/// does not say what to do instead is no better than a crash.
/// </remarks>
public sealed class McpToolException(string message) : ModelContextProtocol.McpException(message);
