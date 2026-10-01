using Horizun.ProjectMcp.Backends;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Analysis;

public sealed record EvmMetrics
{
    public required string Scope { get; init; }

    /// <summary>
    /// What the numbers are denominated in: "cost" where the schedule carries costs, "work_hours"
    /// where it does not. Most construction schedules track hours and no money at all, and earned
    /// value in hours is standard practice — reporting zeros instead would be useless.
    /// </summary>
    public required string Measure { get; init; }
    public string? Label { get; init; }
    public int? Uid { get; init; }
    public required int Tasks { get; init; }

    /// <summary>Budgeted cost of work scheduled — the plan.</summary>
    public required double Bcws { get; init; }

    /// <summary>Budgeted cost of work performed — what has been earned.</summary>
    public required double Bcwp { get; init; }

    /// <summary>Actual cost of work performed — what has been spent. Null when the schedule records
    /// no actuals at all: an AC equal to EV would be a CPI of exactly 1 that nobody measured.</summary>
    public required double? Acwp { get; init; }

    /// <summary>Whether the schedule records any actuals at all. Explicit, because a null AC is dropped
    /// by serializers and an absent field reads as a bug rather than as "not computable".</summary>
    public bool ActualsRecorded => Acwp is not null;

    /// <summary>Budget at completion.</summary>
    public required double Bac { get; init; }

    public double ScheduleVariance => Math.Round(Bcwp - Bcws, 2);
    public double? CostVariance => Acwp is null ? null : Math.Round(Bcwp - Acwp.Value, 2);
    public double? Spi => Bcws <= 0 ? null : Math.Round(Bcwp / Bcws, 3);
    public double? Cpi => Acwp is not > 0 ? null : Math.Round(Bcwp / Acwp.Value, 3);

    /// <summary>Estimate at completion, projected at the current cost efficiency.</summary>
    public double? Eac => Cpi is null or 0 ? null : Math.Round(Bac / Cpi.Value, 2);

    /// <summary>To-complete performance index: the efficiency the remaining work must run at.</summary>
    public double? Tcpi
    {
        get
        {
            var remainingBudget = Bac - Bcwp;
            if (Acwp is null)
            {
                return null;
            }

            var remainingFunds = Bac - Acwp.Value;
            return remainingFunds <= 0 ? null : Math.Round(remainingBudget / remainingFunds, 3);
        }
    }
}

