using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Horizun.ProjectMcp.Backends;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Analysis;

public sealed record LinkedActivity
{
    public required string Key { get; init; }
    public string? Label { get; init; }
    public required int Occurrences { get; init; }
    public required double Share { get; init; }
    public required string Type { get; init; }
    public required double MedianLagDays { get; init; }
}

public sealed record ActivityEntry
{
    public required string Key { get; init; }
    public string? Label { get; init; }
    public string? Code { get; init; }
    public required int Occurrences { get; init; }
    public required int Projects { get; init; }
    public required double MedianDurationDays { get; init; }
    public required double MeanDurationDays { get; init; }
    public required double MinDurationDays { get; init; }
    public required double MaxDurationDays { get; init; }

    /// <summary>The duration eight times in ten came in at or under — what to plan with.</summary>
    public required double P80DurationDays { get; init; }

    public double? MedianQuantity { get; init; }
    public string? Unit { get; init; }

    /// <summary>Quantity per working day, where the source schedules carried quantities.</summary>
    public double? ProductivityPerDay { get; init; }

    public required IReadOnlyList<LinkedActivity> TypicalPredecessors { get; init; }
    public required IReadOnlyList<LinkedActivity> TypicalSuccessors { get; init; }

    /// <summary>
    /// True when this activity's usual predecessor is itself — the signature of work that repeats
    /// unit by unit, one apartment or floor after the next. It is the most common shape in
    /// construction and the reason a generated draft needs a repeat count rather than one task.
    /// </summary>
    public bool RepeatsInSequence { get; init; }

    /// <summary>Median gap between consecutive repetitions, in working days.</summary>
    public double RepeatLagDays { get; init; }

    /// <summary>How many repetitions the source schedules typically ran per project.</summary>
    public int TypicalRepetitions { get; init; }
}

