using System.ComponentModel;
using Horizun.ProjectMcp.Analysis;
using Horizun.ProjectMcp.Backends;
using Horizun.ProjectMcp.Model;
using Horizun.ProjectMcp.Writes;
using ModelContextProtocol.Server;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Tools;

public sealed record TaskOp
{
    [Description(
        "create, update, delete, or outline. There is no reorder operation: this backend cannot "
        + "move a task to a different position in the outline, only change its level.")]
    public required string Op { get; init; }

    [Description("Target task uid. Required for update, delete, move, and outline.")]
    public int? Uid { get; init; }

    [Description("For create: place the task after this uid.")]
    public int? AfterUid { get; init; }

    [Description("For create: make the task a child of this uid.")]
    public int? ParentUid { get; init; }

    [Description("For create: a name for this new task within the batch, so later operations in the same "
                 + "batch can put tasks under it with parentKey — a whole outline in one call.")]
    public string? Key { get; init; }

    [Description("For create: make the task a child of the task created earlier in this batch with this key.")]
    public string? ParentKey { get; init; }

    public string? Name { get; init; }

    [Description("Duration with a unit: 5d, 8h, 2w, 0.")]
    public string? Duration { get; init; }

    [Description("yyyy-MM-dd. On an auto-scheduled task the engine may recalculate over it — the result says so.")]
    public string? Start { get; init; }

    public string? Finish { get; init; }
    public double? PercentComplete { get; init; }

    [Description("yyyy-MM-dd HH:mm. 'none' clears it, returning the task to not started (with its actual "
                 + "finish and progress).")]
    public string? ActualStart { get; init; }
    public string? ActualFinish { get; init; }
    public bool? Milestone { get; init; }
    public bool? Active { get; init; }
    public string? Notes { get; init; }
    public string? Deadline { get; init; }

    [Description("AsSoonAsPossible, MustStartOn, FinishNoLaterThan, and so on.")]
    public string? ConstraintType { get; init; }

    public string? ConstraintDate { get; init; }
    public double? Cost { get; init; }

    [Description("For outline: positive indents, negative outdents.")]
    public int? Indent { get; init; }

    [Description("Task calendar, by name. '' removes it, so the task follows the project calendar.")]
    public string? Calendar { get; init; }

    [Description("With a task calendar: schedule on it alone, ignoring the resources' calendars.")]
    public bool? IgnoreResourceCalendar { get; init; }

    [Description("FixedUnits, FixedDuration, or FixedWork.")]
    public string? TaskType { get; init; }

    public bool? EffortDriven { get; init; }

    [Description("Fixed cost of the task, besides its resources.")]
    public double? FixedCost { get; init; }

    [Description("When the fixed cost accrues: Start, End, or Prorated.")]
    public string? FixedCostAccrual { get; init; }

    [Description("Baseline cost to store without touching the baseline dates — the budget that arrives "
                 + "after the baseline was approved. Goes into baselineSlot.")]
    public double? BaselineCost { get; init; }

    [Description("Baseline 0-10 that baselineCost goes into. Defaults to 0.")]
    public int? BaselineSlot { get; init; }

    [Description("Custom fields to set, e.g. {\"Text1\": \"CWA-3\", \"Number1\": \"12.5\", \"Flag1\": \"true\", "
                 + "\"Date1\": \"2026-10-05\", \"WBS\": \"1.2.3\"}. Text1-30, Number1-20, Date1-10, Flag1-20 and WBS; "
                 + "any other key is rejected.")]
    public Dictionary<string, string>? Custom { get; init; }
}

public sealed record LinkOp
{
    [Description("link, unlink, or relink.")]
    public required string Op { get; init; }

    [Description("Predecessor task uid.")]
    public required int From { get; init; }

    [Description("Successor task uid.")]
    public required int To { get; init; }

    [Description("FS (default), SS, FF, or SF.")]
    public string? Type { get; init; }

    [Description("Lag with a unit: 2d, -1d (a lead), 3ed (elapsed days, through weekends — a cure), 50% "
                 + "(half the predecessor's duration), 0.")]
    public string? Lag { get; init; }
}

public sealed record ResourceOp
{
    [Description("create, update, delete, assign, or unassign.")]
    public required string Op { get; init; }

    public int? Uid { get; init; }
    public string? Name { get; init; }

    [Description("Work, Material, or Cost.")]
    public string? Type { get; init; }

    public double? MaxUnits { get; init; }

    [Description("Cost per hour for a work resource, per unit of material for a material one.")]
    public double? StandardRate { get; init; }

    [Description("Overtime cost per hour.")]
    public double? OvertimeRate { get; init; }

    [Description("For a material resource: its unit, e.g. 'm3', 'kg', 'bls'. Assign it with units = the "
                 + "quantity, not a percentage.")]
    public string? MaterialLabel { get; init; }

    [Description("Base calendar the resource works on, by name (its own calendar derives from it).")]
    public string? Calendar { get; init; }

    [Description("For assign and unassign.")]
    public int? TaskUid { get; init; }

    [Description("For assign: percentage of a work resource (e.g. 400 for a crew of four), or the "
                 + "quantity of a material resource in its unit (e.g. 12.5 m3).")]
    public double? Units { get; init; }
}

public sealed record CalendarOp
{
    [Description(
        "create, delete, set_week, set_exception, or set_project_calendar. set_week sets the working "
        + "hours of days of the week (e.g. days='mon-fri', hours='08:00-12:00,13:00-17:00'; days='sat', "
        + "hours='08:00-13:00'; hours='' makes the days non-working). set_exception sets specific dates, "
        + "non-working by default, or working with hours. set_project_calendar makes the named calendar "
        + "the project's.")]
    public required string Op { get; init; }

    [Description("The calendar. Defaults to the project calendar for set_week and set_exception.")]
    public string? Name { get; init; }

    [Description("For create: the calendar to copy the week and exceptions from. The new calendar is always "
                 + "a base calendar, usable for tasks and the project. Without it, the standard week.")]
    public string? BasedOn { get; init; }

    [Description("For set_week: mon-sun or lun-dom, ranges and lists, e.g. 'mon-fri', 'sat', 'lun,mie,vie'.")]
    public string? Days { get; init; }

    [Description("Working hours, e.g. '08:00-12:00,13:00-17:00'. Empty means not worked.")]
    public string? Hours { get; init; }

    [Description("Exception start, yyyy-MM-dd.")]
    public string? From { get; init; }

    [Description("Exception end, yyyy-MM-dd. Defaults to From.")]
    public string? To { get; init; }

    [Description("Whether the exception days are working days. A working exception takes hours "
                 + "(default 08:00-12:00,13:00-17:00).")]
    public bool? Working { get; init; }
}

[McpServerToolType]
public static class WriteTools
{
    /// <summary>Stated in full on tasks_write; the other write tools point at it.</summary>
    private const string VerifiedContract =
        "Every change is re-read from the model after the write and only then counted as applied; "
        + "anything the schedule refused comes back under 'rejected' with the reason and what the value "
        + "actually became. Pass dryRun=true to run the whole batch against a throwaway copy and see the "
        + "impact — how many tasks move, how far the finish date shifts, whether the critical path changes "
        + "— without touching the open document.";

    private const string SameContract =
        "Verified and dry-runnable exactly as tasks_write describes.";

    [McpServerTool(Name = "tasks_write")]
    [Description(
        "Create, update, delete and re-outline tasks in one batch. Progress (percentComplete, "
        + "actual dates) is an ordinary update. Tasks are addressed by their stable uid — never by row id. "
        + VerifiedContract)]
    public static WriteResult TasksWrite(
        [Description("Document handle from project_open.")] string handle,
        [Description("The operations to apply, in order.")] TaskOp[] ops,
        [Description("Simulate against a copy and report the impact without committing.")] bool dryRun = false)
    {
        return SessionStore.UseForWrite(handle, session =>
        {
            if (ops.Length == 0)
            {
                throw new McpToolException("No operations supplied.");
            }

            return WriteEngine.Run(session, dryRun, (project, rejected) => ApplyTaskOps(project, ops, rejected));
    });
    }

    /// <summary>
    /// Gives a newly added task a unique id when the library did not.
    /// </summary>
    /// <remarks>
    /// MPXJ assigns one automatically in a project built from scratch but not in one read from a
    /// file, so a task added to an existing schedule came out with no identity: invisible to every
    /// query, unaddressable by any write, and silently changing the task count. Adding work to a
    /// schedule somebody sent you is a core use case, and it was broken.
    /// </remarks>
    private static void EnsureUniqueId(ProjectFile project, MPXJ.Net.Task task)
    {
        if (task.UniqueID is > 0)
        {
            return;
        }

        var next = project.Tasks.Select(t => t.UniqueID ?? 0).DefaultIfEmpty(0).Max() + 1;
        task.UniqueID = next;

        if (task.ID is null)
        {
            task.ID = project.Tasks.Select(t => t.ID ?? 0).DefaultIfEmpty(0).Max() + 1;
        }
    }

