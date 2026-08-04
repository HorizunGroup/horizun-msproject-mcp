using Horizun.ProjectMcp.Model;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Backends;

/// <summary>Translates between MPXJ's object model and the DTOs the tools expose.</summary>
public static class MpxjMapper
{
    public const double HoursPerDay = 8.0;

    public static string? Iso(DateTime? value) => value?.ToString("yyyy-MM-ddTHH:mm:ss");

    /// <summary>
    /// Duration in days. MPXJ carries a value plus its unit; everything downstream
    /// (float thresholds, DCMA limits, impact deltas) reasons in days, so normalise once here.
    /// </summary>
    public static double? Days(MPXJ.Net.Duration? duration)
    {
        if (duration is null)
        {
            return null;
        }

        var amount = duration.DurationValue;
        return duration.Units switch
        {
            TimeUnit.Minutes => amount / 60.0 / HoursPerDay,
            TimeUnit.ElapsedMinutes => amount / 60.0 / 24.0,
            TimeUnit.Hours => amount / HoursPerDay,
            TimeUnit.ElapsedHours => amount / 24.0,
            TimeUnit.Days => amount,
            TimeUnit.ElapsedDays => amount,
            TimeUnit.Weeks => amount * 5.0,
            TimeUnit.ElapsedWeeks => amount * 7.0,
            TimeUnit.Months => amount * 20.0,
            TimeUnit.ElapsedMonths => amount * 30.0,
            TimeUnit.Years => amount * 240.0,
            TimeUnit.ElapsedYears => amount * 365.0,
            _ => amount,
        };
    }

    public static double? Hours(MPXJ.Net.Duration? duration)
    {
        var days = Days(duration);
        return days is null ? null : days * HoursPerDay;
    }

    public static string? DurationText(MPXJ.Net.Duration? duration)
    {
        var days = Days(duration);
        return days is null ? null : $"{days:0.##}d";
    }

    /// <summary>Parses the duration shorthand the tools accept: "5d", "8h", "2w", "0".</summary>
    public static MPXJ.Net.Duration ParseDuration(string text)
    {
        var trimmed = text.Trim().ToLowerInvariant();
        var unit = TimeUnit.Days;
        var numeric = trimmed;

        foreach (var (suffix, u) in new[]
                 {
                     ("mo", TimeUnit.Months), ("w", TimeUnit.Weeks), ("d", TimeUnit.Days),
                     ("h", TimeUnit.Hours), ("m", TimeUnit.Minutes),
                 })
        {
            if (trimmed.EndsWith(suffix, StringComparison.Ordinal))
            {
                unit = u;
                numeric = trimmed[..^suffix.Length];
                break;
            }
        }

        if (!double.TryParse(numeric, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            throw new McpToolException(
                $"Cannot read '{text}' as a duration. Use a number with a unit: 5d, 8h, 2w, 3mo, or 0.");
        }

        return MPXJ.Net.Duration.GetInstance(value, unit);
    }

    public static TaskDto ToDto(MPXJ.Net.Task task, bool includeCustom = false)
    {
        return new TaskDto
        {
            Uid = task.UniqueID ?? -1,
            Id = task.ID,
            Wbs = task.WBS,
            Name = task.Name,
            OutlineLevel = task.OutlineLevel,
            Summary = task.Summary,
            Milestone = task.Milestone,
            Active = task.Active,
            Start = Iso(task.Start),
            Finish = Iso(task.Finish),
            Duration = DurationText(task.Duration),
            PercentComplete = task.PercentageComplete,
            Critical = task.Critical,
            TotalFloatDays = Days(task.TotalSlack),
            FreeFloatDays = Days(task.FreeSlack),
            EarlyStart = Iso(task.EarlyStart),
            EarlyFinish = Iso(task.EarlyFinish),
            LateStart = Iso(task.LateStart),
            LateFinish = Iso(task.LateFinish),
            Deadline = Iso(task.Deadline),
            ConstraintType = task.ConstraintType?.ToString(),
            ConstraintDate = Iso(task.ConstraintDate),
            ActualStart = Iso(task.ActualStart),
            ActualFinish = Iso(task.ActualFinish),
            WorkHours = Hours(task.Work),
            Cost = task.Cost,
            BaselineStart = Iso(task.BaselineStart),
            BaselineFinish = Iso(task.BaselineFinish),
            ParentUid = task.ParentTask?.UniqueID,
            Notes = string.IsNullOrWhiteSpace(task.Notes) ? null : task.Notes,
            Custom = includeCustom ? ReadCustom(task) : null,
        };
    }

    /// <summary>
    /// Reads the Text1..Text30 slots that carry organisation-specific codes (CWA, budget code,
    /// discipline). Empty slots are omitted so the payload stays small.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ReadCustom(MPXJ.Net.Task task)
    {
        Dictionary<string, string>? map = null;
        for (var i = 1; i <= 30; i++)
        {
            string? value;
            try
            {
                value = task.GetText(i);
            }
            catch
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                (map ??= new Dictionary<string, string>())[$"Text{i}"] = value;
            }
        }

        return map;
    }

    public static LinkDto ToDto(Relation relation, bool driving = false) => new()
    {
        FromUid = relation.PredecessorTask?.UniqueID ?? -1,
        ToUid = relation.SuccessorTask?.UniqueID ?? -1,
        Type = relation.Type switch
        {
            RelationType.FinishStart => "FS",
            RelationType.StartStart => "SS",
            RelationType.FinishFinish => "FF",
            RelationType.StartFinish => "SF",
            _ => "FS",
        },
        LagDays = Days(relation.Lag) ?? 0,
        FromName = relation.PredecessorTask?.Name,
        ToName = relation.SuccessorTask?.Name,
        Driving = driving,
    };

    public static RelationType ParseRelationType(string type) => type.Trim().ToUpperInvariant() switch
    {
        "FS" => RelationType.FinishStart,
        "SS" => RelationType.StartStart,
        "FF" => RelationType.FinishFinish,
        "SF" => RelationType.StartFinish,
        _ => throw new McpToolException($"Unknown dependency type '{type}'. Use FS, SS, FF, or SF."),
    };

    /// <summary>
    /// MPXJ exposes no overallocation flag — it is a derived fact, so the caller passes in the
    /// value computed by <see cref="Analysis.ResourceAnalyzer"/> rather than this mapper guessing.
    /// </summary>
    public static ResourceDto ToDto(Resource resource, bool includeAssignments = false, bool overallocated = false)
    {
        var assignments = includeAssignments
            ? resource.TaskAssignments.Select(ToDto).ToList()
            : null;

        return new ResourceDto
        {
            Uid = resource.UniqueID ?? -1,
            Id = resource.ID,
            Name = resource.Name,
            Type = resource.Type?.ToString(),
            MaxUnits = resource.MaxUnits,
            StandardRate = resource.StandardRate?.Amount,
            CostTotal = resource.Cost,
            WorkHours = Hours(resource.Work),
            Overallocated = overallocated,
            Assignments = assignments,
        };
    }

    public static AssignmentDto ToDto(ResourceAssignment assignment) => new()
    {
        TaskUid = assignment.Task?.UniqueID ?? -1,
        ResourceUid = assignment.Resource?.UniqueID ?? -1,
        TaskName = assignment.Task?.Name,
        ResourceName = assignment.Resource?.Name,
        Units = assignment.Units,
        WorkHours = Hours(assignment.Work),
        Cost = assignment.Cost,
        Start = Iso(assignment.Start),
        Finish = Iso(assignment.Finish),
    };
}
