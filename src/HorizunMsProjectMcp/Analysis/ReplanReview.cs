using Horizun.ProjectMcp.Backends;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Analysis;

/// <summary>A task a planner should look at again, with the reasons a planner would give.</summary>
public sealed record ReplanItem
{
    public required int Uid { get; init; }
    public string? Name { get; init; }

    /// <summary>high, medium or low — how much of the programme is at stake if it is left alone.</summary>
    public required string Severity { get; init; }

    /// <summary>Stable keys, for filtering: not_started, overdue, slow_progress, behind_baseline,
    /// negative_float, out_of_sequence.</summary>
    public required IReadOnlyList<string> Criteria { get; init; }

    /// <summary>The same reasons in words, with the figures behind them.</summary>
    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>What to do about it — a planner's move, not a software setting.</summary>
    public required string Action { get; init; }

    public double? PercentComplete { get; init; }
    public bool OnCriticalPath { get; init; }
    public int SuccessorsAffected { get; init; }
}

/// <summary>
/// Reviews a schedule the way a planner does at a progress cut-off, and lists the tasks that should
/// be re-planned — each with why, and what to do.
/// </summary>
/// <remarks>
/// "Late" in the software sense is only one reason, and not the most useful. A task that is on its
/// dates but 20% done against 60% of its time elapsed will be late; one that started before its
/// predecessor finished means the logic no longer describes the site; one that has drifted from its
/// baseline or eaten its float is a warning before it is a delay. Each criterion here is stated with
/// the figures behind it, so the planner can agree or overrule it — this proposes, it does not decide.
/// </remarks>
public static class ReplanReview
{
    /// <summary>Points of percent-complete behind the share of time elapsed before progress counts as slow.</summary>
    private const double SlowProgressMargin = 15;

    /// <summary>Working days past the baseline finish before it is worth raising.</summary>
    private const double BaselineSlipDays = 5;