    /// <summary>Max units, rates, material unit and calendar, each verified on its own.</summary>
    /// <remarks>
    /// Max units live in the availability table as well as in the field: Microsoft Project and MPXJ
    /// both read the table, so writing only the field changed nothing either would show. Rates live in
    /// the cost rate table, which is what the file carries — standardRate used to be accepted and
    /// dropped.
    /// </remarks>
    private static void ApplyResourceFields(
        ProjectFile project, Resource resource, ResourceOp op, PendingOp pending, List<RejectedWrite> rejected)
    {
        var uid = resource.UniqueID ?? -1;
        void Check(string field, object? expected, Func<Resource, object?> read, string reason) =>
            pending.Checks.Add(file =>
            {
                var check = file.GetResourceByUniqueID(uid);
                var actual = check is null ? null : read(check);
                return Equals(actual?.ToString(), expected?.ToString())
                    ? null
                    : WriteEngine.Reject(uid, field, expected, actual, reason);
            });

        if (op.MaxUnits is not null)
        {
            if (op.MaxUnits <= 0)
            {
                rejected.Add(WriteEngine.Reject(uid, "maxUnits", op.MaxUnits, null, "Max units must be above 0."));
            }
            else
            {
                resource.Set(ResourceField.MaxUnits, op.MaxUnits.Value);
                resource.Availability.Clear();
                // Project's own bounds for "available always" (NA to NA); an open range breaks the MSPDI writer.
                resource.Availability.Add(new Availability(new DateTime(1984, 1, 1), new DateTime(2049, 12, 31, 23, 59, 0), op.MaxUnits.Value));
                Check("maxUnits", op.MaxUnits.Value, r => Analysis.ResourceAnalyzer.MaxUnits(r),
                    "Max units did not survive the write.");
            }
        }

        if (op.StandardRate is not null || op.OvertimeRate is not null)
        {
            var table = resource.GetCostRateTable(0);
            var index = table.Count == 0 ? -1 : Math.Max(0, table.GetIndexByDate(DateTime.Now));
            var entry = index < 0 ? null : table[index];
            const TimeUnit perUnit = TimeUnit.Hours; // a material rate is per unit; Project stores it the same way
            var standard = op.StandardRate is { } sr ? new Rate(sr, perUnit) : entry?.StandardRate ?? new Rate(0, perUnit);
            var overtime = op.OvertimeRate is { } or ? new Rate(or, perUnit) : entry?.OvertimeRate ?? new Rate(0, perUnit);
            var replacement = new CostRateTableEntry(project,
                entry?.StartDate ?? DateTime.MinValue, entry?.EndDate ?? DateTime.MaxValue,
                entry?.CostPerUse, new[] { standard, overtime });
            if (index < 0) table.Add(replacement);
            else table[index] = replacement;

            if (op.StandardRate is not null)
            {
                Check("standardRate", op.StandardRate.Value, r => r.StandardRate?.Amount, "The rate did not survive the write.");
            }

            if (op.OvertimeRate is not null)
            {
                Check("overtimeRate", op.OvertimeRate.Value, r => r.OvertimeRate?.Amount, "The rate did not survive the write.");
            }
        }

        if (op.MaterialLabel is not null)
        {
            if (resource.Type is not ResourceType.Material)
            {
                rejected.Add(WriteEngine.Reject(uid, "materialLabel", op.MaterialLabel, null,
                    "Only a material resource has a unit; create it with type='Material'."));
            }
            else
            {
                resource.Set(ResourceField.MaterialLabel, op.MaterialLabel);
                Check("materialLabel", op.MaterialLabel, r => r.MaterialLabel, "The unit did not survive the write.");
            }
        }

        if (op.Calendar is not null)
        {
            if (project.GetCalendarByName(op.Calendar) is not { Parent: null } basis)
            {
                rejected.Add(WriteEngine.Reject(uid, "calendar", op.Calendar, null,
                    $"No base calendar named '{op.Calendar}'. Create it with calendars_write."));
            }
            else
            {
                var own = resource.Calendar;
                if (own is null || own.Parent is null)
                {
                    own = resource.AddCalendar();
                }

                own.Parent = basis;
                Check("calendar", op.Calendar, r => r.Calendar?.Parent?.Name ?? r.Calendar?.Name,
                    "The resource calendar did not survive the write.");
            }
        }
    }

    /// <summary>
    /// Gives a brand-new task dates when nothing else has.
    /// </summary>
    /// <remarks>
    /// An imported schedule is deliberately never rescheduled, so a task added to one would
    /// otherwise come back with no start and no finish — invisible on a Gantt, useless to every
    /// analysis, and puzzling to whoever added it. This dates the new task alone, on its own
    /// calendar, and touches nothing else in the schedule.
    /// </remarks>
    private static void GiveDatesIfMissing(ProjectFile project, MPXJ.Net.Task task)
    {
        if (task.Start is not null && task.Finish is not null)
        {
            return;
        }

        var calendar = new Analysis.CalendarSet(project).For(task);
        var anchor = task.Start
                     ?? task.ConstraintDate
                     ?? project.ProjectProperties.StatusDate
                     ?? project.ProjectProperties.StartDate
                     ?? DateTime.Today;

        var start = calendar.NextWorkingDay(anchor);
        var days = MpxjMapper.Days(task.Duration, calendar.HoursPerDay) ?? 0;

        // The calendar's own hours: a milestone on a calendar that starts at 07:00 sits at 07:00.
        task.Start = calendar.StartOn(start);
        task.Finish = days <= 0 ? task.Start : calendar.FinishOn(calendar.AddWorkingDays(start, days));
    }

