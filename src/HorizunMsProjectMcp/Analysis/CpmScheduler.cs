using Horizun.ProjectMcp.Backends;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Analysis;

public sealed record ScheduleRunReport
{
    public required int TasksScheduled { get; init; }
    public required int SummariesRolledUp { get; init; }
    public required int CriticalTasks { get; init; }
    public required int NegativeFloatTasks { get; init; }
    public string? ProjectStart { get; init; }
    public string? ProjectFinish { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// A critical-path scheduler: forward pass, backward pass, float, and critical flags, honouring
/// relationship types, lag, constraints, actual dates, and the working calendar.
/// </summary>
/// <remarks>
/// MPXJ reads and writes schedule files but does not schedule them — a task created through it has
/// no dates at all. Without this, dates, float, the critical path, earned value and every dry-run
/// impact figure would be empty on any schedule this server authored. Microsoft Project's own engine
/// remains the reference where COM is available; this is what makes the file-based backend a
/// planning tool rather than a viewer.
/// </remarks>
public static class CpmScheduler
{
    private sealed class Node
    {
        public required MPXJ.Net.Task Task { get; init; }
        public required int Uid { get; init; }
        public double DurationDays { get; set; }
        public DateTime EarlyStart { get; set; }
        public DateTime EarlyFinish { get; set; }
        public DateTime LateStart { get; set; }
        public DateTime LateFinish { get; set; }
        public bool Pinned { get; set; }
    }

    public static ScheduleRunReport Run(ProjectFile project)
    {
        var calendars = new CalendarSet(project);
        var calendar = calendars.Default;
        var warnings = new List<string>();

        var leaves = project.Tasks
            .Where(t => t.UniqueID is not null and not 0 && !t.Summary)
            .ToList();

        if (leaves.Count == 0)
        {
            return new ScheduleRunReport
            {
                TasksScheduled = 0,
                SummariesRolledUp = 0,
                CriticalTasks = 0,
                NegativeFloatTasks = 0,
                Warnings = new[] { "Nothing to schedule: the project has no working (non-summary) tasks." },
            };
        }

        var projectStart = calendar.NextWorkingDay(
            project.ProjectProperties.StartDate
            ?? leaves.Where(t => t.Start is not null).Min(t => t.Start)
            ?? DateTime.Today);

        var nodes = leaves.ToDictionary(
            t => t.UniqueID!.Value,
            t => new Node
            {
                Task = t,
                Uid = t.UniqueID!.Value,
                DurationDays = Math.Max(
                    MpxjMapper.Days(t.Duration, calendars.For(t).HoursPerDay) ?? 0, 0),
                EarlyStart = projectStart,
                EarlyFinish = projectStart,
            });

        var order = TopologicalOrder(nodes, warnings);

        // ---------------- forward pass: earliest possible dates ----------------
        foreach (var uid in order)
        {
            var node = nodes[uid];
            var task = node.Task;
            var cal = calendars.For(task);

            // Work that has actually started is anchored to reality, not to the logic.
            // A task that nothing drives keeps the date it already has: real schedules are full of
            // tasks positioned by hand, and pulling them all to the project start rewrites the
            // planner's intent rather than recalculating it. Measured on real files, honouring this
            // is the difference between matching Microsoft Project and moving most of the schedule
            // by a month. Only tasks the logic actually drives get moved.
            var driven = task.Predecessors.Any(r =>
                r.PredecessorTask?.UniqueID is { } pid && nodes.ContainsKey(pid));

            var earliest = task.ActualStart is not null
                ? cal.NextWorkingDay(task.ActualStart.Value)
                : !driven && task.Start is not null
                    ? cal.NextWorkingDay(task.Start.Value)
                    : projectStart;

            foreach (var relation in task.Predecessors)
            {
                var predUid = relation.PredecessorTask?.UniqueID;
                if (predUid is null || !nodes.TryGetValue(predUid.Value, out var pred))
                {
                    continue;
                }

                var lag = MpxjMapper.Days(relation.Lag, cal.HoursPerDay) ?? 0;
                var candidate = relation.Type switch
                {
                    RelationType.StartStart => pred.EarlyStart,
                    RelationType.FinishFinish => cal.SubtractWorkingDays(
                        pred.EarlyFinish, Math.Max(node.DurationDays, 1)),
                    RelationType.StartFinish => cal.SubtractWorkingDays(
                        pred.EarlyStart, Math.Max(node.DurationDays, 1)),
                    _ => node.DurationDays <= 0
                        ? pred.EarlyFinish
                        : cal.NextWorkingDay(pred.EarlyFinish.AddDays(1)),
                };

                if (Math.Abs(lag) > 0.0001)
                {
                    candidate = cal.Shift(candidate, lag);
                }

                if (candidate > earliest)
                {
                    earliest = candidate;
                }
            }

            // Constraints override the logic where they are harder than it.
            (earliest, var pinned) = ApplyConstraint(task, earliest, node.DurationDays, cal);
            node.Pinned = pinned;

            node.EarlyStart = cal.NextWorkingDay(earliest);
            node.EarlyFinish = node.DurationDays <= 0
                ? node.EarlyStart
                : cal.AddWorkingDays(node.EarlyStart, node.DurationDays);

            if (task.ActualFinish is not null)
            {
                node.EarlyFinish = cal.PreviousWorkingDay(task.ActualFinish.Value);
            }
        }

        var projectFinish = nodes.Values.Max(n => n.EarlyFinish);

        // Anchor the backward pass on the finish we just computed — never on
        // ProjectProperties.FinishDate, which MPXJ derives from the task dates as they were
        // before this pass. Using it makes the schedule fight the previous run's answer and
        // hands every task on the driving path one day of spurious negative float.
        // Negative float comes from task deadlines and hard constraints, exactly as it does in
        // Microsoft Project.
        var backwardFrom = projectFinish;

        // ---------------- backward pass: latest permissible dates ----------------
        foreach (var uid in Enumerable.Reverse(order))
        {
            var node = nodes[uid];
            var task = node.Task;
            var cal = calendars.For(task);
            var latest = backwardFrom;

            foreach (var relation in task.Successors)
            {
                var succUid = relation.SuccessorTask?.UniqueID;
                if (succUid is null || !nodes.TryGetValue(succUid.Value, out var succ))
                {
                    continue;
                }

                var lag = MpxjMapper.Days(relation.Lag, cal.HoursPerDay) ?? 0;
                var candidate = relation.Type switch
                {
                    RelationType.StartStart => cal.AddWorkingDays(
                        succ.LateStart, Math.Max(node.DurationDays, 1)),
                    RelationType.FinishFinish => succ.LateFinish,
                    RelationType.StartFinish => succ.LateFinish,
                    _ => node.DurationDays <= 0
                        ? succ.LateStart
                        : cal.PreviousWorkingDay(succ.LateStart.AddDays(-1)),
                };

                if (Math.Abs(lag) > 0.0001)
                {
                    candidate = cal.Shift(candidate, -lag);
                }

                if (candidate < latest)
                {
                    latest = candidate;
                }
            }

            // A deadline is a soft target that still produces negative float when missed.
            if (task.Deadline is not null)
            {
                var byDeadline = cal.PreviousWorkingDay(task.Deadline.Value);
                if (byDeadline < latest)
                {
                    latest = byDeadline;
                }
            }

            node.LateFinish = latest;
            node.LateStart = node.DurationDays <= 0
                ? node.LateFinish
                : cal.SubtractWorkingDays(node.LateFinish, node.DurationDays);
        }

        // ---------------- write the results back ----------------
        var critical = 0;
        var negative = 0;

        foreach (var node in nodes.Values)
        {
            var task = node.Task;
            var cal = calendars.For(task);
            var totalFloat = cal.WorkingDaysBetween(node.EarlyStart, node.LateStart);

            task.Start = task.ActualStart ?? WorkingCalendar.AtStart(node.EarlyStart);
            task.Finish = task.ActualFinish ?? WorkingCalendar.AtFinish(node.EarlyFinish);
            task.EarlyStart = WorkingCalendar.AtStart(node.EarlyStart);
            task.EarlyFinish = WorkingCalendar.AtFinish(node.EarlyFinish);
            task.LateStart = WorkingCalendar.AtStart(node.LateStart);
            task.LateFinish = WorkingCalendar.AtFinish(node.LateFinish);
            task.TotalSlack = MPXJ.Net.Duration.GetInstance(totalFloat, TimeUnit.Days);

            var freeFloat = FreeFloat(node, nodes, cal);
            task.FreeSlack = MPXJ.Net.Duration.GetInstance(freeFloat, TimeUnit.Days);

            var isCritical = totalFloat <= 0.0001;
            task.Critical = isCritical;

            if (isCritical)
            {
                critical++;
            }

            if (totalFloat < -0.0001)
            {
                negative++;
            }
        }

        var summaries = RollUpSummaries(project);

        if (project.ProjectProperties.StartDate is null)
        {
            project.ProjectProperties.StartDate = WorkingCalendar.AtStart(projectStart);
        }

        return new ScheduleRunReport
        {
            TasksScheduled = nodes.Count,
            SummariesRolledUp = summaries,
            CriticalTasks = critical,
            NegativeFloatTasks = negative,
            ProjectStart = MpxjMapper.Iso(WorkingCalendar.AtStart(projectStart)),
            ProjectFinish = MpxjMapper.Iso(WorkingCalendar.AtFinish(projectFinish)),
            Warnings = warnings,
        };
    }

    /// <summary>
    /// How long a task can slip without moving any successor — distinct from total float, which
    /// measures slack against the project end.
    /// </summary>
    private static double FreeFloat(Node node, Dictionary<int, Node> nodes, WorkingCalendar calendar)
    {
        double? earliestSuccessorStart = null;

        foreach (var relation in node.Task.Successors)
        {
            var succUid = relation.SuccessorTask?.UniqueID;
            if (succUid is null || !nodes.TryGetValue(succUid.Value, out var succ))
            {
                continue;
            }

            var gap = calendar.WorkingDaysBetween(node.EarlyFinish, succ.EarlyStart) - 1;
            earliestSuccessorStart = earliestSuccessorStart is null
                ? gap
                : Math.Min(earliestSuccessorStart.Value, gap);
        }

        return Math.Max(earliestSuccessorStart ?? calendar.WorkingDaysBetween(node.EarlyStart, node.LateStart), 0);
    }

    private static (DateTime Start, bool Pinned) ApplyConstraint(
        MPXJ.Net.Task task, DateTime logicStart, double duration, WorkingCalendar calendar)
    {
        if (task.ConstraintType is not { } type || task.ConstraintDate is not { } date)
        {
            return (logicStart, false);
        }

        var constraint = calendar.NextWorkingDay(date);

        return type switch
        {
            ConstraintType.MustStartOn => (constraint, true),
            ConstraintType.MustFinishOn => (calendar.SubtractWorkingDays(constraint, Math.Max(duration, 1)), true),
            ConstraintType.StartNoEarlierThan => (constraint > logicStart ? constraint : logicStart, false),
            ConstraintType.FinishNoEarlierThan => (
                calendar.SubtractWorkingDays(constraint, Math.Max(duration, 1)) is var s && s > logicStart
                    ? s
                    : logicStart, false),
            _ => (logicStart, false),
        };
    }

    /// <summary>Summary tasks span their children — they are not scheduled in their own right.</summary>
    private static int RollUpSummaries(ProjectFile project)
    {
        var count = 0;

        foreach (var summary in project.Tasks.Where(t => t.Summary).OrderByDescending(t => t.OutlineLevel ?? 0))
        {
            var children = summary.ChildTasks.ToList();
            if (children.Count == 0)
            {
                continue;
            }

            var starts = children.Where(c => c.Start is not null).Select(c => c.Start!.Value).ToList();
            var finishes = children.Where(c => c.Finish is not null).Select(c => c.Finish!.Value).ToList();
            if (starts.Count == 0 || finishes.Count == 0)
            {
                continue;
            }

            summary.Start = starts.Min();
            summary.Finish = finishes.Max();
            summary.Critical = children.Any(c => c.Critical);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Predecessors-first ordering. A cycle cannot be scheduled, so the remaining tasks are appended
    /// in a stable order and the cycle is reported rather than silently looping.
    /// </summary>
    private static List<int> TopologicalOrder(Dictionary<int, Node> nodes, List<string> warnings)
    {
        var inDegree = nodes.Keys.ToDictionary(uid => uid, _ => 0);

        foreach (var node in nodes.Values)
        {
            foreach (var relation in node.Task.Predecessors)
            {
                var predUid = relation.PredecessorTask?.UniqueID;
                if (predUid is not null && nodes.ContainsKey(predUid.Value))
                {
                    inDegree[node.Uid]++;
                }
            }
        }

        var ready = new Queue<int>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy(x => x));
        var order = new List<int>(nodes.Count);

        while (ready.Count > 0)
        {
            var uid = ready.Dequeue();
            order.Add(uid);

            foreach (var relation in nodes[uid].Task.Successors)
            {
                var succUid = relation.SuccessorTask?.UniqueID;
                if (succUid is null || !inDegree.ContainsKey(succUid.Value))
                {
                    continue;
                }

                if (--inDegree[succUid.Value] == 0)
                {
                    ready.Enqueue(succUid.Value);
                }
            }
        }

        if (order.Count < nodes.Count)
        {
            var stuck = nodes.Keys.Except(order).OrderBy(x => x).ToList();
            warnings.Add(
                $"{stuck.Count} task(s) form a dependency cycle and could not be ordered: " +
                $"{string.Join(", ", stuck.Take(20))}. Their dates are unreliable until the cycle is broken — "
                + "run schedule_qa to locate it.");
            order.AddRange(stuck);
        }

        return order;
    }
}
