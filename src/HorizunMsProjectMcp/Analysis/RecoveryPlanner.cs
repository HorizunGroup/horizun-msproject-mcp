using Horizun.ProjectMcp.Backends;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Analysis;

public sealed record LateTask
{
    public required int Uid { get; init; }
    public string? Name { get; init; }
    public required string Kind { get; init; }
    public string? PlannedStart { get; init; }
    public string? PlannedFinish { get; init; }
    public double? PercentComplete { get; init; }
    public required double SlipDays { get; init; }
    public double? TotalFloatDays { get; init; }
    public required bool OnCriticalPath { get; init; }
    public required int SuccessorsBlocked { get; init; }
}

public sealed record RecoveryOption
{
    public required string Lever { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<int> Uids { get; init; }
    public required string ProjectFinishBefore { get; init; }
    public required string ProjectFinishAfter { get; init; }
    public required double DaysRecovered { get; init; }
    public required int TasksMoved { get; init; }
    public int NewNegativeFloat { get; init; }
    public required string HowToApply { get; init; }
    public IReadOnlyList<string> Risks { get; init; } = Array.Empty<string>();
}

public sealed record RecoveryReport
{
    public required string StatusDate { get; init; }
    public required string? ForecastFinish { get; init; }
    public string? TargetFinish { get; init; }
    public double? DaysBehindTarget { get; init; }
    public required int LateCount { get; init; }
    public required double WorstSlipDays { get; init; }
    public required IReadOnlyList<LateTask> Late { get; init; }
    public required IReadOnlyList<RecoveryOption> Options { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Works out what is late, what it is costing the finish date, and which changes to the logic
/// would actually recover time.
/// </summary>
/// <remarks>
/// Every option is measured, not guessed: the lever is applied to a throwaway copy of the schedule,
/// the copy is rescheduled with the critical-path engine, and the resulting finish date is compared
/// with the original. An option that recovers nothing is reported as recovering nothing rather than
/// being offered as advice.
/// </remarks>
public static class RecoveryPlanner
{
    public static RecoveryReport Analyse(
        ProjectFile project, DateTime statusDate, DateTime? targetFinish, int maxOptions)
    {
        var notes = new List<string>();
        var leaves = ScheduleAnalyzer.Leaves(project).ToList();
        var forecast = MpxjBackend.ProjectFinish(project);

        var late = FindLate(project, leaves, statusDate);

        double? behind = null;
        if (targetFinish is not null && forecast is not null)
        {
            var calendar = new WorkingCalendar(project);
            behind = Math.Round(calendar.WorkingDaysBetween(targetFinish.Value, forecast.Value), 1);
        }

        var options = BuildOptions(project, leaves, statusDate, maxOptions, notes);

        if (late.Count == 0)
        {
            notes.Add(
                $"Nothing is behind as of {statusDate:yyyy-MM-dd}. Either the work is on plan or no "
                + "progress has been recorded — schedule_qa reports which.");
        }

        if (behind is <= 0)
        {
            notes.Add("The forecast finish already meets the target; the options below buy margin rather than recover it.");
        }

        return new RecoveryReport
        {
            StatusDate = statusDate.ToString("yyyy-MM-dd"),
            ForecastFinish = MpxjMapper.Iso(forecast),
            TargetFinish = targetFinish is null ? null : targetFinish.Value.ToString("yyyy-MM-dd"),
            DaysBehindTarget = behind,
            LateCount = late.Count,
            WorstSlipDays = late.Count == 0 ? 0 : late.Max(l => l.SlipDays),
            Late = late.Take(100).ToList(),
            Options = options,
            Notes = notes,
        };
    }

    /// <summary>
    /// Work that should have started or finished by the status date and has not. Ordered by how
    /// much of the schedule is stuck behind it, because that is what decides where to intervene.
    /// </summary>
    public static List<LateTask> FindLate(
        ProjectFile project, IReadOnlyList<MPXJ.Net.Task> leaves, DateTime statusDate)
    {
        var calendar = new WorkingCalendar(project);
        var late = new List<LateTask>();

        foreach (var task in leaves)
        {
            var percent = task.PercentageComplete ?? 0;
            if (percent >= 100 || task.ActualFinish is not null)
            {
                continue;
            }

            string? kind = null;
            double slip = 0;

            if (task.Finish is { } finish && finish < statusDate)
            {
                kind = percent > 0 ? "overrunning" : "not_finished";
                slip = calendar.WorkingDaysBetween(finish, statusDate);
            }
            else if (task.Start is { } start && start < statusDate && percent <= 0 && task.ActualStart is null)
            {
                kind = "not_started";
                slip = calendar.WorkingDaysBetween(start, statusDate);
            }

            if (kind is null || slip <= 0)
            {
                continue;
            }

            late.Add(new LateTask
            {
                Uid = task.UniqueID!.Value,
                Name = task.Name,
                Kind = kind,
                PlannedStart = MpxjMapper.Iso(task.Start),
                PlannedFinish = MpxjMapper.Iso(task.Finish),
                PercentComplete = percent,
                SlipDays = Math.Round(slip, 1),
                TotalFloatDays = MpxjMapper.Days(task.TotalSlack),
                OnCriticalPath = task.Critical,
                SuccessorsBlocked = CountDownstream(project, task.UniqueID!.Value),
            });
        }

        return late
            .OrderByDescending(l => l.OnCriticalPath)
            .ThenByDescending(l => l.SuccessorsBlocked)
            .ThenByDescending(l => l.SlipDays)
            .ToList();
    }

    /// <summary>How much of the network sits downstream of a task — its blast radius.</summary>
    private static int CountDownstream(ProjectFile project, int uid)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(uid);

        while (stack.Count > 0 && seen.Count < 5000)
        {
            var current = stack.Pop();
            var task = project.GetTaskByUniqueID(current);
            if (task is null)
            {
                continue;
            }

            foreach (var relation in task.Successors)
            {
                var next = relation.SuccessorTask?.UniqueID;
                if (next is not null && seen.Add(next.Value))
                {
                    stack.Push(next.Value);
                }
            }
        }

        return seen.Count;
    }

    /// <summary>
    /// Simulates each recovery lever on a copy and keeps the ones that actually move the date.
    /// </summary>
    private static List<RecoveryOption> BuildOptions(
        ProjectFile project, IReadOnlyList<MPXJ.Net.Task> leaves, DateTime statusDate,
        int maxOptions, List<string> notes)
    {
        var baseline = MpxjBackend.ProjectFinish(project);
        if (baseline is null)
        {
            notes.Add("The schedule has no finish date to measure recovery against.");
            return new List<RecoveryOption>();
        }

        var options = new List<RecoveryOption>();
        var criticalChain = leaves.Where(t => t.Critical).ToList();

        if (criticalChain.Count == 0)
        {
            notes.Add(
                "No task is marked critical, so there is no driving chain to compress. Run schedule_qa — "
                + "a schedule where almost everything carries float usually has missing successor logic.");
            return options;
        }

        // Lever 1 — remove the lags sitting on the driving chain. Waiting time is the cheapest
        // thing to recover because nobody has to work faster.
        var laggedCritical = criticalChain
            .SelectMany(t => t.Predecessors.Select(r => (Task: t, Relation: r)))
            .Where(x => (MpxjMapper.Days(x.Relation.Lag) ?? 0) > 0.001)
            .ToList();

        if (laggedCritical.Count > 0)
        {
            var pairs = laggedCritical
                .Select(x => (From: x.Relation.PredecessorTask?.UniqueID ?? -1, To: x.Task.UniqueID!.Value))
                .Where(x => x.From > 0)
                .ToList();

            options.Add(Simulate(
                project, baseline.Value,
                lever: "remove_lags_on_critical_path",
                summary: $"Remove the waiting time on {pairs.Count} critical dependency(ies).",
                uids: pairs.Select(x => x.To).Distinct().ToList(),
                howToApply: "links_write with op='relink' and lag='0' on the listed dependencies.",
                risks: new[]
                {
                    "Check each lag is genuinely slack and not a cure, delivery or approval period "
                    + "that was modelled as a lag rather than as its own task.",
                },
                mutate: clone =>
                {
                    foreach (var (from, to) in pairs)
                    {
                        var target = clone.GetTaskByUniqueID(to);
                        var relation = target?.Predecessors.FirstOrDefault(r => r.PredecessorTask?.UniqueID == from);
                        if (relation is null || target is null)
                        {
                            continue;
                        }

                        try
                        {
                            target.Predecessors.Remove(relation);
                            target.AddPredecessor(new Relation.Builder(clone)
                                .PredecessorTask(relation.PredecessorTask!)
                                .SuccessorTask(target)
                                .Type(relation.Type ?? RelationType.FinishStart)
                                .Lag(MPXJ.Net.Duration.GetInstance(0, TimeUnit.Days)));
                        }
                        catch
                        {
                            // A relation this backend cannot rewrite simply does not contribute.
                        }
                    }
                }));
        }

        // Lever 2 — fast-track: turn finish-to-start into start-to-start on the longest critical
        // tasks so the work overlaps.
        var fastTrackable = criticalChain
            .Where(t => (MpxjMapper.Days(t.Duration) ?? 0) >= 5)
            .SelectMany(t => t.Predecessors
                .Where(r => r.Type is null or RelationType.FinishStart && r.PredecessorTask is not null)
                .Select(r => (Task: t, Relation: r)))
            .OrderByDescending(x => MpxjMapper.Days(x.Task.Duration) ?? 0)
            .Take(10)
            .ToList();

        if (fastTrackable.Count > 0)
        {
            var pairs = fastTrackable
                .Select(x => (From: x.Relation.PredecessorTask!.UniqueID!.Value, To: x.Task.UniqueID!.Value))
                .ToList();

            options.Add(Simulate(
                project, baseline.Value,
                lever: "fast_track_critical_path",
                summary: $"Overlap {pairs.Count} critical hand-off(s): finish-to-start becomes start-to-start.",
                uids: pairs.Select(x => x.To).Distinct().ToList(),
                howToApply: "links_write with op='relink' and type='SS' on the listed dependencies.",
                risks: new[]
                {
                    "Overlapping trades raises rework risk and congestion on site. Confirm each pair "
                    + "can genuinely run together before committing.",
                },
                mutate: clone =>
                {
                    foreach (var (from, to) in pairs)
                    {
                        var target = clone.GetTaskByUniqueID(to);
                        var relation = target?.Predecessors.FirstOrDefault(r => r.PredecessorTask?.UniqueID == from);
                        if (relation is null || target is null)
                        {
                            continue;
                        }

                        try
                        {
                            var lag = relation.Lag;
                            target.Predecessors.Remove(relation);
                            target.AddPredecessor(new Relation.Builder(clone)
                                .PredecessorTask(relation.PredecessorTask!)
                                .SuccessorTask(target)
                                .Type(RelationType.StartStart)
                                .Lag(lag ?? MPXJ.Net.Duration.GetInstance(0, TimeUnit.Days)));
                        }
                        catch
                        {
                            // As above.
                        }
                    }
                }));
        }

        // Lever 3 — crash: shorten the longest tasks on the driving chain by a quarter.
        var longest = criticalChain
            .Where(t => !t.Milestone && (MpxjMapper.Days(t.Duration) ?? 0) >= 5)
            .OrderByDescending(t => MpxjMapper.Days(t.Duration) ?? 0)
            .Take(10)
            .ToList();

        if (longest.Count > 0)
        {
            var uids = longest.Select(t => t.UniqueID!.Value).ToList();

            options.Add(Simulate(
                project, baseline.Value,
                lever: "crash_longest_critical_tasks",
                summary: $"Shorten the {uids.Count} longest critical task(s) by 25% — more crews, longer shifts.",
                uids: uids,
                howToApply: "tasks_write with a reduced duration on each listed task, after confirming the "
                            + "resources exist to work at that rate.",
                risks: new[]
                {
                    "Costs money and assumes the extra crews or hours are actually available.",
                    "Productivity per worker usually falls as a crew is enlarged.",
                },
                mutate: clone =>
                {
                    foreach (var uid in uids)
                    {
                        var task = clone.GetTaskByUniqueID(uid);
                        var days = MpxjMapper.Days(task?.Duration);
                        if (task is null || days is null or <= 0)
                        {
                            continue;
                        }

                        var previous = task.Duration;
                        task.Duration = MPXJ.Net.Duration.GetInstance(
                            Math.Max(1, Math.Round(days.Value * 0.75, 1)), TimeUnit.Days);
                        // Compress what remains, keeping the work already done, as Project would.
                        Writes.ProgressRules.DurationChanged(clone, task, previous);
                    }
                }));
        }

        return options
            .Where(o => o.DaysRecovered > 0)
            .OrderByDescending(o => o.DaysRecovered)
            .Take(Math.Max(maxOptions, 1))
            .ToList();
    }

    private static RecoveryOption Simulate(
        ProjectFile project, DateTime baselineFinish, string lever, string summary,
        IReadOnlyList<int> uids, string howToApply, IReadOnlyList<string> risks,
        Action<ProjectFile> mutate)
    {
        var clone = MpxjBackend.Clone(project);

        // With the internal engine, bring the copy onto that engine's own baseline first, so the
        // recovery measured is caused by the lever rather than by the engine disagreeing with
        // Microsoft Project's stored dates. When Project itself schedules, the stored dates already
        // are its dates — measured identical on seven real schedules — so that pass would be a wasted
        // trip through Project.
        if (!Scheduler.UsesProject)
        {
            CpmScheduler.Run(clone);
        }

        var before = MpxjBackend.ProjectFinish(clone) ?? baselineFinish;
        var beforeDates = ScheduleAnalyzer.Leaves(clone)
            .ToDictionary(t => t.UniqueID!.Value, t => t.Start);

        mutate(clone);
        Scheduler.Run(clone);

        var after = MpxjBackend.ProjectFinish(clone) ?? before;
        var calendar = new WorkingCalendar(clone);
        var moved = ScheduleAnalyzer.Leaves(clone)
            .Count(t => beforeDates.TryGetValue(t.UniqueID!.Value, out var was) && was != t.Start);

        return new RecoveryOption
        {
            Lever = lever,
            Summary = summary,
            Uids = uids.Take(50).ToList(),
            ProjectFinishBefore = MpxjMapper.Iso(before)!,
            ProjectFinishAfter = MpxjMapper.Iso(after)!,
            DaysRecovered = Math.Round(calendar.WorkingDaysBetween(after, before), 1),
            TasksMoved = moved,
            NewNegativeFloat = ScheduleAnalyzer.Leaves(clone)
                .Count(t => (MpxjMapper.Days(t.TotalSlack) ?? 0) < -0.001),
            HowToApply = howToApply,
            Risks = risks,
        };
    }
}
