using System.ComponentModel;
using Horizun.ProjectMcp.Analysis;
using Horizun.ProjectMcp.Backends;
using Horizun.ProjectMcp.Model;
using ModelContextProtocol.Server;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Tools;

public sealed record Scenario
{
    [Description("Short name for the table, e.g. 'A: full Saturdays'.")]
    public required string Name { get; init; }

    [Description("Calendar operations, as calendars_write takes them. Applied first.")]
    public CalendarOp[]? Calendars { get; init; }

    [Description("Resource operations, as resources_write takes them.")]
    public ResourceOp[]? Resources { get; init; }

    [Description("Task operations, as tasks_write takes them.")]
    public TaskOp[]? Tasks { get; init; }

    [Description("Dependency operations, as links_write takes them. Applied last.")]
    public LinkOp[]? Links { get; init; }
}

public sealed record ScenarioOutcome
{
    public required string Name { get; init; }
    public string? Finish { get; init; }
    public double? FinishDeltaDays { get; init; }
    public double Cost { get; init; }
    public double CostDelta { get; init; }
    public int OverallocationWindows { get; init; }
    public double PeakLoadPercentOfCapacity { get; init; }
    public int CriticalTasks { get; init; }
    public int Applied { get; init; }
    public IReadOnlyList<RejectedWrite> Rejected { get; init; } = Array.Empty<RejectedWrite>();
}

public sealed record ScenarioComparison
{
    /// <summary>The unit of every day figure in this report.</summary>
    public string DayUnit { get; init; } = Horizun.ProjectMcp.Model.DayUnits.Working;
    public required string Engine { get; init; }
    public required ScenarioOutcome Current { get; init; }
    public required IReadOnlyList<ScenarioOutcome> Scenarios { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// What-if: several alternative changes, each applied to its own copy of the schedule and scheduled,
/// side by side with the schedule as it is. Nothing is committed.
/// </summary>
/// <remarks>
/// A recovery is chosen between options — full Saturdays, overlap by zones, another crew — and each
/// used to be tried with dry runs one tool at a time, with no way to combine calendar, resource and
/// logic changes into one alternative, nor to see the alternatives in one table.
/// </remarks>
[McpServerToolType]
public static class ScenarioTools
{
    [McpServerTool(Name = "schedule_scenarios")]
    [Description(
        "Compare what-if scenarios without committing anything. Each scenario is a set of calendar, "
        + "resource, task and dependency operations (the same ones the write tools take), applied to its "
        + "own copy of the schedule, which is then scheduled. Returns, next to the schedule as it is: finish "
        + "and its change in working days, total cost and its change, resource overallocation windows and "
        + "peak load, critical task count, and anything a scenario asked for that was refused.")]
    public static ScenarioComparison ScheduleScenarios(
        [Description("Document handle from project_open.")] string handle,
        [Description("The scenarios to compare (1-8).")] Scenario[] scenarios)
    {
        return SessionStore.Use(handle, session =>
        {
            if (scenarios.Length is 0 or > 8)
            {
                throw new McpToolException("Pass between 1 and 8 scenarios.");
            }

            // Every alternative is measured against the same schedule, scheduled by the same engine
            // — otherwise the differences would be the engine's, not the scenario's.
            var reference = MpxjBackend.Clone(session.File);
            var engine = Scheduler.Run(reference).Engine;
            var current = Measure("current", reference, null, 0, Array.Empty<RejectedWrite>());

            var outcomes = new List<ScenarioOutcome>();
            foreach (var scenario in scenarios)
            {
                var copy = MpxjBackend.Clone(session.File);
                var rejected = new List<RejectedWrite>();
                var pending = new List<Writes.PendingOp>();
                pending.AddRange(WriteTools.ApplyCalendarOps(copy, scenario.Calendars ?? Array.Empty<CalendarOp>(), rejected));
                pending.AddRange(WriteTools.ApplyResourceOps(copy, scenario.Resources ?? Array.Empty<ResourceOp>(), rejected));
                pending.AddRange(WriteTools.ApplyTaskOps(copy, scenario.Tasks ?? Array.Empty<TaskOp>(), rejected));
                pending.AddRange(WriteTools.ApplyLinkOps(copy, scenario.Links ?? Array.Empty<LinkOp>(), rejected));

                var applied = 0;
                foreach (var op in pending.Where(p => p.Checks.Count > 0))
                {
                    var failures = op.Checks.Select(check => check(copy)).Where(f => f is not null).ToList();
                    if (failures.Count == 0)
                    {
                        applied++;
                    }
                    else
                    {
                        rejected.AddRange(failures!);
                        op.Rollback?.Invoke();
                    }
                }

                Scheduler.Run(copy);
                outcomes.Add(Measure(scenario.Name, copy, current, applied, rejected));
            }

            return new ScenarioComparison
            {
                Engine = engine,
                Current = current,
                Scenarios = outcomes,
                Notes = new[]
                {
                    "Nothing was committed. Apply a scenario with the write tools, passing the same operations.",
                    "Cost is the sum of detail-task cost as the schedule computes it: resource rates, fixed costs "
                    + "and materials. Overtime is not priced unless the resources carry an overtime rate.",
                },
            };
        });
    }

    private static ScenarioOutcome Measure(
        string name, ProjectFile project, ScenarioOutcome? against, int applied, IReadOnlyList<RejectedWrite> rejected)
    {
        var finish = MpxjBackend.ProjectFinish(project);
        var leaves = ScheduleAnalyzer.Leaves(project).ToList();
        var cost = Math.Round(leaves.Sum(Writes.Costs.Of), 2);
        var windows = ResourceAnalyzer.Find(project);
        var calendar = new WorkingCalendar(project);
        double? delta = null;
        if (against?.Finish is { } before && finish is not null)
        {
            delta = Math.Round(calendar.WorkingDaysBetween(DateTime.Parse(before, System.Globalization.CultureInfo.InvariantCulture), finish.Value), 1);
        }

        return new ScenarioOutcome
        {
            Name = name,
            Finish = MpxjMapper.Iso(finish),
            FinishDeltaDays = delta,
            Cost = cost,
            CostDelta = against is null ? 0 : Math.Round(cost - against.Cost, 2),
            OverallocationWindows = windows.Count,
            PeakLoadPercentOfCapacity = windows.Count == 0 ? 0 : Math.Round(windows.Max(w => w.PeakUnits / Math.Max(w.MaxUnits, 1) * 100), 1),
            CriticalTasks = leaves.Count(t => t.Critical),
            Applied = applied,
            Rejected = rejected,
        };
    }
}
