using Horizun.ProjectMcp.Analysis;
using Horizun.ProjectMcp.Backends;
using Horizun.ProjectMcp.Model;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Writes;

/// <summary>
/// One requested operation plus the checks that prove it landed. Grouping the checks per operation
/// is what lets "applied" mean "operations that fully succeeded" instead of "fields that happened
/// to stick".
/// </summary>
public sealed class PendingOp
{
    public required string Label { get; init; }
    public List<Func<ProjectFile, RejectedWrite?>> Checks { get; } = new();
}

/// <summary>State captured before a write, so the impact can be measured rather than predicted.</summary>
internal sealed record Snapshot
{
    public required DateTime? ProjectFinish { get; init; }
    public required IReadOnlyDictionary<int, (DateTime? Start, DateTime? Finish)> Dates { get; init; }
    public required IReadOnlySet<int> CriticalUids { get; init; }
    public required int NegativeFloatCount { get; init; }

    public static Snapshot Capture(ProjectFile project)
    {
        var dates = new Dictionary<int, (DateTime?, DateTime?)>();
        var critical = new HashSet<int>();
        var negative = 0;

        foreach (var task in project.Tasks)
        {
            if (task.UniqueID is not { } uid)
            {
                continue;
            }

            dates[uid] = (task.Start, task.Finish);
            if (task.Critical)
            {
                critical.Add(uid);
            }

            if ((MpxjMapper.Days(task.TotalSlack) ?? 0) < -0.001)
            {
                negative++;
            }
        }

        return new Snapshot
        {
            ProjectFinish = MpxjBackend.ProjectFinish(project),
            Dates = dates,
            CriticalUids = critical,
            NegativeFloatCount = negative,
        };
    }
}

/// <summary>
/// Runs a batch of writes under the contract the whole design rests on:
/// nothing is reported as applied unless it was re-read from the model and matched.
/// </summary>
/// <remarks>
/// A dry run is a real simulation, not a prediction — the schedule is deep-copied, the batch is
/// applied to the copy, the impact is measured against it, and the copy is discarded.
/// </remarks>
public static class WriteEngine
{
    /// <summary>
    /// Applies <paramref name="apply"/> and verifies the outcome.
    /// </summary>
    /// <param name="session">Target document. Ignored when <paramref name="dryRun"/> is set.</param>
    /// <param name="dryRun">Run against a throwaway copy and report the impact without committing.</param>
    /// <param name="apply">
    /// Receives the file to mutate and a sink for rejections. Returns one <see cref="PendingOp"/>
    /// per requested operation, each carrying the checks to run afterwards. An operation counts as
    /// applied only when every one of its checks passes.
    /// </param>
    public static WriteResult Run(
        ProjectSession session,
        bool dryRun,
        Func<ProjectFile, List<RejectedWrite>, List<PendingOp>> apply)
    {
        var target = dryRun ? MpxjBackend.Clone(session.File) : session.File;
        var before = Snapshot.Capture(target);

        var rejected = new List<RejectedWrite>();
        var pending = apply(target, rejected);

        // The contract: re-read every intended change and only then count it. An operation that
        // half-landed is a rejected operation, not a partial success.
        var applied = 0;
        var fieldsVerified = 0;

        foreach (var op in pending)
        {
            // An operation that registered no check did not do anything this engine can vouch for
            // (it bailed out early, and said why in `rejected`). Unverified is never applied.
            if (op.Checks.Count == 0)
            {
                continue;
            }

            var opFailures = new List<RejectedWrite>();
            foreach (var check in op.Checks)
            {
                var failure = check(target);
                if (failure is null)
                {
                    fieldsVerified++;
                }
                else
                {
                    opFailures.Add(failure);
                }
            }

            if (opFailures.Count == 0)
            {
                applied++;
            }
            else
            {
                rejected.AddRange(opFailures);
            }
        }

        // Reschedule before measuring. Without this the impact report would only ever show the
        // fields that were touched, never the downstream movement that is the whole reason to ask.
        if (applied > 0)
        {
            Analysis.CpmScheduler.Run(target);
        }

        var after = Snapshot.Capture(target);

        if (!dryRun && applied > 0)
        {
            session.Dirty = true;
        }

        return new WriteResult
        {
            DryRun = dryRun,
            Applied = applied,
            FieldsVerified = fieldsVerified,
            Rejected = rejected,
            Impact = MeasureImpact(before, after),
            Notes = dryRun
                ? new[]
                {
                    "Dry run: applied to a throwaway copy of the schedule, measured, and discarded. "
                    + "Nothing was written to the open document.",
                }
                : Array.Empty<string>(),
        };
    }

    private static ImpactReport MeasureImpact(Snapshot before, Snapshot after)
    {
        var moved = 0;
        foreach (var (uid, afterDates) in after.Dates)
        {
            if (before.Dates.TryGetValue(uid, out var beforeDates) &&
                (beforeDates.Start != afterDates.Start || beforeDates.Finish != afterDates.Finish))
            {
                moved++;
            }
        }

        var delta = before.ProjectFinish is not null && after.ProjectFinish is not null
            ? Math.Round((after.ProjectFinish.Value - before.ProjectFinish.Value).TotalDays, 2)
            : 0;

        return new ImpactReport
        {
            TasksMoved = moved,
            ProjectFinishBefore = MpxjMapper.Iso(before.ProjectFinish),
            ProjectFinishAfter = MpxjMapper.Iso(after.ProjectFinish),
            ProjectFinishDeltaDays = delta,
            CriticalPathChanged = !before.CriticalUids.SetEquals(after.CriticalUids),
            NewNegativeFloat = Math.Max(0, after.NegativeFloatCount - before.NegativeFloatCount),
        };
    }

    /// <summary>
    /// Builds the rejection message for a value the schedule refused to keep. The reason has to
    /// tell the agent what to do differently, not merely that it failed.
    /// </summary>
    public static RejectedWrite Reject(int uid, string field, object? requested, object? actual, string reason) => new()
    {
        Uid = uid,
        Field = field,
        Requested = requested?.ToString(),
        Actual = actual?.ToString(),
        Reason = reason,
    };

    /// <summary>
    /// Explains why a date write did not stick. Almost always the scheduling engine recalculating
    /// over it — the single most common silent failure when driving Microsoft Project.
    /// </summary>
    public static string DateRejectionReason(MPXJ.Net.Task task, string field)
    {
        if (task.Summary)
        {
            return $"'{field}' is rolled up from the child tasks on a summary task and cannot be set directly. "
                   + "Change the children instead.";
        }

        if (task.ConstraintType is not null and not ConstraintType.AsSoonAsPossible and not ConstraintType.AsLateAsPossible)
        {
            return $"'{field}' is governed by the constraint {task.ConstraintType} on this task. "
                   + "Clear or change the constraint first.";
        }

        return $"'{field}' is calculated from the task's duration and its predecessors. "
               + "Change the duration or the logic, or set a constraint, rather than writing the date.";
    }
}