    private static void ApplyFields(
        ProjectFile project, MPXJ.Net.Task task, TaskOp op,
        List<RejectedWrite> rejected, PendingOp pending, bool isNew)
    {
        var uid = task.UniqueID ?? -1;

        if (op.Name is not null && !isNew)
        {
            task.Name = op.Name;
            Verify(pending, uid, "name", op.Name, t => t.Name, "The name did not survive the write.");
        }

        if (op.Duration is not null)
        {
            if (task.Summary)
            {
                rejected.Add(WriteEngine.Reject(uid, "duration", op.Duration, MpxjMapper.DurationText(task.Duration),
                    "Duration on a summary task is rolled up from its children and cannot be set directly."));
            }
            else
            {
                var parsed = MpxjMapper.ParseDuration(op.Duration);
                var previous = task.Duration;
                task.Duration = parsed;
                Writes.ProgressRules.DurationChanged(project, task, previous);
                var expected = MpxjMapper.Days(parsed);
                Verify(pending, uid, "duration", op.Duration,
                    t => MpxjMapper.Days(t.Duration) is { } d && expected is not null
                         && Math.Abs(d - expected.Value) < 0.01 ? op.Duration : MpxjMapper.DurationText(t.Duration),
                    "The duration did not survive the write.");
            }
        }

        if (op.Milestone is not null)
        {
            task.Milestone = op.Milestone.Value;
            Verify(pending, uid, "milestone", op.Milestone, t => t.Milestone,
                "The milestone flag did not survive the write.");
        }

        if (op.Active is not null)
        {
            task.Active = op.Active.Value;
            Verify(pending, uid, "active", op.Active, t => t.Active, "The active flag did not survive the write.");
        }

        if (op.PercentComplete is not null)
        {
            task.PercentageComplete = op.PercentComplete.Value;
            Writes.ProgressRules.PercentChanged(project, task);
            Verify(pending, uid, "percentComplete", op.PercentComplete,
                t => t.PercentageComplete, "Percent complete did not survive the write.");
        }

        if (op.Notes is not null)
        {
            task.Notes = op.Notes;
            Verify(pending, uid, "notes", op.Notes, t => t.Notes, "The notes did not survive the write.");
        }

        if (op.Cost is not null)
        {
            task.Cost = op.Cost.Value;
            Verify(pending, uid, "cost", op.Cost, t => t.Cost,
                "Cost is calculated from resource assignments when any exist; set it on the assignment instead.");
        }

        SetDate(task, op.Start, "start", (t, d) => t.Start = d, t => t.Start, uid, pending, rejected);
        SetDate(task, op.Finish, "finish", (t, d) => t.Finish = d, t => t.Finish, uid, pending, rejected);
        if (op.ActualStart is not null && op.ActualStart.Trim().ToLowerInvariant() is "none" or "na" or "nod" or "")
        {
            // Back to not started: no actual dates, no progress. Microsoft Project's own token for
            // this is locale-bound ("NA", "NOD" in Spanish); here it is simply 'none'.
            task.ActualFinish = null;
            task.ActualStart = null;
            task.PercentageComplete = 0;
            Writes.ProgressRules.PercentChanged(project, task);
            Verify(pending, uid, "actualStart", "none",
                t => t.ActualStart is null && (t.PercentageComplete ?? 0) == 0 ? "none" : MpxjMapper.Iso(t.ActualStart),
                "The task still has an actual start.");
        }
        else
        {
            SetDate(task, op.ActualStart, "actualStart", (t, d) => t.ActualStart = d, t => t.ActualStart, uid, pending, rejected);
        }
        SetDate(task, op.ActualFinish, "actualFinish", (t, d) => t.ActualFinish = d, t => t.ActualFinish, uid, pending, rejected);
        SetDate(task, op.Deadline, "deadline", (t, d) => t.Deadline = d, t => t.Deadline, uid, pending, rejected);
        SetDate(task, op.ConstraintDate, "constraintDate", (t, d) => t.ConstraintDate = d, t => t.ConstraintDate, uid, pending, rejected);

        if (op.ConstraintType is not null)
        {
            if (Enum.TryParse<ConstraintType>(op.ConstraintType, ignoreCase: true, out var parsed))
            {
                task.ConstraintType = parsed;
                Verify(pending, uid, "constraintType", parsed, t => t.ConstraintType,
                    "The constraint type did not survive the write.");
            }
            else
            {
                rejected.Add(WriteEngine.Reject(uid, "constraintType", op.ConstraintType, null,
                    $"Unknown constraint type. Valid values: {string.Join(", ", Enum.GetNames<ConstraintType>())}."));
            }
        }

        if (op.Calendar is not null)
        {
            if (op.Calendar.Length == 0)
            {
                task.Calendar = null;
                Verify(pending, uid, "calendar", "", t => t.Calendar?.Name ?? "", "The task calendar did not clear.");
            }
            else if (project.GetCalendarByName(op.Calendar) is { } resourceOwn
                     && (resourceOwn.Parent is not null || project.Resources.Any(r => r.Calendar == resourceOwn)))
            {
                rejected.Add(WriteEngine.Reject(uid, "calendar", op.Calendar, task.Calendar?.Name,
                    $"'{op.Calendar}' is a resource's calendar; a task calendar has to be a base calendar, or "
                    + "Microsoft Project drops it. Create one with calendars_write op='create' (basedOn copies it)."));
            }
            else if (project.GetCalendarByName(op.Calendar) is { } calendar)
            {
                task.Calendar = calendar;
                Verify(pending, uid, "calendar", op.Calendar, t => t.Calendar?.Name,
                    "The task calendar did not survive the write.");
            }
            else
            {
                rejected.Add(WriteEngine.Reject(uid, "calendar", op.Calendar, task.Calendar?.Name,
                    $"No calendar named '{op.Calendar}'. Create it with calendars_write, or call project_info."));
            }
        }

        if (op.IgnoreResourceCalendar is not null)
        {
            task.IgnoreResourceCalendar = op.IgnoreResourceCalendar.Value;
            Verify(pending, uid, "ignoreResourceCalendar", op.IgnoreResourceCalendar, t => t.IgnoreResourceCalendar,
                "The flag did not survive the write.");
        }

        if (op.TaskType is not null)
        {
            if (Enum.TryParse<TaskType>(op.TaskType, ignoreCase: true, out var type) && type != MPXJ.Net.TaskType.FixedDurationAndUnits)
            {
                task.Type = type;
                Verify(pending, uid, "taskType", type, t => t.Type, "The task type did not survive the write.");
            }
            else
            {
                rejected.Add(WriteEngine.Reject(uid, "taskType", op.TaskType, task.Type,
                    "Task type is FixedUnits, FixedDuration, or FixedWork."));
            }
        }

        if (op.EffortDriven is not null)
        {
            task.EffortDriven = op.EffortDriven.Value;
            Verify(pending, uid, "effortDriven", op.EffortDriven, t => t.EffortDriven, "The flag did not survive the write.");
        }

        if (op.FixedCost is not null)
        {
            task.FixedCost = op.FixedCost.Value;
            Verify(pending, uid, "fixedCost", op.FixedCost, t => t.FixedCost, "The fixed cost did not survive the write.");
        }

        if (op.FixedCostAccrual is not null)
        {
            if (Enum.TryParse<AccrueType>(op.FixedCostAccrual, ignoreCase: true, out var accrual))
            {
                task.FixedCostAccrual = accrual;
                Verify(pending, uid, "fixedCostAccrual", accrual, t => t.FixedCostAccrual,
                    "The accrual did not survive the write.");
            }
            else
            {
                rejected.Add(WriteEngine.Reject(uid, "fixedCostAccrual", op.FixedCostAccrual, task.FixedCostAccrual,
                    "Accrual is Start, End, or Prorated."));
            }
        }

        if (op.BaselineCost is not null)
        {
            var slot = op.BaselineSlot ?? 0;
            if (slot is < 0 or > 10)
            {
                rejected.Add(WriteEngine.Reject(uid, "baselineSlot", slot, null, "Baselines are numbered 0 to 10."));
            }
            else
            {
                if (slot == 0) task.BaselineCost = op.BaselineCost.Value;
                else task.SetBaselineCost(slot, op.BaselineCost.Value);
                Verify(pending, uid, "baselineCost", op.BaselineCost,
                    t => slot == 0 ? t.BaselineCost : t.GetBaselineCost(slot),
                    "The baseline cost did not survive the write.");
            }
        }

        if (op.Custom is not null)
        {
            foreach (var (field, value) in op.Custom)
            {
                if (TaskCustomField.Parse(field) is not { } target)
                {
                    rejected.Add(WriteEngine.Reject(uid, field, value, null,
                        $"'{field}' is not a field this tool writes. Use Text1-30, Number1-20, Date1-10, "
                        + "Flag1-20 or WBS."));
                    continue;
                }

                if (!target.TryConvert(value, out var converted))
                {
                    rejected.Add(WriteEngine.Reject(uid, field, value, null,
                        $"'{value}' is not a valid {target.Kind} value for {field}."));
                    continue;
                }

                try
                {
                    target.Write(task, converted);
                    pending.Checks.Add(file =>
                    {
                        var check = file.GetTaskByUniqueID(uid);
                        var actual = check is null ? null : target.Read(check);
                        return target.Same(converted, actual)
                            ? null
                            : WriteEngine.Reject(uid, field, value, actual,
                                $"{field} did not survive the write.");
                    });
                }
                catch (Exception ex)
                {
                    rejected.Add(WriteEngine.Reject(uid, field, value, null,
                        $"Could not set {field}: {ex.Message}"));
                }
            }
        }
    }

    private static void SetDate(
        MPXJ.Net.Task task, string? raw, string field,
        Action<MPXJ.Net.Task, DateTime?> setter, Func<MPXJ.Net.Task, DateTime?> getter,
        int uid, PendingOp pending, List<RejectedWrite> rejected)
    {
        if (raw is null)
        {
            return;
        }

        var parsed = QueryTools.ParseDate(raw);
        if (parsed is null)
        {
            return;
        }

        if (task.Summary && field is "start" or "finish")
        {
            rejected.Add(WriteEngine.Reject(uid, field, raw, MpxjMapper.Iso(getter(task)),
                WriteEngine.DateRejectionReason(task, field)));
            return;
        }

        var reason = WriteEngine.DateRejectionReason(task, field);
        setter(task, parsed);

        pending.Checks.Add(file =>
        {
            var check = file.GetTaskByUniqueID(uid);
            var actual = check is null ? null : getter(check);
            return actual is not null && Math.Abs((actual.Value - parsed.Value).TotalMinutes) < 1
                ? null
                : WriteEngine.Reject(uid, field, raw, MpxjMapper.Iso(actual), reason);
        });
    }

    private static void Verify<T>(
        PendingOp pending,
        int uid, string field, T expected, Func<MPXJ.Net.Task, object?> read, string reason)
    {
        pending.Checks.Add(file =>
        {
            var task = file.GetTaskByUniqueID(uid);
            if (task is null)
            {
                return WriteEngine.Reject(uid, field, expected, null, "The task vanished during the write.");
            }

            var actual = read(task);
            return Equals(actual?.ToString(), expected?.ToString())
                ? null
                : WriteEngine.Reject(uid, field, expected, actual, reason);
        });
    }

    [McpServerTool(Name = "links_write")]
    [Description(
        "Add, remove, and change dependencies in one batch. Cycles are detected and refused before "
        + "anything is applied, with the offending chain named in the rejection. " + SameContract)]
    public static WriteResult LinksWrite(
        [Description("Document handle from project_open.")] string handle,
        [Description("The dependency operations to apply.")] LinkOp[] ops,
        [Description("Simulate against a copy and report the impact without committing.")] bool dryRun = false)
    {
        return SessionStore.UseForWrite(handle, session =>
        {
            if (ops.Length == 0)
            {
                throw new McpToolException("No operations supplied.");
            }

            return WriteEngine.Run(session, dryRun, (project, rejected) => ApplyLinkOps(project, ops, rejected));
    });
    }

