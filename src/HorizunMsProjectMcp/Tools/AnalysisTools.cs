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
        + "Ask for only the aspects you need — each one is computed independently.")]
    public static ScheduleAnalysis ScheduleAnalyze(
        [Description("Document handle from project_open.")] string handle,
        [Description(
            "Which analyses to run: critical_path, float_distribution, longest_path, overallocation, "
            + "milestones, dependency_health. Omit for all except longest_path.")]
        string[]? aspects = null)
    {
        var session = SessionStore.Get(handle);
        return ScheduleAnalyzer.Analyze(session.File, aspects ?? Array.Empty<string>());
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
        string? budgetCodeField = null)
    {
        var session = SessionStore.Get(handle);
        var effective = QueryTools.ParseDate(statusDate)
                        ?? session.File.ProjectProperties.StatusDate
                        ?? DateTime.Today;

        var canRecalculate = EnvironmentDoctor.Run(deep: false).Capabilities
            .TryGetValue("recalculate", out var recalc) && recalc;

        return Dcma14.Run(session.File, effective, canRecalculate, budgetCodeField);
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
        var session = SessionStore.Get(handle);
        var effective = QueryTools.ParseDate(statusDate)
                        ?? session.File.ProjectProperties.StatusDate
                        ?? DateTime.Today;

        if (baseline is < 0 or > 10)
        {
            throw new McpToolException("Baseline must be between 0 and 10.");
        }

        return EarnedValue.Compare(session.File, effective, baseline, byBranch);
    }
}
