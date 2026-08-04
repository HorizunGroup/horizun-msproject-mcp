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
/// so this walks each assignment's date span day by day and sums the committed units.
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

            var capacity = resource.MaxUnits ?? 100.0;
            if (capacity <= 0)
            {
                capacity = 100.0;
            }

            // Committed units per calendar day, plus which tasks contributed.
            var perDay = new SortedDictionary<DateTime, (double Units, HashSet<int> Tasks)>();

            foreach (var assignment in resource.TaskAssignments)
            {
                var start = assignment.Start ?? assignment.Task?.Start;
                var finish = assignment.Finish ?? assignment.Task?.Finish;
                if (start is null || finish is null)
                {
                    continue;
                }

                var units = assignment.Units ?? 100.0;
                var taskUid = assignment.Task?.UniqueID ?? -1;

                for (var day = start.Value.Date; day <= finish.Value.Date; day = day.AddDays(1))
                {
                    if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    {
                        continue;
                    }

                    if (!perDay.TryGetValue(day, out var slot))
                    {
                        slot = (0, new HashSet<int>());
                    }

                    perDay[day] = (slot.Units + units, slot.Tasks.Append(taskUid).ToHashSet());
                }
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
                if (slot.Units > capacity + 0.001)
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

        return windows.OrderByDescending(w => w.PeakUnits / w.MaxUnits).ToList();
    }
}
