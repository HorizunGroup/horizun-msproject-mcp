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
    [Description("create, update, delete, move, or outline.")]
    public required string Op { get; init; }

    [Description("Target task uid. Required for update, delete, move, and outline.")]
    public int? Uid { get; init; }

    [Description("For create and move: place the task after this uid.")]
    public int? AfterUid { get; init; }

    [Description("For create: make the task a child of this uid.")]
    public int? ParentUid { get; init; }

    public string? Name { get; init; }

    [Description("Duration with a unit: 5d, 8h, 2w, 0.")]
    public string? Duration { get; init; }

    [Description("yyyy-MM-dd. On an auto-scheduled task the engine may recalculate over it — the result says so.")]
    public string? Start { get; init; }

    public string? Finish { get; init; }
    public double? PercentComplete { get; init; }
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

    [Description("Custom text fields to set, e.g. {\"Text1\": \"CWA-3\"}.")]
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

    [Description("Lag with a unit: 2d, -1d, 0.")]
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
    public double? StandardRate { get; init; }

    [Description("For assign and unassign.")]
    public int? TaskUid { get; init; }

    [Description("For assign: percentage of the resource, e.g. 100.")]
    public double? Units { get; init; }
}

public sealed record CalendarOp
{
    [Description("create, delete, set_exception, or set_working_hours.")]
    public required string Op { get; init; }

    public string? Name { get; init; }

    [Description("Exception start, yyyy-MM-dd.")]
    public string? From { get; init; }

    [Description("Exception end, yyyy-MM-dd. Defaults to From.")]
    public string? To { get; init; }

    [Description("Whether the exception days are working days.")]
    public bool? Working { get; init; }

    [Description("Day of week for set_working_hours: Monday..Sunday.")]
    public string? DayOfWeek { get; init; }

    [Description("Working hours for that day, e.g. 8.")]
    public double? Hours { get; init; }
}

[McpServerToolType]
public static class WriteTools
{
    private const string VerifiedContract =
        "Every change is re-read from the model after the write and only then counted as applied; "
        + "anything the schedule refused comes back under 'rejected' with the reason and what the value "
        + "actually became. Pass dryRun=true to run the whole batch against a throwaway copy and see the "
        + "impact — how many tasks move, how far the finish date shifts, whether the critical path changes "
        + "— without touching the open document.";

