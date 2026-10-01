namespace Horizun.ProjectMcp.Backends;

/// <summary>
/// The things only an installed Microsoft Project can do, on top of <see cref="ProjectAutomation"/>.
/// </summary>
public static class ComBridge
{
    /// <summary>
    /// Writes a genuine binary .mpp by having Microsoft Project open the schedule and save it in its
    /// own format — the only way the format can be authored at all — and proves it before claiming it.
    /// </summary>
    /// <remarks>
    /// <para>The schedule goes to Project through <see cref="ProjectHandoff"/>, which is what keeps its
    /// dates Project's own. Progress does not survive that import (Project derives it from assignment
    /// work), so it is put back afterwards through Project's own fields, with the status date.</para>
    /// <para>Nothing is claimed on the strength of Project not throwing. The .mpp is written beside the
    /// target, read back, and compared task by task with the schedule it came from — progress, actual
    /// dates, actual duration and work, baseline, status date. Only a file that matches replaces the
    /// target, so a save that would lose data never overwrites anything; it fails with what was lost,
    /// and leaves the unfaithful copy beside the target for inspection.</para>
    /// </remarks>
    /// <returns>A one-line summary of what was verified.</returns>
    public static string SaveAsMpp(MPXJ.Net.ProjectFile project, string targetMppPath)
    {
        var token = $"hzpm-{Guid.NewGuid():N}";
        var staging = Path.Combine(Path.GetTempPath(), token + ".xml");
        var directory = Path.GetDirectoryName(Path.GetFullPath(targetMppPath)) ?? Path.GetTempPath();
        var candidate = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(targetMppPath)}.{token}.mpp");

        var progress = project.Tasks
            .Where(t => t.UniqueID is not null and not 0 && !t.Summary && !t.Null
                        && (t.ActualStart is not null || (t.PercentageComplete ?? 0) > 0))
            .Select(t => new ProgressRecord(
                t.UniqueID!.Value,
                t.ActualStart ?? ((t.PercentageComplete ?? 0) > 0 ? t.Start : null),
                t.ActualFinish ?? ((t.PercentageComplete ?? 0) >= 100 ? t.Finish : null),
                Convert.ToDouble(t.PercentageComplete ?? 0),
                MinutesOf(project, t.ActualDuration),
                t.RemainingDuration is null ? null : MinutesOf(project, t.RemainingDuration) ?? 0))
            .ToList();

        try
        {
            ProjectHandoff.Write(project, staging);
            ProjectAutomation.Run(host =>
            {
                host.Open(staging, token);
                host.ApplyProgress(token, project.ProjectProperties.StatusDate, progress);
                var resumes = Analysis.ProjectScheduler.PendingResumes(project);
                if (resumes.Count > 0)
                {
                    host.ApplyResumes(token, resumes);
                    host.Calculate(token);
                }
                host.SaveMpp(token, candidate);
                host.Close(token);
                return 0;
            });
        }
        catch (Exception ex) when (ex is McpToolException or NullReferenceException or InvalidOperationException
                                       or System.Xml.XmlException or IOException)
        {
            TryDelete(candidate);
            throw new McpToolException(
                $"Microsoft Project could not write the .mpp: {ex.Message} Nothing was written. Write MSPDI "
                + "instead (format='mspdi' — a .xml Microsoft Project opens natively), or run project_health "
                + "with deep=true to check the COM server.");
        }
        finally
        {
            TryDelete(staging);
        }

        var written = MpxjBackend.Read(candidate);
        var losses = ExportFidelity.Compare(project, written);
        var critical = losses.Where(l => l.Critical).ToList();
        if (critical.Count > 0)
        {
            var kept = Path.ChangeExtension(targetMppPath, ".unverified.mpp");
            File.Move(candidate, kept, overwrite: true);
            throw new McpToolException(
                $"The .mpp did not keep the schedule, so '{targetMppPath}' was NOT written. Read back, it "
                + $"differs: {ExportFidelity.Describe(critical)} The file Project produced is at '{kept}' "
                + "for inspection. The schedule itself is untouched; MSPDI (format='mspdi') keeps all of it.");
        }

        File.Move(candidate, targetMppPath, overwrite: true);
        var verified = $"Verified by reading the .mpp back: {ExportFidelity.Census(written)}; progress, actual "
                       + "dates, baseline and status date all preserved.";
        var derived = losses.Where(l => !l.Critical).ToList();
        return derived.Count == 0
            ? verified
            : verified + " WARNING — Microsoft Project re-derived figures the source states inconsistently "
              + "with its own dates, and kept the dates: " + ExportFidelity.Describe(derived)
              + " Check those tasks' durations in the source if they matter.";
    }

    private static double? MinutesOf(MPXJ.Net.ProjectFile project, MPXJ.Net.Duration? duration)
    {
        var hoursPerDay = project.ProjectProperties.MinutesPerDay is > 0
            ? Convert.ToDouble(project.ProjectProperties.MinutesPerDay) / 60.0
            : MpxjMapper.HoursPerDay;
        var hours = MpxjMapper.Hours(duration, hoursPerDay);
        return hours is > 0 ? Math.Round(hours.Value * 60, 1) : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A leftover temp file is not worth failing the save over.
        }
    }
}
