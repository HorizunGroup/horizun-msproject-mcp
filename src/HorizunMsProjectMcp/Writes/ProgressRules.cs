using Horizun.ProjectMcp.Backends;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Writes;

/// <summary>
/// Microsoft Project's arithmetic for progress, applied when a write changes it — so a task edited
/// here carries the same percent complete, actual and remaining duration as the same edit made in
/// Project.
/// </summary>
/// <remarks>
/// These are applied at write time rather than read back from Project's calculation, and that is
/// deliberate. Measured on a real schedule: Project re-importing an in-progress task from XML — even
/// its own export — puts part of the work already done back into remaining, so a task 50% done
/// came back 44% done and the error rolled up into every summary above it. The dates survive that
/// trip; progress does not. So progress is kept where the schedule already had it, and when a write
/// moves it, it moves by Project's rules:
/// <list type="bullet">
/// <item>A new duration on a started task keeps the work already done: actual stays, remaining is
/// the rest, percent is actual over duration.</item>
/// <item>A new percent complete sets actual to that share of the duration, starts the task if it had
/// not started, finishes it at 100%, and un-finishes it below.</item>
/// </list>
/// Durations are converted with the schedule's hours-per-day setting, which is how Project reads
/// "days" — not the calendar's working hours, which only decide how the time is laid out.
/// </remarks>
public static class ProgressRules
{
    private static readonly object Marker = new();

    /// <summary>Assignments resized by an edit and not yet recalculated by Microsoft Project.</summary>
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ResourceAssignment, object> Resized = new();

    public static void DurationChanged(ProjectFile project, MPXJ.Net.Task task, MPXJ.Net.Duration? before)
    {
        var hours = HoursPerDay(project);
        ResizeAssignments(task, hours);

        var percent = task.PercentageComplete ?? 0;
        var started = task.ActualStart is not null || percent > 0;
        var duration = MpxjMapper.Days(task.Duration, hours) ?? 0;
        if (task.Summary || duration < 0)
        {
            return;
        }

        if (!started)
        {
            // Nothing done yet, so all of it remains — otherwise the old remaining duration stays
            // behind and says the task is smaller than it now is.
            Set(task, hours, duration, 0, 0);
            return;
        }

        if (duration == 0)
        {
            return;
        }

        var actual = MpxjMapper.Days(task.ActualDuration, hours)
                     ?? percent / 100.0 * (MpxjMapper.Days(before, hours) ?? duration);
        actual = Math.Min(actual, duration);

        Set(task, hours, duration, actual);
    }

    /// <summary>
    /// A new duration resizes the task's resource assignments by the task type, as in Project: fixed
    /// units and fixed duration keep the units, so work grows or shrinks with the duration; fixed work
    /// keeps the work, so the units change instead. Left untouched, Microsoft Project would read the
    /// old work back as the truth and recompute the old duration from it — which is how a 600-day
    /// delay injected into a resourced task once vanished without a trace.
    /// </summary>
    private static void ResizeAssignments(MPXJ.Net.Task task, double hoursPerDay)
    {
        var days = MpxjMapper.Days(task.Duration, hoursPerDay) ?? 0;
        if (days <= 0 || task.Summary)
        {
            return;
        }

        var fixedWork = task.Type == TaskType.FixedWork;
        foreach (var assignment in task.ResourceAssignments)
        {
            if (assignment.Resource is null)
            {
                continue; // the placeholder; ProjectHandoff drops it when it no longer fits
            }

            // Where the work ends is Project's to work out from the new size. Handed over with its
            // old finish, the assignment is what Project believes: it recomputed a 600-day task back
            // to its original 5 days to make it fit. MPXJ rebuilds the finish on export, so the
            // hand-off removes it instead, for the assignments marked here.
            Resized.AddOrUpdate(assignment, Marker);

            var actual = MpxjMapper.Hours(assignment.ActualWork, hoursPerDay) ?? 0;
            if (fixedWork)
            {
                var work = MpxjMapper.Hours(assignment.Work, hoursPerDay) ?? 0;
                if (work > 0)
                {
                    assignment.Units = Math.Round(work / (days * hoursPerDay) * 100, 2);
                }

                continue;
            }

            var units = Convert.ToDouble(assignment.Units ?? 100) / 100.0;
            var total = days * hoursPerDay * units;
            assignment.Work = MPXJ.Net.Duration.GetInstance(Math.Round(total * 60, 1), TimeUnit.Minutes);
            assignment.RemainingWork = MPXJ.Net.Duration.GetInstance(
                Math.Round(Math.Max(0, total - actual) * 60, 1), TimeUnit.Minutes);
        }
    }

