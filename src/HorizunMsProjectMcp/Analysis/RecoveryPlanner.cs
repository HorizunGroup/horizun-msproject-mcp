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

    /// <summary>The share of the task the plan expected done by the status date, when progress is
    /// measured against it ('behind_plan').</summary>
    public double? PlannedPercent { get; init; }
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

    /// <summary>The change itself, to replay in the combined option. Not serialised.</summary>
    internal Action<ProjectFile>? Mutate { get; init; }
}

public sealed record RecoveryReport
{
    /// <summary>The unit of every day figure in this report.</summary>
    public string DayUnit { get; init; } = Horizun.ProjectMcp.Model.DayUnits.Working;
    public required string StatusDate { get; init; }
    public required string? ForecastFinish { get; init; }
    public string? TargetFinish { get; init; }
    public double? DaysBehindTarget { get; init; }
    public required int LateCount { get; init; }

    /// <summary>What lateness is measured against: "baseline" when one is saved, else "current plan".</summary>
    public string MeasuredAgainst { get; init; } = "current plan";

    /// <summary>The schedule performance index at the status date, as baseline_compare reports it, so the
    /// late list and earned value can be read side by side.</summary>
    public double? Spi { get; init; }
    public required double WorstSlipDays { get; init; }
    public required IReadOnlyList<LateTask> Late { get; init; }
    public required IReadOnlyList<RecoveryOption> Options { get; init; }

    /// <summary>Tasks a planner should re-plan, with the reasons and the move to make — see ReplanReview.</summary>
    public IReadOnlyList<ReplanItem> ReplanReview { get; init; } = Array.Empty<ReplanItem>();

    /// <summary>How many tasks each criterion flagged.</summary>
    public IReadOnlyDictionary<string, int> ReplanCriteria { get; init; } = new Dictionary<string, int>();

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
        var hasBaseline = leaves.Any(t => t.BaselineFinish is not null);
        var evm = EarnedValue.Compare(project, statusDate, 0, byBranch: false).Project;

        double? behind = null;
        if (targetFinish is not null && forecast is not null)
        {
            var calendar = new WorkingCalendar(project);
            behind = Math.Round(calendar.WorkingDaysBetween(targetFinish.Value, forecast.Value), 1);
        }

        var options = BuildOptions(project, leaves, statusDate, maxOptions, notes);
        var review = Analysis.ReplanReview.Review(project, statusDate);

        if (late.Count == 0 && evm.Spi is < 1)
        {
            notes.Add(
                $"No single task is behind its {(hasBaseline ? "baseline" : "plan")} dates as of {statusDate:yyyy-MM-dd}, "
                + $"yet earned value reads SPI {evm.Spi:0.###}: the shortfall is spread thinly across work in progress.");
        }
        else if (late.Count == 0)
        {
            notes.Add(
                $"Nothing is behind as of {statusDate:yyyy-MM-dd}. Either the work is on plan or no "
                + "progress has been recorded — schedule_qa reports which.");
        }

