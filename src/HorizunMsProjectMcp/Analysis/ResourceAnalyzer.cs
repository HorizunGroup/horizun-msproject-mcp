using MPXJ.Net;

namespace Horizun.ProjectMcp.Analysis;

public sealed record OverallocationWindow
{
    public required int ResourceUid { get; init; }
    public required string? ResourceName { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public required double PeakUnits { get; init; }
    public required double MaxUnits { get; init; }
    public required IReadOnlyList<int> TaskUids { get; init; }
}

/// <summary>
/// Works out which resources are overbooked and when. MPXJ carries no overallocation flag,
/// so for each working day this finds the most units committed at the same moment, against the
/// units the resource is available at that day.
/// </summary>
public static class ResourceAnalyzer
{
    /// <summary>Resource UIDs that are overbooked on at least one day.</summary>
    public static HashSet<int> OverallocatedUids(ProjectFile project) =>
        Find(project).Select(w => w.ResourceUid).ToHashSet();

    public static IReadOnlyList<OverallocationWindow> Find(ProjectFile project)
    {
        var windows = new List<OverallocationWindow>();

        foreach (var resource in project.Resources)
        {
            var uid = resource.UniqueID;
            if (uid is null)
            {
                continue;
            }

            // Material and cost resources cannot be "overallocated" in the scheduling sense.
            if (resource.Type is not null && resource.Type != ResourceType.Work)
            {
                continue;
            }

            var capacity = MaxUnits(resource);
            var calendar = project.DefaultCalendar;

            var spans = new List<(DateTime Start, DateTime Finish, double Units, int TaskUid)>();
            foreach (var assignment in resource.TaskAssignments)
            {
                var start = assignment.Start ?? assignment.Task?.Start;
                var finish = assignment.Finish ?? assignment.Task?.Finish;
                if (start is null || finish is null || finish <= start)
                {
                    continue;
                }

                spans.Add((start.Value, finish.Value, Convert.ToDouble(assignment.Units ?? 100.0),
                    assignment.Task?.UniqueID ?? -1));
            }

            // Peak units committed at any one moment of each day, and which tasks were at work then.
            // Summing whole days counted a task finishing at noon and its successor starting at one
            // as working together; they never are.
            var perDay = new SortedDictionary<DateTime, (double Units, HashSet<int> Tasks)>();
            foreach (var day in spans.SelectMany(s => Days(s.Start, s.Finish)).Distinct())
            {
                if (!IsWorkingDay(calendar, day))
                {
                    continue;
                }

                var dayEnd = day.AddDays(1);
                var today = spans.Where(s => s.Start < dayEnd && s.Finish > day).ToList();
                var (peak, tasks) = Peak(today, day, dayEnd, CapacityOn(resource, day, capacity));
                perDay[day] = (peak, tasks);
            }

            // Collapse consecutive overbooked days into one window so the report stays readable.
            DateTime? runStart = null;
            DateTime runEnd = default;
            var runPeak = 0.0;
            var runTasks = new HashSet<int>();

            void Flush()
            {
                if (runStart is null)
                {
                    return;
                }

                windows.Add(new OverallocationWindow
                {
                    ResourceUid = uid.Value,
                    ResourceName = resource.Name,
                    From = runStart.Value.ToString("yyyy-MM-dd"),
                    To = runEnd.ToString("yyyy-MM-dd"),
                    PeakUnits = Math.Round(runPeak, 2),
                    MaxUnits = capacity,
                    TaskUids = runTasks.OrderBy(x => x).ToList(),
                });

                runStart = null;
                runPeak = 0;
                runTasks = new HashSet<int>();
            }

            foreach (var (day, slot) in perDay)
            {
                if (slot.Units > CapacityOn(resource, day, capacity) + 0.001)
                {
                    if (runStart is null)
                    {
                        runStart = day;
                    }
                    else if (day > runEnd.AddDays(3))
                    {
                        // A gap longer than a weekend means a separate episode.
                        Flush();
                        runStart = day;
                    }

                    runEnd = day;
                    runPeak = Math.Max(runPeak, slot.Units);
                    foreach (var t in slot.Tasks)
                    {
                        runTasks.Add(t);
                    }
                }
                else if (runStart is not null && day > runEnd.AddDays(3))
                {
                    Flush();
                }
            }

            Flush();
        }

        return windows.OrderByDescending(w => w.PeakUnits / Math.Max(w.MaxUnits, 1)).ToList();
    }

