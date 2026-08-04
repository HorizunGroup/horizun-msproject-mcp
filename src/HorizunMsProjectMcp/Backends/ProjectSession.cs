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
    public DateTime OpenedAt { get; } = DateTime.UtcNow;

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

    public static ProjectSession Add(string path, ProjectFile file, bool readOnly)
    {
        var handle = $"d{Interlocked.Increment(ref _counter)}";
        var session = new ProjectSession
        {
            Handle = handle,
            Path = path,
            File = file,
            ReadOnly = readOnly,
            Fingerprint = ProjectSession.ComputeFingerprint(path, file),
        };
        Sessions[handle] = session;
        return session;
    }

    public static ProjectSession Get(string handle) =>
        Sessions.TryGetValue(handle, out var s)
            ? s
            : throw new McpToolException(
                $"No open document with handle '{handle}'. Call project_open first; " +
                $"currently open: {(Sessions.IsEmpty ? "none" : string.Join(", ", Sessions.Keys))}.");

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
