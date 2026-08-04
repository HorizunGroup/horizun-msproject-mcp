using Horizun.ProjectMcp.Backends;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Analysis;

public sealed record OrphanTask
{
    public required int Uid { get; init; }
    public string? Name { get; init; }
    public required string Problem { get; init; }
    public string? Start { get; init; }
    public string? Finish { get; init; }
    public required string Consequence { get; init; }
}

public sealed record DrivingLink
{
    public required int Uid { get; init; }
    public string? Name { get; init; }
    public required double DurationDays { get; init; }
    public required string RequiredStart { get; init; }
    public required string RequiredFinish { get; init; }
    public required double FloatDays { get; init; }
}

public sealed record TargetReport
{
    public required string Target { get; init; }
    public required string Anchor { get; init; }
    public required string? ForecastFinish { get; init; }
    public required string? ForecastStart { get; init; }
    public required double GapWorkingDays { get; init; }
    public required bool Achievable { get; init; }
    public required int TasksWithNegativeFloat { get; init; }
    public required double CompressionNeededDays { get; init; }
    public required IReadOnlyList<DrivingLink> DrivingChain { get; init; }
    public required IReadOnlyList<OrphanTask> Orphans { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Answers "can this be finished by that date, and if not what has to give".
/// </summary>
/// <remarks>
/// A target date is only meaningful if the network can carry it, so this reports the driving chain
/// with the dates each task would have to hit, and refuses to be optimistic about a schedule full
/// of orphan tasks: work with no predecessor or no successor is not held in place by anything, so
/// any answer computed over it is arithmetic rather than a plan.
/// </remarks>
public static class TargetPlanner
{
    public static TargetReport Analyse(ProjectFile project, DateTime target, bool byFinish)
    {
        var calendar = new WorkingCalendar(project);
        var leaves = ScheduleAnalyzer.Leaves(project).ToList();
        var notes = new List<string>();

        var forecastFinish = MpxjBackend.ProjectFinish(project);
        var forecastStart = leaves.Where(t => t.Start is not null).Min(t => t.Start);

        var anchor = byFinish ? forecastFinish : forecastStart;
        var gap = anchor is null
            ? 0
            : byFinish
                ? calendar.WorkingDaysBetween(target, anchor.Value)
                : calendar.WorkingDaysBetween(anchor.Value, target);

        var orphans = FindOrphans(leaves);

        // The driving chain, with the dates each task must hit for the target to hold.
        var chain = new List<DrivingLink>();
        if (byFinish && forecastFinish is not null)
        {
            var shift = calendar.WorkingDaysBetween(forecastFinish.Value, target);
            foreach (var task in leaves.Where(t => t.Critical).OrderBy(t => t.Start))
            {
                var days = MpxjMapper.Days(task.Duration) ?? 0;
                var requiredStart = task.Start is null ? null : (DateTime?)calendar.Shift(task.Start.Value, shift);
                var requiredFinish = task.Finish is null ? null : (DateTime?)calendar.Shift(task.Finish.Value, shift);

                chain.Add(new DrivingLink
                {
                    Uid = task.UniqueID!.Value,
                    Name = task.Name,
                    DurationDays = Math.Round(days, 2),
                    RequiredStart = requiredStart?.ToString("yyyy-MM-dd") ?? "",
                    RequiredFinish = requiredFinish?.ToString("yyyy-MM-dd") ?? "",
                    FloatDays = Math.Round(MpxjMapper.Days(task.TotalSlack) ?? 0, 2),
                });
            }
        }

        var achievable = gap <= 0;
        var compression = achievable ? 0 : Math.Round(gap, 1);

        if (!achievable)
        {
            notes.Add(
                $"The forecast misses the target by {compression:0.#} working day(s). "
                + "schedule_recovery measures which changes to the logic actually buy that time back "
                + "instead of guessing at it.");
        }

        if (orphans.Count > 0)
        {
            notes.Add(
                $"{orphans.Count} task(s) are not held in place by the network. Until they are linked, "
                + "the target date is arithmetic rather than a plan: nothing makes them move when their "
                + "neighbours do.");
        }

        if (chain.Count == 0 && byFinish)
        {
            notes.Add(
                "No driving chain could be listed because no task is marked critical. A schedule where "
                + "everything carries float is usually missing successor logic — schedule_qa locates it.");
        }

        return new TargetReport
        {
            Target = target.ToString("yyyy-MM-dd"),
            Anchor = byFinish ? "finish" : "start",
            ForecastFinish = MpxjMapper.Iso(forecastFinish),
            ForecastStart = MpxjMapper.Iso(forecastStart),
            GapWorkingDays = Math.Round(gap, 1),
            Achievable = achievable,
            TasksWithNegativeFloat = leaves.Count(t => (MpxjMapper.Days(t.TotalSlack) ?? 0) < -0.001),
            CompressionNeededDays = compression,
            DrivingChain = chain.Take(200).ToList(),
            Orphans = orphans.Take(200).ToList(),
            Notes = notes,
        };
    }

    /// <summary>
    /// Tasks the network does not hold in place. A start milestone with no predecessor and a
    /// finish milestone with no successor are legitimate; everything else is a hole in the logic.
    /// </summary>
    public static List<OrphanTask> FindOrphans(IReadOnlyList<MPXJ.Net.Task> leaves)
    {
        var orphans = new List<OrphanTask>();
        var earliest = leaves.Where(t => t.Start is not null).Min(t => t.Start);
        var latest = leaves.Where(t => t.Finish is not null).Max(t => t.Finish);

        foreach (var task in leaves)
        {
            var noPred = task.Predecessors.Count == 0;
            var noSucc = task.Successors.Count == 0;

            if (!noPred && !noSucc)
            {
                continue;
            }

            // The genuine bookends of the network are not orphans.
            var isStartMilestone = noPred && task.Milestone && task.Start == earliest;
            var isFinishMilestone = noSucc && task.Milestone && task.Finish == latest;
            if (isStartMilestone || isFinishMilestone)
            {
                continue;
            }

            var problem = noPred && noSucc ? "isolated"
                : noPred ? "no_predecessor"
                : "no_successor";

            orphans.Add(new OrphanTask
            {
                Uid = task.UniqueID!.Value,
                Name = task.Name,
                Problem = problem,
                Start = MpxjMapper.Iso(task.Start),
                Finish = MpxjMapper.Iso(task.Finish),
                Consequence = problem switch
                {
                    "isolated" => "Nothing drives this task and nothing waits for it. Its dates will never "
                                  + "move when the schedule does, and it can be silently missed.",
                    "no_predecessor" => "Nothing drives this task, so it stays where it was typed even when "
                                        + "the work it depends on slips.",
                    _ => "Nothing waits for this task, so delaying it costs the schedule nothing on paper. "
                         + "That is almost always wrong and it is why float looks so high.",
                },
            });
        }

        return orphans
            .OrderBy(o => o.Problem == "isolated" ? 0 : 1)
            .ToList();
    }
}
