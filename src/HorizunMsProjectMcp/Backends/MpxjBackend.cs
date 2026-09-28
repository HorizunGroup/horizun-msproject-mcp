using MPXJ.Net;

namespace Horizun.ProjectMcp.Backends;

/// <summary>
/// Reads and writes schedule files through MPXJ.NET — no JVM, no Microsoft Project, no licence.
/// This is the floor the server always stands on; COM is layered on top when it is proven to work.
/// </summary>
public static class MpxjBackend
{
    private static readonly string[] ReadableExtensions =
    {
        ".mpp", ".mpt", ".mpx", ".xml", ".xer", ".pmxml", ".pp", ".planner", ".gan", ".sp", ".prx", ".ppx",
    };

    public static ProjectFile Read(string path)
    {
        Guard.ExistingFile(path, "path");

        try
        {
            var project = new UniversalProjectReader().Read(path);
            if (project is null)
            {
                throw new McpToolException(
                    $"'{path}' was not recognised as a schedule file. Readable formats: " +
                    string.Join(", ", ReadableExtensions) + ".");
            }

            return project;
        }
        catch (McpToolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpToolException($"Failed to read '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the schedule and reads it back, so the result says what the file really holds.
    /// </summary>
    /// <remarks>
    /// A native .mpp is written by Microsoft Project (see <see cref="ComBridge.SaveAsMpp"/>) and is
    /// all-or-nothing: it replaces the target only when it reads back intact, and fails otherwise.
    /// Every other format is written by MPXJ; formats that cannot hold everything (MPX has no
    /// baseline, XER renumbers some fields) are written anyway, and what they dropped is listed.
    /// </remarks>
    public static (string Path, IReadOnlyList<string> Notes) WriteVerified(ProjectFile project, string path, string format)
    {
        var normalized = format.Trim().ToLowerInvariant();
        if (normalized == "mpp")
        {
            Writes.Structure.Normalize(project);
            var full = Path.GetFullPath(path);
            var note = WriteNativeMpp(project, full);
            return (full, new[] { note });
        }

        var written = Write(project, path, format);
        if (normalized is "json" or "sdef")
        {
            return (written, new[] { $"Not read back: {normalized} is written for other tools, not re-read by this server." });
        }

        try
        {
            var losses = ExportFidelity.Compare(project, Read(written));
            return losses.Count == 0
                ? (written, new[] { $"Verified by reading the file back: {ExportFidelity.Census(project)} — all preserved." })
                : (written, new[]
                {
                    $"WARNING — written, but this format did not keep everything. Read back, it differs: "
                    + ExportFidelity.Describe(losses) + " Use mspdi or mpp to keep all of it.",
                });
        }
        catch (Exception ex)
        {
            return (written, new[] { $"WARNING — written, but it could not be read back to verify it: {ex.Message}" });
        }
    }

    /// <summary>Writes without checking; <see cref="WriteVerified"/> is what the tools call.</summary>
    public static string Write(ProjectFile project, string path, string format)
    {
        var normalized = format.Trim().ToLowerInvariant();

        // Anything added without an id or a place in the outline — by an older build, or a write
        // that failed half-way — would otherwise break the writer or land in the wrong place.
        Writes.Structure.Normalize(project);

        if (normalized == "mpp")
        {
            WriteNativeMpp(project, Path.GetFullPath(path));
            return Path.GetFullPath(path);
        }

        IProjectWriter writer = normalized switch
        {
            "mspdi" or "xml" => new MSPDIWriter(),
            "mpx" => new MPXWriter(),
            "json" => new JsonWriter(),
            "xer" => new PrimaveraXERFileWriter(),
            "pmxml" => new PrimaveraPMFileWriter(),
            "planner" => new PlannerWriter(),
            "sdef" => new SDEFWriter(),
            _ => throw new McpToolException(
                $"Unknown output format '{format}'. Use mspdi, mpx, json, mpp, xer, pmxml, planner, or sdef."),
        };

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            writer.Write(project, path);
            return path;
        }
        catch (McpToolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpToolException($"Failed to write '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Native .mpp is the one format no library can author. Where Microsoft Project is installed and
    /// its COM server actually starts, hand the job to it; otherwise refuse and say what to do instead.
    /// </summary>
    private static string WriteNativeMpp(ProjectFile project, string path)
    {
        // Registration is enough to try. If the server then refuses to start, ComBridge surfaces the
        // HRESULT diagnosis, which is more useful than pre-emptively declining.
        var probe = Diagnostics.ComProbe.Inspect();
        if (!probe.Available)
        {
            throw new McpToolException(
                "Native .mpp cannot be written here: no library can author that format, and Microsoft " +
                $"Project is not available for COM automation on this machine ({probe.Status}). " +
                "Write MSPDI instead (format='mspdi' — a .xml Microsoft Project opens natively), or run " +
                "project_health with deep=true for the diagnosis and repair steps.");
        }

        return ComBridge.SaveAsMpp(project, path);
    }

    /// <summary>
    /// Deep-copies a schedule by round-tripping it through MSPDI in a temp file.
    /// This is what makes dry-run a real simulation: apply to the copy, measure, throw the copy away.
    /// </summary>
    public static ProjectFile Clone(ProjectFile project)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"hzpm-{Guid.NewGuid():N}.xml");
        try
        {
            // Ids only: a copy must not renumber the live schedule it is taken from.
            Writes.Structure.EnsureIds(project);
            new MSPDIWriter().Write(project, temp);
            return new MSPDIReader().Read(temp)
                   ?? throw new McpToolException("Could not clone the schedule for simulation.");
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // A leftover temp file is not worth failing the operation over.
            }
        }
    }

    public static DateTime? ProjectFinish(ProjectFile project)
    {
        DateTime? latest = null;
        foreach (var task in project.Tasks)
        {
            var finish = task.Finish;
            if (finish is not null && (latest is null || finish > latest))
            {
                latest = finish;
            }
        }

        return latest ?? project.ProjectProperties.FinishDate;
    }
}