    [McpServerTool(Name = "tasks_write")]
    [Description(
        "Create, update, delete, move, and re-outline tasks in one batch. Progress (percentComplete, "
        + "actual dates) is an ordinary update. Tasks are addressed by their stable uid — never by row id. "
        + VerifiedContract)]
    public static WriteResult TasksWrite(
        [Description("Document handle from project_open.")] string handle,
        [Description("The operations to apply, in order.")] TaskOp[] ops,
        [Description("Simulate against a copy and report the impact without committing.")] bool dryRun = false)
    {
        if (ops.Length == 0)
        {
            throw new McpToolException("No operations supplied.");
        }

        var session = SessionStore.GetForWrite(handle);

        return WriteEngine.Run(session, dryRun, (project, rejected) =>
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
                        var parent = op.ParentUid is not null ? project.GetTaskByUniqueID(op.ParentUid.Value) : null;
                        if (op.ParentUid is not null && parent is null)
                        {
                            rejected.Add(WriteEngine.Reject(op.ParentUid.Value, "parentUid", op.ParentUid, null,
                                "No task with that uid to parent under."));
                            continue;
                        }

                        var created = parent is null ? project.AddTask() : parent.AddTask();
                        EnsureUniqueId(project, created);
                        created.Name = op.Name ?? "New task";
                        ApplyFields(created, op, rejected, current, isNew: true);
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

                        ApplyFields(task, op, rejected, current, isNew: false);
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

                    case "move":
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

                        // MPXJ has no reparent primitive; outline level is the honest approximation
                        // it can verify, so state the limit rather than pretend the move happened.
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

                        if (op.Op.Trim().Equals("move", StringComparison.OrdinalIgnoreCase))
                        {
                            rejected.Add(new RejectedWrite
                            {
                                Uid = uid,
                                Field = "move",
                                Reason = "This backend cannot reorder tasks within the outline — only the "
                                         + "outline level was changed. Reorder in Microsoft Project, or run "
                                         + "this server where project_health reports the COM backend.",
                            });
                        }

                        break;
                    }

                    default:
                        rejected.Add(WriteEngine.Reject(op.Uid ?? -1, "op", op.Op, null,
                            $"Unknown operation '{op.Op}'. Use create, update, delete, move, or outline."));
                        break;
                }
            }

            return pending;
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

        task.Start = Analysis.WorkingCalendar.AtStart(start);
        task.Finish = Analysis.WorkingCalendar.AtFinish(
            days <= 0 ? start : calendar.AddWorkingDays(start, days));
    }

    private static void ApplyFields(
        MPXJ.Net.Task task, TaskOp op,
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
                task.Duration = parsed;
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
        SetDate(task, op.ActualStart, "actualStart", (t, d) => t.ActualStart = d, t => t.ActualStart, uid, pending, rejected);
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

        if (op.Custom is not null)
        {
            foreach (var (field, value) in op.Custom)
            {
                var slot = Dcma14.ParseTextSlot(field);
                try
                {
                    task.SetText(slot, value);
                    var expected = value;
                    pending.Checks.Add(file =>
                    {
                        var check = file.GetTaskByUniqueID(uid);
                        var actual = check is null ? null : Dcma14.SafeGetText(check, slot);
                        return actual == expected
                            ? null
                            : WriteEngine.Reject(uid, field, expected, actual,
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
        + "anything is applied, with the offending chain named in the rejection. " + VerifiedContract)]
    public static WriteResult LinksWrite(
        [Description("Document handle from project_open.")] string handle,
        [Description("The dependency operations to apply.")] LinkOp[] ops,
        [Description("Simulate against a copy and report the impact without committing.")] bool dryRun = false)
    {
        if (ops.Length == 0)
        {
            throw new McpToolException("No operations supplied.");
        }

        var session = SessionStore.GetForWrite(handle);

        return WriteEngine.Run(session, dryRun, (project, rejected) =>
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
        + VerifiedContract)]
    public static WriteResult ResourcesWrite(
        [Description("Document handle from project_open.")] string handle,
        [Description("The resource operations to apply.")] ResourceOp[] ops,
        [Description("Simulate against a copy and report the impact without committing.")] bool dryRun = false)
    {
        if (ops.Length == 0)
        {
            throw new McpToolException("No operations supplied.");
        }

        var session = SessionStore.GetForWrite(handle);

        return WriteEngine.Run(session, dryRun, (project, rejected) =>
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
                        resource.Name = op.Name ?? "New resource";
                        if (op.MaxUnits is not null) resource.Set(ResourceField.MaxUnits, op.MaxUnits);
                        if (op.Type is not null && Enum.TryParse<ResourceType>(op.Type, true, out var rt))
                        {
                            resource.Type = rt;
                        }

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
                        if (op.MaxUnits is not null) resource.Set(ResourceField.MaxUnits, op.MaxUnits);

                        var uid = op.Uid.Value;
                        var expectedName = resource.Name;
                        var expectedUnits = resource.MaxUnits;
                        current.Checks.Add(file =>
                        {
                            var check = file.GetResourceByUniqueID(uid);
                            return check is not null && check.Name == expectedName && check.MaxUnits == expectedUnits
                                ? null
                                : WriteEngine.Reject(uid, "resource", expectedName, check?.Name,
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

            return pending;
        });
    }

    [McpServerTool(Name = "calendars_write")]
    [Description(
        "Manage working calendars: create and delete them, and add exceptions for holidays, site "
        + "shutdowns, or a rainy season. " + VerifiedContract)]
    public static WriteResult CalendarsWrite(
        [Description("Document handle from project_open.")] string handle,
        [Description("The calendar operations to apply.")] CalendarOp[] ops,
        [Description("Simulate against a copy and report the impact without committing.")] bool dryRun = false)
    {
        if (ops.Length == 0)
        {
            throw new McpToolException("No operations supplied.");
        }

        var session = SessionStore.GetForWrite(handle);

        return WriteEngine.Run(session, dryRun, (project, rejected) =>
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

                        var calendar = project.AddDefaultBaseCalendar();
                        calendar.Name = op.Name;
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

                        if (op.Working == true)
                        {
                            rejected.Add(new RejectedWrite
                            {
                                Field = "working",
                                Reason = "This backend can only add non-working exceptions (holidays, shutdowns, "
                                         + "weather stand-downs). Turning a non-working day into a working one "
                                         + "means editing the calendar's hour ranges — do that in Microsoft Project.",
                            });
                            continue;
                        }

                        var toDate = QueryTools.ParseDate(op.To) ?? fromDate.Value;
                        var start = DateOnly.FromDateTime(fromDate.Value);
                        var end = DateOnly.FromDateTime(toDate);
                        calendar.AddCalendarException(start, end);

                        var calendarName = calendar.Name;
                        current.Checks.Add(file =>
                        {
                            var check = calendarName is null ? null : file.GetCalendarByName(calendarName);
                            return check?.GetException(start) is not null
                                ? null
                                : WriteEngine.Reject(-1, "exception", start.ToString("yyyy-MM-dd"), null,
                                    "The exception was not present when re-read after the write.");
                        });
                        break;
                    }

                    case "set_working_hours":
                    {
                        var current = new PendingOp { Label = "set_working_hours" };
                        pending.Add(current);
                        rejected.Add(new RejectedWrite
                        {
                            Field = "set_working_hours",
                            Reason = "Editing the weekly working-hours pattern is not implemented on this backend. "
                                     + "Use set_exception for specific dates, or edit the calendar in Microsoft Project.",
                        });
                        break;
                    }

                    default:
                        rejected.Add(WriteEngine.Reject(-1, "op", op.Op, null,
                            $"Unknown operation '{op.Op}'. Use create, delete, set_exception, or set_working_hours."));
                        break;
                }
            }

            return pending;
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
        [Description("save_baseline, clear_baseline, set_status_date, reschedule_incomplete, level_resources, or recalculate.")]
        string op,
        [Description("Baseline slot 0-10 for save_baseline and clear_baseline. Defaults to 0.")]
        int baseline = 0,
        [Description("Status date for set_status_date and reschedule_incomplete, yyyy-MM-dd.")]
        string? statusDate = null,
        [Description(
            "Allow save_baseline to replace a baseline that already holds data. Off by default: "
            + "overwriting one destroys the original plan every variance is measured against.")]
        bool overwriteBaseline = false,
        [Description("Simulate against a copy and report the impact without committing.")] bool dryRun = false)
    {
        var session = SessionStore.GetForWrite(handle);
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
                    var current = new PendingOp { Label = "recalculate" };
                    pending.Add(current);

                    // Asking for a recalculation is the authorisation. From here on this document
                    // is scheduled by our engine, so later writes may reschedule it too.
                    if (!session.MayReschedule)
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

                    var report = Analysis.CpmScheduler.Run(project);
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

                case "reschedule_incomplete":
                {
                    var current = new PendingOp { Label = "reschedule_incomplete" };
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

                    Analysis.CpmScheduler.Run(project);

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
    }

    private static DateTime? SafeBaselineFinish(MPXJ.Net.Task task, int slot)
    {
        try { return task.GetBaselineFinish(slot); } catch { return null; }
    }
}
