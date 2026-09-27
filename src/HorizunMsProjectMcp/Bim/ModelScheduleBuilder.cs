using System.Text.RegularExpressions;
using Horizun.ProjectMcp.Backends;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Bim;

/// <summary>What was built from the model, and on what assumptions.</summary>
public sealed record ModelBuildReport
{
    public required int Levels { get; init; }
    public required int Trades { get; init; }
    public required int TasksCreated { get; init; }
    public required int LinksCreated { get; init; }
    public required int ElementsLinked { get; init; }
    public required int ElementsSkipped { get; init; }
    public required int DurationsFromRates { get; init; }
    public required int DurationsAssumed { get; init; }
    public required IReadOnlyList<string> TradeOrder { get; init; }
    public required IReadOnlyList<string> LevelOrder { get; init; }
    public string? LinkFile { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Builds a first schedule straight from model elements — what Revit says will be built, level by
/// level and trade by trade — with durations from productivity rates where they are given, and the
/// element-to-task mapping the 4D export needs already in place.
/// </summary>
/// <remarks>
/// The elements arrive as data, not a file: an agent reads them from the Revit MCP and hands them
/// over in the same conversation, so the model and the schedule talk without anyone exporting.
/// The logic is a plain construction sequence — each trade follows the one before it on a level, and
/// each trade moves up the building level by level — with a start and a finish milestone so nothing
/// is left open-ended. It is a first draft to be reviewed (schedule_qa), not a finished programme.
/// </remarks>
public static class ModelScheduleBuilder
{
    /// <summary>
    /// The default order of trades, matched as case-insensitive fragments of the element's code,
    /// category, family or type, in English and Spanish Revit category names.
    /// </summary>
    public static readonly string[] DefaultSequence =
    {
        "excava|movimiento de tierra|earthwork",
        "cimenta|foundation|zapata|pilote|pile|dado",
        "column|columna|pilar",
        "structural framing|framing|viga|armazón|armazon",
        "floor|losa|slab|suelo|piso",
        "stair|escalera|rampa|ramp",
        "wall|muro|tabique|mamposter",
        "roof|cubierta|techo",
        "pipe|tuber|plumbing|sanitari|hidráulic|hidraulic|aparato",
        "duct|conducto|mechanical|mecánic|mecanic|hvac",
        "conduit|cable|electrical|eléctric|electric|lighting|luminaria",
        "window|ventana|curtain|muro cortina|fachada",
        "door|puerta",
        "ceiling|cielo|plafón|plafon|falso techo",
        "finish|acabado|pintura|enchape|revoque|generic|genérico|generico",
        "furniture|mobiliario|casework|equipment|equipo",
    };

    public static ModelBuildReport Build(
        ProjectFile project,
        string projectPath,
        IReadOnlyList<BimElement> elements,
        IReadOnlyDictionary<string, double>? productivity,
        double defaultDays,
        IReadOnlyList<string>? sequence,
        string codeField)
    {
        var notes = new List<string>();
        var usable = elements.Where(e => !string.IsNullOrWhiteSpace(e.Code) || !string.IsNullOrWhiteSpace(e.Category)).ToList();
        var skipped = elements.Count - usable.Count;
        if (usable.Count == 0)
        {
            throw new McpToolException(
                "None of the elements carries a code or a category, so there is nothing to group work by. "
                + "Read the elements from the model with their category and the code parameter "
                + "(HRZ_COD_PRES, keynote, assembly code).");
        }

        var patterns = (sequence is { Count: > 0 } ? sequence : DefaultSequence).ToList();
        string TradeOf(BimElement e) => string.IsNullOrWhiteSpace(e.Code) ? e.Category!.Trim() : e.Code!.Trim();
        int RankOf(IEnumerable<BimElement> group)
        {
            var e = group.First();
            var text = $"{e.Code} {e.Category} {e.Family} {e.Type}".ToLowerInvariant();
            for (var i = 0; i < patterns.Count; i++)
            {
                if (patterns[i].ToLowerInvariant().Split('|').Any(p => p.Length > 0 && text.Contains(p.Trim())))
                {
                    return i;
                }
            }

            return patterns.Count;
        }

        var trades = usable.GroupBy(TradeOf).OrderBy(RankOf).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var tradeOrder = trades.Select(g => g.Key).ToList();
        var unranked = trades.Where(g => RankOf(g) == patterns.Count).Select(g => g.Key).ToList();
        if (unranked.Count > 0)
        {
            notes.Add($"{unranked.Count} trade(s) matched no step of the construction sequence and were placed "
                      + $"last: {string.Join(", ", unranked.Take(8))}. Pass 'sequence' to place them.");
        }

        var levelOrder = usable.Select(e => string.IsNullOrWhiteSpace(e.Level) ? "(no level)" : e.Level!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(LevelKey)
            .ThenBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

        MPXJ.Net.Task NewTask(MPXJ.Net.Task? parent, string name)
        {
            var task = parent is null ? project.AddTask() : parent.AddTask();
            if (task.UniqueID is not > 0)
            {
                task.UniqueID = project.Tasks.Select(t => t.UniqueID ?? 0).DefaultIfEmpty(0).Max() + 1;
            }

            task.Name = name;
            return task;
        }

        var links = 0;
        void Link(MPXJ.Net.Task from, MPXJ.Net.Task to)
        {
            to.AddPredecessor(new Relation.Builder(project)
                .PredecessorTask(from).SuccessorTask(to)
                .Type(RelationType.FinishStart)
                .Lag(MPXJ.Net.Duration.GetInstance(0, TimeUnit.Days)));
            links++;
        }

        var start = NewTask(null, "Inicio");
        start.Duration = MPXJ.Net.Duration.GetInstance(0, TimeUnit.Days);
        start.Milestone = true;

        var slot = Analysis.Dcma14.ParseTextSlot(string.IsNullOrWhiteSpace(codeField) ? "Text1" : codeField);
        var mappings = new List<BimMapping>();
        var created = 0;
        var fromRates = 0;
        var assumed = 0;
        var previousOnLevel = new Dictionary<string, MPXJ.Net.Task>(StringComparer.OrdinalIgnoreCase); // trade -> task on the level below

        foreach (var level in levelOrder)
        {
            var summary = NewTask(null, level);
            summary.Summary = true;
            MPXJ.Net.Task? previousTrade = null;

            foreach (var trade in trades)
            {
                var here = trade.Where(e => string.Equals(
                    string.IsNullOrWhiteSpace(e.Level) ? "(no level)" : e.Level!.Trim(), level,
                    StringComparison.OrdinalIgnoreCase)).ToList();
                if (here.Count == 0)
                {
                    continue;
                }

                var first = here[0];
                var label = string.IsNullOrWhiteSpace(first.Code)
                    ? first.Category!
                    : string.IsNullOrWhiteSpace(first.Category) ? first.Code! : $"{first.Category} {first.Code}";
                var task = NewTask(summary, $"{label} — {level}");
                created++;

                var quantity = here.Sum(e => e.Quantity ?? 0);
                var unit = here.Select(e => e.Unit).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
                var rate = RateFor(productivity, first, unit);
                double days;
                if (rate is > 0 && quantity > 0)
                {
                    days = Math.Max(0.5, Math.Ceiling(quantity / rate.Value * 2) / 2);
                    fromRates++;
                    task.Notes = $"{here.Count} element(s), {quantity:0.##} {unit}. Duration from {rate:0.##} {unit}/day.";
                }
                else
                {
                    days = defaultDays;
                    assumed++;
                    task.Notes = $"{here.Count} element(s), {quantity:0.##} {unit}. Duration ASSUMED ({defaultDays:0.#} d) — no productivity rate given.";
                }

                task.Duration = MPXJ.Net.Duration.GetInstance(days, TimeUnit.Days);
                if (!string.IsNullOrWhiteSpace(first.Code))
                {
                    task.SetText(slot, first.Code);
                }

                // Trade after trade on this level; the same trade after itself on the level below.
                if (previousTrade is not null)
                {
                    Link(previousTrade, task);
                }

                if (previousOnLevel.TryGetValue(trade.Key, out var below))
                {
                    Link(below, task);
                }

                if (previousTrade is null && !previousOnLevel.ContainsKey(trade.Key))
                {
                    Link(start, task);
                }

                previousTrade = task;
                previousOnLevel[trade.Key] = task;

                mappings.Add(new BimMapping
                {
                    TaskUid = task.UniqueID!.Value,
                    TaskName = task.Name,
                    Code = first.Code,
                    ElementIds = here.Select(e => e.ElementId).ToList(),
                    Quantity = quantity,
                    Unit = unit,
                    Confidence = 100,
                    MatchedOn = "built from the model",
                });
            }
        }

        var finish = NewTask(null, "Fin de obra");
        finish.Duration = MPXJ.Net.Duration.GetInstance(0, TimeUnit.Days);
        finish.Milestone = true;
        foreach (var task in project.Tasks.Where(t => t.UniqueID is not null and not 0 && !t.Summary
                                                      && t != start && t != finish && t.Successors.Count == 0).ToList())
        {
            Link(task, finish);
        }

        string? linkFile = null;
        try
        {
            linkFile = BimLinkStore.Save(projectPath, new BimLinkSet
            {
                ProjectPath = projectPath,
                CodeField = string.IsNullOrWhiteSpace(codeField) ? "Text1" : codeField,
                Mappings = mappings,
            });
        }
        catch (Exception ex)
        {
            notes.Add($"The element-to-task mapping could not be saved ({ex.Message}); run bim_link to rebuild it.");
        }

        if (assumed > 0)
        {
            notes.Add($"{assumed} of {created} duration(s) are an assumption of {defaultDays:0.#} day(s), marked in each "
                      + "task's notes. Supply 'productivity' (per code, category or unit, e.g. {\"m3\": 25}) to size them from quantities.");
        }

        if (skipped > 0)
        {
            notes.Add($"{skipped} element(s) had neither a code nor a category and were left out.");
        }

        notes.Add("Logic is a plain construction sequence: trade after trade on each level, each trade level by "
                  + "level, from a start to a finish milestone. Review it with schedule_qa and adjust with links_write.");

        return new ModelBuildReport
        {
            Levels = levelOrder.Count,
            Trades = trades.Count,
            TasksCreated = created,
            LinksCreated = links,
            ElementsLinked = mappings.Sum(m => m.ElementIds.Count),
            ElementsSkipped = skipped,
            DurationsFromRates = fromRates,
            DurationsAssumed = assumed,
            TradeOrder = tradeOrder,
            LevelOrder = levelOrder,
            LinkFile = linkFile,
            Notes = notes,
        };
    }

    private static double? RateFor(IReadOnlyDictionary<string, double>? rates, BimElement e, string? unit)
    {
        if (rates is null || rates.Count == 0)
        {
            return null;
        }

        foreach (var key in new[] { e.Code, e.Category, unit })
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var hit = rates.FirstOrDefault(kv => string.Equals(kv.Key.Trim(), key.Trim(), StringComparison.OrdinalIgnoreCase));
            if (hit.Key is not null)
            {
                return hit.Value;
            }
        }

        return null;
    }

    /// <summary>Orders levels by the first number in their name (N-1, Nivel 2, Piso 10, +3.20),
    /// so "Nivel 10" comes after "Nivel 9"; names with no number go last.</summary>
    private static double LevelKey(string level)
    {
        var match = Regex.Match(level, @"[-+]?\d+(?:[.,]\d+)?");
        if (!match.Success)
        {
            return double.MaxValue;
        }

        var text = match.Value.Replace(',', '.');
        var value = double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        return level.Contains("sót", StringComparison.OrdinalIgnoreCase) || level.Contains("sot", StringComparison.OrdinalIgnoreCase)
               || level.Contains("basement", StringComparison.OrdinalIgnoreCase)
            ? -Math.Abs(value)
            : value;
    }
}
