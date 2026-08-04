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
        if (!File.Exists(path))
        {
            throw new McpToolException($"No file at '{path}'.");
        }

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
    /// Writes the schedule. MPXJ cannot author the native binary .mpp format, so a request to
    /// write one is refused outright rather than quietly producing a different format under
    /// the requested name.
    /// </summary>
    public static string Write(ProjectFile project, string path, string format)
    {
        var normalized = format.Trim().ToLowerInvariant();

        if (normalized == "mpp")
        {
            return WriteNativeMpp(project, Path.GetFullPath(path));
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

        var staging = Path.Combine(Path.GetTempPath(), $"hzpm-mpp-{Guid.NewGuid():N}.xml");
        try
        {
            new MSPDIWriter().Write(project, staging);
            ComBridge.SaveAsMpp(staging, path);
        }
        finally
        {
            try
            {
                File.Delete(staging);
            }
            catch
            {
                // A leftover temp file is not worth failing the save over.
            }
        }

        return path;
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
