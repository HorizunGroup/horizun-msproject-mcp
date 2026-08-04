using Horizun.ProjectMcp.Backends;
using Horizun.ProjectMcp.Model;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Analysis;

public sealed record QaReport
{
    public required string StatusDate { get; init; }
    public required int Checks { get; init; }
    public required int Passed { get; init; }
    public required int Failed { get; init; }
    public required int NotEvaluated { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
}

/// <summary>
/// The DCMA 14-point schedule assessment, plus Horizun's own rules.
/// </summary>
/// <remarks>
/// This is the industry standard for judging whether a schedule is fit to run a project on.
/// The Primavera MCP servers implement it; none of the Microsoft Project ones do.
/// <para>
/// Check 12 (the Critical Path Test) needs a scheduling engine to recalculate after injecting a
/// delay. Where no engine is available it is reported as <c>not_evaluated</c> with the reason —
/// never as a pass.
/// </para>
/// </remarks>
public static class Dcma14
{
    private const double HighFloatDays = 44;
    private const double HighDurationDays = 44;

    public static QaReport Run(
        ProjectFile project,
        DateTime statusDate,
        bool canRecalculate,
        string? budgetCodeField = null)
    {
        var leaves = ScheduleAnalyzer.Leaves(project).ToList();
        var findings = new List<Finding>();
        var total = Math.Max(leaves.Count, 1);

        // ---- 1. Logic: every task needs a predecessor and a successor ----
        var noPred = leaves.Where(t => t.Predecessors.Count == 0).ToList();
        var noSucc = leaves.Where(t => t.Successors.Count == 0).ToList();
        var danglers = noPred.Concat(noSucc).DistinctBy(t => t.UniqueID).ToList();
        findings.Add(Ratio(
            "dcma_01_logic", "Missing logic (no predecessor or no successor)",
            danglers.Count, total, 5,
            danglers, "Link the task into the network, or mark it as a genuine start/finish milestone."));

        // ---- 2. Leads: negative lag is never acceptable ----
        var leads = RelationsWhere(leaves, r => (MpxjMapper.Days(r.Lag) ?? 0) < -0.001);
        findings.Add(Ratio(
            "dcma_02_leads", "Negative lag (leads)",
            leads.Count, total, 0,
            leads, "Replace the lead with a proper SS/FF relationship, or break the task down."));

        // ---- 3. Lags ----
        var lags = RelationsWhere(leaves, r => (MpxjMapper.Days(r.Lag) ?? 0) > 0.001);
        findings.Add(Ratio(
            "dcma_03_lags", "Positive lags",
            lags.Count, total, 5,
            lags, "Model the waiting time as its own task (curing, delivery, approval) instead of a lag."));

        // ---- 4. Relationship types: FS should dominate ----
        var health = ScheduleAnalyzer.BuildDependencyHealth(project);
        var relations = health.FinishStart + health.StartStart + health.FinishFinish + health.StartFinish;
        var fsShare = relations == 0 ? 0 : 100.0 * health.FinishStart / relations;
        findings.Add(new Finding
        {
            Rule = "dcma_04_relationship_types",
            Severity = fsShare >= 90 ? "info" : "warning",
            Summary = $"Finish-to-Start relationships are {fsShare:0.#}% of {relations} links (target >= 90%).",
            Measured = Math.Round(fsShare, 2),
            Threshold = 90,
            Passed = relations == 0 || fsShare >= 90,
            FixHint = fsShare >= 90 ? null : "Prefer FS. SS/FF pairs often hide a task that should be split.",
        });

        // ---- 5. Hard constraints ----
        var hard = leaves.Where(t => t.ConstraintType is
            ConstraintType.MustStartOn or ConstraintType.MustFinishOn or
            ConstraintType.StartNoLaterThan or ConstraintType.FinishNoLaterThan).ToList();
        findings.Add(Ratio(
            "dcma_05_hard_constraints", "Hard constraints",
            hard.Count, total, 5,
            hard, "Replace the constraint with a deadline or with logic; hard constraints stop the network from working."));

        // ---- 6. High float ----
        var highFloat = leaves.Where(t => (MpxjMapper.Days(t.TotalSlack) ?? 0) > HighFloatDays).ToList();
        findings.Add(Ratio(
            "dcma_06_high_float", $"Total float above {HighFloatDays} days",
            highFloat.Count, total, 5,
            highFloat, "High float usually means missing successor logic rather than genuine slack."));

        // ---- 7. Negative float ----
        var negFloat = leaves.Where(t => (MpxjMapper.Days(t.TotalSlack) ?? 0) < -0.001).ToList();
        findings.Add(Ratio(
            "dcma_07_negative_float", "Negative total float",
            negFloat.Count, total, 0,
            negFloat, "The schedule cannot meet a date it is constrained to. Re-plan or move the constraint."));

        // ---- 8. High duration ----
        var longTasks = leaves
            .Where(t => !t.Milestone && (MpxjMapper.Days(t.Duration) ?? 0) > HighDurationDays)
            .ToList();
        findings.Add(Ratio(
            "dcma_08_high_duration", $"Durations above {HighDurationDays} days",
            longTasks.Count, total, 5,
            longTasks, "Break long tasks down so progress can actually be measured."));

        // ---- 9. Invalid dates ----
        var invalid = leaves.Where(t =>
            (t.ActualStart is not null && t.ActualStart > statusDate) ||
            (t.ActualFinish is not null && t.ActualFinish > statusDate) ||
            (t.ActualFinish is null && t.Finish is not null && t.Finish < statusDate && (t.PercentageComplete ?? 0) < 100))
            .ToList();
        findings.Add(Ratio(
            "dcma_09_invalid_dates", "Invalid dates (actuals in the future, or forecasts in the past)",
            invalid.Count, total, 0,
            invalid, $"Update progress against the status date ({statusDate:yyyy-MM-dd}) and reschedule incomplete work."));

        // ---- 10. Resources ----
        var assignedUids = project.ResourceAssignments
            .Select(a => a.Task?.UniqueID)
            .Where(u => u is not null)
            .Select(u => u!.Value)
            .ToHashSet();
        var unresourced = leaves
            .Where(t => !t.Milestone && (MpxjMapper.Days(t.Duration) ?? 0) > 0 && !assignedUids.Contains(t.UniqueID!.Value))
            .ToList();
        findings.Add(Ratio(
            "dcma_10_resources", "Tasks with duration but no resource assigned",
            unresourced.Count, total, 5,
            unresourced, "Assign a resource, or accept the task as a level-of-effort placeholder."));

        // ---- 11. Missed tasks vs baseline ----
        var withBaseline = leaves.Where(t => t.BaselineFinish is not null).ToList();
        var missed = withBaseline.Where(t =>
            (t.ActualFinish is not null && t.ActualFinish > t.BaselineFinish) ||
            (t.ActualFinish is null && t.Finish is not null && t.Finish > t.BaselineFinish))
            .ToList();
        if (withBaseline.Count == 0)
        {
            findings.Add(NotEvaluated(
                "dcma_11_missed_tasks", "Missed tasks vs baseline",
                "No baseline dates are stored in this schedule. Save a baseline before measuring against one."));
        }
        else
        {
            findings.Add(Ratio(
                "dcma_11_missed_tasks", "Tasks finishing later than their baseline",
                missed.Count, withBaseline.Count, 5,
                missed, "Investigate the drivers before re-baselining."));
        }

        // ---- 12. Critical Path Test ----
        findings.Add(CriticalPathTest(project, leaves));

        // ---- 13. CPLI (Critical Path Length Index) ----
        var finish = MpxjBackend.ProjectFinish(project);
        var start = project.ProjectProperties.StartDate ?? leaves.Min(t => t.Start);
        var deadline = project.ProjectProperties.FinishDate;
        if (finish is null || start is null || deadline is null)
        {
            findings.Add(NotEvaluated(
                "dcma_13_cpli", "Critical Path Length Index",
                "Needs a project start, a forecast finish, and a target finish date. Set the project's finish "
                + "date in its properties to enable it."));
        }
        else
        {
            var cpLength = (finish.Value - start.Value).TotalDays;
            var totalFloat = (deadline.Value - finish.Value).TotalDays;
            var cpli = cpLength <= 0 ? 1 : (cpLength + totalFloat) / cpLength;
            findings.Add(new Finding
            {
                Rule = "dcma_13_cpli",
                Severity = cpli >= 0.95 ? "info" : "warning",
                Summary = $"CPLI is {cpli:0.###} (target >= 0.95). Critical path {cpLength:0} days, "
                          + $"float to the target finish {totalFloat:0} days.",
                Measured = Math.Round(cpli, 3),
                Threshold = 0.95,
                Passed = cpli >= 0.95,
                FixHint = cpli >= 0.95 ? null : "The schedule is forecasting past its target date — compress or re-plan.",
            });
        }

        // ---- 14. BEI (Baseline Execution Index) ----
        var dueByNow = withBaseline.Where(t => t.BaselineFinish <= statusDate).ToList();
        if (dueByNow.Count == 0)
        {
            findings.Add(NotEvaluated(
                "dcma_14_bei", "Baseline Execution Index",
                $"No baselined task was due by the status date ({statusDate:yyyy-MM-dd}), so there is nothing to index."));
        }
        else
        {
            var completed = dueByNow.Count(t => t.ActualFinish is not null);
            var bei = (double)completed / dueByNow.Count;
            findings.Add(new Finding
            {
                Rule = "dcma_14_bei",
                Severity = bei >= 0.95 ? "info" : "warning",
                Summary = $"BEI is {bei:0.###} — {completed} of {dueByNow.Count} tasks baselined to finish by "
                          + $"{statusDate:yyyy-MM-dd} are actually complete (target >= 0.95).",
                Measured = Math.Round(bei, 3),
                Threshold = 0.95,
                Passed = bei >= 0.95,
                Uids = dueByNow.Where(t => t.ActualFinish is null).Take(50).Select(t => t.UniqueID!.Value).ToList(),
                FixHint = bei >= 0.95 ? null : "Execution is behind plan — the listed tasks were due and are not finished.",
            });
        }

        findings.AddRange(HorizunRules(project, leaves, budgetCodeField));

        return new QaReport
        {
            StatusDate = statusDate.ToString("yyyy-MM-dd"),
            Checks = findings.Count,
            Passed = findings.Count(f => f.Passed == true),
            Failed = findings.Count(f => f.Passed == false),
            NotEvaluated = findings.Count(f => !f.Evaluated),
            Findings = findings,
        };
    }

