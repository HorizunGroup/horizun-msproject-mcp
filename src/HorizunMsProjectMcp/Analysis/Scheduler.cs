using Horizun.ProjectMcp.Diagnostics;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Analysis;

/// <summary>
/// Decides who computes the dates. Where Microsoft Project is installed, Project does — exactly,
/// because it is Project. Everywhere else the internal critical-path engine does, and says so.
/// </summary>
/// <remarks>
/// The internal engine agreed with Microsoft Project to the day on 100%, 69%, 23% and 23% of tasks
/// across four real schedules: it does not implement task types, effort-driven scheduling, resource
/// calendars, manual or split tasks. Routed through Project, the same schedules — and three more —
/// agree on every task. So when Project is present it is used, and when it is present but fails, the
/// failure is reported rather than quietly replaced by dates that would differ from Project's.
/// <para>
/// <c>HORIZUN_MSPROJECT_ENGINE</c> overrides the choice: <c>project</c>, <c>internal</c>, or
/// <c>auto</c> (the default).
/// </para>
/// </remarks>
public static class Scheduler
{
    public const string MicrosoftProject = "microsoft-project";
    public const string Internal = "internal-cpm";

    private static readonly Lazy<bool> ProjectInstalled = new(() => ComProbe.Inspect().Available);

    /// <summary>The engine <see cref="Run"/> will use.</summary>
    public static string Engine
    {
        get
        {
            var requested = Environment.GetEnvironmentVariable("HORIZUN_MSPROJECT_ENGINE")?.Trim().ToLowerInvariant();
            return requested switch
            {
                "internal" or "cpm" => Internal,
                "project" or "microsoft-project" => MicrosoftProject,
                _ => ProjectInstalled.Value ? MicrosoftProject : Internal,
            };
        }
    }

    /// <summary>True when dates come from Microsoft Project itself, so rescheduling an imported
    /// schedule cannot put it on anybody else's arithmetic.</summary>
    public static bool UsesProject => Engine == MicrosoftProject;

    public static ScheduleRunReport Run(ProjectFile project)
    {
        var report = UsesProject ? ProjectScheduler.Run(project) : CpmScheduler.Run(project);

        // A summary's progress is its detail tasks' actual over their duration, laid over its own
        // span — and the span is only known once the schedule has been calculated.
        Writes.ProgressRules.RollUp(project);
        return report;
    }
}
