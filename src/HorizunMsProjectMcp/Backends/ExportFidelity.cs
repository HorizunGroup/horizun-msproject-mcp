using MPXJ.Net;

namespace Horizun.ProjectMcp.Backends;

/// <summary>One field that did not survive a write, with how many tasks lost it and a few examples.</summary>
public sealed record FidelityLoss
{
    public required string Field { get; init; }
    public required int Tasks { get; init; }
    public IReadOnlyList<string> Examples { get; init; } = Array.Empty<string>();

    /// <summary>True for what defines a schedule's state — progress, actual dates, baseline, status
    /// date, the task itself. False for figures Project derives from those (durations, actual
    /// duration and work), which it recomputes wherever a source's own figures disagree.</summary>
    public bool Critical { get; init; } = true;
}

/// <summary>
/// Reads a written schedule back and compares it, task by task, with what was meant to be written:
/// progress, actual dates, actual duration and work, baseline and status date.
/// </summary>
/// <remarks>
/// An export used to report success because the writer did not throw. A native .mpp came back with
/// 41 finished tasks turned into 1 — Project, importing the MSPDI it is handed, derives progress from
/// assignment work that was not there — and nothing said so. Success now means "read back and
/// matched"; anything else is reported by field.
/// </remarks>
public static class ExportFidelity
{
    public static IReadOnlyList<FidelityLoss> Compare(ProjectFile expected, ProjectFile actual)
    {
        var losses = new Dictionary<string, List<string>>();
        void Lost(string field, MPXJ.Net.Task task, object? want, object? got)
        {
            if (!losses.TryGetValue(field, out var list))
            {
                losses[field] = list = new List<string>();
            }

            list.Add($"uid {task.UniqueID} '{task.Name}': {Show(want)} -> {Show(got)}");
        }

        var wantStatus = expected.ProjectProperties.StatusDate;
        var gotStatus = actual.ProjectProperties.StatusDate;
        if (wantStatus is not null && (gotStatus is null || wantStatus.Value.Date != gotStatus.Value.Date))
        {
            losses["statusDate"] = new List<string> { $"{Show(wantStatus)} -> {Show(gotStatus)}" };
        }

        var hours = expected.ProjectProperties.MinutesPerDay is > 0
            ? Convert.ToDouble(expected.ProjectProperties.MinutesPerDay) / 60.0
            : MpxjMapper.HoursPerDay;

        foreach (var want in expected.Tasks)
        {
            // Detail tasks only: a summary's progress and dates are Project's rollup of them, recomputed
            // on save, and are right exactly when the tasks beneath are.
            if (want.UniqueID is not int uid || uid == 0 || want.Null || want.Summary
                || string.IsNullOrWhiteSpace(want.Name) && want.Start is null)
            {
                continue;
            }

            var got = actual.GetTaskByUniqueID(uid);
            if (got is null || got.Null)
            {
                if (!string.IsNullOrWhiteSpace(want.Name))
                {
                    Lost("task", want, "present", "missing");
                }

                continue;
            }

            if (Math.Abs(Convert.ToDouble(want.PercentageComplete ?? 0) - Convert.ToDouble(got.PercentageComplete ?? 0)) > 1)
            {
                Lost("percentComplete", want, want.PercentageComplete ?? 0, got.PercentageComplete ?? 0);
            }

            if (!SameMoment(want.ActualStart, got.ActualStart))
            {
                Lost("actualStart", want, want.ActualStart, got.ActualStart);
            }

            if (!SameMoment(want.ActualFinish, got.ActualFinish))
            {
                Lost("actualFinish", want, want.ActualFinish, got.ActualFinish);
            }

            // A finished task's actual duration is derived from its actual dates, which are checked
            // above; where a source's stated duration disagrees with its own dates, Project keeps the
            // dates. For work in progress it is percent of duration, allowed the rounding of a whole
            // percent.
            var wantDuration = MpxjMapper.Hours(want.Duration, hours) ?? 0;
            var gotDuration = MpxjMapper.Hours(got.Duration, hours) ?? 0;
            if (Math.Abs(wantDuration - gotDuration) > 0.1)
            {
                Lost("duration", want, $"{wantDuration:0.##}h", $"{gotDuration:0.##}h");
            }

            var wantActual = MpxjMapper.Hours(want.ActualDuration, hours) ?? 0;
            var gotActual = MpxjMapper.Hours(got.ActualDuration, hours) ?? 0;
            var finished = (want.PercentageComplete ?? 0) >= 100;
            var slack = Math.Max(0.1, (MpxjMapper.Hours(want.Duration, hours) ?? 0) * 0.02);
            if (!finished && wantActual > 0 && Math.Abs(wantActual - gotActual) > slack)
            {
                Lost("actualDuration", want, $"{wantActual:0.##}h", $"{gotActual:0.##}h");
            }

            var wantWork = MpxjMapper.Hours(want.ActualWork, hours) ?? 0;
            var gotWork = MpxjMapper.Hours(got.ActualWork, hours) ?? 0;
            if (wantWork > 0 && Math.Abs(wantWork - gotWork) > 0.1)
            {
                Lost("actualWork", want, $"{wantWork:0.##}h", $"{gotWork:0.##}h");
            }

            if (want.BaselineStart is not null && !SameMinute(want.BaselineStart, got.BaselineStart))
            {
                Lost("baselineStart", want, want.BaselineStart, got.BaselineStart);
            }

            if (want.BaselineFinish is not null && !SameMinute(want.BaselineFinish, got.BaselineFinish))
            {
                Lost("baselineFinish", want, want.BaselineFinish, got.BaselineFinish);
            }

            var wantBaselineDuration = MpxjMapper.Hours(want.BaselineDuration, hours);
            var gotBaselineDuration = MpxjMapper.Hours(got.BaselineDuration, hours);
            // A zero baseline duration (a milestone) and none at all say the same thing.
            if (wantBaselineDuration is > 0
                && (gotBaselineDuration is null || Math.Abs(wantBaselineDuration.Value - gotBaselineDuration.Value) > 0.1))
            {
                Lost("baselineDuration", want, $"{wantBaselineDuration:0.##}h",
                    gotBaselineDuration is null ? null : $"{gotBaselineDuration:0.##}h");
            }
        }

        return losses
            .Select(kv => new FidelityLoss
            {
                Field = kv.Key,
                Tasks = kv.Value.Count,
                Examples = kv.Value.Take(5).ToList(),
                Critical = kv.Key is not ("duration" or "actualDuration" or "actualWork"),
            })
            .OrderByDescending(l => l.Tasks)
            .ToList();
    }