    /// <summary>
    /// DCMA check 12, run for real: inject a 600-day delay into a critical task on a throwaway copy,
    /// reschedule, and confirm the project finish moves with it. If it does not, the network logic is
    /// broken — the "critical path" on screen is not actually driving the end date, which is the most
    /// dangerous defect a schedule can have because everything downstream looks fine.
    /// </summary>
    private static Finding CriticalPathTest(ProjectFile project, IReadOnlyList<MPXJ.Net.Task> leaves)
    {
        const double injectedDays = 600;

        var candidate = leaves.FirstOrDefault(t => t.Critical && !t.Milestone && t.UniqueID is not null)
                        ?? leaves.FirstOrDefault(t => !t.Milestone && t.UniqueID is not null);

        if (candidate is null)
        {
            return NotEvaluated(
                "dcma_12_critical_path_test", "Critical Path Test",
                "The schedule has no non-milestone task to inject a test delay into.");
        }

        try
        {
            var sandbox = MpxjBackend.Clone(project);
            var before = MpxjBackend.ProjectFinish(sandbox);

            var target = sandbox.GetTaskByUniqueID(candidate.UniqueID!.Value);
            if (target is null || before is null)
            {
                return NotEvaluated(
                    "dcma_12_critical_path_test", "Critical Path Test",
                    "The test copy of the schedule could not be prepared.");
            }

            var original = MpxjMapper.Days(target.Duration) ?? 0;
            target.Duration = MPXJ.Net.Duration.GetInstance(original + injectedDays, TimeUnit.Days);
            CpmScheduler.Run(sandbox);

            var after = MpxjBackend.ProjectFinish(sandbox);

            // Measure in working days so the figure is comparable with the working days injected —
            // a calendar-day count would read as ~840 for a 600-day injection and look like a defect.
            var calendar = new WorkingCalendar(sandbox);
            var moved = after is null ? 0 : calendar.WorkingDaysBetween(before.Value, after.Value);

            // Anything close to the injected delay proves the chain carries through.
            var passed = moved >= injectedDays * 0.5;

            return new Finding
            {
                Rule = "dcma_12_critical_path_test",
                Severity = passed ? "info" : "error",
                Summary = $"Injected {injectedDays:0} working days into '{candidate.Name}' "
                          + $"(uid {candidate.UniqueID}) and the project finish moved {moved:0} working days.",
                Measured = Math.Round(moved, 1),
                Threshold = injectedDays * 0.5,
                Passed = passed,
                Uids = new[] { candidate.UniqueID!.Value },
                FixHint = passed
                    ? null
                    : "The delay did not propagate, so the critical path is not actually driving the finish "
                      + "date. Look for missing successor logic or hard constraints pinning the end of the "
                      + "schedule — checks 1 and 5 usually point at the cause.",
            };
        }
        catch (Exception ex)
        {
            return NotEvaluated(
                "dcma_12_critical_path_test", "Critical Path Test",
                $"The test could not be run on a copy of this schedule: {ex.Message}");
        }
    }

