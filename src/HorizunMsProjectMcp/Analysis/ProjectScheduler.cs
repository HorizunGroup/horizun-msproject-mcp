using Horizun.ProjectMcp.Backends;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Analysis;

/// <summary>
/// Schedules by handing the work to Microsoft Project itself: the schedule is written as MSPDI,
/// Project opens it and calculates it, and the dates it computes are copied back onto the live model.
/// </summary>
/// <remarks>
/// This is the reason the server can promise Microsoft Project's dates rather than an imitation of
/// them. The other half of that promise is <see cref="ProjectHandoff"/>, without which Project does
/// not compute the same dates from an XML file as from the .mpp it came from.
/// </remarks>
public static class ProjectScheduler
{
    public static ScheduleRunReport Run(ProjectFile project)
    {
        var token = $"hzpm-{Guid.NewGuid():N}";
        var directory = Path.GetTempPath();
        var input = Path.Combine(directory, token + ".xml");
        var output = Path.Combine(directory, token + "-calculated.xml");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timings = new List<string>();
        void Lap(string stage)
        {
            timings.Add($"{stage}: {clock.Elapsed.TotalSeconds:0.0}s");
            clock.Restart();
        }

        try
        {
            ProjectHandoff.Write(project, input);
            Lap("handoff");

            ProjectAutomation.Run(host =>
            {
                host.Open(input, token);
                host.Calculate(token);
                host.SaveXml(token, output);
                host.Close(token);
                return 0;
            });
            Lap("microsoft project");

            var calculated = new MSPDIReader().Read(output)
                             ?? throw new McpToolException("Microsoft Project's calculated schedule could not be read back.");
            Lap("read back");

            var report = CopyBack(calculated, project);
            Lap("copy back");
            KeepForDiagnosis(input, output, timings);
            return report;
        }
        finally
        {
            foreach (var file in new[] { input, output })
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // A leftover temp file is not worth failing the calculation over.
                }
            }
        }
    }

    /// <summary>
    /// HORIZUN_MSPROJECT_DEBUG_DIR keeps a copy of what went to Project and what came back — the two
    /// files that answer "why does this date differ" without having to reproduce the session.
    /// </summary>
    private static void KeepForDiagnosis(string input, string output, IEnumerable<string> timings)
    {
        var directory = Environment.GetEnvironmentVariable("HORIZUN_MSPROJECT_DEBUG_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            File.Copy(input, Path.Combine(directory, "handoff.xml"), overwrite: true);
            File.Copy(output, Path.Combine(directory, "calculated.xml"), overwrite: true);
            File.WriteAllLines(Path.Combine(directory, "timings.txt"), timings);
        }
        catch
        {
            // Diagnosis is optional; the calculation is not.
        }
    }

    /// <summary>Copies what Project computed onto the live model — the same fields the internal engine
    /// writes, so everything downstream reads them the same way whichever engine produced them.</summary>
    private static ScheduleRunReport CopyBack(ProjectFile calculated, ProjectFile live)
    {
        var warnings = new List<string>();
        int scheduled = 0, summaries = 0, critical = 0, negative = 0, missing = 0;

        foreach (var source in calculated.Tasks)
        {
            if (source.UniqueID is not int uid || uid == 0)
            {
                continue;
            }

            // A blank row the hand-off sent over as blank comes back without dates; the model keeps
            // the ones MPXJ gave it rather than losing them.
            if (source.Null)
            {
                continue;
            }

            var target = live.GetTaskByUniqueID(uid);
            if (target is null)
            {
                missing++;
                continue;
            }

            target.Start = source.Start;
            target.Finish = source.Finish;
            target.Duration = source.Duration;
            target.EarlyStart = source.EarlyStart;
            target.EarlyFinish = source.EarlyFinish;
            target.LateStart = source.LateStart;
            target.LateFinish = source.LateFinish;
            target.TotalSlack = source.TotalSlack;
            target.FreeSlack = source.FreeSlack;
            target.Critical = source.Critical;

            // Progress is deliberately not copied back. Project re-importing an in-progress task
            // from XML — its own export included — moves part of the work done into remaining (a
            // task 50% done came back 44%), while the dates come through intact. The schedule's own
            // progress is already Project's, and writes keep it that way through ProgressRules.

            if (source.Summary)
            {
                summaries++;
                continue;
            }

            scheduled++;
            if (source.Critical)
            {
                critical++;
            }

            if ((MpxjMapper.Days(source.TotalSlack) ?? 0) < 0)
            {
                negative++;
            }
        }

        // Assignment dates move with their tasks. Placeholders the hand-off removed come back from
        // Project under new ids and have no counterpart here, which is fine: they carry nothing.
        var liveAssignments = live.ResourceAssignments
            .Where(a => a.UniqueID is not null)
            .GroupBy(a => a.UniqueID!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var source in calculated.ResourceAssignments)
        {
            if (source.UniqueID is not int uid || !liveAssignments.TryGetValue(uid, out var target))
            {
                continue;
            }

            target.Start = source.Start;
            target.Finish = source.Finish;
            // Project has sized it now; its dates are current again.
            Writes.ProgressRules.Resized.Remove(target);
        }

        if (missing > 0)
        {
            warnings.Add($"{missing} task(s) Microsoft Project returned have no counterpart in this schedule and were ignored.");
        }

        var leaves = calculated.Tasks.Where(t => !t.Summary && t.UniqueID is not null and not 0).ToList();
        var start = leaves.Where(t => t.Start is not null).Select(t => t.Start).DefaultIfEmpty().Min();
        var finish = leaves.Where(t => t.Finish is not null).Select(t => t.Finish).DefaultIfEmpty().Max();

        return new ScheduleRunReport
        {
            Engine = Scheduler.MicrosoftProject,
            TasksScheduled = scheduled,
            SummariesRolledUp = summaries,
            CriticalTasks = critical,
            NegativeFloatTasks = negative,
            ProjectStart = MpxjMapper.Iso(start),
            ProjectFinish = MpxjMapper.Iso(finish),
            Warnings = warnings,
        };
    }
}
