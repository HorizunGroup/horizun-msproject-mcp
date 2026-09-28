using Horizun.ProjectMcp.Backends;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Analysis;

/// <summary>The period status report a PMO issues at each cut-off.</summary>
public sealed record StatusReport
{
    public required string StatusDate { get; init; }
    public required string Measure { get; init; }

    /// <summary>green, amber or red — from the schedule and cost indices against stated thresholds.</summary>
    public required string Health { get; init; }

    public required double PlannedPercent { get; init; }
    public required double EarnedPercent { get; init; }
    public required double Bac { get; init; }
    public required double PlannedValue { get; init; }
    public required double EarnedValue { get; init; }
    public double? ActualCost { get; init; }
    public double? Spi { get; init; }
    public double? Cpi { get; init; }

    /// <summary>Earned schedule: the date at which the baseline planned to have earned what has been
    /// earned now. Unlike SPI it does not drift back to 1.0 as the project nears its end.</summary>
    public string? EarnedSchedule { get; init; }

    /// <summary>SPI(t): earned schedule over actual time.</summary>
    public double? SpiTime { get; init; }

    /// <summary>Schedule variance in working days, from earned schedule.</summary>
    public double? ScheduleVarianceDays { get; init; }

    public string? BaselineFinish { get; init; }
    public string? ForecastFinish { get; init; }

    /// <summary>Finish forecast from SPI(t): baseline duration over SPI(t) — the independent check on the
    /// scheduled forecast.</summary>
    public string? ForecastFinishBySpiT { get; init; }

    /// <summary>Estimates at completion by the standard methods: at budget rate, at current CPI, at
    /// CPI times SPI.</summary>
    public IReadOnlyDictionary<string, double> Eac { get; init; } = new Dictionary<string, double>();

