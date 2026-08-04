using System.ComponentModel;
using Horizun.ProjectMcp.Analysis;
using Horizun.ProjectMcp.Backends;
using Horizun.ProjectMcp.Model;
using ModelContextProtocol.Server;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Tools;

public sealed record ProjectInfo
{
    public required string Handle { get; init; }
    public required string Path { get; init; }
    public string? Name { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Company { get; init; }
    public string? StartDate { get; init; }
    public string? FinishDate { get; init; }
    public string? StatusDate { get; init; }
    public required int Tasks { get; init; }
    public required int LeafTasks { get; init; }
    public required int SummaryTasks { get; init; }
    public required int Milestones { get; init; }
    public required int Resources { get; init; }
    public required int Assignments { get; init; }
    public required int CriticalTasks { get; init; }
    public required IReadOnlyList<string> Calendars { get; init; }
    public required IReadOnlyList<int> BaselinesWithData { get; init; }
    public required IReadOnlyDictionary<string, string> CustomFieldAliases { get; init; }
    public required IReadOnlyDictionary<string, int> TasksByStatus { get; init; }
}

public sealed record TimephasedBucket
{
    public required string Period { get; init; }
    public required double Value { get; init; }
}

public sealed record TimephasedResult
{
    public required string Measure { get; init; }
    public required string Granularity { get; init; }
    public required string RollUp { get; init; }
    public required int Buckets { get; init; }
    public required IReadOnlyList<TimephasedBucket> Series { get; init; }
    public required IReadOnlyList<TimephasedBucket> Cumulative { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

[McpServerToolType]
public static class QueryTools
{
    private const int MaxBuckets = 2000;

    [McpServerTool(Name = "project_info")]
    [Description(
        "Summarise an open schedule: dates, counts, calendars, which baselines actually hold data, "
        + "and the aliases of the custom fields. Read this before tasks_query — without the aliases "
        + "nobody can tell what Text1 means in this particular schedule.")]
    public static ProjectInfo ProjectInfo(
        [Description("Document handle from project_open.")] string handle)
    {
        var session = SessionStore.Get(handle);
        var project = session.File;
        var properties = project.ProjectProperties;
        var leaves = ScheduleAnalyzer.Leaves(project).ToList();

        var baselines = new List<int>();
        if (project.Tasks.Any(t => t.BaselineFinish is not null))
        {
            baselines.Add(0);
        }

        for (var n = 1; n <= 10; n++)
        {
            var index = n;
            if (project.Tasks.Any(t =>
                {
                    try { return t.GetBaselineFinish(index) is not null; }
                    catch { return false; }
                }))
            {
                baselines.Add(n);
            }
        }

        var aliases = new Dictionary<string, string>();
        foreach (var field in project.CustomFields)
        {
            var alias = field.Alias;
            var name = field.FieldType?.ToString();
            if (!string.IsNullOrWhiteSpace(alias) && !string.IsNullOrWhiteSpace(name))
            {
                aliases[name!] = alias!;
            }
        }

        var byStatus = new Dictionary<string, int>
        {
            ["not_started"] = leaves.Count(t => (t.PercentageComplete ?? 0) <= 0),
            ["in_progress"] = leaves.Count(t => (t.PercentageComplete ?? 0) is > 0 and < 100),
            ["complete"] = leaves.Count(t => (t.PercentageComplete ?? 0) >= 100),
        };

        return new ProjectInfo
        {
            Handle = handle,
            Path = session.Path,
            Name = properties.Name,
            Title = properties.ProjectTitle,
            Author = properties.Author,
            Company = properties.Company,
            StartDate = MpxjMapper.Iso(properties.StartDate),
            FinishDate = MpxjMapper.Iso(MpxjBackend.ProjectFinish(project)),
            StatusDate = MpxjMapper.Iso(properties.StatusDate),
            Tasks = project.Tasks.Count,
            LeafTasks = leaves.Count,
            SummaryTasks = project.Tasks.Count(t => t.Summary),
            Milestones = project.Tasks.Count(t => t.Milestone),
            Resources = project.Resources.Count,
            Assignments = project.ResourceAssignments.Count,
            CriticalTasks = leaves.Count(t => t.Critical),
            Calendars = project.Calendars.Select(c => c.Name ?? "(unnamed)").ToList(),
            BaselinesWithData = baselines,
            CustomFieldAliases = aliases,
            TasksByStatus = byStatus,
        };
    }

    [McpServerTool(Name = "tasks_query")]
    [Description(
        "The workhorse read. Filter, sort, and page through tasks without pulling the whole schedule "
        + "into context. Every task is identified by its stable uid — the id field is the row number "
        + "and shifts when tasks are inserted, so never use it to address a task in a write. "
        + "Defaults to a compact field set and 50 rows; ask for more only when you need it.")]
    public static Page<TaskDto> TasksQuery(
        [Description("Document handle from project_open.")] string handle,
        [Description("Restrict to these task uids.")] int[]? uids = null,
        [Description("Only tasks whose WBS starts with this prefix, e.g. '1.2'.")] string? wbsPrefix = null,
        [Description("Case-insensitive substring match on name and notes.")] string? text = null,
        [Description("true = only critical tasks, false = only non-critical, null = both.")] bool? critical = null,
        [Description("true = only milestones, false = exclude milestones, null = both.")] bool? milestone = null,
        [Description("Status filter: not_started, in_progress, complete, or late.")] string? status = null,
        [Description("Only tasks this resource is assigned to (name substring).")] string? resource = null,
        [Description("Only tasks whose float is at or below this many days — use 0 to find the driving work.")]
        double? maxFloatDays = null,
        [Description("Only tasks whose duration exceeds this many days.")] double? minDurationDays = null,
        [Description("Date filter field: start, finish, baseline_finish, or deadline.")] string? dateField = null,
        [Description("Date filter lower bound, yyyy-MM-dd.")] string? dateFrom = null,
        [Description("Date filter upper bound, yyyy-MM-dd.")] string? dateTo = null,
        [Description("Custom field filter, e.g. 'Text1'. Pair with customValue.")] string? customField = null,
        [Description("Value the custom field must contain.")] string? customValue = null,
        [Description("Shape: flat (default), leaf_only, or summary_only.")] string shape = "flat",
        [Description("Sort by start (default), finish, float, duration, or wbs.")] string sort = "start",
        [Description("Include the Text1..Text30 custom fields on each row. Off by default — they are bulky.")]
        bool includeCustom = false,
        [Description("Maximum rows to return. Defaults to 50.")] int limit = 50,
        [Description("Row offset from a previous nextCursor.")] int cursor = 0)
    {
        var session = SessionStore.Get(handle);
        IEnumerable<MPXJ.Net.Task> query = session.File.Tasks.Where(t => t.UniqueID is not null and not 0);

        query = shape.Trim().ToLowerInvariant() switch
        {
            "leaf_only" => query.Where(t => !t.Summary),
            "summary_only" => query.Where(t => t.Summary),
            _ => query,
        };

        if (uids is { Length: > 0 })
        {
            var wanted = uids.ToHashSet();
            query = query.Where(t => wanted.Contains(t.UniqueID!.Value));
        }

        if (!string.IsNullOrWhiteSpace(wbsPrefix))
        {
            query = query.Where(t => t.WBS?.StartsWith(wbsPrefix, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            query = query.Where(t =>
                t.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) == true ||
                t.Notes?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (critical is not null)
        {
            query = query.Where(t => t.Critical == critical.Value);
        }

        if (milestone is not null)
        {
            query = query.Where(t => t.Milestone == milestone.Value);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var statusDate = session.File.ProjectProperties.StatusDate ?? DateTime.Today;
            query = status.Trim().ToLowerInvariant() switch
            {
                "not_started" => query.Where(t => (t.PercentageComplete ?? 0) <= 0),
                "in_progress" => query.Where(t => (t.PercentageComplete ?? 0) is > 0 and < 100),
                "complete" => query.Where(t => (t.PercentageComplete ?? 0) >= 100),
                "late" => query.Where(t => (t.PercentageComplete ?? 0) < 100
                                           && t.Finish is not null && t.Finish < statusDate),
                _ => throw new McpToolException(
                    $"Unknown status '{status}'. Use not_started, in_progress, complete, or late."),
            };
        }

        if (!string.IsNullOrWhiteSpace(resource))
        {
            var uidsWithResource = session.File.ResourceAssignments
                .Where(a => a.Resource?.Name?.Contains(resource, StringComparison.OrdinalIgnoreCase) == true)
                .Select(a => a.Task?.UniqueID)
                .Where(u => u is not null)
                .Select(u => u!.Value)
                .ToHashSet();
            query = query.Where(t => uidsWithResource.Contains(t.UniqueID!.Value));
        }

        if (maxFloatDays is not null)
        {
            query = query.Where(t => (MpxjMapper.Days(t.TotalSlack) ?? double.MaxValue) <= maxFloatDays.Value);
        }

        if (minDurationDays is not null)
        {
            query = query.Where(t => (MpxjMapper.Days(t.Duration) ?? 0) > minDurationDays.Value);
        }

        if (!string.IsNullOrWhiteSpace(dateField) && (dateFrom is not null || dateTo is not null))
        {
            var from = ParseDate(dateFrom);
            var to = ParseDate(dateTo);
            Func<MPXJ.Net.Task, DateTime?> selector = dateField.Trim().ToLowerInvariant() switch
            {
                "start" => t => t.Start,
                "finish" => t => t.Finish,
                "baseline_finish" => t => t.BaselineFinish,
                "deadline" => t => t.Deadline,
                _ => throw new McpToolException(
                    $"Unknown dateField '{dateField}'. Use start, finish, baseline_finish, or deadline."),
            };

            query = query.Where(t =>
            {
                var value = selector(t);
                if (value is null) return false;
                if (from is not null && value < from) return false;
                if (to is not null && value > to) return false;
                return true;
            });
        }

        if (!string.IsNullOrWhiteSpace(customField) && !string.IsNullOrWhiteSpace(customValue))
        {
            var slot = Dcma14.ParseTextSlot(customField);
            query = query.Where(t =>
                Dcma14.SafeGetText(t, slot)?.Contains(customValue, StringComparison.OrdinalIgnoreCase) == true);
        }

        query = sort.Trim().ToLowerInvariant() switch
        {
            "finish" => query.OrderBy(t => t.Finish ?? DateTime.MaxValue),
            "float" => query.OrderBy(t => MpxjMapper.Days(t.TotalSlack) ?? double.MaxValue),
            "duration" => query.OrderByDescending(t => MpxjMapper.Days(t.Duration) ?? 0),
            "wbs" => query.OrderBy(t => t.WBS, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderBy(t => t.Start ?? DateTime.MaxValue),
        };

        var all = query.ToList();
        var safeLimit = Math.Clamp(limit, 1, 1000);
        var page = all.Skip(Math.Max(cursor, 0)).Take(safeLimit).ToList();

        return new Page<TaskDto>
        {
            Total = all.Count,
            Returned = page.Count,
            NextCursor = cursor + page.Count < all.Count ? cursor + page.Count : null,
            Items = page.Select(t => MpxjMapper.ToDto(t, includeCustom)).ToList(),
        };
    }

    [McpServerTool(Name = "links_query")]
    [Description(
        "Read the dependency network. Almost always you want the subgraph around a few tasks rather "
        + "than every link, so pass uids and a depth. drivingOnly returns just the chain that actually "
        + "pushes the finish date, which is what you want when asking why the project ends when it does.")]
    public static Page<LinkDto> LinksQuery(
        [Description("Document handle from project_open.")] string handle,
        [Description("Centre the subgraph on these task uids. Omit for the whole network.")] int[]? uids = null,
        [Description("'pred', 'succ', or 'both' (default).")] string direction = "both",
        [Description("How many hops out from the seed tasks. 0 = immediate neighbours only. Defaults to 1.")]
        int depth = 1,
        [Description("Return only the driving chain that sets the project finish date.")]
        bool drivingOnly = false,
        [Description("Maximum rows. Defaults to 200.")] int limit = 200,
        [Description("Row offset from a previous nextCursor.")] int cursor = 0)
    {
        var session = SessionStore.Get(handle);
        var project = session.File;

        List<LinkDto> links;

        if (drivingOnly)
        {
            links = ScheduleAnalyzer.BuildLongestPath(project).ToList();
        }
        else if (uids is { Length: > 0 })
        {
            var wantPred = direction is "pred" or "both";
            var wantSucc = direction is "succ" or "both";
            var frontier = uids.ToHashSet();
            var visited = new HashSet<int>(frontier);
            var collected = new List<Relation>();

            for (var hop = 0; hop <= Math.Max(depth, 0); hop++)
            {
                var next = new HashSet<int>();
                foreach (var uid in frontier)
                {
                    var task = project.GetTaskByUniqueID(uid);
                    if (task is null)
                    {
                        continue;
                    }

                    if (wantPred)
                    {
                        foreach (var relation in task.Predecessors)
                        {
                            collected.Add(relation);
                            var predUid = relation.PredecessorTask?.UniqueID;
                            if (predUid is not null && visited.Add(predUid.Value))
                            {
                                next.Add(predUid.Value);
                            }
                        }
                    }

                    if (wantSucc)
                    {
                        foreach (var relation in task.Successors)
                        {
                            collected.Add(relation);
                            var succUid = relation.SuccessorTask?.UniqueID;
                            if (succUid is not null && visited.Add(succUid.Value))
                            {
                                next.Add(succUid.Value);
                            }
                        }
                    }
                }

                frontier = next;
                if (frontier.Count == 0)
                {
                    break;
                }
            }

            links = collected
                .DistinctBy(r => (r.PredecessorTask?.UniqueID, r.SuccessorTask?.UniqueID, r.Type))
                .Select(r => MpxjMapper.ToDto(r))
                .ToList();
        }
        else
        {
            links = project.Tasks
                .SelectMany(t => t.Predecessors)
                .Select(r => MpxjMapper.ToDto(r))
                .ToList();
        }

        var safeLimit = Math.Clamp(limit, 1, 2000);
        var page = links.Skip(Math.Max(cursor, 0)).Take(safeLimit).ToList();

        return new Page<LinkDto>
        {
            Total = links.Count,
            Returned = page.Count,
            NextCursor = cursor + page.Count < links.Count ? cursor + page.Count : null,
            Items = page,
        };
    }

    [McpServerTool(Name = "resources_query")]
    [Description(
        "Read resources, their assignments, and who is overbooked. Overallocation is computed day by "
        + "day from the assignment spans — Microsoft Project's file format carries no such flag, so this "
        + "is a real calculation rather than a field read.")]
    public static Page<ResourceDto> ResourcesQuery(
        [Description("Document handle from project_open.")] string handle,
        [Description("Name substring filter.")] string? name = null,
        [Description("Only resources that are overbooked on at least one day.")] bool overallocatedOnly = false,
        [Description("Filter by type: work, material, or cost.")] string? type = null,
        [Description("Include each resource's assignments. Off by default — they are bulky.")]
        bool includeAssignments = false,
        [Description("Maximum rows. Defaults to 50.")] int limit = 50,
        [Description("Row offset from a previous nextCursor.")] int cursor = 0)
    {
        var session = SessionStore.Get(handle);
        var overallocated = ResourceAnalyzer.OverallocatedUids(session.File);

        IEnumerable<Resource> query = session.File.Resources.Where(r => r.UniqueID is not null);

        if (!string.IsNullOrWhiteSpace(name))
        {
            query = query.Where(r => r.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            query = query.Where(r => string.Equals(r.Type?.ToString(), type, StringComparison.OrdinalIgnoreCase));
        }

        if (overallocatedOnly)
        {
            query = query.Where(r => overallocated.Contains(r.UniqueID!.Value));
        }

        var all = query.OrderBy(r => r.ID ?? int.MaxValue).ToList();
        var safeLimit = Math.Clamp(limit, 1, 1000);
        var page = all.Skip(Math.Max(cursor, 0)).Take(safeLimit).ToList();

        return new Page<ResourceDto>
        {
            Total = all.Count,
            Returned = page.Count,
            NextCursor = cursor + page.Count < all.Count ? cursor + page.Count : null,
            Items = page
                .Select(r => MpxjMapper.ToDto(r, includeAssignments, overallocated.Contains(r.UniqueID!.Value)))
                .ToList(),
        };
    }

    [McpServerTool(Name = "timephased_query")]
    [Description(
        "Work or cost spread over time — the S-curve. Kept separate from the other reads because it is "
        + "the expensive one: it refuses ranges that would generate an unreasonable number of buckets "
        + "and tells you to coarsen the granularity instead of quietly returning tens of thousands of rows.")]
    public static TimephasedResult TimephasedQuery(
        [Description("Document handle from project_open.")] string handle,
        [Description("'work' (default), 'cost', 'baseline_work', or 'baseline_cost'.")] string measure = "work",
        [Description("'day', 'week' (default), or 'month'.")] string granularity = "week",
        [Description("Restrict to these task uids. Omit for the whole project.")] int[]? uids = null,
        [Description("Range start, yyyy-MM-dd. Defaults to the project start.")] string? from = null,
        [Description("Range end, yyyy-MM-dd. Defaults to the project finish.")] string? to = null)
    {
        var session = SessionStore.Get(handle);
        var project = session.File;

        var tasks = (uids is { Length: > 0 }
                ? uids.Select(project.GetTaskByUniqueID).Where(t => t is not null)!
                : ScheduleAnalyzer.Leaves(project))
            .Where(t => t is not null && !t.Summary)
            .ToList();

        var rangeStart = ParseDate(from)
                         ?? tasks.Where(t => t.Start is not null).Min(t => t.Start) ?? DateTime.Today;
        var rangeEnd = ParseDate(to)
                       ?? tasks.Where(t => t.Finish is not null).Max(t => t.Finish) ?? rangeStart.AddDays(30);

        if (rangeEnd < rangeStart)
        {
            throw new McpToolException("The range end is before its start.");
        }

        var step = granularity.Trim().ToLowerInvariant();
        var estimated = step switch
        {
            "day" => (rangeEnd - rangeStart).TotalDays,
            "month" => (rangeEnd - rangeStart).TotalDays / 30,
            _ => (rangeEnd - rangeStart).TotalDays / 7,
        };

        if (estimated > MaxBuckets)
        {
            throw new McpToolException(
                $"That range at '{step}' granularity would produce about {estimated:0} buckets, over the " +
                $"{MaxBuckets} limit. Use a coarser granularity or a shorter range.");
        }

        var buckets = new SortedDictionary<DateTime, double>();

        foreach (var task in tasks)
        {
            var start = measure.StartsWith("baseline") ? task.BaselineStart ?? task.Start : task.Start;
            var finish = measure.StartsWith("baseline") ? task.BaselineFinish ?? task.Finish : task.Finish;
            if (start is null || finish is null)
            {
                continue;
            }

            double total = measure.Trim().ToLowerInvariant() switch
            {
                "cost" => task.Cost ?? 0,
                "baseline_cost" => task.BaselineCost ?? 0,
                "baseline_work" => MpxjMapper.Hours(task.BaselineWork) ?? 0,
                _ => MpxjMapper.Hours(task.Work) ?? 0,
            };

            if (Math.Abs(total) < 0.0001)
            {
                continue;
            }

            // Spread the total evenly across the working days the task spans.
            var days = WorkingDays(start.Value.Date, finish.Value.Date).ToList();
            if (days.Count == 0)
            {
                continue;
            }

            var perDay = total / days.Count;
            foreach (var day in days)
            {
                if (day < rangeStart.Date || day > rangeEnd.Date)
                {
                    continue;
                }

                var key = BucketKey(day, step);
                buckets[key] = buckets.GetValueOrDefault(key) + perDay;
            }
        }

        var series = buckets
            .Select(kv => new TimephasedBucket
            {
                Period = kv.Key.ToString("yyyy-MM-dd"),
                Value = Math.Round(kv.Value, 2),
            })
            .ToList();

        var running = 0.0;
        var cumulative = series
            .Select(b =>
            {
                running += b.Value;
                return new TimephasedBucket { Period = b.Period, Value = Math.Round(running, 2) };
            })
            .ToList();

        var notes = new List<string>();
        if (series.Count == 0)
        {
            notes.Add(
                $"Nothing to spread: no task in range carries a '{measure}' value. "
                + "Assign resources or costs first.");
        }

        notes.Add(
            "Values are spread evenly across each task's working days. The COM backend can return "
            + "Microsoft Project's own timephased contour instead, where a contour has been applied.");

        return new TimephasedResult
        {
            Measure = measure,
            Granularity = step,
            RollUp = uids is { Length: > 0 } ? "selection" : "project",
            Buckets = series.Count,
            Series = series,
            Cumulative = cumulative,
            Notes = notes,
        };
    }

    private static IEnumerable<DateTime> WorkingDays(DateTime start, DateTime finish)
    {
        for (var day = start; day <= finish; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                yield return day;
            }
        }
    }

    private static DateTime BucketKey(DateTime day, string granularity) => granularity switch
    {
        "day" => day.Date,
        "month" => new DateTime(day.Year, day.Month, 1),
        _ => day.Date.AddDays(-(int)day.DayOfWeek),
    };

    internal static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        throw new McpToolException($"Cannot read '{value}' as a date. Use yyyy-MM-dd.");
    }
}