    /// <summary>What was recorded, for the report: how many tasks carried progress and a baseline.</summary>
    public static string Census(ProjectFile project)
    {
        var leaves = project.Tasks.Where(t => t.UniqueID is not null and not 0 && !t.Summary && !t.Null).ToList();
        var done = leaves.Count(t => (t.PercentageComplete ?? 0) >= 100);
        var inProgress = leaves.Count(t => (t.PercentageComplete ?? 0) is > 0 and < 100);
        var baselined = leaves.Count(t => t.BaselineStart is not null);
        var status = project.ProjectProperties.StatusDate;
        return $"{done} complete, {inProgress} in progress, {leaves.Count - done - inProgress} not started; "
               + $"{baselined} with a baseline; status date {(status is null ? "none" : status.Value.ToString("yyyy-MM-dd"))}";
    }

    public static string Describe(IReadOnlyList<FidelityLoss> losses) =>
        string.Join(" ", losses.Select(l =>
            $"{l.Field}: {l.Tasks} task(s) (e.g. {string.Join("; ", l.Examples.Take(2))})."));

    /// <summary>The same moment — or, when the source gave only a date (00:00, outside any working
    /// day), the same date: Project moves such a time to the edge of the working day and the date is
    /// what was recorded.</summary>
    private static bool SameMoment(DateTime? want, DateTime? got) =>
        SameMinute(want, got)
        || want is { TimeOfDay.Ticks: 0 } w && got is { } g && (g.Date == w.Date || g.Date == w.Date.AddDays(-1) && g.Hour >= 12);

    private static bool SameMinute(DateTime? a, DateTime? b) =>
        a is null ? b is null : b is not null && Math.Abs((a.Value - b.Value).TotalMinutes) < 1;

    private static string Show(object? value) => value switch
    {
        null => "none",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm"),
        _ => value.ToString() ?? "none",
    };
}