    /// <summary>
    /// Walks forward from <paramref name="to"/> looking for <paramref name="from"/>. If it is
    /// reachable, adding from -> to would close a loop.
    /// </summary>
    private static List<int>? FindCycle(ProjectFile project, int from, int to)
    {
        var stack = new Stack<(int Uid, List<int> Path)>();
        stack.Push((to, new List<int> { to }));
        var seen = new HashSet<int>();

        while (stack.Count > 0)
        {
            var (uid, path) = stack.Pop();
            if (uid == from)
            {
                path.Add(from);
                return path;
            }

            if (!seen.Add(uid))
            {
                continue;
            }

            var task = project.GetTaskByUniqueID(uid);
            if (task is null)
            {
                continue;
            }

            foreach (var relation in task.Successors)
            {
                var next = relation.SuccessorTask?.UniqueID;
                if (next is not null)
                {
                    stack.Push((next.Value, new List<int>(path) { next.Value }));
                }
            }
        }

        return null;
    }

    [McpServerTool(Name = "resources_write")]
    [Description(
        "Create, update, and delete resources, and assign or unassign them to tasks, in one batch. "
        + SameContract)]
    public static WriteResult ResourcesWrite(
        [Description("Document handle from project_open.")] string handle,
        [Description("The resource operations to apply.")] ResourceOp[] ops,
        [Description("Simulate against a copy and report the impact without committing.")] bool dryRun = false)
    {
        return SessionStore.UseForWrite(handle, session =>
        {
            if (ops.Length == 0)
            {
                throw new McpToolException("No operations supplied.");
            }

            return WriteEngine.Run(session, dryRun, (project, rejected) => ApplyResourceOps(project, ops, rejected));
    });
    }

    [McpServerTool(Name = "calendars_write")]
    [Description(
        "Manage working calendars: create them (optionally derived from another), delete them, set the "
        + "working week and its hours (set_week), set exceptions for holidays, shutdowns or extra working "
        + "days with their hours (set_exception), and choose the project calendar (set_project_calendar). "
        + "Task and resource calendars are set with tasks_write and resources_write. " + SameContract)]
    public static WriteResult CalendarsWrite(
        [Description("Document handle from project_open.")] string handle,
        [Description("The calendar operations to apply.")] CalendarOp[] ops,
        [Description("Simulate against a copy and report the impact without committing.")] bool dryRun = false)
    {
        return SessionStore.UseForWrite(handle, session =>
        {
            if (ops.Length == 0)
            {
                throw new McpToolException("No operations supplied.");
            }

            return WriteEngine.Run(session, dryRun, (project, rejected) => ApplyCalendarOps(project, ops, rejected));
    });
    }