    /// <summary>Rules DCMA does not cover but that sink real construction schedules.</summary>
    private static IEnumerable<Finding> HorizunRules(
        ProjectFile project, IReadOnlyList<MPXJ.Net.Task> leaves, string? budgetCodeField)
    {
        var total = Math.Max(leaves.Count, 1);

        // Milestones must be zero-duration, or they are not milestones.
        var fatMilestones = leaves
            .Where(t => t.Milestone && (MpxjMapper.Days(t.Duration) ?? 0) > 0.001)
            .ToList();
        yield return Ratio(
            "hrz_milestone_duration", "Milestones with a duration other than zero",
            fatMilestones.Count, total, 0,
            fatMilestones, "A milestone marks an instant. Give it zero duration or make it a normal task.");

        // Duplicate names make progress reporting ambiguous.
        var duplicates = leaves
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .GroupBy(t => t.Name!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToList();
        yield return Ratio(
            "hrz_duplicate_names", "Tasks sharing a name with another task",
            duplicates.Count, total, 5,
            duplicates, "Qualify the names by location or system so progress reports are unambiguous.");

        // Unnamed tasks.
        var unnamed = leaves.Where(t => string.IsNullOrWhiteSpace(t.Name)).ToList();
        yield return Ratio(
            "hrz_unnamed_tasks", "Tasks with no name",
            unnamed.Count, total, 0, unnamed, "Name every task.");

        // The link to the budget — and therefore to BIM and to the cost model.
        if (!string.IsNullOrWhiteSpace(budgetCodeField))
        {
            var slot = ParseTextSlot(budgetCodeField);
            var missing = leaves
                .Where(t => !t.Milestone && string.IsNullOrWhiteSpace(SafeGetText(t, slot)))
                .ToList();
            yield return Ratio(
                "hrz_budget_code", $"Tasks with no budget code in {budgetCodeField}",
                missing.Count, total, 5,
                missing, "Without a budget code a task cannot be tied to the cost model or to the BIM elements.");
        }
    }

    private static List<MPXJ.Net.Task> RelationsWhere(
        IReadOnlyList<MPXJ.Net.Task> leaves, Func<Relation, bool> predicate) =>
        leaves.Where(t => t.Predecessors.Any(predicate)).ToList();

    private static Finding Ratio(
        string rule, string label, int count, int total, double thresholdPercent,
        IReadOnlyList<MPXJ.Net.Task> offenders, string fixHint)
    {
        var percent = 100.0 * count / Math.Max(total, 1);
        var passed = percent <= thresholdPercent + 0.0001;

        return new Finding
        {
            Rule = rule,
            Severity = passed ? "info" : thresholdPercent == 0 ? "error" : "warning",
            Summary = $"{label}: {count} of {total} ({percent:0.#}%, threshold {thresholdPercent:0.#}%).",
            Measured = Math.Round(percent, 2),
            Threshold = thresholdPercent,
            Passed = passed,
            Uids = offenders.Where(t => t.UniqueID is not null).Take(50).Select(t => t.UniqueID!.Value).ToList(),
            FixHint = passed ? null : fixHint,
        };
    }

    private static Finding NotEvaluated(string rule, string label, string why) => new()
    {
        Rule = rule,
        Severity = "info",
        Summary = $"{label}: not evaluated. {why}",
        Evaluated = false,
        Passed = null,
        FixHint = why,
    };

    internal static int ParseTextSlot(string field)
    {
        var digits = new string(field.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) && n is >= 1 and <= 30 ? n : 1;
    }

    internal static string? SafeGetText(MPXJ.Net.Task task, int slot)
    {
        try
        {
            return task.GetText(slot);
        }
        catch
        {
            return null;
        }
    }
}