public sealed record ActivityLibrary
{
    public string Version { get; init; } = "1";
    public DateTime BuiltUtc { get; init; } = DateTime.UtcNow;
    public required IReadOnlyList<string> Sources { get; init; }
    public required int TasksRead { get; init; }
    public required IReadOnlyList<ActivityEntry> Activities { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Mines finished schedules for how long each kind of activity actually takes and what usually
/// comes before and after it, so a new schedule can start from experience rather than a blank page.
/// </summary>
/// <remarks>
/// Construction schedules repeat: the same activity appears once per apartment, per floor, per
/// block, across every project a builder has ever run. That repetition is the data. Names are
/// normalised so "Estructura apto 101" and "ESTRUCTURA APTO 214" count as the same activity, and
/// durations are reported as a distribution rather than an average, because planning to the mean
/// of a skewed distribution is how schedules end up late.
/// </remarks>
public static class ScheduleLibrary
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ActivityLibrary Learn(
        IReadOnlyList<string> paths, string? codeField, int minOccurrences,
        IReadOnlyList<string>? quantitySources = null)
    {
        var samples = new Dictionary<string, List<Sample>>(StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var projectsByKey = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var notes = new List<string>();
        var sources = new List<string>();
        var tasksRead = 0;
        var slot = codeField is null ? 0 : Dcma14.ParseTextSlot(codeField);

        // Quantities come from the model, not the schedule: construction programmes almost never
        // carry them, which is why productivity cannot be learned from durations alone. Element
        // exports are keyed by the same code the schedule uses, so the two join on it.
        //
        // Crucially they are read PER PROJECT. Totalling them across projects and dividing by one
        // project's duration inflates every rate by however many projects were supplied — measured
        // at 1.8x on two — and a rate that wrong is worse than no rate at all.
        var quantitySets = LoadQuantitiesPerSource(quantitySources, notes);
        var pairByIndex = quantitySets.Count == paths.Count && quantitySets.Count > 1;

        if (quantitySets.Count > 1 && !pairByIndex)
        {
            notes.Add(
                $"{quantitySets.Count} quantity source(s) were given for {paths.Count} schedule(s). "
                + "They cannot be paired one to one, so each code takes the median across sources "
                + "rather than a total. Supply one export per schedule, in the same order, for rates "
                + "measured against the project they belong to.");
        }

        for (var sourceIndex = 0; sourceIndex < paths.Count; sourceIndex++)
        {
            var path = paths[sourceIndex];
            var (quantityByCode, unitByCode) = ResolveQuantities(quantitySets, sourceIndex, pairByIndex);

            ProjectFile project;
            try
            {
                project = MpxjBackend.Read(Path.GetFullPath(path));
            }
            catch (Exception ex)
            {
                notes.Add($"Skipped '{path}': {ex.Message}");
                continue;
            }

            sources.Add(Path.GetFileName(path));
            var leaves = ScheduleAnalyzer.Leaves(project).ToList();
            var calendar = new WorkingCalendar(project);

            // Key every task first, so the logic can be recorded between keys rather than uids.
            var keyByUid = new Dictionary<int, string>();
            foreach (var task in leaves)
            {
                var key = Normalise(task.Name);
                if (key.Length == 0)
                {
                    continue;
                }

                keyByUid[task.UniqueID!.Value] = key;
                if (!labels.ContainsKey(key))
                {
                    labels[key] = task.Name!.Trim();
                }
            }

            foreach (var task in leaves)
            {
                if (!keyByUid.TryGetValue(task.UniqueID!.Value, out var key))
                {
                    continue;
                }

                var days = MpxjMapper.Days(task.Duration, calendar.HoursPerDay) ?? 0;
                if (days <= 0 || task.Milestone)
                {
                    continue;
                }

                tasksRead++;

                var code = slot > 0 ? Dcma14.SafeGetText(task, slot) : null;
                double? quantity = null;
                string? unit = null;
                if (!string.IsNullOrWhiteSpace(code))
                {
                    var normalisedCode = code.Trim().ToUpperInvariant();
                    if (quantityByCode.TryGetValue(normalisedCode, out var q))
                    {
                        quantity = q;
                        unit = unitByCode.GetValueOrDefault(normalisedCode);
                    }
                }

                var sample = new Sample
                {
                    DurationDays = days,
                    Quantity = quantity,
                    Unit = unit,
                    Code = code,
                    Predecessors = task.Predecessors
                        .Select(r => (
                            Key: r.PredecessorTask?.UniqueID is { } u && keyByUid.TryGetValue(u, out var k) ? k : null,
                            Type: RelationCode(r.Type),
                            Lag: MpxjMapper.Days(r.Lag, calendar.HoursPerDay) ?? 0))
                        .Where(x => x.Key is not null)
                        .Select(x => (x.Key!, x.Type, x.Lag))
                        .ToList(),
                    Successors = task.Successors
                        .Select(r => (
                            Key: r.SuccessorTask?.UniqueID is { } u && keyByUid.TryGetValue(u, out var k) ? k : null,
                            Type: RelationCode(r.Type),
                            Lag: MpxjMapper.Days(r.Lag, calendar.HoursPerDay) ?? 0))
                        .Where(x => x.Key is not null)
                        .Select(x => (x.Key!, x.Type, x.Lag))
                        .ToList(),
                };

                if (!samples.TryGetValue(key, out var list))
                {
                    samples[key] = list = new List<Sample>();
                }

                list.Add(sample);

                if (!projectsByKey.TryGetValue(key, out var set))
                {
                    projectsByKey[key] = set = new HashSet<string>();
                }

                set.Add(path);
            }
        }

        var activities = new List<ActivityEntry>();
        foreach (var (key, list) in samples)
        {
            if (list.Count < Math.Max(minOccurrences, 1))
            {
                continue;
            }

            var durations = list.Select(s => s.DurationDays).OrderBy(d => d).ToList();

            // Productivity is what a crew actually gets through in a day. Take it per sample and
            // then the median, rather than dividing the totals: one mis-scoped outlier would
            // otherwise drag the rate for every future estimate built on it.
            var quantities = list.Where(s => s.Quantity is > 0)
                .Select(s => s.Quantity!.Value)
                .OrderBy(q => q)
                .ToList();
            var rates = list
                .Where(s => s.Quantity is > 0 && s.DurationDays > 0)
                .Select(s => s.Quantity!.Value / s.DurationDays)
                .OrderBy(r => r)
                .ToList();
            double? productivity = rates.Count == 0 ? null : Math.Round(Percentile(rates, 0.5), 3);

            // Work that repeats unit by unit shows up as an activity whose predecessor is itself.
            var selfLinks = list
                .SelectMany(s => s.Predecessors)
                .Where(x => x.Key == key)
                .ToList();
            var selfLinked = selfLinks.Count;
            var selfLag = selfLinks.Count == 0
                ? 0
                : Math.Round(Percentile(selfLinks.Select(x => x.Lag).OrderBy(x => x).ToList(), 0.5), 2);

            activities.Add(new ActivityEntry
            {
                Key = key,
                Label = labels.GetValueOrDefault(key),
                Code = list.Select(s => s.Code).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)),
                Occurrences = list.Count,
                Projects = projectsByKey.GetValueOrDefault(key)?.Count ?? 1,
                MedianDurationDays = Math.Round(Percentile(durations, 0.5), 2),
                MeanDurationDays = Math.Round(durations.Average(), 2),
                MinDurationDays = Math.Round(durations.First(), 2),
                MaxDurationDays = Math.Round(durations.Last(), 2),
                P80DurationDays = Math.Round(Percentile(durations, 0.8), 2),
                MedianQuantity = quantities.Count == 0 ? null : Math.Round(Percentile(quantities, 0.5), 3),
                Unit = list.Select(s => s.Unit).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)),
                ProductivityPerDay = productivity,
                TypicalPredecessors = Summarise(list.SelectMany(s => s.Predecessors), list.Count),
                TypicalSuccessors = Summarise(list.SelectMany(s => s.Successors), list.Count),
                RepeatsInSequence = selfLinked > list.Count * 0.5,
                RepeatLagDays = selfLag,
                TypicalRepetitions = Math.Max(1,
                    (int)Math.Round((double)list.Count / Math.Max(projectsByKey.GetValueOrDefault(key)?.Count ?? 1, 1))),
            });
        }

        if (activities.Count == 0)
        {
            notes.Add(
                $"No activity appeared at least {Math.Max(minOccurrences, 1)} time(s) across these files. "
                + "Lower minOccurrences, or supply more schedules from the same kind of project.");
        }
        else
        {
            notes.Add(
                "Durations are reported as a distribution. Plan with the median for a normal run and the "
                + "80th percentile where the activity has bitten before — planning to the mean of a skewed "
                + "distribution is how schedules end up late.");

            var repeating = activities.Count(a => a.RepeatsInSequence);
            if (repeating > 0)
            {
                notes.Add(
                    $"{repeating} activity(ies) repeat unit by unit — the same work once per apartment, "
                    + "floor or block, each waiting on the one before. schedule_generate reproduces that "
                    + "chain when you give it a repeat count; without one it creates a single instance and "
                    + "the draft comes back with most of its logic missing.");
            }
        }

        return new ActivityLibrary
        {
            Sources = sources,
            TasksRead = tasksRead,
            Activities = activities.OrderByDescending(a => a.Occurrences).ToList(),
            Notes = notes,
        };
    }

    private static IReadOnlyList<LinkedActivity> Summarise(
        IEnumerable<(string Key, string Type, double Lag)> links, int occurrences)
    {
        return links
            .GroupBy(x => (x.Key, x.Type))
            .Select(g => new LinkedActivity
            {
                Key = g.Key.Key,
                Occurrences = g.Count(),
                Share = Math.Round(100.0 * g.Count() / Math.Max(occurrences, 1), 1),
                Type = g.Key.Type,
                MedianLagDays = Math.Round(Percentile(g.Select(x => x.Lag).OrderBy(x => x).ToList(), 0.5), 2),
            })
            .Where(l => l.Share >= 20)
            .OrderByDescending(l => l.Occurrences)
            .Take(8)
            .ToList();
    }

    private static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static string RelationCode(RelationType? type) => type switch
    {
        RelationType.StartStart => "SS",
        RelationType.FinishFinish => "FF",
        RelationType.StartFinish => "SF",
        _ => "FS",
    };

    /// <summary>
    /// Collapses a task name to the activity it represents: accents removed, case folded, and the
    /// unit identifiers stripped, so one apartment's worth of work matches another's.
    /// </summary>
    public static string Normalise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var folded = new string(name.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray())
            .ToUpperInvariant();

        var builder = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            builder.Append(char.IsLetter(c) ? c : ' ');
        }

        // Words that are pure identifiers carry no meaning about the activity itself.
        var words = builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1)
            .ToList();

        return string.Join(' ', words);
    }

    public static string Save(ActivityLibrary library, string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(full, JsonSerializer.Serialize(library, Json));
        return full;
    }

    public static ActivityLibrary Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new McpToolException(
                $"No activity library at '{full}'. Build one first with schedule_learn.");
        }

        try
        {
            return JsonSerializer.Deserialize<ActivityLibrary>(File.ReadAllText(full),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new McpToolException($"'{full}' is empty.");
        }
        catch (McpToolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpToolException($"'{full}' is not a readable activity library: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads element exports and totals the quantity behind each code, so an activity's
    /// productivity can be measured against the work it actually covered.
    /// </summary>
    private static List<(Dictionary<string, double> Quantity, Dictionary<string, string> Unit)>
        LoadQuantitiesPerSource(IReadOnlyList<string>? sources, List<string> notes)
    {
        var sets = new List<(Dictionary<string, double>, Dictionary<string, string>)>();

        if (sources is null || sources.Count == 0)
        {
            return sets;
        }

        foreach (var source in sources)
        {
            var totals = new Dictionary<string, double>(StringComparer.Ordinal);
            var units = new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                foreach (var element in Bim.BimLinkStore.LoadElements(Path.GetFullPath(source)))
                {
                    if (string.IsNullOrWhiteSpace(element.Code) || element.Quantity is not > 0)
                    {
                        continue;
                    }

                    // Summing WITHIN one export is right: an activity covers many elements.
                    var code = element.Code.Trim().ToUpperInvariant();
                    totals[code] = totals.GetValueOrDefault(code) + element.Quantity.Value;
                    if (!string.IsNullOrWhiteSpace(element.Unit))
                    {
                        units.TryAdd(code, element.Unit!);
                    }
                }
            }
            catch (Exception ex)
            {
                notes.Add($"Could not read quantities from '{source}': {ex.Message}");
            }

            sets.Add((totals, units));
        }

        var codes = sets.SelectMany(x => x.Item1.Keys).Distinct().Count();
        if (codes > 0)
        {
            notes.Add(
                $"Quantities loaded for {codes} code(s) across {sets.Count} export(s). Activities that "
                + "match one report a productivity rate, so a future schedule can be sized from "
                + "measured quantities rather than from a duration somebody typed.");
        }

        return sets;
    }

    /// <summary>
    /// The quantities that belong with one schedule. Paired by position where an export was given
    /// per schedule; otherwise the median across exports, which at least does not scale with how
    /// many were supplied.
    /// </summary>
    private static (Dictionary<string, double>, Dictionary<string, string>) ResolveQuantities(
        List<(Dictionary<string, double> Quantity, Dictionary<string, string> Unit)> sets,
        int index, bool pairByIndex)
    {
        if (sets.Count == 0)
        {
            return (new Dictionary<string, double>(StringComparer.Ordinal),
                    new Dictionary<string, string>(StringComparer.Ordinal));
        }

        if (pairByIndex)
        {
            return (sets[index].Quantity, sets[index].Unit);
        }

        if (sets.Count == 1)
        {
            return (sets[0].Quantity, sets[0].Unit);
        }

        var merged = new Dictionary<string, double>(StringComparer.Ordinal);
        var units = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var code in sets.SelectMany(s => s.Quantity.Keys).Distinct(StringComparer.Ordinal))
        {
            var values = sets
                .Where(s => s.Quantity.ContainsKey(code))
                .Select(s => s.Quantity[code])
                .OrderBy(v => v)
                .ToList();

            merged[code] = values[values.Count / 2];

            var unit = sets.Select(s => s.Unit.GetValueOrDefault(code))
                .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
            if (unit is not null)
            {
                units[code] = unit;
            }
        }

        return (merged, units);
    }

    private sealed class Sample
    {
        public required double DurationDays { get; init; }
        public double? Quantity { get; init; }
        public string? Unit { get; init; }
        public string? Code { get; init; }
        public required List<(string Key, string Type, double Lag)> Predecessors { get; init; }
        public required List<(string Key, string Type, double Lag)> Successors { get; init; }
    }
}