    /// <summary>
    /// The resource's maximum units as Project shows them. MPXJ answers <c>MaxUnits</c> from the
    /// availability row covering today, and 100 when none does — so a crew of six available from the
    /// project's start next month read as one. The row covering today still wins; otherwise the
    /// availability the resource is given at all.
    /// </summary>
    public static double MaxUnits(Resource resource)
    {
        var rows = Availability(resource);
        var now = DateTime.Now;
        var current = rows.FindIndex(r => r.From <= now && now <= r.To);
        if (current >= 0)
        {
            return rows[current].Units; // 0 is a resource that is not available now, and says so
        }

        var units = rows.Count > 0 ? rows.Max(r => r.Units) : Convert.ToDouble(resource.MaxUnits ?? 100.0);
        return units > 0 ? units : 100.0;
    }

    private static double CapacityOn(Resource resource, DateTime day, double fallback)
    {
        var rows = Availability(resource);
        var index = rows.FindIndex(r => r.From.Date <= day && day <= r.To);
        return index >= 0 ? rows[index].Units : fallback;
    }

    private static List<(DateTime From, DateTime To, double Units)> Availability(Resource resource)
    {
        var rows = new List<(DateTime, DateTime, double)>();
        try
        {
            foreach (var row in resource.Availability)
            {
                if (row.Range is { } range && row.Units is { } units)
                {
                    rows.Add((range.Start ?? DateTime.MinValue, range.End ?? DateTime.MaxValue, Convert.ToDouble(units)));
                }
            }
        }
        catch
        {
            // No availability table is the same as one row of MaxUnits.
        }

        return rows;
    }

    private static IEnumerable<DateTime> Days(DateTime start, DateTime finish)
    {
        for (var day = start.Date; day < finish; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    private static bool IsWorkingDay(ProjectCalendar? calendar, DateTime day)
    {
        if (calendar is not null)
        {
            try
            {
                return calendar.IsWorkingDate(DateOnly.FromDateTime(day));
            }
            catch
            {
                // Fall through to the week the engine assumes without a calendar.
            }
        }

        return day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    /// <summary>The highest total of units at work at the same moment within one day.</summary>
    private static (double Units, HashSet<int> Tasks) Peak(
        IReadOnlyList<(DateTime Start, DateTime Finish, double Units, int TaskUid)> spans,
        DateTime dayStart, DateTime dayEnd, double capacity)
    {
        var edges = spans.SelectMany(s => new[]
            {
                (At: s.Start < dayStart ? dayStart : s.Start, Delta: s.Units),
                (At: s.Finish > dayEnd ? dayEnd : s.Finish, Delta: -s.Units),
            })
            .OrderBy(e => e.At).ThenBy(e => e.Delta) // a finish before a start at the same minute
            .ToList();

        var running = 0.0;
        var peak = 0.0;
        var over = new HashSet<int>();
        foreach (var edge in edges)
        {
            running += edge.Delta;
            if (running > peak)
            {
                peak = running;
            }

            if (edge.Delta > 0 && running > capacity + 0.001)
            {
                foreach (var s in spans.Where(s => s.Start <= edge.At && s.Finish > edge.At))
                {
                    over.Add(s.TaskUid);
                }
            }
        }

        return (Math.Round(peak, 2), over.Count > 0 ? over : spans.Select(s => s.TaskUid).ToHashSet());
    }
}
