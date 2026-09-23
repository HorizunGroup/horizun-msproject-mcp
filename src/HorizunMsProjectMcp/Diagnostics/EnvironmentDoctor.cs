using System.Runtime.InteropServices;

namespace Horizun.ProjectMcp.Diagnostics;

/// <summary>Which adapter is serving requests, and what it can do.</summary>
public enum Backend
{
    /// <summary>MPXJ via IKVM. No JVM, no Microsoft Project, no licence. The default.</summary>
    Mpxj,

    /// <summary>Microsoft Project driven over COM. Native .mpp writes and the real scheduling engine.</summary>
    Com,
}

public sealed record HealthReport
{
    public required string Server { get; init; }
    public required string Version { get; init; }
    public required string Backend { get; init; }

    /// <summary>Who computes dates: "microsoft-project" (Project itself, exact) or "internal-cpm".</summary>
    public required string SchedulingEngine { get; init; }
    public required RuntimeInfo Runtime { get; init; }
    public required SessionInfo Sessions { get; init; }
    public required ComProbeResult MicrosoftProject { get; init; }
    public required IReadOnlyDictionary<string, bool> Capabilities { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>What is open and what it is costing, so housekeeping is visible before it bites.</summary>
public sealed record SessionInfo
{
    public required int Open { get; init; }
    public required int Limit { get; init; }
    public required int MemoryMb { get; init; }
    public required IReadOnlyList<string> Documents { get; init; }
}

public sealed record RuntimeInfo
{
    public required string Framework { get; init; }
    public required string OperatingSystem { get; init; }
    public required string Architecture { get; init; }
    public required bool Elevated { get; init; }
}

/// <summary>
/// Answers "what can this server actually do on this machine, right now" — the first call
/// of every session.
/// </summary>
/// <remarks>
/// The contract this type exists to enforce: a capability is reported as available only when
/// the thing backing it was checked. Where a capability depends on COM and COM does not start,
/// it reports <c>false</c> with the reason attached, rather than being advertised and then
/// failing at the call site.
/// </remarks>
public static class EnvironmentDoctor
{
    public const string ServerName = "horizun-project-mcp";
    public const string ServerVersion = "0.1.0";

    public static HealthReport Run(bool deep)
    {
        var project = deep ? ComProbe.Launch() : ComProbe.Inspect();

        // COM is the accelerator, not the floor: it is selected only when it has been
        // proven to start, which means only under a deep probe.
        var backend = project is { Available: true, Launched: true } ? Backend.Com : Backend.Mpxj;

        return new HealthReport
        {
            Server = ServerName,
            Version = ServerVersion,
            Backend = backend.ToString().ToLowerInvariant(),
            SchedulingEngine = Analysis.Scheduler.Engine,
            Runtime = DescribeRuntime(),
            Sessions = DescribeSessions(),
            MicrosoftProject = project,
            Capabilities = DescribeCapabilities(backend, project),
            Notes = BuildNotes(backend, project, deep),
        };
    }

    private static SessionInfo DescribeSessions()
    {
        var open = Backends.SessionStore.All();
        return new SessionInfo
        {
            Open = open.Count,
            Limit = Backends.SessionStore.MaxOpenDocuments,
            MemoryMb = (int)(System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024),
            Documents = open.Select(s => $"{s.Handle}: {System.IO.Path.GetFileName(s.Path)}").ToList(),
        };
    }

    private static RuntimeInfo DescribeRuntime() => new()
    {
        Framework = RuntimeInformation.FrameworkDescription,
        OperatingSystem = RuntimeInformation.OSDescription,
        Architecture = RuntimeInformation.OSArchitecture.ToString(),
        Elevated = IsElevated(),
    };

    /// <summary>
    /// The capability matrix. Every entry here is a promise the tools honour — a tool whose
    /// capability is false fails with an explicit message instead of degrading silently.
    /// </summary>
    private static IReadOnlyDictionary<string, bool> DescribeCapabilities(Backend backend, ComProbeResult project)
    {
        var com = backend == Backend.Com;

        // Writing native .mpp only needs Microsoft Project to be registered — the save is delegated
        // to it on demand rather than requiring the whole session to run on the COM backend.
        var canWriteMpp = project.Available;

        return new Dictionary<string, bool>
        {
            // Both backends.
            ["read_schedule"] = true,
            ["read_multiformat"] = true,   // .mpp, .xml, .mpx, .xer, .pmxml — MPXJ reads them all
            ["write_mspdi"] = true,        // .xml that Microsoft Project opens natively
            ["schedule_qa"] = true,        // DCMA-14 runs on our own analysis
            ["timephased"] = true,         // spread computed from the schedule, not read from a contour

            // Served by Microsoft Project where it is installed, by the internal engine elsewhere —
            // so they hold everywhere, and schedulingEngine says which answered.
            ["recalculate"] = true,
            ["dry_run_simulation"] = true, // a real copy-apply-recalc-diff, not a static guess
            ["reschedule_incomplete"] = true,
            ["critical_path_test"] = true, // DCMA check 12 needs an engine to inject a delay

            // Still COM only: nothing else can author the binary format, and resource levelling
            // is Microsoft Project's own heuristic rather than a published algorithm.
            ["write_native_mpp"] = canWriteMpp,
            ["level_resources"] = com,
            ["native_engine"] = Analysis.Scheduler.UsesProject, // dates are Project's own, not an imitation
        };
    }

    private static List<string> BuildNotes(Backend backend, ComProbeResult project, bool deep)
    {
        var notes = new List<string>();

        if (Analysis.Scheduler.UsesProject)
        {
            notes.Add(
                "Dates are calculated by Microsoft Project itself: recalculations, rescheduling after a "
                + "write, dry runs, recovery options, target dates and the DCMA Critical Path Test all "
                + "hand the schedule to Project and take back the dates it computes. They are the dates "
                + "Project shows. If you have Project open, it is used as-is: your window stays visible, "
                + "your documents are never recalculated or closed, and only a temporary copy is touched.");
        }
        else
        {
            notes.Add(
                "Dates, float and the critical path are computed by this server's own critical-path "
                + "engine, because Microsoft Project is not available here. It is not Project's scheduler: "
                + "on imported schedules that use task types, effort-driven or resource-driven dates, "
                + "manual or split tasks, its dates can differ. Install Microsoft Project on this machine "
                + "to have Project calculate them instead.");
        }

        if (!deep && project.Available)
        {
            notes.Add(
                "Microsoft Project is registered but was not launched. A valid registration does not "
                + "prove the COM server starts — call project_health with deep=true to find out.");
        }

        if (!project.Available && project.Diagnosis is not null)
        {
            notes.Add(project.Diagnosis);
        }

        if (backend == Backend.Mpxj)
        {
            notes.Add(
                "Two capabilities stay false here and will refuse rather than approximate: writing the "
                + "native binary .mpp (only Microsoft Project can author it) and resource levelling "
                + "(its heuristic is unpublished, so any imitation would be a different answer wearing "
                + "the same name).");
        }

        return notes;
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