public sealed record BaselineComparison
{
    /// <summary>The unit of every day figure in this report.</summary>
    public string DayUnit { get; init; } = Horizun.ProjectMcp.Model.DayUnits.Working;
    public required string StatusDate { get; init; }
    public required int BaselineNumber { get; init; }
    public required bool BaselinePresent { get; init; }
    public required EvmMetrics Project { get; init; }
    public IReadOnlyList<EvmMetrics>? ByBranch { get; init; }
    public IReadOnlyList<VarianceRow>? WorstVariances { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record VarianceRow
{
    public required int Uid { get; init; }
    public string? Name { get; init; }
    public double? StartVarianceDays { get; init; }
    public double? FinishVarianceDays { get; init; }
    public double? CostVariance { get; init; }
    public double? WorkVarianceHours { get; init; }
}

/// <summary>
/// Earned-value analysis against a stored baseline. This is what feeds the S-curve and the
/// cost dashboard; it is the number a project board actually asks for.
/// </summary>
public static class EarnedValue
{
    public static BaselineComparison Compare(
        ProjectFile project, DateTime statusDate, int baselineNumber, bool byBranch)
    {
        var leaves = ScheduleAnalyzer.Leaves(project).ToList();
        var notes = new List<string>();

        // Value has to be denominated in something the schedule actually carries. Fall through
        // cost, then loaded hours, then duration — plenty of real schedules hold neither money nor
        // resource-loaded work, and weighting by duration is the standard method for those. It is
        // the difference between a usable report and a column of zeros.
        var hasCost = leaves.Any(t => Writes.Costs.Of(t) > 0 || (BaselineCost(t, baselineNumber) ?? 0) > 0);
        var hasWork = leaves.Any(t => (MpxjMapper.Hours(t.Work) ?? 0) > 0);
        var measure = hasCost ? "cost" : hasWork ? "work_hours" : "duration_days";

        if (!hasCost)
        {
            notes.Add(hasWork
                ? "This schedule carries no costs, so earned value is measured in work hours instead. "
                  + "SPI and the variances read the same way; the currency figures simply are not there."
                : "This schedule carries neither costs nor loaded work, so earned value is weighted by "
                  + "task duration instead — a standard method, and the only one this schedule supports. "
                  + "SPI reads normally. CPI is null because actual effort is not recorded anywhere.");
        }

        var withBaseline = leaves.Count(t => BaselineFinish(t, baselineNumber) is not null);
        if (withBaseline == 0)
        {
            notes.Add(
                $"No baseline {baselineNumber} dates are stored, so BCWS falls back to the current plan and "
                + "schedule variance will read as zero. Save a baseline to get meaningful numbers.");
        }

        var projectMetrics = Compute(leaves, statusDate, baselineNumber, measure, "project", null, null);
        if (projectMetrics.Acwp is null && measure != "duration_days")
        {
            notes.Add(measure == "cost"
                ? "No actual cost is recorded, so AC, the cost variance, CPI, EAC and TCPI cannot be computed — "
                  + "they are null, not 1. Record actual costs to read them."
                : "No actual work is recorded, so AC, the cost variance, CPI, EAC and TCPI cannot be computed.");
        }

        List<EvmMetrics>? branches = null;
        if (byBranch)
        {
            branches = new List<EvmMetrics>();
            foreach (var summary in project.Tasks.Where(t => t.Summary && t.OutlineLevel == 1 && t.UniqueID is not null))
            {
                var descendants = Descendants(summary).Where(t => !t.Summary).ToList();
                if (descendants.Count > 0)
                {
                    branches.Add(Compute(
                        descendants, statusDate, baselineNumber, measure, "branch",
                        summary.Name, summary.UniqueID));
                }
            }
        }

        if (projectMetrics.Bac <= 0)
        {
            notes.Add(
                "The tasks carry no cost, no work and no duration, so there is nothing to earn value "
                + "against at all.");
        }

        var calendar = new WorkingCalendar(project);
        double? Diff(DateTime? actual, DateTime? planned) =>
            actual is null || planned is null ? null : calendar.ShiftInWorkingDays(planned.Value, actual.Value);
        var worst = leaves
            .Select(t => new VarianceRow
            {
                Uid = t.UniqueID!.Value,
                Name = t.Name,
                StartVarianceDays = Diff(t.Start, BaselineStart(t, baselineNumber)),
                FinishVarianceDays = Diff(t.Finish, BaselineFinish(t, baselineNumber)),
                CostVariance = t.Cost is null || BaselineCost(t, baselineNumber) is null
                    ? null
                    : Math.Round(t.Cost.Value - BaselineCost(t, baselineNumber)!.Value, 2),
                WorkVarianceHours = MpxjMapper.Hours(t.Work) is null || MpxjMapper.Hours(BaselineWork(t, baselineNumber)) is null
                    ? null
                    : Math.Round(MpxjMapper.Hours(t.Work)!.Value - MpxjMapper.Hours(BaselineWork(t, baselineNumber))!.Value, 2),
            })
            .Where(r => r.FinishVarianceDays is not null && Math.Abs(r.FinishVarianceDays.Value) > 0.001)
            .OrderByDescending(r => Math.Abs(r.FinishVarianceDays!.Value))
            .Take(25)
            .ToList();

        return new BaselineComparison
        {
            StatusDate = statusDate.ToString("yyyy-MM-dd"),
            BaselineNumber = baselineNumber,
            BaselinePresent = withBaseline > 0,
            Project = projectMetrics,
            ByBranch = branches,
            WorstVariances = worst.Count == 0 ? null : worst,
            Notes = notes,
        };
    }

    private static EvmMetrics Compute(
        IReadOnlyList<MPXJ.Net.Task> tasks, DateTime statusDate, int baselineNumber,
        string measure, string scope, string? label, int? uid)
    {
        double bcws = 0, bcwp = 0, acwp = 0, bac = 0;
        var actualsRecorded = false;

        foreach (var task in tasks)
        {
            // Budget for this task: the baseline value where one exists, otherwise the current plan.
            var budget = measure switch
            {
                "cost" => BaselineCost(task, baselineNumber) ?? Writes.Costs.Of(task),
                "work_hours" => MpxjMapper.Hours(BaselineWork(task, baselineNumber))
                                ?? MpxjMapper.Hours(task.Work) ?? 0,
                _ => MpxjMapper.Days(BaselineDuration(task, baselineNumber))
                     ?? MpxjMapper.Days(task.Duration) ?? 0,
            };
            bac += budget;

            // BCWS — the share of the budget the plan said should be earned by the status date.
            var plannedStart = BaselineStart(task, baselineNumber) ?? task.Start;
            var plannedFinish = BaselineFinish(task, baselineNumber) ?? task.Finish;
            bcws += budget * PlannedFraction(plannedStart, plannedFinish, statusDate);

            // BCWP — budget times physical progress.
            var percent = (task.PercentageComplete ?? 0) / 100.0;
            bcwp += budget * Math.Clamp(percent, 0, 1);

            // ACWP — what was actually spent or worked, as recorded. Where nothing is recorded it is
            // not observable: it used to collapse onto earned value, which reported a CPI of 1 that
            // status_report and the S-curve rightly called not computable.
            var actual = measure switch
            {
                "cost" => task.ActualCost,
                "work_hours" => MpxjMapper.Hours(task.ActualWork),
                _ => null,
            };
            acwp += actual ?? 0;
            actualsRecorded |= actual is > 0;
        }

        return new EvmMetrics
        {
            Scope = scope,
            Measure = measure,
            Label = label,
            Uid = uid,
            Tasks = tasks.Count,
            Bcws = Math.Round(bcws, 2),
            Bcwp = Math.Round(bcwp, 2),
            Acwp = actualsRecorded ? Math.Round(acwp, 2) : null,
            Bac = Math.Round(bac, 2),
        };
    }

    /// <summary>How much of a task the plan says should be done by the status date, spread linearly.</summary>
    private static double PlannedFraction(DateTime? start, DateTime? finish, DateTime statusDate)
    {
        if (start is null || finish is null)
        {
            return 0;
        }

        if (statusDate >= finish)
        {
            return 1;
        }

        if (statusDate <= start)
        {
            return 0;
        }

        var span = (finish.Value - start.Value).TotalDays;
        return span <= 0 ? 1 : Math.Clamp((statusDate - start.Value).TotalDays / span, 0, 1);
    }

    private static IEnumerable<MPXJ.Net.Task> Descendants(MPXJ.Net.Task task)
    {
        foreach (var child in task.ChildTasks)
        {
            yield return child;
            foreach (var grandchild in Descendants(child))
            {
                yield return grandchild;
            }
        }
    }


    // Baseline 0 lives on the dedicated properties; 1..10 are the numbered slots.
    private static DateTime? BaselineStart(MPXJ.Net.Task t, int n) =>
        n == 0 ? t.BaselineStart : Safe(() => t.GetBaselineStart(n));

    private static DateTime? BaselineFinish(MPXJ.Net.Task t, int n) =>
        n == 0 ? t.BaselineFinish : Safe(() => t.GetBaselineFinish(n));

    private static double? BaselineCost(MPXJ.Net.Task t, int n) =>
        n == 0 ? t.BaselineCost : Safe(() => t.GetBaselineCost(n));

    private static MPXJ.Net.Duration? BaselineWork(MPXJ.Net.Task t, int n) =>
        n == 0 ? t.BaselineWork : SafeRef(() => t.GetBaselineWork(n));

    private static MPXJ.Net.Duration? BaselineDuration(MPXJ.Net.Task t, int n) =>
        n == 0 ? t.BaselineDuration : SafeRef(() => t.GetBaselineDuration(n));

    private static T? Safe<T>(Func<T?> read) where T : struct
    {
        try { return read(); } catch { return null; }
    }

    private static T? SafeRef<T>(Func<T?> read) where T : class
    {
        try { return read(); } catch { return null; }
    }
}