    [McpServerTool(Name = "schedule_update")]
    [Description(
        "Engine-level operations that take no per-task payload: save or clear a baseline, set the "
        + "status date, reschedule incomplete work, level resources, or force a recalculation. "
        + "Levelling and rescheduling are the two that do the most damage by accident, so they require "
        + "a dry run to be seen first. Operations that need Microsoft Project's scheduling engine are "
        + "refused outright on the MPXJ backend rather than approximated.")]
    public static WriteResult ScheduleUpdate(
        [Description("Document handle from project_open.")] string handle,
        [Description("save_baseline, clear_baseline, set_status_date, reschedule_incomplete, load_budget, level_resources, or recalculate.")]
        string op,
        [Description("Baseline slot 0-10 for save_baseline and clear_baseline. Defaults to 0.")]
        int baseline = 0,
        [Description("Status date for set_status_date and reschedule_incomplete, yyyy-MM-dd.")]
        string? statusDate = null,
        [Description(
            "Allow save_baseline to replace a baseline that already holds data. Off by default: "
            + "overwriting one destroys the original plan every variance is measured against.")]
        bool overwriteBaseline = false,
        [Description(
            "Why — recorded in the change log. Required to replace a baseline (a rebaseline is a change to "
            + "the approved plan and has to be justified); recommended for every save_baseline and load_budget.")]
        string? reason = null,
        [Description("Who approved the change, for the change log.")] string? approvedBy = null,
        [Description("For load_budget: amount per code, e.g. {\"D021-A1-A01\": 125000000}.")]
        Dictionary<string, double>? budget = null,
        [Description("For load_budget: or a CSV of code,amount.")] string? budgetPath = null,
        [Description("For load_budget: the task field holding the code. Defaults to Text1.")] string codeField = "Text1",
        [Description("For level_resources: delay tasks only within their slack, so the finish date cannot move.")]
        bool withinSlack = false,
        [Description("For level_resources: let levelling split remaining work.")] bool levelingCanSplit = true,
        [Description("Simulate against a copy and report the impact without committing.")] bool dryRun = false)
    {
        return SessionStore.UseForWrite(handle, session =>
        {
            var operation = op.Trim().ToLowerInvariant();

            var capabilities = Diagnostics.EnvironmentDoctor.Run(deep: false).Capabilities;
            if (capabilities.TryGetValue(operation, out var available) && !available)
            {
                throw new McpToolException(
                    $"'{operation}' is not available on this backend — project_health reports it as false in the " +
                    "capability matrix, and this server refuses to approximate it. Resource levelling in " +
                    "particular is Microsoft Project's own unpublished heuristic; any imitation would be a " +
                    "different answer wearing the same name. Run where project_health with deep=true reports " +
                    "the COM backend, or do it in Microsoft Project.");
            }

            return WriteEngine.Run(session, dryRun, (project, rejected) =>
            {
                var pending = new List<PendingOp>();

                switch (operation)
                {
                    case "save_baseline":
                    {
                        var current = new PendingOp { Label = "save_baseline" };
                        pending.Add(current);

                        // Overwriting a baseline destroys the record of the original plan — the thing
                        // every variance and every earned-value figure is measured against, and which
                        // cannot be reconstructed from the file afterwards. Never do it by accident.
                        var existing = project.Tasks.Count(t => baseline == 0
                            ? t.BaselineFinish is not null
                            : SafeBaselineFinish(t, baseline) is not null);

                        if (existing > 0 && overwriteBaseline && string.IsNullOrWhiteSpace(reason))
                        {
                            throw new McpToolException(
                                $"Replacing baseline {baseline} is a rebaseline — a change to the approved plan — and needs a "
                                + "reason for the change log (pass reason, and approvedBy). Integrated change control "
                                + "means the record of why the plan moved outlives the plan itself.");
                        }

                        var previousFinish = project.Tasks.Where(t => !t.Summary)
                            .Select(t => baseline == 0 ? t.BaselineFinish : SafeBaselineFinish(t, baseline))
                            .Where(d => d is not null).DefaultIfEmpty().Max();
                        var previousBudget = project.Tasks.Where(t => !t.Summary).Sum(t => Convert.ToDouble(t.BaselineCost ?? 0));

                        if (existing > 0 && !overwriteBaseline)
                        {
                            throw new McpToolException(
                                $"Baseline {baseline} already holds dates for {existing} task(s). Saving over it " +
                                "would destroy the record of the original plan — every variance and earned-value " +
                                "figure is measured against it, and it cannot be recovered from the file " +
                                "afterwards. Save to an empty slot (project_info lists which are in use), or pass " +
                                "overwriteBaseline=true if replacing it is genuinely what you want.");
                        }

                        foreach (var task in project.Tasks)
                        {
                            if (baseline == 0)
                            {
                                task.BaselineStart = task.Start;
                                task.BaselineFinish = task.Finish;
                                task.BaselineDuration = task.Duration;
                                task.BaselineWork = task.Work;
                                task.BaselineCost = task.Cost;
                            }
                            else
                            {
                                if (task.Start is not null) task.SetBaselineStart(baseline, task.Start.Value);
                                if (task.Finish is not null) task.SetBaselineFinish(baseline, task.Finish.Value);
                                task.SetBaselineDuration(baseline, task.Duration);
                                task.SetBaselineWork(baseline, task.Work);
                                task.SetBaselineCost(baseline, task.Cost);
                            }
                        }

                        var slot = baseline;
                        current.Checks.Add(file =>
                        {
                            var stored = file.Tasks.Count(t => slot == 0
                                ? t.BaselineFinish is not null
                                : SafeBaselineFinish(t, slot) is not null);
                            return stored > 0
                                ? null
                                : WriteEngine.Reject(-1, "save_baseline", $"baseline {slot}", "no dates stored",
                                    "No baseline dates were present when the model was re-read.");
                        });

                        if (!dryRun)
                        {
                            var newFinish = project.Tasks.Where(t => !t.Summary).Select(t => t.Finish)
                                .Where(d => d is not null).DefaultIfEmpty().Max();
                            var newBudget = project.Tasks.Where(t => !t.Summary).Sum(t => Convert.ToDouble(t.Cost ?? 0));
                            Analysis.PmoLedger.Record(session.Path, new Analysis.ChangeEntry
                            {
                                Kind = existing > 0 ? "rebaseline" : "baseline",
                                Summary = $"Baseline {slot} {(existing > 0 ? "replaced" : "saved")}.",
                                Reason = reason,
                                ApprovedBy = approvedBy,
                                FinishBefore = MpxjMapper.Iso(previousFinish),
                                FinishAfter = MpxjMapper.Iso(newFinish),
                                FinishDeltaDays = previousFinish is null || newFinish is null ? null
                                    : Math.Round((newFinish.Value - previousFinish.Value).TotalDays, 1),
                                BudgetBefore = previousBudget,
                                BudgetAfter = newBudget,
                            });
                        }

                        break;
                    }

                    case "load_budget":
                    {
                        var current = new PendingOp { Label = "load_budget" };
                        pending.Add(current);
                        var lines = budget is { Count: > 0 }
                            ? budget
                            : !string.IsNullOrWhiteSpace(budgetPath)
                                ? Writes.BudgetLoader.ReadCsv(Guard.ExistingFile(budgetPath, "budgetPath"))
                                : throw new McpToolException("load_budget needs 'budget' (code to amount) or 'budgetPath' (a CSV of code,amount).");
                        var before = project.Tasks.Where(t => !t.Summary).Sum(t => Convert.ToDouble(t.Cost ?? 0));
                        var loaded = Writes.BudgetLoader.Load(project, lines, codeField);
                        foreach (var note in loaded.Notes)
                        {
                            rejected.Add(new RejectedWrite { Field = "load_budget", Reason = note });
                        }

                        if (loaded.CodesWithoutTasks.Count > 0)
                        {
                            rejected.Add(new RejectedWrite
                            {
                                Field = "budget",
                                Reason = "Budget lines with no task carrying their code: " + string.Join("; ", loaded.CodesWithoutTasks.Take(15)),
                            });
                        }

                        rejected.Add(new RejectedWrite
                        {
                            Field = "load_budget",
                            Reason = $"Loaded {loaded.Loaded:N0} of a {loaded.BudgetTotal:N0} budget onto {loaded.TasksCosted} task(s); "
                                     + $"{loaded.TasksWithoutBudget} detail task(s) carry no budget.",
                        });

                        var expected = loaded.Loaded;
                        current.Checks.Add(file =>
                        {
                            var total = file.Tasks.Where(t => !t.Summary).Sum(t => Convert.ToDouble(t.FixedCost ?? 0));
                            return Math.Abs(total - expected) < Math.Max(1, expected * 0.001)
                                ? null
                                : WriteEngine.Reject(-1, "load_budget", expected, total, "The loaded costs did not survive the write.");
                        });

                        if (!dryRun)
                        {
                            Analysis.PmoLedger.Record(session.Path, new Analysis.ChangeEntry
                            {
                                Kind = "budget",
                                Summary = $"Budget loaded: {loaded.Loaded:N0} on {loaded.TasksCosted} task(s).",
                                Reason = reason,
                                ApprovedBy = approvedBy,
                                BudgetBefore = before,
                                BudgetAfter = loaded.Loaded,
                            });
                        }

                        break;
                    }

                    case "clear_baseline":
                    {
                        var current = new PendingOp { Label = "clear_baseline" };
                        pending.Add(current);
                        foreach (var task in project.Tasks)
                        {
                            if (baseline == 0)
                            {
                                task.BaselineStart = null;
                                task.BaselineFinish = null;
                                task.BaselineCost = null;
                            }
                        }

                        if (baseline != 0)
                        {
                            rejected.Add(new RejectedWrite
                            {
                                Field = "clear_baseline",
                                Reason = $"Clearing numbered baseline {baseline} is not supported on this backend; "
                                         + "only baseline 0 can be cleared here.",
                            });
                            break;
                        }

                        current.Checks.Add(file => file.Tasks.Any(t => t.BaselineFinish is not null)
                            ? WriteEngine.Reject(-1, "clear_baseline", "cleared", "dates still present",
                                "Baseline dates were still present when the model was re-read.")
                            : null);
                        break;
                    }

                    case "set_status_date":
                    {
                        var current = new PendingOp { Label = "set_status_date" };
                        pending.Add(current);
                        var parsed = QueryTools.ParseDate(statusDate)
                                     ?? throw new McpToolException("set_status_date needs statusDate, yyyy-MM-dd.");
                        project.ProjectProperties.StatusDate = parsed;
                        current.Checks.Add(file => file.ProjectProperties.StatusDate == parsed
                            ? null
                            : WriteEngine.Reject(-1, "statusDate", parsed, file.ProjectProperties.StatusDate,
                                "The status date did not survive the write."));
                        break;
                    }

                    case "recalculate":
                    {
                        var current = new PendingOp { Label = "recalculate", SchedulesItself = true };
                        pending.Add(current);

                        // Asking for a recalculation is the authorisation. From here on this document
                        // is scheduled by our engine, so later writes may reschedule it too.
                        if (!session.MayReschedule && !Analysis.Scheduler.UsesProject)
                        {
                            session.RescheduleAuthorised = true;
                            rejected.Add(new RejectedWrite
                            {
                                Field = "recalculate",
                                Reason = "This schedule was imported, so its dates were Microsoft Project's. "
                                         + "They have now been replaced by this server's critical-path engine, "
                                         + "which does not reproduce Microsoft Project exactly on schedules "
                                         + "that use resource-driven dates, task types or manual scheduling — "
                                         + "measured on real files, most tasks can move. Close without saving "
                                         + "if that was not what you wanted.",
                            });
                        }

                        var report = Analysis.Scheduler.Run(project);
                        current.Checks.Add(file =>
                            file.Tasks.Any(t => !t.Summary && t.Start is not null)
                                ? null
                                : WriteEngine.Reject(-1, "recalculate", "dates computed", "no dates",
                                    "The schedule was recalculated but no task came back with a start date."));

                        if (report.Warnings.Count > 0)
                        {
                            foreach (var warning in report.Warnings)
                            {
                                rejected.Add(new RejectedWrite { Field = "recalculate", Reason = warning });
                            }
                        }

                        break;
                    }

                    case "level_resources":
                    {
                        var current = new PendingOp { Label = "level_resources", SchedulesItself = true };
                        pending.Add(current);
                        var before = ResourceAnalyzer.Find(project).Count;
                        var report = Analysis.ProjectScheduler.Run(project, (withinSlack, levelingCanSplit));
                        Writes.ProgressRules.RollUp(project);
                        var after = ResourceAnalyzer.Find(project).Count;
                        var slackOnly = withinSlack;
                        current.Checks.Add(_ => report.Engine == Analysis.Scheduler.MicrosoftProject
                            ? null
                            : WriteEngine.Reject(-1, "level_resources", "levelled by Microsoft Project", report.Engine,
                                "Levelling only runs in Microsoft Project."));
                        if (after > 0)
                        {
                            rejected.Add(new RejectedWrite
                            {
                                Field = "level_resources",
                                Requested = $"{before} overallocation window(s)",
                                Actual = $"{after} remain",
                                Reason = slackOnly
                                    ? "Levelled within slack only: what remains cannot be resolved without moving the finish. "
                                      + "Level again with withinSlack=false, add resources, or accept it."
                                    : "Microsoft Project levelled what it could; what remains needs more resources or "
                                      + "a change of logic. This counts as applied.",
                            });
                        }

                        break;
                    }

                    case "reschedule_incomplete":
                    {
                        var current = new PendingOp { Label = "reschedule_incomplete", SchedulesItself = true };
                        pending.Add(current);

                        var asOf = QueryTools.ParseDate(statusDate)
                                   ?? project.ProjectProperties.StatusDate
                                   ?? throw new McpToolException(
                                       "reschedule_incomplete needs a status date — pass statusDate, or set one "
                                       + "first with op='set_status_date'.");

                        var calendar = new Analysis.WorkingCalendar(project);
                        var moved = 0;

                        foreach (var task in project.Tasks.Where(t => !t.Summary && t.UniqueID is not null))
                        {
                            // Work that is finished stays where it is; unstarted work in the past is
                            // pulled forward so the forecast stops claiming the impossible.
                            if (task.ActualFinish is not null || (task.PercentageComplete ?? 0) >= 100)
                            {
                                continue;
                            }

                            if (task.Start is null || task.Start >= asOf || task.ActualStart is not null)
                            {
                                continue;
                            }

                            task.ConstraintType = ConstraintType.StartNoEarlierThan;
                            task.ConstraintDate = Analysis.WorkingCalendar.AtStart(calendar.NextWorkingDay(asOf));
                            moved++;
                        }

                        Analysis.Scheduler.Run(project);

                        var expected = moved;
                        var cutoff = asOf;
                        current.Checks.Add(file =>
                        {
                            var stragglers = file.Tasks.Count(t =>
                                !t.Summary && t.ActualStart is null && (t.PercentageComplete ?? 0) < 100
                                && t.Start is not null && t.Start < cutoff);

                            return stragglers == 0
                                ? null
                                : WriteEngine.Reject(-1, "reschedule_incomplete", $"{expected} moved",
                                    $"{stragglers} still before the status date",
                                    "Some unstarted work still forecasts a start before the status date, most "
                                    + "likely because a hard constraint holds it there. Run schedule_qa to find them.");
                        });
                        break;
                    }

                    default:
                        throw new McpToolException(
                            $"Unknown operation '{op}'. Use save_baseline, clear_baseline, set_status_date, " +
                            "reschedule_incomplete, level_resources, or recalculate.");
                }

                return pending;
            });
    });
    }

