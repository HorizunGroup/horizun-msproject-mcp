using System.ComponentModel;
using Horizun.ProjectMcp.Analysis;
using Horizun.ProjectMcp.Backends;
using Horizun.ProjectMcp.Diagnostics;
using ModelContextProtocol.Server;

namespace Horizun.ProjectMcp.Tools;

[McpServerToolType]
public static class AnalysisTools
{
    [McpServerTool(Name = "schedule_analyze")]
    [Description(
        "Answer questions about the schedule without downloading it. Computes the critical path, the "
        + "float distribution, the driving (longest) path, day-by-day resource overallocation, milestone "
        + "status against deadlines and baseline, and the health of the dependency network. "
        + "PMO control: status_report is the period report (planned vs earned %, SPI/CPI, earned-schedule "
        + "SPI(t), EAC by three methods, TCPI, forecast finish, milestones at risk, trend of past cut-offs, "
        + "health); lookahead is the short-interval (Last Planner) plan for the next weeks with each task's "
        + "constraints and ready/constrained state, plus PPC of the last recorded commitment; change_log "
        + "lists every rebaseline, budget load and committed edit with reason, approver and finish impact. "
        + "Ask for only the aspects you need — each one is computed independently.")]
    public static ScheduleAnalysis ScheduleAnalyze(
        [Description("Document handle from project_open.")] string handle,
        [Description(
            "Which analyses to run: critical_path, float_distribution, longest_path, overallocation, "
            + "milestones, dependency_health, status_report, lookahead, change_log. Omit for the first six "
            + "except longest_path.")]
        string[]? aspects = null,
        [Description("Status date for status_report and lookahead, yyyy-MM-dd. Defaults to the project's status date, then today.")]
        string? statusDate = null,
        [Description("Look-ahead window in weeks (1-8). Defaults to 3.")]
        int weeks = 3,
        [Description("lookahead: record the first week's ready tasks as the team's commitment, so the next report measures PPC against it.")]
        bool commit = false,
        [Description("status_report: keep this cut-off in the project's history (.hzpmo.json beside the file) for the trend.")]
        bool record = false,
        [Description("lookahead: text field holding who answers for each task, e.g. 'Text2'. Without it, the crews assigned.")]
        string? ownerField = null,
        [Description("lookahead: text field listing constraints logic cannot see, e.g. 'Text3' holding "
                     + "'material: acero; permiso: municipal'. Items starting 'ok' or saying 'listo'/'liberado' count as released.")]
        string? constraintsField = null)
    {
        return SessionStore.Use(handle, session =>
        {
            var list = aspects ?? Array.Empty<string>();
            var want = list.Select(a => a.Trim().ToLowerInvariant()).ToHashSet();
            var classic = list.Where(a => a.Trim().ToLowerInvariant() is not ("status_report" or "lookahead" or "change_log")).ToList();
            var result = list.Length == 0 || classic.Count > 0
                ? ScheduleAnalyzer.Analyze(session.File, classic)
                : new ScheduleAnalysis();
            var effective = QueryTools.ParseDate(statusDate)
                            ?? session.File.ProjectProperties.StatusDate
                            ?? DateTime.Today;
            return result with
            {
                StatusReport = want.Contains("status_report")
                    ? PmoControl.Status(session.File, effective, session.Path, record) : null,
                Lookahead = want.Contains("lookahead")
                    ? PmoControl.Lookahead(session.File, effective, Math.Clamp(weeks, 1, 8), session.Path, commit, ownerField, constraintsField) : null,
                ChangeLog = want.Contains("change_log") && session.Path is not null
                    ? PmoLedger.Load(session.Path).Changes : null,
            };
        });
    }