        if (!hasBaseline)
        {
            notes.Add("No baseline is saved, so lateness is measured against the current plan, which moves with every "
                      + "reschedule: work pushed forward by a slip never shows as late. Save a baseline "
                      + "(schedule_update op='save_baseline') to measure against what was agreed.");
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
            MeasuredAgainst = hasBaseline ? "baseline" : "current plan",
            Spi = evm.Spi,
            WorstSlipDays = late.Count == 0 ? 0 : late.Max(l => l.SlipDays),
            Late = late.Take(100).ToList(),
            Options = options,
            ReplanReview = review.Take(150).ToList(),
            ReplanCriteria = review.SelectMany(r => r.Criteria).GroupBy(c => c)
                .ToDictionary(g => g.Key, g => g.Count()),
            Notes = notes,
        };
    }

    /// <summary>
    /// Work that should have started or finished by the status date and has not, or is in progress
    /// with less done than planned. Ordered by how much of the schedule is stuck behind it, because
    /// that is what decides where to intervene.
    /// </summary>
    /// <remarks>
    /// "Should have" is the baseline wherever the task has one — the plan earned value measures SPI
    /// against. Measured against the current dates instead, nothing is ever late once the schedule is
    /// recalculated: a reschedule moves every unfinished task past the status date, and a field run
    /// reported "nothing is behind" beside an SPI of 0.818 on the same cut.
    /// </remarks>
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

            var plannedStart = task.BaselineStart ?? task.Start;
            var plannedFinish = task.BaselineFinish ?? task.Finish;
            string? kind = null;
            double slip = 0;
            double? plannedPercent = null;

            if (plannedFinish is { } finish && finish < statusDate)
            {
                kind = percent > 0 ? "overrunning" : "not_finished";
                slip = calendar.WorkingDaysBetween(finish, Later(task.Finish, statusDate));
            }
            else if (plannedStart is { } start && start < statusDate && percent <= 0 && task.ActualStart is null)
            {
                kind = "not_started";
                slip = calendar.WorkingDaysBetween(start, Later(task.Start, statusDate));
            }
            else if (percent > 0 && plannedStart is { } from && plannedFinish is { } to && to > from)
            {
                // In progress, but less of it done than the plan expected by now.
                var planned = calendar.WorkingHoursBetween(from, statusDate) / Math.Max(1e-9, calendar.WorkingHoursBetween(from, to));
                planned = Math.Clamp(planned, 0, 1) * 100;
                var days = MpxjMapper.Days(task.BaselineDuration ?? task.Duration, calendar.HoursPerDay) ?? 0;
                if (planned - percent >= 1 && days > 0)
                {
                    kind = "behind_plan";
                    plannedPercent = Math.Round(planned);
                    slip = (planned - percent) / 100.0 * days;
                }
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
                PlannedStart = MpxjMapper.Iso(plannedStart),
                PlannedFinish = MpxjMapper.Iso(plannedFinish),
                PercentComplete = percent,
                PlannedPercent = plannedPercent,
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

    private static DateTime Later(DateTime? forecast, DateTime statusDate) =>
        forecast is { } f && f > statusDate ? f : statusDate;

    /// <summary>How much of the network sits downstream of a task — its blast radius.</summary>
    internal static int CountDownstream(ProjectFile project, int uid)
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
        var open = leaves.Where(t => (t.PercentageComplete ?? 0) < 100).ToList();
        var criticalChain = open.Where(t => t.Critical).ToList();

        // A schedule nobody has calculated since it was edited marks nothing critical. The driving
        // chain is still the one with the least float.
        if (criticalChain.Count == 0)
        {
            var least = open.Select(t => MpxjMapper.Days(t.TotalSlack)).Where(d => d is not null).DefaultIfEmpty().Min();
            if (least is not null)
            {
                criticalChain = open.Where(t => (MpxjMapper.Days(t.TotalSlack) ?? double.MaxValue) <= least + 0.5).ToList();
                notes.Add($"No open task is marked critical; the {criticalChain.Count} with the least total float "
                          + $"({least:0.#} d) were treated as the driving chain.");
            }
        }

        if (criticalChain.Count == 0)
        {
            notes.Add(
                "There is no driving chain to compress: no open task is critical and none carries float "
                + "figures. Recalculate (schedule_update op='recalculate'), then run schedule_qa — a "
                + "schedule where almost everything floats usually has missing successor logic.");
            return options;
        }

        void Skip(string lever, string why) => notes.Add($"{lever}: {why}");

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
                            Writes.Links.Remove(clone, relation);
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

        else
        {
            Skip("remove_lags_on_critical_path", "no lag sits on the driving chain.");
        }

        // Lever 2 — fast-track: finish-to-start becomes start-to-start with a lag of half the
        // predecessor, so the successor starts once half of it is done — the partial overlap a site
        // actually runs, rather than starting both on the same day.
        var fastTrackable = criticalChain
            .Where(t => (MpxjMapper.Days(t.Duration) ?? 0) >= 2)
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
                summary: $"Overlap {pairs.Count} critical hand-off(s): finish-to-start becomes start-to-start "
                         + "once half of the predecessor is done.",
                uids: pairs.Select(x => x.To).Distinct().ToList(),
                howToApply: "links_write with op='relink', type='SS' and a lag of half the predecessor's duration "
                            + "on the listed dependencies (by zone or floor where the work allows it).",
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
                            var predecessorDays = MpxjMapper.Days(relation.PredecessorTask!.RemainingDuration)
                                                  ?? MpxjMapper.Days(relation.PredecessorTask!.Duration) ?? 0;
                            var lagDays = Math.Max(0, Math.Round(predecessorDays / 2, 1)
                                                      + (MpxjMapper.Days(relation.Lag) ?? 0));
                            Writes.Links.Remove(clone, relation);
                            target.AddPredecessor(new Relation.Builder(clone)
                                .PredecessorTask(relation.PredecessorTask!)
                                .SuccessorTask(target)
                                .Type(RelationType.StartStart)
                                .Lag(MPXJ.Net.Duration.GetInstance(lagDays, TimeUnit.Days)));
                        }
                        catch
                        {
                            // As above.
                        }
                    }
                }));
        }

        else
        {
            Skip("fast_track_critical_path", "no finish-to-start hand-off on the driving chain into a task of 2 days or more.");
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

        else
        {
            Skip("crash_longest_critical_tasks", "no task of 5 days or more on the driving chain to add crews to.");
        }

        // Lever 4 — work longer weeks: full Saturdays on the project calendar, the recovery a site
        // most often buys first.
        var calendar = project.DefaultCalendar;
        var saturday = calendar is null ? null : Writes.CalendarEdits.Describe(calendar, DayOfWeek.Saturday);
        if (calendar is not null && saturday != "08:00-12:00,13:00-17:00")
        {
            var fullDay = new[] { (new TimeOnly(8, 0), new TimeOnly(12, 0)), (new TimeOnly(13, 0), new TimeOnly(17, 0)) };
            options.Add(Simulate(
                project, baseline.Value,
                lever: "full_saturdays",
                summary: string.IsNullOrEmpty(saturday)
                    ? "Work Saturdays, 08:00-17:00, on the project calendar."
                    : $"Work full Saturdays, 08:00-17:00, instead of {saturday}.",
                uids: Array.Empty<int>(),
                howToApply: "calendars_write op='set_week' days='sat' hours='08:00-12:00,13:00-17:00' on the "
                            + "project calendar (or a task calendar on the critical tasks only).",
                risks: new[]
                {
                    "Overtime cost, and fatigue if kept up for weeks. Check the resource calendars: a crew "
                    + "whose own calendar keeps Saturday off does not follow.",
                },
                mutate: clone =>
                {
                    if (clone.DefaultCalendar is { } c)
                    {
                        Writes.CalendarEdits.SetWeek(c, new[] { DayOfWeek.Saturday }, fullDay);
                    }
                }));
        }
        else
        {
            Skip("full_saturdays", "the project calendar already works full Saturdays.");
        }

        foreach (var option in options.Where(o => o.DaysRecovered <= 0))
        {
            Skip(option.Lever, "evaluated, but the finish did not move — another chain drives it, or the "
                               + "change is absorbed by constraints or resource calendars.");
        }

        var useful = options.Where(o => o.DaysRecovered > 0).ToList();

        // Levers add up only partly — shortening a task that has also been overlapped recovers less
        // twice — so the combination is measured, not summed.
        if (useful.Count >= 2)
        {
            var chosen = useful.Select(o => o.Mutate).OfType<Action<ProjectFile>>().ToList();
            useful.Add(Simulate(
                project, baseline.Value,
                lever: "combined",
                summary: "All of the above together: " + string.Join(", ", useful.Select(o => o.Lever)) + ".",
                uids: useful.SelectMany(o => o.Uids).Distinct().ToList(),
                howToApply: "Apply each of the options above.",
                risks: useful.SelectMany(o => o.Risks).Distinct().ToList(),
                mutate: clone =>
                {
                    foreach (var mutate in chosen)
                    {
                        mutate(clone);
                    }
                }));
        }

        if (useful.Count == 0)
        {
            notes.Add("No lever moved the finish. See the reason given for each above.");
        }

        return useful
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
            Mutate = mutate,
        };
    }
}