    internal static List<PendingOp> ApplyTaskOps(ProjectFile project, IReadOnlyList<TaskOp> ops, List<RejectedWrite> rejected)
    {
        var pending = new List<PendingOp>();
        var keyed = new Dictionary<string, MPXJ.Net.Task>(StringComparer.OrdinalIgnoreCase);

        foreach (var op in ops)
        {
            switch (op.Op.Trim().ToLowerInvariant())
            {
                case "create":
                {
                    var current = new PendingOp { Label = "create" };
                    pending.Add(current);
                    var parent = op.ParentUid is not null ? project.GetTaskByUniqueID(op.ParentUid.Value) : null;
                    if (op.ParentUid is not null && parent is null)
                    {
                        rejected.Add(WriteEngine.Reject(op.ParentUid.Value, "parentUid", op.ParentUid, null,
                            "No task with that uid to parent under."));
                        continue;
                    }

                    if (op.ParentKey is not null)
                    {
                        if (!keyed.TryGetValue(op.ParentKey, out parent))
                        {
                            rejected.Add(WriteEngine.Reject(-1, "parentKey", op.ParentKey, null,
                                "No task with that key was created earlier in this batch."));
                            continue;
                        }
                    }

                    var created = parent is null ? project.AddTask() : parent.AddTask();
                    EnsureUniqueId(project, created);
                    Writes.Structure.Place(project, created, parent);
                    if (op.Key is not null)
                    {
                        keyed[op.Key] = created;
                    }
                    created.Name = op.Name ?? "New task";
                    ApplyFields(project, created, op, rejected, current, isNew: true);
                    GiveDatesIfMissing(project, created);

                    var newUid = created.UniqueID;
                    var expectedName = created.Name;
                    current.Checks.Add(file =>
                    {
                        var check = newUid is null ? null : file.GetTaskByUniqueID(newUid.Value);
                        return check is null
                            ? WriteEngine.Reject(newUid ?? -1, "create", op.Name, null,
                                "The task was added but could not be read back afterwards.")
                            : check.Name != expectedName
                                ? WriteEngine.Reject(newUid!.Value, "name", expectedName, check.Name,
                                    "The name did not survive the write.")
                                : null;
                    });
                    break;
                }

                case "update":
                {
                    var current = new PendingOp { Label = "update" };
                    pending.Add(current);
                    if (op.Uid is null)
                    {
                        rejected.Add(WriteEngine.Reject(-1, "uid", null, null, "update needs a uid."));
                        continue;
                    }

                    var task = project.GetTaskByUniqueID(op.Uid.Value);
                    if (task is null)
                    {
                        rejected.Add(WriteEngine.Reject(op.Uid.Value, "uid", op.Uid, null,
                            "No task with that uid. Row ids shift — use the uid from tasks_query."));
                        continue;
                    }

                    ApplyFields(project, task, op, rejected, current, isNew: false);
                    break;
                }

                case "delete":
                {
                    var current = new PendingOp { Label = "delete" };
                    pending.Add(current);
                    if (op.Uid is null)
                    {
                        rejected.Add(WriteEngine.Reject(-1, "uid", null, null, "delete needs a uid."));
                        continue;
                    }

                    var task = project.GetTaskByUniqueID(op.Uid.Value);
                    if (task is null)
                    {
                        rejected.Add(WriteEngine.Reject(op.Uid.Value, "uid", op.Uid, null,
                            "No task with that uid."));
                        continue;
                    }

                    var doomed = op.Uid.Value;
                    var childCount = task.ChildTasks.Count;
                    project.RemoveTask(task);
                    current.Checks.Add(file => file.GetTaskByUniqueID(doomed) is null
                        ? null
                        : WriteEngine.Reject(doomed, "delete", "removed", "still present",
                            "The task is still in the model after the delete."));

                    if (childCount > 0)
                    {
                        rejected.Add(new RejectedWrite
                        {
                            Uid = doomed,
                            Field = "delete",
                            Reason = $"Deleting this summary task also removed its {childCount} child task(s). "
                                     + "That is counted as one applied operation.",
                        });
                    }

                    break;
                }

                case "outline":
                {
                    var current = new PendingOp { Label = "outline" };
                    pending.Add(current);
                    if (op.Uid is null)
                    {
                        rejected.Add(WriteEngine.Reject(-1, "uid", null, null, $"{op.Op} needs a uid."));
                        continue;
                    }

                    var task = project.GetTaskByUniqueID(op.Uid.Value);
                    if (task is null)
                    {
                        rejected.Add(WriteEngine.Reject(op.Uid.Value, "uid", op.Uid, null, "No task with that uid."));
                        continue;
                    }

                    // The level is what is set; Structure.Normalize, at the end of the batch,
                    // rebuilds the hierarchy from it, as Project does from row order and level.
                    var currentLevel = task.OutlineLevel ?? 1;
                    var targetLevel = Math.Max(1, currentLevel + (op.Indent ?? 0));
                    task.OutlineLevel = targetLevel;

                    var uid = op.Uid.Value;
                    current.Checks.Add(file =>
                    {
                        var check = file.GetTaskByUniqueID(uid);
                        return check?.OutlineLevel == targetLevel
                            ? null
                            : WriteEngine.Reject(uid, "outlineLevel", targetLevel, check?.OutlineLevel,
                                "The outline level did not survive the write.");
                    });

                    break;
                }

                default:
                    rejected.Add(WriteEngine.Reject(op.Uid ?? -1, "op", op.Op, null,
                        op.Op.Trim().Equals("move", StringComparison.OrdinalIgnoreCase)
                            ? "There is no 'move' operation: this backend cannot reorder tasks "
                              + "within the outline. Use 'outline' with indent to change a task's "
                              + "level, or reorder in Microsoft Project."
                            : $"Unknown operation '{op.Op}'. Use create, update, delete, or outline."));
                    break;
            }
        }

        // Any of these ops can change detail-task progress; summaries show it rolled up.
        ProgressRules.RollUp(project);
        Writes.Structure.Normalize(project);
        return pending;
    }

    internal static List<PendingOp> ApplyLinkOps(ProjectFile project, IReadOnlyList<LinkOp> ops, List<RejectedWrite> rejected)
    {
        var pending = new List<PendingOp>();

        foreach (var op in ops)
        {
            var from = project.GetTaskByUniqueID(op.From);
            var to = project.GetTaskByUniqueID(op.To);

            if (from is null || to is null)
            {
                rejected.Add(WriteEngine.Reject(from is null ? op.From : op.To, "uid", null, null,
                    "No task with that uid. Row ids shift — use the uid from tasks_query."));
                continue;
            }

            var operation = op.Op.Trim().ToLowerInvariant();

            if (operation is "link" or "relink")
            {
                if (op.From == op.To)
                {
                    rejected.Add(WriteEngine.Reject(op.From, "link", $"{op.From}->{op.To}", null,
                        "A task cannot depend on itself."));
                    continue;
                }

                var cycle = FindCycle(project, op.From, op.To);
                if (cycle is not null)
                {
                    rejected.Add(WriteEngine.Reject(op.From, "link", $"{op.From}->{op.To}", null,
                        $"That link would create a cycle: {string.Join(" -> ", cycle)}. Nothing was applied for it."));
                    continue;
                }
            }

            switch (operation)
            {
                case "unlink":
                case "relink":
                {
                    var current = new PendingOp { Label = "relink" };
                    pending.Add(current);
                    var existing = to.Predecessors
                        .Where(r => r.PredecessorTask?.UniqueID == op.From)
                        .ToList();

                    if (existing.Count == 0 && operation == "unlink")
                    {
                        rejected.Add(WriteEngine.Reject(op.To, "unlink", $"{op.From}->{op.To}", null,
                            "There is no dependency between those tasks to remove."));
                        continue;
                    }

                    foreach (var relation in existing)
                    {
                        // MPXJ exposes no remove-relation primitive; the predecessor collection
                        // is the only handle on it. If the list turns out to be immutable the
                        // verification step below catches it and reports honestly.
                        try
                        {
                            to.Predecessors.Remove(relation);
                        }
                        catch (Exception ex)
                        {
                            rejected.Add(WriteEngine.Reject(op.To, "unlink", $"{op.From}->{op.To}", null,
                                $"This backend could not remove the dependency: {ex.Message}. "
                                + "Remove it in Microsoft Project, or run where the COM backend is available."));
                        }
                    }

                    if (operation == "unlink")
                    {
                        var f = op.From;
                        var t = op.To;
                        current.Checks.Add(file =>
                        {
                            var target = file.GetTaskByUniqueID(t);
                            return target?.Predecessors.Any(r => r.PredecessorTask?.UniqueID == f) == true
                                ? WriteEngine.Reject(t, "unlink", "removed", "still linked",
                                    "The dependency is still present after the unlink.")
                                : null;
                        });
                        continue;
                    }

                    goto case "link";
                }

                case "link":
                {
                    var current = new PendingOp { Label = "link" };
                    pending.Add(current);
                    var type = MpxjMapper.ParseRelationType(op.Type ?? "FS");
                    var lag = op.Lag is null
                        ? MPXJ.Net.Duration.GetInstance(0, TimeUnit.Days)
                        : MpxjMapper.ParseDuration(op.Lag);

                    to.AddPredecessor(new Relation.Builder(project)
                        .PredecessorTask(from)
                        .SuccessorTask(to)
                        .Type(type)
                        .Lag(lag));

                    var f = op.From;
                    var t = op.To;
                    current.Checks.Add(file =>
                    {
                        var target = file.GetTaskByUniqueID(t);
                        var found = target?.Predecessors.FirstOrDefault(
                            r => r.PredecessorTask?.UniqueID == f && r.Type == type);
                        return found is not null
                            ? null
                            : WriteEngine.Reject(t, "link", $"{f}->{t} {op.Type ?? "FS"}", null,
                                "The dependency was not present when re-read after the write.");
                    });
                    break;
                }

                default:
                    rejected.Add(WriteEngine.Reject(op.From, "op", op.Op, null,
                        $"Unknown operation '{op.Op}'. Use link, unlink, or relink."));
                    break;
            }
        }

        return pending;
    }