    public static IReadOnlyList<ReplanItem> Review(ProjectFile project, DateTime statusDate)
    {
        var calendar = new WorkingCalendar(project);
        var items = new List<ReplanItem>();

        foreach (var task in ScheduleAnalyzer.Leaves(project))
        {
            // A milestone has no work to re-plan: it is reached when what drives it finishes, and that
            // work is what gets flagged.
            if (task.UniqueID is not int uid || task.Milestone)
            {
                continue;
            }

            var percent = Convert.ToDouble(task.PercentageComplete ?? 0);
            var done = percent >= 100 || task.ActualFinish is not null;
            var started = task.ActualStart is not null || percent > 0;
            var criteria = new List<string>();
            var reasons = new List<string>();

            if (!done && !started && task.Start is { } start && start < statusDate)
            {
                var days = calendar.WorkingDaysBetween(start, statusDate);
                if (days > 0)
                {
                    criteria.Add("not_started");
                    reasons.Add($"Due to start {start:yyyy-MM-dd} and has not started — {days:0.#} working days behind.");
                }
            }

            if (!done && task.Finish is { } finish && finish < statusDate)
            {
                var days = calendar.WorkingDaysBetween(finish, statusDate);
                if (days > 0)
                {
                    criteria.Add("overdue");
                    reasons.Add($"Due to finish {finish:yyyy-MM-dd} and is at {percent:0}% — {days:0.#} working days past it.");
                }
            }

            if (!done && started && task.ActualStart is { } actualStart && task.Finish is { } plannedFinish
                && actualStart < statusDate)
            {
                var span = calendar.WorkingDaysBetween(actualStart, plannedFinish);
                var elapsed = calendar.WorkingDaysBetween(actualStart, statusDate);
                if (span > 0 && elapsed > 0)
                {
                    var expected = Math.Min(100, elapsed / span * 100);
                    if (expected - percent >= SlowProgressMargin)
                    {
                        criteria.Add("slow_progress");
                        var projected = percent > 0 ? elapsed / (percent / 100) : double.PositiveInfinity;
                        var slip = double.IsInfinity(projected) ? (double?)null : Math.Max(0, projected - span);
                        reasons.Add(
                            $"Progress {percent:0}% against {expected:0}% of its time elapsed"
                            + (slip is null
                                ? " — no measurable progress since it started."
                                : $" — at this rate it needs about {slip:0.#} more working days than planned."));
                    }
                }
            }

            if (!done && task.BaselineFinish is { } baselineFinish && task.Finish is { } forecast && forecast > baselineFinish)
            {
                var days = calendar.WorkingDaysBetween(baselineFinish, forecast);
                if (days >= BaselineSlipDays)
                {
                    criteria.Add("behind_baseline");
                    reasons.Add($"Forecast finish {forecast:yyyy-MM-dd} is {days:0.#} working days after its baseline ({baselineFinish:yyyy-MM-dd}).");
                }
            }

            var floatDays = MpxjMapper.Days(task.TotalSlack);
            if (!done && floatDays is < 0)
            {
                criteria.Add("negative_float");
                reasons.Add($"Total float is {floatDays:0.#} days: as planned it already pushes a deadline or the finish.");
            }

            if (started && !done || task.ActualStart is not null)
            {
                var early = task.Predecessors
                    .Where(r => r.Type == RelationType.FinishStart && r.PredecessorTask is { } p
                                && (p.PercentageComplete ?? 0) < 100 && p.ActualFinish is null)
                    .Select(r => r.PredecessorTask!)
                    .ToList();
                if (early.Count > 0 && !done)
                {
                    criteria.Add("out_of_sequence");
                    reasons.Add(
                        $"Started while {string.Join(", ", early.Take(3).Select(p => $"'{p.Name}'"))} "
                        + "— which it is set to follow finish-to-start — is not finished: the logic no longer describes the site.");
                }
            }

            if (criteria.Count == 0)
            {
                continue;
            }

            var downstream = RecoveryPlanner.CountDownstream(project, uid);
            var severity = task.Critical || criteria.Contains("negative_float") || criteria.Contains("overdue") && downstream > 0
                ? "high"
                : criteria.Count > 1 || downstream >= 5 ? "medium" : "low";

            items.Add(new ReplanItem
            {
                Uid = uid,
                Name = task.Name,
                Severity = severity,
                Criteria = criteria,
                Reasons = reasons,
                Action = ActionFor(criteria, task.Critical),
                PercentComplete = percent,
                OnCriticalPath = task.Critical,
                SuccessorsAffected = downstream,
            });
        }

        var rank = new Dictionary<string, int> { ["high"] = 0, ["medium"] = 1, ["low"] = 2 };
        return items
            .OrderBy(i => rank[i.Severity])
            .ThenByDescending(i => i.OnCriticalPath)
            .ThenByDescending(i => i.SuccessorsAffected)
            .ToList();
    }

    private static string ActionFor(IReadOnlyList<string> criteria, bool critical)
    {
        if (criteria.Contains("out_of_sequence"))
        {
            return "Confirm with site what actually happened, then fix the logic to match — typically a "
                   + "start-to-start link with a lag instead of finish-to-start — so the forecast reflects the real sequence.";
        }

        if (criteria.Contains("slow_progress"))
        {
            return critical
                ? "On the critical path: add a crew or shift, or overlap the successor, and extend the duration "
                  + "to the realistic rate if neither is possible."
                : "Re-estimate the remaining duration at the rate actually achieved, and check it stays within its float.";
        }

        if (criteria.Contains("not_started") || criteria.Contains("overdue"))
        {
            return "Move the remaining work to start from the status date (schedule_update "
                   + "op='reschedule_incomplete'), and record why it did not happen — materials, permits, "
                   + "a predecessor, labour — so the new date is a commitment rather than a guess.";
        }

        if (criteria.Contains("negative_float"))
        {
            return "Recover time on its driving chain (see the options measured in this report) or agree a "
                   + "new date for the deadline it is breaching.";
        }

        return "Review against the baseline: either accept the new forecast formally (new baseline, with "
               + "justification) or plan the recovery.";
    }
}
