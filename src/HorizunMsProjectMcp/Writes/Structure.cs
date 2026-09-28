using MPXJ.Net;

namespace Horizun.ProjectMcp.Writes;

/// <summary>
/// Keeps the outline a schedule's rows, levels, ids and codes describe the one Microsoft Project
/// would build from them.
/// </summary>
/// <remarks>
/// <para>MPXJ numbers what is added to a schedule it built itself, but not to one read from a file:
/// there, a new task comes out with no outline level, no outline number and a WBS of "0", and a new
/// resource, its calendar and a new assignment with no unique id.</para>
/// <para>It also keeps tasks in the order they were added, and writes them in that order. A task
/// added under an earlier summary after later rows existed was written after them, and Project —
/// which builds its outline from row order and level — hung it under the wrong summary. And an
/// 'outline' edit changed a level without changing the hierarchy MPXJ keeps, so summaries went on
/// being read as detail tasks.</para>
/// <para>So: rows are numbered in outline order, the hierarchy is rebuilt from row order and level,
/// and outline numbers follow. A WBS that only mirrored the outline number moves with it; a code the
/// schedule chose stays, and a new task under it continues it.</para>
/// </remarks>
public static class Structure
{
    public static void Normalize(ProjectFile project)
    {
        EnsureIds(project);

        var summaryRow = project.Tasks.FirstOrDefault(IsProjectSummary);
        var rows = new List<(MPXJ.Net.Task Task, int Depth)>();
        Walk(Roots(project, summaryRow), 1, rows);

        // A row outside the hierarchy — a blank row, an orphan — keeps its place: right after the
        // row that preceded it. Numbered with the others, never left holding an id another row takes.
        var reached = rows.Select(r => r.Task).ToHashSet();
        foreach (var stray in project.Tasks.Where(t => !reached.Contains(t) && t != summaryRow)
                     .OrderBy(t => t.ID ?? int.MaxValue).ToList())
        {
            var before = rows.FindLastIndex(r => (r.Task.ID ?? int.MaxValue) < (stray.ID ?? int.MaxValue)
                                                 && !ReferenceEquals(r.Task, stray));
            rows.Insert(before + 1, (stray, stray.OutlineLevel ?? Math.Max(1, before >= 0 ? rows[before].Depth : 1)));
        }

        var outOfPlace = false;
        for (var i = 0; i < rows.Count; i++)
        {
            var (task, depth) = rows[i];
            task.OutlineLevel ??= depth;
            outOfPlace |= task.ID != i + 1 || task.OutlineLevel != depth || task.OutlineNumber is null;
        }

        if (!outOfPlace)
        {
            return;
        }

        var previousOutline = rows.ToDictionary(r => r.Task, r => r.Task.OutlineNumber);
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].Task.ID = i + 1;
        }

        if (summaryRow is not null)
        {
            summaryRow.ID = 0;
            summaryRow.OutlineLevel = 0;
        }

        // MPXJ orders the rows by id and hangs each under the nearest shallower row before it —
        // exactly how Project reads an outline.
        project.UpdateStructure();
        Number(Roots(project, project.Tasks.FirstOrDefault(IsProjectSummary)), null, previousOutline);
    }

    /// <summary>Places a task just added — the last of its siblings — before anything else is set on
    /// it, so a WBS given in the same operation wins over the one it follows from its parent.</summary>
    public static void Place(ProjectFile project, MPXJ.Net.Task task, MPXJ.Net.Task? parent)
    {
        if (parent is not null && IsProjectSummary(parent))
        {
            parent = null;
        }

        var index = parent is null
            ? Roots(project, project.Tasks.FirstOrDefault(IsProjectSummary)).Count
            : parent.ChildTasks.Count;
        var outline = parent is null ? $"{index}" : $"{parent.OutlineNumber}.{index}";
        task.OutlineLevel = parent is null ? 1 : (parent.OutlineLevel ?? 0) + 1;
        task.OutlineNumber = outline;
        task.WBS = parent is null || string.IsNullOrWhiteSpace(parent.WBS) ? outline : $"{parent.WBS}.{index}";
    }

    public static bool IsProjectSummary(MPXJ.Net.Task task) =>
        task.UniqueID == 0 || task.ID == 0 && task.OutlineLevel == 0;

    private static List<MPXJ.Net.Task> Roots(ProjectFile project, MPXJ.Net.Task? summaryRow) =>
        project.ChildTasks.Where(t => !IsProjectSummary(t))
            .Concat(summaryRow?.ChildTasks ?? Enumerable.Empty<MPXJ.Net.Task>())
            .Distinct()
            .ToList();

    /// <summary>Rows in outline order: each task, then everything beneath it. Siblings keep their
    /// row order, and a task added last is last among them.</summary>
    private static void Walk(IEnumerable<MPXJ.Net.Task> siblings, int depth, List<(MPXJ.Net.Task, int)> rows)
    {
        foreach (var task in siblings.OrderBy(t => t.ID ?? int.MaxValue))
        {
            rows.Add((task, depth));
            Walk(task.ChildTasks, depth + 1, rows);
        }
    }

    private static void Number(
        IEnumerable<MPXJ.Net.Task> siblings, MPXJ.Net.Task? parent,
        IReadOnlyDictionary<MPXJ.Net.Task, string?> previousOutline)
    {
        var index = 0;
        foreach (var task in siblings.OrderBy(t => t.ID ?? int.MaxValue))
        {
            index++;
            var outline = parent is null ? $"{index}" : $"{parent.OutlineNumber}.{index}";
            previousOutline.TryGetValue(task, out var before);
            var wbsFollowsOutline = string.IsNullOrWhiteSpace(task.WBS) || task.WBS == before;
            if (wbsFollowsOutline)
            {
                var parentWbs = parent?.WBS;
                task.WBS = parent is null || string.IsNullOrWhiteSpace(parentWbs) || parentWbs == parent.OutlineNumber
                    ? outline
                    : $"{parentWbs}.{index}";
            }

            task.OutlineNumber = outline;
            task.OutlineLevel = parent is null ? 1 : (parent.OutlineLevel ?? 0) + 1;
            Number(task.ChildTasks, task, previousOutline);
        }
    }

    /// <summary>Gives every task, calendar, resource and assignment without a unique id one — all a
    /// writer needs, without touching the outline.</summary>
    public static void EnsureIds(ProjectFile project)
    {
        foreach (var task in project.Tasks.Where(t => t.UniqueID is null))
        {
            task.UniqueID = project.Tasks.Select(t => t.UniqueID ?? 0).DefaultIfEmpty(0).Max() + 1;
        }

        // A resource's own calendar too: without an id the MSPDI writer fails on a null reference.
        foreach (var calendar in project.Calendars.Where(c => c.UniqueID is null))
        {
            calendar.UniqueID = project.Calendars.Select(c => c.UniqueID ?? 0).DefaultIfEmpty(0).Max() + 1;
        }

        foreach (var resource in project.Resources.Where(r => r.UniqueID is null))
        {
            resource.UniqueID = project.Resources.Select(r => r.UniqueID ?? 0).DefaultIfEmpty(0).Max() + 1;
            resource.ID ??= project.Resources.Select(r => r.ID ?? 0).DefaultIfEmpty(0).Max() + 1;
        }

        foreach (var assignment in project.ResourceAssignments.Where(a => a.UniqueID is null))
        {
            assignment.UniqueID = project.ResourceAssignments.Select(a => a.UniqueID ?? 0).DefaultIfEmpty(0).Max() + 1;
        }
    }
}
