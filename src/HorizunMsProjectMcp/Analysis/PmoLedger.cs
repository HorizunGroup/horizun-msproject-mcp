using System.Text.Json;
using System.Text.Json.Serialization;
using Horizun.ProjectMcp.Backends;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Analysis;

/// <summary>One progress cut-off as reported, kept so the next report can show the trend.</summary>
public sealed record StatusSnapshot
{
    public required string StatusDate { get; init; }
    public required string Measure { get; init; }
    public double PlannedPercent { get; init; }
    public double EarnedPercent { get; init; }
    public double? Spi { get; init; }
    public double? SpiTime { get; init; }
    public double? Cpi { get; init; }
    public string? ForecastFinish { get; init; }
    public DateTime RecordedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>A week's commitment for the look-ahead: what the team said it would do.</summary>
public sealed record WeeklyCommitment
{
    public required string WeekStart { get; init; }
    public required string WeekEnd { get; init; }
    public required IReadOnlyList<int> Uids { get; init; }
    public DateTime RecordedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>An entry in the change log: a rebaseline, a budget load, or a committed batch of edits.</summary>
public sealed record ChangeEntry
{
    /// <summary>The unit of every day figure in this report.</summary>
    public string DayUnit { get; init; } = Horizun.ProjectMcp.Model.DayUnits.Working;
    public required string Kind { get; init; }
    public required string Summary { get; init; }
    public string? Reason { get; init; }
    public string? ApprovedBy { get; init; }
    public string? FinishBefore { get; init; }
    public string? FinishAfter { get; init; }
    public double? FinishDeltaDays { get; init; }
    public double? BudgetBefore { get; init; }
    public double? BudgetAfter { get; init; }
    public DateTime RecordedUtc { get; init; } = DateTime.UtcNow;
}

public sealed record PmoLedgerData
{
    public string Version { get; init; } = "1";
    public List<StatusSnapshot> Status { get; init; } = new();
    public List<WeeklyCommitment> Commitments { get; init; } = new();
    public List<ChangeEntry> Changes { get; init; } = new();
}

/// <summary>
/// The project-control record a PMO keeps beside the schedule: every reported cut-off, every weekly
/// commitment, every approved change.
/// </summary>
/// <remarks>
/// A sidecar next to the schedule (<c>name.hzpmo.json</c>), like the BIM mapping, rather than fields
/// inside the .mpp: this history outlives any one version of the file, and a schedule re-exported by
/// someone else would otherwise lose the record of how it got there.
/// </remarks>
public static class PmoLedger
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string PathFor(string schedulePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(schedulePath)) ?? ".",
            Path.GetFileNameWithoutExtension(schedulePath) + ".hzpmo.json");

    public static PmoLedgerData Load(string schedulePath)
    {
        var path = PathFor(schedulePath);
        if (!File.Exists(path))
        {
            return new PmoLedgerData();
        }

        try
        {
            return JsonSerializer.Deserialize<PmoLedgerData>(File.ReadAllText(path), Json) ?? new PmoLedgerData();
        }
        catch
        {
            return new PmoLedgerData();
        }
    }

    public static void Save(string schedulePath, PmoLedgerData data)
    {
        try
        {
            File.WriteAllText(PathFor(schedulePath), JsonSerializer.Serialize(data, Json));
        }
        catch
        {
            // The ledger is a record, not the schedule; failing to write it must not fail the work.
        }
    }

    public static void Record(string schedulePath, ChangeEntry entry)
    {
        var data = Load(schedulePath);
        data.Changes.Add(entry);
        Save(schedulePath, data);
    }
}

/// <summary>
/// What each task is worth for earned value and its curves, in whatever the schedule can measure:
/// cost where it carries costs, loaded hours where it carries hours, duration where it carries neither.
/// </summary>
public static class ValueWeights
{
    public static (string Measure, Dictionary<int, double> Budget) For(ProjectFile project, IReadOnlyList<MPXJ.Net.Task> leaves)
    {
        var hasCost = leaves.Any(t => (t.BaselineCost ?? 0) > 0 || Writes.Costs.Of(t) > 0);
        var hasWork = leaves.Any(t => (MpxjMapper.Hours(t.Work) ?? 0) > 0);
        var measure = hasCost ? "cost" : hasWork ? "work_hours" : "duration_days";
        var budget = new Dictionary<int, double>();
        foreach (var t in leaves.Where(t => t.UniqueID is not null))
        {
            budget[t.UniqueID!.Value] = measure switch
            {
                "cost" => t.BaselineCost is { } planned ? Convert.ToDouble(planned) : Writes.Costs.Of(t),
                "work_hours" => MpxjMapper.Hours(t.BaselineWork) ?? MpxjMapper.Hours(t.Work) ?? 0,
                _ => MpxjMapper.Days(t.BaselineDuration) ?? MpxjMapper.Days(t.Duration) ?? 0,
            };
        }

        return (measure, budget);
    }

    /// <summary>Share of a planned window elapsed at a date, by calendar time.</summary>
    public static double Fraction(DateTime? start, DateTime? finish, DateTime at)
    {
        if (start is null || finish is null)
        {
            return 0;
        }

        if (at >= finish)
        {
            return 1;
        }

        if (at <= start)
        {
            return 0;
        }

        var span = (finish.Value - start.Value).TotalHours;
        return span <= 0 ? 1 : (at - start.Value).TotalHours / span;
    }
}
