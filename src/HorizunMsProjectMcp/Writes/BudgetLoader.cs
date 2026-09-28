using System.Globalization;
using Horizun.ProjectMcp.Analysis;
using Horizun.ProjectMcp.Backends;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Writes;

public sealed record BudgetLoadResult
{
    public required double BudgetTotal { get; init; }
    public required double Loaded { get; init; }
    public required int TasksCosted { get; init; }
    public required IReadOnlyList<string> CodesWithoutTasks { get; init; }
    public required int TasksWithoutBudget { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Loads a budget onto the schedule by the code both share — the cost-loaded schedule a PMO measures
/// earned value and cash flow against.
/// </summary>
/// <remarks>
/// Each budget line is spread over the detail tasks carrying its code, in proportion to their
/// duration, as fixed cost. Nothing is dropped quietly: budget lines no task carries and costed tasks'
/// complement — tasks with no line — are both reported, because a budget that does not add up to the
/// schedule is the first thing a cost review will find.
/// </remarks>
public static class BudgetLoader
{
    public static BudgetLoadResult Load(ProjectFile project, IReadOnlyDictionary<string, double> budget, string codeField)
    {
        var slot = Dcma14.ParseTextSlot(string.IsNullOrWhiteSpace(codeField) ? "Text1" : codeField);
        var leaves = ScheduleAnalyzer.Leaves(project).Where(t => t.UniqueID is not null).ToList();
        var byCode = leaves
            .Select(t => (Task: t, Code: Dcma14.SafeGetText(t, slot)?.Trim()))
            .Where(x => !string.IsNullOrEmpty(x.Code))
            .GroupBy(x => x.Code!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Task).ToList(), StringComparer.OrdinalIgnoreCase);

        var missing = new List<string>();
        var costed = new HashSet<int>();
        var loaded = 0.0;
        foreach (var (code, amount) in budget)
        {
            if (!byCode.TryGetValue(code.Trim(), out var tasks) || tasks.Count == 0)
            {
                missing.Add($"{code} ({amount.ToString("N0", CultureInfo.InvariantCulture)})");
                continue;
            }

            var weights = tasks.Select(t => Math.Max(MpxjMapper.Days(t.Duration) ?? 0, 0.001)).ToList();
            var sum = weights.Sum();
            for (var i = 0; i < tasks.Count; i++)
            {
                var share = Math.Round(amount * weights[i] / sum, 2);
                tasks[i].FixedCost = share;
                tasks[i].Cost = share;
                costed.Add(tasks[i].UniqueID!.Value);
                loaded += share;
            }
        }

        var notes = new List<string>
        {
            "Costs are loaded as fixed cost on each task. Save a baseline now (schedule_update op='save_baseline', "
            + "with a reason) so earned value and the cash flow curve have a budget to measure against.",
        };
        if (missing.Count > 0)
        {
            notes.Add($"{missing.Count} budget line(s) match no task code and were not loaded.");
        }

        return new BudgetLoadResult
        {
            BudgetTotal = Math.Round(budget.Values.Sum(), 2),
            Loaded = Math.Round(loaded, 2),
            TasksCosted = costed.Count,
            CodesWithoutTasks = missing.Take(50).ToList(),
            TasksWithoutBudget = leaves.Count(t => !t.Milestone && !costed.Contains(t.UniqueID!.Value)),
            Notes = notes,
        };
    }

    /// <summary>Reads code,amount rows from a CSV (comma or semicolon; decimal point or comma).</summary>
    public static Dictionary<string, double> ReadCsv(string path)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0)
        {
            return result;
        }

        var separator = lines[0].Contains(';') ? ';' : ',';
        foreach (var line in lines)
        {
            var parts = line.Split(separator);
            if (parts.Length < 2)
            {
                continue;
            }

            var raw = parts[^1].Trim().Trim('"').Replace(" ", "").Replace("$", "");
            if (separator == ';')
            {
                raw = raw.Replace(".", "").Replace(',', '.');
            }

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                var code = parts[0].Trim().Trim('"');
                result[code] = result.GetValueOrDefault(code) + amount;
            }
        }

        return result;
    }
}
