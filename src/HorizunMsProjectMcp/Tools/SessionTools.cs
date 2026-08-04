using System.ComponentModel;
using Horizun.ProjectMcp.Backends;
using ModelContextProtocol.Server;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Tools;

public sealed record OpenResult
{
    public required string Handle { get; init; }
    public required string Path { get; init; }
    public required string Fingerprint { get; init; }
    public required bool ReadOnly { get; init; }
    public required int Tasks { get; init; }
    public required int Resources { get; init; }
    public required int Assignments { get; init; }
    public string? Name { get; init; }
    public string? Start { get; init; }
    public string? Finish { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record SaveResult
{
    public required string Op { get; init; }
    public required string Handle { get; init; }
    public string? Path { get; init; }
    public string? Format { get; init; }
    public bool StillOpen { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

[McpServerToolType]
public static class SessionTools
{
    [McpServerTool(Name = "project_open")]
    [Description(
        "Open a schedule — or start a new one — and get a handle for every other tool. Reads .mpp, .mpt, "
        + ".mpx, MSPDI .xml, Primavera .xer and .pmxml, Asta .pp and more, with no Microsoft Project needed. "
        + "Returns a fingerprint of the file; later writes revalidate it and refuse if the file changed on "
        + "disk in the meantime, so a stale handle can never overwrite somebody else's edits. "
        + "Pass create=true to begin an empty schedule at that path instead of reading one.")]
    public static OpenResult ProjectOpen(
        [Description("Absolute path to the schedule file.")] string path,
        [Description("'readwrite' (default) or 'readonly'. A read-only handle refuses every write tool.")]
        string mode = "readwrite",
        [Description("Start a new empty schedule instead of reading an existing file. Refuses to clobber an existing file.")]
        bool create = false,
        [Description("Project name for a newly created schedule.")] string? name = null,
        [Description("Project start date for a newly created schedule, yyyy-MM-dd.")] string? startDate = null)
    {
        var full = Path.GetFullPath(path);
        var readOnly = mode.Trim().Equals("readonly", StringComparison.OrdinalIgnoreCase);

        ProjectFile project;
        if (create)
        {
            if (File.Exists(full))
            {
                throw new McpToolException(
                    $"'{full}' already exists. Refusing to overwrite it — open it without create=true, " +
                    "or choose another path.");
            }

            if (readOnly)
            {
                throw new McpToolException("A newly created schedule cannot be opened read-only.");
            }

            project = new ProjectFile();
            project.ProjectProperties.Name = name ?? Path.GetFileNameWithoutExtension(full);
            var start = QueryTools.ParseDate(startDate) ?? DateTime.Today;
            project.ProjectProperties.StartDate = start;
            project.ProjectProperties.StatusDate = start;

            // AddDefaultBaseCalendar creates the calendar but does not make it the project's
            // default, and without that calendars_write has nowhere to put an exception.
            project.DefaultCalendar = project.AddDefaultBaseCalendar();
        }
        else
        {
            project = MpxjBackend.Read(full);
        }

        var session = SessionStore.Add(full, project, readOnly);
        if (create)
        {
            // Nothing is on disk yet, so the handle is dirty from birth — project_save must run
            // before the fingerprint means anything.
            session.Dirty = true;
        }

        var notes = new List<string>();
        if (create)
        {
            notes.Add(
                "New empty schedule. Nothing has been written to disk yet — call project_save when you are ready.");
        }
        else if (project.Tasks.Count == 0)
        {
            notes.Add("The file opened but contains no tasks.");
        }

        if (!project.Tasks.Any(t => t.BaselineFinish is not null))
        {
            notes.Add(
                "No baseline is stored, so baseline_compare and the DCMA checks that measure against a "
                + "baseline will report as not evaluated rather than guessing.");
        }

        return new OpenResult
        {
            Handle = session.Handle,
            Path = full,
            Fingerprint = session.Fingerprint,
            ReadOnly = readOnly,
            Tasks = project.Tasks.Count,
            Resources = project.Resources.Count,
            Assignments = project.ResourceAssignments.Count,
            Name = project.ProjectProperties.Name ?? project.ProjectProperties.ProjectTitle,
            Start = MpxjMapper.Iso(project.ProjectProperties.StartDate),
            Finish = MpxjMapper.Iso(MpxjBackend.ProjectFinish(project)),
            Notes = notes,
        };
    }

    [McpServerTool(Name = "project_save")]
    [Description(
        "Save or close an open schedule. Never saves on its own — closing with unsaved changes requires "
        + "either saving first or passing discardChanges=true, so work is never silently lost. "
        + "Note that no library can author native .mpp; use format='mspdi' for a .xml Microsoft Project "
        + "opens natively.")]
    public static SaveResult ProjectSave(
        [Description("Document handle from project_open.")] string handle,
        [Description("'save' (write back over the original path), 'save_as' (write to path), or 'close'.")]
        string op = "save",
        [Description("Target path for save_as. Ignored for save and close.")] string? path = null,
        [Description("Output format: 'mspdi' (default, .xml), 'mpx', or 'json'.")] string format = "mspdi",
        [Description("Keep the handle open after saving. Defaults to true.")] bool keepOpen = true,
        [Description("Allow close to drop unsaved changes. Defaults to false.")] bool discardChanges = false)
    {
        var session = SessionStore.Get(handle);
        var operation = op.Trim().ToLowerInvariant();

        switch (operation)
        {
            case "close":
                if (session.Dirty && !discardChanges)
                {
                    throw new McpToolException(
                        $"Document '{handle}' has unsaved changes. Save it first, or pass " +
                        "discardChanges=true to close and lose them.");
                }

                SessionStore.Remove(handle);
                return new SaveResult
                {
                    Op = "close",
                    Handle = handle,
                    StillOpen = false,
                    Notes = session.Dirty
                        ? new[] { "Closed with unsaved changes discarded, as requested." }
                        : Array.Empty<string>(),
                };

            case "save":
            case "save_as":
            {
                if (session.ReadOnly)
                {
                    throw new McpToolException(
                        $"Document '{handle}' was opened read-only. Reopen it with mode='readwrite' to save.");
                }

                var target = operation == "save_as"
                    ? path ?? throw new McpToolException("save_as needs a target path.")
                    : DefaultTargetFor(session.Path, format);

                var written = MpxjBackend.Write(session.File, Path.GetFullPath(target), format);

                session.Dirty = false;
                session.Fingerprint = ProjectSession.ComputeFingerprint(session.Path, session.File);

                if (!keepOpen)
                {
                    SessionStore.Remove(handle);
                }

                var notes = new List<string>();
                if (operation == "save" && !written.Equals(session.Path, StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add(
                        $"The original file is '{session.Path}', which this backend cannot author. " +
                        $"Wrote '{written}' instead — open it in Microsoft Project and save as .mpp if you " +
                        "need the native format back.");
                }

                return new SaveResult
                {
                    Op = operation,
                    Handle = handle,
                    Path = written,
                    Format = format,
                    StillOpen = keepOpen,
                    Notes = notes,
                };
            }

            default:
                throw new McpToolException($"Unknown op '{op}'. Use save, save_as, or close.");
        }
    }

    /// <summary>
    /// A "save" over a format we cannot author would either fail or silently write something else.
    /// Redirect to a sibling file in a format we can write, and say so.
    /// </summary>
    private static string DefaultTargetFor(string originalPath, string format)
    {
        var extension = Path.GetExtension(originalPath).ToLowerInvariant();
        var writable = format.Trim().ToLowerInvariant() switch
        {
            "mpx" => ".mpx",
            "json" => ".json",
            _ => ".xml",
        };

        return extension == writable ? originalPath : Path.ChangeExtension(originalPath, writable);
    }
}
