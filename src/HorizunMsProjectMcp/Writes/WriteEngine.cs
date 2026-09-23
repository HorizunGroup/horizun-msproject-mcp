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

    /// <summary>The operation scheduled the document itself (recalculate, reschedule_incomplete).
    /// A batch made only of these needs no second pass — which, through Microsoft Project, would
    /// double the time for nothing.</summary>
    public bool SchedulesItself { get; init; }
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

        // On a dry run the copy is disposable, so bring it onto our engine's own baseline first.
        // Otherwise the diff would be dominated by our engine disagreeing with Microsoft Project's
        // stored dates rather than by the change being simulated.
        if (dryRun && !session.MayReschedule && !Analysis.Scheduler.UsesProject)
        {
            Analysis.CpmScheduler.Run(target);
        }

        var before = Snapshot.Capture(target);

        var rejected = new List<RejectedWrite>();
        var pending = apply(target, rejected);

        // The contract: re-read every intended change and only then count it. An operation that
        // half-landed is a rejected operation, not a partial success.
        var applied = 0;
        var fieldsVerified = 0;
        var appliedOps = new List<PendingOp>();

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
                appliedOps.Add(op);
            }
            else
            {
                rejected.AddRange(opFailures);
            }
        }

        // Reschedule before measuring, so the impact shows the downstream movement rather than
        // only the fields that were touched. On an imported schedule the live document is left
        // exactly as Microsoft Project computed it — see ProjectSession.Authored.
        var rescheduled = false;
        string? engine = null;
        var selfScheduled = appliedOps.Count > 0 && appliedOps.All(op => op.SchedulesItself);
        if (selfScheduled)
        {
            rescheduled = true;
            engine = Analysis.Scheduler.Engine;
        }
        else if (applied > 0 && (dryRun || session.MayReschedule))
        {
            engine = Analysis.Scheduler.Run(target).Engine;
            rescheduled = true;

            // Microsoft Project applies its own rules when it calculates: a start typed onto a task
            // with predecessors, a duration its assignment contradicts, a finish on an auto-scheduled
            // task. Checked only before the calculation, such a write would be reported as applied
            // and then quietly undone. So every applied operation is checked again against what
            // Project left, and one it undid is reported as rejected, with the reason.
            if (engine == Analysis.Scheduler.MicrosoftProject)
            {
                foreach (var op in appliedOps)
                {
                    var undone = op.Checks.Select(check => check(target)).Where(f => f is not null).ToList();
                    if (undone.Count == 0)
                    {
                        continue;
                    }

                    applied--;
                    fieldsVerified -= op.Checks.Count;
                    rejected.AddRange(undone.Select(f => f! with
                    {
                        Reason = "Microsoft Project recalculated this back when it scheduled the change. "
                                 + f!.Reason,
                    }));
                }
            }
        }

        var after = Snapshot.Capture(target);

        // Mark the document dirty whenever the model was touched at all — not only when every
        // check passed. A batch that mutated something and then failed verification still leaves
        // the file different from what is on disk, and a clean flag would make the next write trip
        // the changed-on-disk guard with an error about the wrong problem.
        if (!dryRun && (applied > 0 || rejected.Count > 0))
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
            Notes = BuildNotes(dryRun, rescheduled, applied, session, engine),
        };
    }

    private static string[] BuildNotes(bool dryRun, bool rescheduled, int applied, ProjectSession session, string? engine)
    {
        var notes = new List<string>();

        if (dryRun)
        {
            notes.Add(
                "Dry run: applied to a throwaway copy of the schedule, measured, and discarded. "
                + "Nothing was written to the open document.");
        }

        if (rescheduled && engine == Analysis.Scheduler.MicrosoftProject)
        {
            notes.Add("Dates were calculated by Microsoft Project itself, so they are the dates Project shows.");
        }
        else if (rescheduled && dryRun && !session.MayReschedule)
        {
            notes.Add(
                "The impact is measured against this server's own critical-path engine on both "
                + "sides, so the movement shown is caused by your change. The absolute dates it "
                + "would produce differ from Microsoft Project's on an imported schedule — install "
                + "Microsoft Project on this machine to have it calculate them instead.");
        }
        else if (!dryRun && applied > 0 && !rescheduled)
        {
            notes.Add(
                "Dates were not recalculated. This is an imported schedule and Microsoft Project is not "
                + "available here, so its dates remain the ones Project computed; Project will reschedule "
                + "when it next opens the file. Only the fields written are reflected here. Call "
                + "schedule_update with op='recalculate' to use this server's engine instead.");
        }

        return notes.ToArray();
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