    [McpServerTool(Name = "schedule_risk")]
    [Description(
        "Quantitative schedule risk analysis (Monte Carlo). Samples every remaining duration from a "
        + "three-point range and re-runs the network logic (all four link types and lags) thousands of "
        + "times, then reports the finish date at P10/P50/P80/P90, the probability of meeting a target "
        + "and the baseline finish, the contingency needed for P80, and the risk drivers — how often "
        + "each task is critical (criticality) and how strongly it moves the finish (sensitivity). "
        + "Give per-task ranges from the team where you have them; the rest get the default spread.")]
    public static RiskReport ScheduleRiskTool(
        [Description("Document handle from project_open.")] string handle,
        [Description("Number of simulations (100-20000). Defaults to 2000.")] int iterations = 2000,
        [Description("Default optimistic deviation of remaining duration, percent (negative). Defaults to -10.")]
        double optimisticPct = -10,
        [Description("Default pessimistic deviation of remaining duration, percent. Defaults to 30.")]
        double pessimisticPct = 30,
        [Description("Per-task three-point ranges in working days, keyed by UniqueID: {\"12\": {\"optimistic\":4, \"mostLikely\":5, \"pessimistic\":9}}.")]
        Dictionary<string, RiskRange>? ranges = null,
        [Description("Target finish date to test, yyyy-MM-dd.")] string? target = null,
        [Description("triangular (default) or pert.")] string distribution = "triangular",
        [Description("Random seed, for a repeatable run.")] int? seed = null,
        [Description("Data date the simulation starts from, yyyy-MM-dd. Defaults to the status date, then the project start.")]
        string? statusDate = null)
    {
        return SessionStore.Use(handle, session =>
        {
            var dataDate = QueryTools.ParseDate(statusDate)
                           ?? session.File.ProjectProperties.StatusDate
                           ?? session.File.ProjectProperties.StartDate
                           ?? DateTime.Today;
            var parsed = ranges?
                .Where(kv => int.TryParse(kv.Key, out _))
                .ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
            return ScheduleRisk.Analyse(session.File, dataDate, iterations, optimisticPct, pessimisticPct,
                parsed, QueryTools.ParseDate(target), distribution, seed);
        });
    }

    [McpServerTool(Name = "schedule_qa")]
    [Description(
        "Run the DCMA 14-point schedule assessment plus Horizun's own rules, and report what is wrong "
        + "with the schedule. This is the industry standard for judging whether a schedule can be run "
        + "on: missing logic, leads and lags, hard constraints, high float and duration, negative float, "
        + "invalid dates, unresourced work, missed baseline tasks, CPLI and BEI. "
        + "Checks that genuinely cannot be evaluated (no baseline stored, no scheduling engine) are "
        + "reported as not evaluated with the reason — never as a pass.")]
    public static QaReport ScheduleQa(
        [Description("Document handle from project_open.")] string handle,
        [Description("Status date for the progress checks, yyyy-MM-dd. Defaults to the project's own status date, then today.")]
        string? statusDate = null,
        [Description(
            "Custom field carrying the budget code, e.g. 'Text1'. Supply it to also check that every "
            + "task is tied to the cost model — the same code the BIM tools match on.")]
        string? budgetCodeField = null,
        [Description("Uids of tasks whose incoming lags or leads are justified (curing, a requested fast-track): "
                     + "left out of the lag and lead checks, and listed in the notes.")]
        int[]? justifiedLags = null,
        [Description("The finish CPLI (check 13) is measured against, yyyy-MM-dd. Defaults to a deadline or "
                     + "finish constraint on the finish milestone, then the baseline finish.")]
        string? targetFinish = null)
    {
        return SessionStore.Use(handle, session =>
        {
            var effective = QueryTools.ParseDate(statusDate)
                            ?? session.File.ProjectProperties.StatusDate
                            ?? DateTime.Today;

            var canRecalculate = EnvironmentDoctor.Run(deep: false).Capabilities
                .TryGetValue("recalculate", out var recalc) && recalc;

            return Dcma14.Run(session.File, effective, canRecalculate, budgetCodeField, justifiedLags,
                QueryTools.ParseDate(targetFinish));
    });
    }

    [McpServerTool(Name = "baseline_compare")]
    [Description(
        "Earned-value analysis against a stored baseline: BCWS, BCWP, ACWP, schedule and cost variance, "
        + "SPI, CPI, EAC and TCPI, for the whole project and optionally per top-level WBS branch, plus "
        + "the tasks with the worst slippage. This is the number a project board asks for and what feeds "
        + "the S-curve. Says so plainly when no baseline is stored rather than returning zeros that look real.")]
    public static BaselineComparison BaselineCompare(
        [Description("Document handle from project_open.")] string handle,
        [Description("Status date, yyyy-MM-dd. Defaults to the project's own status date, then today.")]
        string? statusDate = null,
        [Description("Which baseline to measure against: 0 (the main one, default) through 10.")]
        int baseline = 0,
        [Description("Also break the metrics down by top-level WBS branch. Defaults to true.")]
        bool byBranch = true)
    {
        return SessionStore.Use(handle, session =>
        {
            var effective = QueryTools.ParseDate(statusDate)
                            ?? session.File.ProjectProperties.StatusDate
                            ?? DateTime.Today;

            if (baseline is < 0 or > 10)
            {
                throw new McpToolException("Baseline must be between 0 and 10.");
            }

            return EarnedValue.Compare(session.File, effective, baseline, byBranch);
    });
    }
}