    public static void PercentChanged(ProjectFile project, MPXJ.Net.Task task)
    {
        if (task.Summary)
        {
            return;
        }

        var hours = HoursPerDay(project);
        var percent = Math.Clamp(task.PercentageComplete ?? 0, 0, 100);
        var duration = MpxjMapper.Days(task.Duration, hours) ?? 0;

        Set(task, hours, duration, percent / 100.0 * duration, percent);

        if (percent > 0 && task.ActualStart is null)
        {
            task.ActualStart = task.Start;
        }
        else if (percent == 0)
        {
            task.ActualStart = null;
        }

        task.ActualFinish = percent >= 100 ? task.ActualFinish ?? task.Finish : null;
    }

    /// <summary>
    /// Rolls progress up to every summary the way Project does: the actual duration of all the detail
    /// tasks beneath it over their total duration. Inferred from real schedules rather than assumed —
    /// it reproduced Project's figure on 11 of 11 summaries, where using only the immediate children
    /// got 8 — and needed because MPXJ does not roll anything up by itself, so a summary would otherwise
    /// keep showing the percentage it had before the edit.
    /// </summary>
    public static void RollUp(ProjectFile project)
    {
        var hours = HoursPerDay(project);
        foreach (var summary in project.Tasks.Where(t => t.Summary && t.UniqueID is not null and not 0))
        {
            var leaves = Leaves(summary).ToList();
            var duration = leaves.Sum(t => MpxjMapper.Days(t.Duration, hours) ?? 0);
            if (duration <= 0)
            {
                continue;
            }

            var fraction = leaves.Sum(t => MpxjMapper.Days(t.ActualDuration, hours) ?? 0) / duration;
            var span = MpxjMapper.Days(summary.Duration, hours) ?? 0;
            Set(summary, hours, span, fraction * span, Math.Round(fraction * 100));

            var started = leaves.Where(t => t.ActualStart is not null).Select(t => t.ActualStart!.Value).ToList();
            summary.ActualStart = started.Count > 0 ? started.Min() : null;
            summary.ActualFinish = leaves.All(t => t.ActualFinish is not null)
                ? leaves.Max(t => t.ActualFinish)
                : null;
        }
    }

    private static IEnumerable<MPXJ.Net.Task> Leaves(MPXJ.Net.Task summary)
    {
        foreach (var child in summary.ChildTasks)
        {
            if (child.Summary)
            {
                foreach (var leaf in Leaves(child))
                {
                    yield return leaf;
                }
            }
            else
            {
                yield return child;
            }
        }
    }

    private static void Set(
        MPXJ.Net.Task task, double hoursPerDay, double duration, double actual, double? percent = null)
    {
        task.ActualDuration = Minutes(actual, hoursPerDay);
        task.RemainingDuration = Minutes(Math.Max(0, duration - actual), hoursPerDay);
        // Project stores percent complete as a whole number and rounds to it.
        task.PercentageComplete = percent ?? (duration > 0 ? Math.Round(actual / duration * 100) : 0);
    }

    /// <summary>Project keeps durations in tenths of a minute; rounding to the same step is what makes
    /// a rolled-up actual duration agree to the second rather than drift by one.</summary>
    private static MPXJ.Net.Duration Minutes(double days, double hoursPerDay) =>
        MPXJ.Net.Duration.GetInstance(Math.Round(days * hoursPerDay * 60, 1), TimeUnit.Minutes);

    private static double HoursPerDay(ProjectFile project)
    {
        var minutes = project.ProjectProperties.MinutesPerDay;
        return minutes is > 0 ? Convert.ToDouble(minutes) / 60.0 : MpxjMapper.HoursPerDay;
    }
}