    internal static List<PendingOp> ApplyResourceOps(ProjectFile project, IReadOnlyList<ResourceOp> ops, List<RejectedWrite> rejected)
    {
        var pending = new List<PendingOp>();

        foreach (var op in ops)
        {
            switch (op.Op.Trim().ToLowerInvariant())
            {
                case "create":
                {
                    var current = new PendingOp { Label = "create" };
                    pending.Add(current);
                    var resource = project.AddResource();
                    Writes.Structure.EnsureIds(project);
                    current.Rollback = () => project.RemoveResource(resource);
                    resource.Name = op.Name ?? "New resource";
                    // As in Project: a new resource works the project's calendar. Left without
                    // one, Microsoft Project gives it its own default base calendar — which is
                    // locale-dependent (9:00-13:00, 15:00-19:00 on a Spanish install) — and
                    // every task it is assigned to moves to those hours.
                    var resourceCalendar = resource.AddCalendar();
                    resourceCalendar.Parent = project.DefaultCalendar;
                    if (op.Type is not null && Enum.TryParse<ResourceType>(op.Type, true, out var rt))
                    {
                        resource.Type = rt;
                    }

                    ApplyResourceFields(project, resource, op, current, rejected);
                    var uid = resource.UniqueID;
                    var expected = resource.Name;
                    current.Checks.Add(file =>
                    {
                        var check = uid is null ? null : file.GetResourceByUniqueID(uid.Value);
                        return check?.Name == expected
                            ? null
                            : WriteEngine.Reject(uid ?? -1, "name", expected, check?.Name,
                                "The resource could not be read back after being created.");
                    });
                    break;
                }

                case "update":
                {
                    var current = new PendingOp { Label = "update" };
                    pending.Add(current);
                    if (op.Uid is null || project.GetResourceByUniqueID(op.Uid.Value) is not { } resource)
                    {
                        rejected.Add(WriteEngine.Reject(op.Uid ?? -1, "uid", op.Uid, null,
                            "No resource with that uid."));
                        continue;
                    }

                    if (op.Name is not null) resource.Name = op.Name;
                    ApplyResourceFields(project, resource, op, current, rejected);

                    var uid = op.Uid.Value;
                    var expectedName = resource.Name;
                    current.Checks.Add(file =>
                    {
                        var check = file.GetResourceByUniqueID(uid);
                        return check is not null && check.Name == expectedName
                            ? null
                            : WriteEngine.Reject(uid, "name", expectedName, check?.Name,
                                "The resource change did not survive the write.");
                    });
                    break;
                }

                case "delete":
                {
                    var current = new PendingOp { Label = "delete" };
                    pending.Add(current);
                    if (op.Uid is null || project.GetResourceByUniqueID(op.Uid.Value) is not { } resource)
                    {
                        rejected.Add(WriteEngine.Reject(op.Uid ?? -1, "uid", op.Uid, null,
                            "No resource with that uid."));
                        continue;
                    }

                    var uid = op.Uid.Value;
                    project.RemoveResource(resource);
                    current.Checks.Add(file => file.GetResourceByUniqueID(uid) is null
                        ? null
                        : WriteEngine.Reject(uid, "delete", "removed", "still present",
                            "The resource is still in the model after the delete."));
                    break;
                }

                case "assign":
                {
                    var current = new PendingOp { Label = "assign" };
                    pending.Add(current);
                    if (op.TaskUid is null || op.Uid is null)
                    {
                        rejected.Add(WriteEngine.Reject(op.Uid ?? -1, "assign", null, null,
                            "assign needs both uid (the resource) and taskUid."));
                        continue;
                    }

                    var task = project.GetTaskByUniqueID(op.TaskUid.Value);
                    var resource = project.GetResourceByUniqueID(op.Uid.Value);
                    if (task is null || resource is null)
                    {
                        rejected.Add(WriteEngine.Reject(op.Uid.Value, "assign", null, null,
                            task is null ? "No task with that uid." : "No resource with that uid."));
                        continue;
                    }

                    var assignment = task.AddResourceAssignment(resource);
                    assignment.Units = op.Units ?? 100;

                    // Assigned to work already finished, it is finished too. Left open, Project
                    // gives it remaining work, drops the task to 99% and schedules the rest
                    // after the status date — a material costed onto a finished task moved the
                    // finish by 16 days.
                    if (task.ActualFinish is not null || (task.PercentageComplete ?? 0) >= 100)
                    {
                        assignment.ActualStart = task.ActualStart ?? task.Start;
                        assignment.ActualFinish = task.ActualFinish ?? task.Finish;
                        assignment.PercentageWorkComplete = 100;

                        // Project derives an assignment's progress from its work, so the work is
                        // stated as done: a crew's hours over the task, a material's quantity.
                        var amount = resource.Type == ResourceType.Material
                            ? assignment.Units ?? 0
                            : (MpxjMapper.Hours(task.ActualDuration ?? task.Duration, MpxjMapper.HoursPerDay) ?? 0)
                              * (assignment.Units ?? 100) / 100.0;
                        var done = MPXJ.Net.Duration.GetInstance(amount, TimeUnit.Hours);
                        assignment.Work = done;
                        assignment.ActualWork = done;
                        assignment.RemainingWork = MPXJ.Net.Duration.GetInstance(0, TimeUnit.Hours);
                    }

                    var taskUid = op.TaskUid.Value;
                    var resourceUid = op.Uid.Value;
                    current.Checks.Add(file =>
                    {
                        var check = file.GetTaskByUniqueID(taskUid);
                        return check?.ResourceAssignments.Any(a => a.Resource?.UniqueID == resourceUid) == true
                            ? null
                            : WriteEngine.Reject(taskUid, "assign", resourceUid, null,
                                "The assignment was not present when re-read after the write.");
                    });
                    break;
                }

                case "unassign":
                {
                    var current = new PendingOp { Label = "unassign" };
                    pending.Add(current);
                    if (op.TaskUid is null || op.Uid is null)
                    {
                        rejected.Add(WriteEngine.Reject(op.Uid ?? -1, "unassign", null, null,
                            "unassign needs both uid (the resource) and taskUid."));
                        continue;
                    }

                    var task = project.GetTaskByUniqueID(op.TaskUid.Value);
                    var existing = task?.ResourceAssignments
                        .FirstOrDefault(a => a.Resource?.UniqueID == op.Uid.Value);

                    if (existing is null)
                    {
                        rejected.Add(WriteEngine.Reject(op.TaskUid.Value, "unassign", op.Uid, null,
                            "That resource is not assigned to that task."));
                        continue;
                    }

                    existing.Remove();

                    var taskUid = op.TaskUid.Value;
                    var resourceUid = op.Uid.Value;
                    current.Checks.Add(file =>
                    {
                        var check = file.GetTaskByUniqueID(taskUid);
                        return check?.ResourceAssignments.Any(a => a.Resource?.UniqueID == resourceUid) != true
                            ? null
                            : WriteEngine.Reject(taskUid, "unassign", "removed", "still assigned",
                                "The assignment is still present after the unassign.");
                    });
                    break;
                }

                default:
                    rejected.Add(WriteEngine.Reject(op.Uid ?? -1, "op", op.Op, null,
                        $"Unknown operation '{op.Op}'. Use create, update, delete, assign, or unassign."));
                    break;
            }
        }

        // A new assignment on a schedule read from a file has no unique id either.
        Writes.Structure.Normalize(project);
        return pending;
    }