    public double? Tcpi { get; init; }
    public IReadOnlyList<string> MilestonesAtRisk { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ReplanItem> TopIssues { get; init; } = Array.Empty<ReplanItem>();
    public IReadOnlyList<StatusSnapshot> Trend { get; init; } = Array.Empty<StatusSnapshot>();
    public IReadOnlyList<PpcWeek> Ppc { get; init; } = Array.Empty<PpcWeek>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record PpcWeek
{
    public required string WeekStart { get; init; }
    public required int Committed { get; init; }
    public required int Completed { get; init; }
    public double Ppc => Committed == 0 ? 0 : Math.Round(100.0 * Completed / Committed, 1);
    public IReadOnlyList<string> NotCompleted { get; init; } = Array.Empty<string>();
}

/// <summary>The short-interval plan: what should happen in each of the coming weeks, and whether it can.</summary>
public sealed record LookaheadReport
{
    public required string StatusDate { get; init; }
    public required int Weeks { get; init; }
    public required IReadOnlyList<LookaheadWeek> Plan { get; init; }
    public required int Ready { get; init; }
    public required int Constrained { get; init; }
    public IReadOnlyList<PpcWeek> Ppc { get; init; } = Array.Empty<PpcWeek>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record LookaheadWeek
{
    public required string WeekStart { get; init; }
    public required IReadOnlyList<LookaheadTask> Tasks { get; init; }
}

public sealed record LookaheadTask
{
    public required int Uid { get; init; }
    public string? Name { get; init; }
    public string? Start { get; init; }
    public string? Finish { get; init; }
    public double PercentComplete { get; init; }
    public bool Critical { get; init; }

    /// <summary>True when nothing is known to stand in its way.</summary>
    public required bool Ready { get; init; }

    /// <summary>What has to be released before it can go: unfinished predecessors, no crew, no code…</summary>
    public IReadOnlyList<string> Constraints { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Project control at the level a PMO works: the period status report with earned value and earned
/// schedule, and the short-interval look-ahead with its constraints and Percent Plan Complete.
/// </summary>
public static class PmoControl
{
    private const double Amber = 0.95;
    private const double Red = 0.90;

    public static StatusReport Status(ProjectFile project, DateTime statusDate, string? schedulePath, bool record)
    {
        var notes = new List<string>();
        var leaves = ScheduleAnalyzer.Leaves(project).Where(t => t.UniqueID is not null).ToList();
        var (measure, budget) = ValueWeights.For(project, leaves);
        var bac = budget.Values.Sum();
        var calendar = new WorkingCalendar(project);

        double PlannedAt(DateTime at) => leaves.Sum(t => budget[t.UniqueID!.Value]
            * ValueWeights.Fraction(t.BaselineStart ?? t.Start, t.BaselineFinish ?? t.Finish, at));

        var pv = PlannedAt(statusDate);
        var ev = leaves.Sum(t => budget[t.UniqueID!.Value] * Convert.ToDouble(t.PercentageComplete ?? 0) / 100.0);
        double? ac = measure == "cost" && leaves.Any(t => (t.ActualCost ?? 0) > 0)
            ? leaves.Sum(t => Convert.ToDouble(t.ActualCost ?? 0))
            : null;

        var hasBaseline = leaves.Any(t => t.BaselineStart is not null);
        if (!hasBaseline)
        {
            notes.Add("No baseline is stored, so the plan is today's schedule and every variance reads zero. "
                      + "Save one (schedule_update op='save_baseline') to measure against a commitment.");
        }

        var baselineStart = leaves.Select(t => t.BaselineStart ?? t.Start).Where(d => d is not null).DefaultIfEmpty().Min();
        var baselineFinish = leaves.Select(t => t.BaselineFinish ?? t.Finish).Where(d => d is not null).DefaultIfEmpty().Max();
        var forecast = MpxjBackend.ProjectFinish(project);

        // Earned schedule: the day on the baseline curve where planned value equals today's earned value.
        DateTime? es = null;
        double? spiT = null, svDays = null;
        DateTime? forecastBySpiT = null;
        if (baselineStart is not null && baselineFinish is not null && ev > 0 && bac > 0)
        {
            var day = baselineStart.Value.Date;
            while (day <= baselineFinish.Value.Date && PlannedAt(day.AddDays(1)) <= ev)
            {
                day = day.AddDays(1);
            }

            var before = PlannedAt(day);
            var after = PlannedAt(day.AddDays(1));
            var part = after > before ? Math.Clamp((ev - before) / (after - before), 0, 1) : 0;
            es = day.AddDays(part);

            var at = calendar.WorkingDaysBetween(baselineStart.Value, statusDate);
            var earnedTime = calendar.WorkingDaysBetween(baselineStart.Value, es.Value);
            if (at > 0)
            {
                spiT = Math.Round(earnedTime / at, 3);
                svDays = Math.Round(earnedTime - at, 1);
                var plannedDuration = calendar.WorkingDaysBetween(baselineStart.Value, baselineFinish.Value);
                if (spiT > 0)
                {
                    forecastBySpiT = calendar.AddWorkingDays(baselineStart.Value, plannedDuration / spiT.Value);
                }
            }
        }

        double? spi = pv > 0 ? Math.Round(ev / pv, 3) : null;
        double? cpi = ac is > 0 ? Math.Round(ev / ac.Value, 3) : null;
        var eac = new Dictionary<string, double>();
        if (ac is not null)
        {
            eac["atBudgetRate"] = Math.Round(ac.Value + (bac - ev), 2);
            if (cpi is > 0)
            {
                eac["atCurrentCpi"] = Math.Round(bac / cpi.Value, 2);
            }

            if (cpi is > 0 && spi is > 0)
            {
                eac["atCpiTimesSpi"] = Math.Round(ac.Value + (bac - ev) / (cpi.Value * spi.Value), 2);
            }
        }
        else if (measure == "cost")
        {
            notes.Add("Costs are loaded but no actual cost is recorded, so CPI and EAC cannot be computed.");
        }

        double? tcpi = ac is not null && bac - ac.Value > 0 ? Math.Round((bac - ev) / (bac - ac.Value), 3) : null;

        var index = spiT ?? spi;
        var health = index is null ? "green"
            : index < Red || cpi is < Red ? "red"
            : index < Amber || cpi is < Amber ? "amber" : "green";

        var milestones = leaves
            .Where(t => t.Milestone && (t.PercentageComplete ?? 0) < 100 && t.BaselineFinish is not null
                        && t.Finish is not null && t.Finish > t.BaselineFinish)
            .Select(t => $"{t.Name}: forecast {t.Finish:yyyy-MM-dd}, baseline {t.BaselineFinish:yyyy-MM-dd} "
                         + $"({calendar.WorkingDaysBetween(t.BaselineFinish!.Value, t.Finish!.Value):0.#} working days late)")
            .ToList();

        var snapshot = new StatusSnapshot
        {
            StatusDate = statusDate.ToString("yyyy-MM-dd"),
            Measure = measure,
            PlannedPercent = bac > 0 ? Math.Round(100 * pv / bac, 1) : 0,
            EarnedPercent = bac > 0 ? Math.Round(100 * ev / bac, 1) : 0,
            Spi = spi,
            SpiTime = spiT,
            Cpi = cpi,
            ForecastFinish = MpxjMapper.Iso(forecast),
        };

        var trend = new List<StatusSnapshot>();
        var ppc = new List<PpcWeek>();
        if (schedulePath is not null)
        {
            var ledger = PmoLedger.Load(schedulePath);
            ppc = Ppc(project, ledger, statusDate);
            if (record)
            {
                ledger.Status.RemoveAll(s => s.StatusDate == snapshot.StatusDate);
                ledger.Status.Add(snapshot);
                PmoLedger.Save(schedulePath, ledger);
            }

            trend = ledger.Status.OrderBy(s => s.StatusDate).TakeLast(12).ToList();
        }

        if (measure != "cost")
        {
            notes.Add(measure == "work_hours"
                ? "No costs are loaded, so value is measured in work hours. Load the budget "
                  + "(schedule_update op='load_budget') for a money curve and cost indices."
                : "Neither costs nor loaded hours are present, so value is weighted by duration — SPI and "
                  + "SPI(t) read normally; CPI and EAC need the budget (schedule_update op='load_budget').");
        }

        return new StatusReport
        {
            StatusDate = statusDate.ToString("yyyy-MM-dd"),
            Measure = measure,
            Health = health,
            PlannedPercent = snapshot.PlannedPercent,
            EarnedPercent = snapshot.EarnedPercent,
            Bac = Math.Round(bac, 2),
            PlannedValue = Math.Round(pv, 2),
            EarnedValue = Math.Round(ev, 2),
            ActualCost = ac is null ? null : Math.Round(ac.Value, 2),
            Spi = spi,
            Cpi = cpi,
            EarnedSchedule = es?.ToString("yyyy-MM-dd"),
            SpiTime = spiT,
            ScheduleVarianceDays = svDays,
            BaselineFinish = MpxjMapper.Iso(baselineFinish),
            ForecastFinish = MpxjMapper.Iso(forecast),
            ForecastFinishBySpiT = forecastBySpiT?.ToString("yyyy-MM-dd"),
            Eac = eac,
            Tcpi = tcpi,
            MilestonesAtRisk = milestones,
            TopIssues = ReplanReview.Review(project, statusDate).Take(10).ToList(),
            Trend = trend,
            Ppc = ppc,
            Notes = notes,
        };
    }

    public static LookaheadReport Lookahead(ProjectFile project, DateTime statusDate, int weeks, string? schedulePath, bool commit)
    {
        weeks = Math.Clamp(weeks, 1, 12);
        var notes = new List<string>();
        var monday = statusDate.Date.AddDays(-(((int)statusDate.DayOfWeek + 6) % 7));
        var windowEnd = monday.AddDays(7 * weeks);
        var leaves = ScheduleAnalyzer.Leaves(project).Where(t => t.UniqueID is not null && !t.Milestone).ToList();

        var plan = new List<LookaheadWeek>();
        int ready = 0, constrained = 0;
        for (var w = 0; w < weeks; w++)
        {
            var from = monday.AddDays(7 * w);
            var to = from.AddDays(7);
            var tasks = new List<LookaheadTask>();
            foreach (var t in leaves.Where(t => (t.PercentageComplete ?? 0) < 100 && t.Start < to && t.Finish >= from)
                         .OrderBy(t => t.Start))
            {
                var constraints = new List<string>();
                foreach (var r in t.Predecessors)
                {
                    var p = r.PredecessorTask;
                    if (p is null || (p.PercentageComplete ?? 0) >= 100)
                    {
                        continue;
                    }

                    if (r.Type == RelationType.FinishStart && p.Finish >= to && (t.PercentageComplete ?? 0) == 0)
                    {
                        constraints.Add($"predecessor '{p.Name}' finishes {p.Finish:yyyy-MM-dd}, after this week");
                    }
                    else if (r.Type == RelationType.FinishStart && (t.PercentageComplete ?? 0) == 0)
                    {
                        constraints.Add($"predecessor '{p.Name}' is at {p.PercentageComplete ?? 0:0}% — release it first");
                    }
                }

                if (!t.ResourceAssignments.Any(a => a.Resource is not null))
                {
                    constraints.Add("no crew or resource assigned");
                }

                if (t.Start < statusDate && (t.PercentageComplete ?? 0) == 0)
                {
                    constraints.Add($"was due to start {t.Start:yyyy-MM-dd} and has not — find out why before committing it again");
                }

                // A missing crew alone is common in schedules that are not resource-loaded; it is reported
                // but does not by itself hold the task back.
                var blocking = constraints.Where(c => !c.StartsWith("no crew", StringComparison.Ordinal)).ToList();
                if (blocking.Count == 0)
                {
                    ready++;
                }
                else
                {
                    constrained++;
                }

                tasks.Add(new LookaheadTask
                {
                    Uid = t.UniqueID!.Value,
                    Name = t.Name,
                    Start = MpxjMapper.Iso(t.Start),
                    Finish = MpxjMapper.Iso(t.Finish),
                    PercentComplete = Convert.ToDouble(t.PercentageComplete ?? 0),
                    Critical = t.Critical,
                    Ready = blocking.Count == 0,
                    Constraints = constraints,
                });
            }

            plan.Add(new LookaheadWeek { WeekStart = from.ToString("yyyy-MM-dd"), Tasks = tasks });
        }

        var ppc = new List<PpcWeek>();
        if (schedulePath is not null)
        {
            var ledger = PmoLedger.Load(schedulePath);
            ppc = Ppc(project, ledger, statusDate);
            if (commit && plan.Count > 0)
            {
                // The coming week's commitment: work that is ready and planned to finish within it.
                var first = plan[0];
                var committed = first.Tasks.Where(t => t.Ready && DateTime.TryParse(t.Finish, out var f) && f < monday.AddDays(7))
                    .Select(t => t.Uid).ToList();
                ledger.Commitments.RemoveAll(c => c.WeekStart == first.WeekStart);
                ledger.Commitments.Add(new WeeklyCommitment
                {
                    WeekStart = first.WeekStart,
                    WeekEnd = monday.AddDays(7).ToString("yyyy-MM-dd"),
                    Uids = committed,
                });
                PmoLedger.Save(schedulePath, ledger);
                notes.Add($"{committed.Count} ready task(s) due to finish this week recorded as the week's commitment; "
                          + "the status report after the week measures Percent Plan Complete against it.");
            }
        }
        else if (commit)
        {
            notes.Add("The schedule has not been saved yet, so there is nowhere to record the commitment. Save it first.");
        }

        return new LookaheadReport
        {
            StatusDate = statusDate.ToString("yyyy-MM-dd"),
            Weeks = weeks,
            Plan = plan,
            Ready = ready,
            Constrained = constrained,
            Ppc = ppc,
            Notes = notes,
        };
    }

    /// <summary>Percent Plan Complete for each committed week that has ended: of what was promised, what was done.</summary>
    private static List<PpcWeek> Ppc(ProjectFile project, PmoLedgerData ledger, DateTime statusDate)
    {
        var weeks = new List<PpcWeek>();
        foreach (var c in ledger.Commitments.OrderBy(c => c.WeekStart))
        {
            if (!DateTime.TryParse(c.WeekEnd, out var end) || end > statusDate)
            {
                continue;
            }

            var missed = new List<string>();
            var done = 0;
            foreach (var uid in c.Uids)
            {
                var t = project.GetTaskByUniqueID(uid);
                if (t is null)
                {
                    continue;
                }

                if ((t.PercentageComplete ?? 0) >= 100 && (t.ActualFinish is null || t.ActualFinish <= end.AddDays(1)))
                {
                    done++;
                }
                else
                {
                    missed.Add(t.Name ?? uid.ToString());
                }
            }

            weeks.Add(new PpcWeek { WeekStart = c.WeekStart, Committed = c.Uids.Count, Completed = done, NotCompleted = missed.Take(20).ToList() });
        }

        return weeks;
    }
}
