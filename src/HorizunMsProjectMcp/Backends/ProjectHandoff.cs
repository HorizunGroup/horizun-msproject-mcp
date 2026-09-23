using System.Xml;
using System.Xml.Linq;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Backends;

/// <summary>
/// Writes a schedule as the MSPDI file handed to Microsoft Project — for it to calculate, or to
/// save as a native .mpp.
/// </summary>
/// <remarks>
/// <para>
/// MSPDI is Project's own XML format, yet a plain export does not survive a trip through it. On a
/// real 126-task schedule, Project re-opening even <i>its own</i> XML export put every single task on
/// different dates. What follows is what it takes for Project to compute from this file exactly what
/// it computes from the original .mpp — measured on seven production schedules, 8,823 tasks counting
/// summaries, where it now does on every task, to the minute.
/// </para>
/// <para>
/// <b>Work travels day by day</b>, because a split task keeps its gaps only in its assignment's
/// timephased data. <b>Placeholder assignments mostly do not</b>: Project schedules a placeholder
/// against the unassigned resource's calendar rather than the task's, so one goes over only when it
/// holds something the task cannot say for itself — a split or contour on an unstarted task, the span
/// of a zero-duration task marked done. <b>Started tasks keep their own duration</b>, not the one the
/// timephased writer recomputes. <b>Blank rows go over blank</b>, rather than as the tasks MPXJ
/// makes of them. And <b>what an edit left behind is dropped</b>: an assignment still stating the old
/// work, start or finish is what Project believes, so it would undo the edit to match.
/// </para>
/// </remarks>
public static class ProjectHandoff
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/project";
    private const string UnassignedResource = "-65535";

    public static void Write(ProjectFile project, string path)
    {
        var resized = project.ResourceAssignments
            .Where(a => a.UniqueID is not null && Writes.ProgressRules.Resized.TryGetValue(a, out _))
            .Select(a => a.UniqueID!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToHashSet();

        new MSPDIWriter { WriteTimephasedData = true }.Write(project, path);

        var document = XDocument.Load(path);
        var root = document.Root ?? throw new McpToolException("The MSPDI export came back empty.");

        var tasks = root.Element(Ns + "Tasks")?.Elements(Ns + "Task")
                        .Where(t => Text(t, "UID") is not null)
                        .GroupBy(t => Text(t, "UID")!)
                        .ToDictionary(g => g.Key, g => g.First())
                    ?? new Dictionary<string, XElement>();

        RestoreStartedDurations(project, tasks);
        var blankRows = BlankRowsAsBlank(root, tasks);

        var assignments = root.Element(Ns + "Assignments");
        if (assignments is not null)
        {
            foreach (var assignment in assignments.Elements(Ns + "Assignment").ToList())
            {
                var taskUid = Text(assignment, "TaskUID");
                if (taskUid is null || !tasks.TryGetValue(taskUid, out var task))
                {
                    continue;
                }

                if (blankRows.Contains(taskUid))
                {
                    assignment.Remove();
                    continue;
                }

                // Resized by an edit: its finish and day-by-day work describe the old size, and
                // Project would shrink the task back to fit them.
                if (Text(assignment, "UID") is { } assignmentUid && resized.Contains(assignmentUid))
                {
                    assignment.Element(Ns + "Finish")?.Remove();
                    assignment.Elements(Ns + "TimephasedData").Remove();
                    continue;
                }

                // A started task: its actual start, actual duration and remaining work — the task's
                // own fields — are what Project reschedules it from. Its placeholder assignment goes,
                // because Project lays the actual work of a placeholder on the unassigned resource's
                // calendar, not the task's; where those differ (a file last saved on a Spanish install
                // gets 9:00 to 19:00) every task in progress lost hours. The one exception is a task of
                // zero duration whose actual dates still span time — a milestone marked done over a
                // fortnight — because that span exists nowhere but in its placeholder: without it,
                // Project collapses the milestone onto its actual start and its successors move up.
                if (Text(task, "ActualStart") is not null)
                {
                    if (Text(assignment, "ResourceUID") == UnassignedResource && !OnlyPlaceholderHoldsItsSpan(task))
                    {
                        assignment.Remove();
                    }

                    continue;
                }

                if (Text(assignment, "ResourceUID") == UnassignedResource)
                {
                    if (CarriesNothing(assignment) || DisagreesWith(assignment, task))
                    {
                        assignment.Remove();
                    }
                }
                else if (DisagreesWith(assignment, task))
                {
                    // A real resource stays assigned, but its old day-by-day distribution would pull
                    // the task back to its previous shape. Dropped, Project redistributes the work by
                    // its own rules, as it would after the same edit made by hand.
                    assignment.Elements(Ns + "TimephasedData").Remove();
                }
            }
        }

        document.Save(path);
    }

    /// <summary>
    /// Writing timephased data, MPXJ recomputes the duration of a started task from its assignment —
    /// and a finished milestone, 0 days over a fortnight of actual dates, comes out as 80 hours. With
    /// its placeholder gone, Project would take that at its word. The plain writer keeps the duration
    /// the schedule holds, so started tasks take theirs from it.
    /// </summary>
    private static void RestoreStartedDurations(ProjectFile project, IReadOnlyDictionary<string, XElement> tasks)
    {
        if (!tasks.Values.Any(t => Text(t, "ActualStart") is not null))
        {
            return;
        }

        var plain = Path.Combine(Path.GetTempPath(), $"hzpm-plain-{Guid.NewGuid():N}.xml");
        try
        {
            new MSPDIWriter().Write(project, plain);
            foreach (var source in XDocument.Load(plain).Root?.Element(Ns + "Tasks")?.Elements(Ns + "Task")
                                   ?? Enumerable.Empty<XElement>())
            {
                if (Text(source, "ActualStart") is null
                    || Text(source, "UID") is not { } uid
                    || !tasks.TryGetValue(uid, out var target)
                    || Text(source, "Duration") is not { } duration)
                {
                    continue;
                }

                var element = target.Element(Ns + "Duration");
                if (element is not null)
                {
                    element.Value = duration;
                }
            }
        }
        finally
        {
            try
            {
                File.Delete(plain);
            }
            catch
            {
                // A leftover temp file is not worth failing the hand-off over.
            }
        }
    }

    /// <summary>
    /// A row inserted in Project and left empty is not a task to Project — it has no dates and does
    /// not count towards its summary — but MPXJ reads it as one, dated at the project start with an
    /// estimated day. Handed over like that, Project believed it: a summary on a real schedule grew
    /// back two years to reach it. Such a row goes over as the blank row it is.
    /// </summary>
    private static HashSet<string> BlankRowsAsBlank(XElement root, IReadOnlyDictionary<string, XElement> tasks)
    {
        var linked = root.Descendants(Ns + "PredecessorLink")
            .Select(l => Text(l, "PredecessorUID"))
            .Where(u => u is not null)
            .ToHashSet();

        var blank = new HashSet<string>();
        foreach (var (uid, task) in tasks)
        {
            var isBlankRow = Text(task, "Manual") == "1"
                             && string.IsNullOrWhiteSpace(Text(task, "Name"))
                             && Text(task, "Estimated") == "1"
                             && Text(task, "Summary") != "1"
                             && Text(task, "ActualStart") is null
                             && Text(task, "PercentComplete") is null or "0"
                             && !task.Elements(Ns + "PredecessorLink").Any()
                             && !linked.Contains(uid);
            if (!isBlankRow)
            {
                continue;
            }

            var keep = new HashSet<string> { "UID", "ID", "IsNull", "OutlineNumber", "OutlineLevel", "WBS" };
            foreach (var field in task.Elements().Where(e => !keep.Contains(e.Name.LocalName)).ToList())
            {
                field.Remove();
            }

            var isNull = task.Element(Ns + "IsNull");
            if (isNull is null)
            {
                task.Add(new XElement(Ns + "IsNull", "1"));
            }
            else
            {
                isNull.Value = "1";
            }

            blank.Add(uid);
        }

        return blank;
    }

    private static bool OnlyPlaceholderHoldsItsSpan(XElement task) =>
        (Minutes(Text(task, "Duration")) ?? 0) <= 0
        && DateTime.TryParse(Text(task, "ActualStart"), out var started)
        && DateTime.TryParse(Text(task, "ActualFinish") ?? Text(task, "Finish"), out var finished)
        && finished > started;

    /// <summary>Unsplit and flat, on an unstarted task: everything it says, the task already says.</summary>
    private static bool CarriesNothing(XElement assignment) =>
        Text(assignment, "WorkContour") is null or "0"
        && assignment.Elements(Ns + "TimephasedData").Count() <= 1
        && Text(assignment, "Stop") == Text(assignment, "Resume");

    /// <summary>
    /// An assignment left over from before an edit: it starts elsewhere, or its work no longer spans
    /// the task's duration at its units.
    /// </summary>
    private static bool DisagreesWith(XElement assignment, XElement task)
    {
        var taskStart = Text(task, "Start");
        var assignmentStart = Text(assignment, "Start");
        if (taskStart is not null && assignmentStart is not null && !SameMinute(taskStart, assignmentStart))
        {
            return true;
        }

        var duration = Minutes(Text(task, "Duration"));
        var work = Minutes(Text(assignment, "Work")) ?? 0;

        // Its day-by-day work no longer adds up to its total: the total was changed by an edit and
        // the distribution is the one from before it.
        var spread = assignment.Elements(Ns + "TimephasedData")
            .Where(t => Text(t, "Type") is "1" or "2")
            .Sum(t => Minutes(Text(t, "Value")) ?? 0);
        if (assignment.Elements(Ns + "TimephasedData").Any() && Math.Abs(spread - work) > 1)
        {
            return true;
        }
        var units = double.TryParse(Text(assignment, "Units"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var u) ? u : 1;

        if (duration is null || units <= 0)
        {
            return false;
        }

        return Math.Abs(work / units - duration.Value) > 1;
    }

    private static bool SameMinute(string a, string b) =>
        DateTime.TryParse(a, out var x) && DateTime.TryParse(b, out var y)
            ? Math.Abs((x - y).TotalMinutes) < 1
            : a == b;

    private static double? Minutes(string? isoDuration)
    {
        if (string.IsNullOrEmpty(isoDuration))
        {
            return null;
        }

        try
        {
            return XmlConvert.ToTimeSpan(isoDuration).TotalMinutes;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Text(XElement element, string name)
    {
        var value = element.Element(Ns + name)?.Value;
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