    internal static List<PendingOp> ApplyCalendarOps(ProjectFile project, IReadOnlyList<CalendarOp> ops, List<RejectedWrite> rejected)
    {
        var pending = new List<PendingOp>();

        foreach (var op in ops)
        {
            switch (op.Op.Trim().ToLowerInvariant())
            {
                case "create":
                {
                    var current = new PendingOp { Label = "create" };
                    pending.Add(current);
                    if (string.IsNullOrWhiteSpace(op.Name))
                    {
                        rejected.Add(WriteEngine.Reject(-1, "name", null, null, "create needs a calendar name."));
                        continue;
                    }

                    if (project.GetCalendarByName(op.Name) is not null)
                    {
                        rejected.Add(WriteEngine.Reject(-1, "name", op.Name, op.Name,
                            "A calendar with that name already exists."));
                        continue;
                    }

                    ProjectCalendar? basis = null;
                    if (op.BasedOn is not null && (basis = project.GetCalendarByName(op.BasedOn)) is null)
                    {
                        rejected.Add(WriteEngine.Reject(-1, "basedOn", op.BasedOn, null,
                            $"No calendar named '{op.BasedOn}'. Call project_info to list them."));
                        continue;
                    }

                    // Always a base calendar: one derived from another is, to Microsoft Project, a
                    // resource's calendar, and it drops it as a task or project calendar. basedOn
                    // copies the week and the exceptions instead.
                    var calendar = project.AddDefaultBaseCalendar();
                    if (basis is not null)
                    {
                        Writes.CalendarEdits.CopyInto(basis, calendar);
                    }

                    calendar.Name = op.Name;
                    Writes.Structure.EnsureIds(project);
                    var expected = op.Name;
                    current.Checks.Add(file => file.GetCalendarByName(expected) is not null
                        ? null
                        : WriteEngine.Reject(-1, "calendar", expected, null,
                            "The calendar could not be read back after being created."));
                    break;
                }

                case "delete":
                {
                    var current = new PendingOp { Label = "delete" };
                    pending.Add(current);
                    var calendar = op.Name is null ? null : project.GetCalendarByName(op.Name);
                    if (calendar is null)
                    {
                        rejected.Add(WriteEngine.Reject(-1, "name", op.Name, null, "No calendar with that name."));
                        continue;
                    }

                    var expected = op.Name!;
                    project.RemoveCalendar(calendar);
                    current.Checks.Add(file => file.GetCalendarByName(expected) is null
                        ? null
                        : WriteEngine.Reject(-1, "delete", "removed", "still present",
                            "The calendar is still in the model after the delete."));
                    break;
                }

                case "set_exception":
                {
                    var current = new PendingOp { Label = "set_exception" };
                    pending.Add(current);
                    // A schedule read from a file may carry calendars without one of them being
                    // flagged as the project default; falling back to the first is better than
                    // refusing to add a holiday.
                    var calendar = op.Name is not null
                        ? project.GetCalendarByName(op.Name)
                        : project.DefaultCalendar ?? project.Calendars.FirstOrDefault();

                    if (calendar is null)
                    {
                        rejected.Add(WriteEngine.Reject(-1, "name", op.Name, null,
                            op.Name is null
                                ? "This schedule has no calendar at all. Create one first with "
                                  + "op='create'."
                                : $"No calendar named '{op.Name}'. Call project_info to list them."));
                        continue;
                    }

                    var fromDate = QueryTools.ParseDate(op.From);
                    if (fromDate is null)
                    {
                        rejected.Add(WriteEngine.Reject(-1, "from", op.From, null,
                            "set_exception needs a From date, yyyy-MM-dd."));
                        continue;
                    }

                    var exceptionHours = op.Working == true
                        ? Writes.CalendarEdits.ParseHours(op.Hours ?? "08:00-12:00,13:00-17:00")
                        : Array.Empty<(TimeOnly, TimeOnly)>();
                    if (exceptionHours is null || op.Working == true && exceptionHours.Count == 0)
                    {
                        rejected.Add(WriteEngine.Reject(-1, "hours", op.Hours, null,
                            "A working exception needs hours like '08:00-12:00,13:00-17:00', in order "
                            + "and not overlapping."));
                        continue;
                    }

                    var toDate = QueryTools.ParseDate(op.To) ?? fromDate.Value;
                    var start = DateOnly.FromDateTime(fromDate.Value);
                    var end = DateOnly.FromDateTime(toDate);
                    var exception = calendar.AddCalendarException(start, end);
                    foreach (var (from, to) in exceptionHours)
                    {
                        exception.Add(new TimeOnlyRange(from, to));
                    }

                    var wantWorking = exceptionHours.Count > 0;
                    var calendarName = calendar.Name;
                    current.Checks.Add(file =>
                    {
                        var check = calendarName is null ? null : file.GetCalendarByName(calendarName);
                        return check?.GetException(start) is { } found && found.Working == wantWorking
                            ? null
                            : WriteEngine.Reject(-1, "exception", start.ToString("yyyy-MM-dd"), null,
                                "The exception was not present when re-read after the write.");
                    });
                    break;
                }

                case "set_week":
                case "set_working_hours":
                {
                    var current = new PendingOp { Label = "set_week" };
                    pending.Add(current);
                    var calendar = op.Name is not null
                        ? project.GetCalendarByName(op.Name)
                        : project.DefaultCalendar ?? project.Calendars.FirstOrDefault();
                    if (calendar is null)
                    {
                        rejected.Add(WriteEngine.Reject(-1, "name", op.Name, null,
                            $"No calendar named '{op.Name}'. Call project_info to list them."));
                        continue;
                    }

                    var days = Writes.CalendarEdits.ParseDays(op.Days);
                    if (days is null)
                    {
                        rejected.Add(WriteEngine.Reject(-1, "days", op.Days, null,
                            "set_week needs days like 'mon-fri', 'sat' or 'lun,mie,vie'."));
                        continue;
                    }

                    var hours = Writes.CalendarEdits.ParseHours(op.Hours);
                    if (hours is null)
                    {
                        rejected.Add(WriteEngine.Reject(-1, "hours", op.Hours, null,
                            "Hours are ranges like '08:00-12:00,13:00-17:00', in order and not "
                            + "overlapping; empty makes the days non-working."));
                        continue;
                    }

                    Writes.CalendarEdits.SetWeek(calendar, days, hours);
                    var calendarName = calendar.Name;
                    var wanted = Writes.CalendarEdits.Describe(hours);
                    foreach (var day in days)
                    {
                        current.Checks.Add(file =>
                        {
                            var check = calendarName is null ? null : file.GetCalendarByName(calendarName);
                            var actual = check is null ? null : Writes.CalendarEdits.Describe(check, day);
                            return actual == wanted
                                ? null
                                : WriteEngine.Reject(-1, day.ToString(), wanted, actual,
                                    "The working hours did not survive the write.");
                        });
                    }

                    break;
                }

                case "set_project_calendar":
                {
                    var current = new PendingOp { Label = "set_project_calendar" };
                    pending.Add(current);
                    var calendar = op.Name is null ? null : project.GetCalendarByName(op.Name);
                    if (calendar is null)
                    {
                        rejected.Add(WriteEngine.Reject(-1, "name", op.Name, null,
                            $"No calendar named '{op.Name}'. Call project_info to list them."));
                        continue;
                    }

                    if (calendar.Parent is not null)
                    {
                        rejected.Add(WriteEngine.Reject(-1, "name", op.Name, null,
                            "The project calendar has to be a base calendar; this one derives from "
                            + $"'{calendar.Parent.Name}'."));
                        continue;
                    }

                    project.DefaultCalendar = calendar;
                    var expected = calendar.Name;
                    current.Checks.Add(file => file.DefaultCalendar?.Name == expected
                        ? null
                        : WriteEngine.Reject(-1, "projectCalendar", expected, file.DefaultCalendar?.Name,
                            "The project calendar did not survive the write."));
                    break;
                }

                default:
                    rejected.Add(WriteEngine.Reject(-1, "op", op.Op, null,
                        $"Unknown operation '{op.Op}'. Use create, delete, set_week, set_exception, "
                        + "or set_project_calendar."));
                    break;
            }
        }

        return pending;
    }

    private static DateTime? SafeBaselineFinish(MPXJ.Net.Task task, int slot)
    {
        try { return task.GetBaselineFinish(slot); } catch { return null; }
    }
}
