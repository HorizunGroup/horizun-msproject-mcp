using System.Text.Json;
using Horizun.ProjectMcp.Analysis;
using Horizun.ProjectMcp.Backends;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Bim;

public sealed record BimLinkResult
{
    public required string Op { get; init; }
    public required int Mapped { get; init; }
    public required int UnmatchedTasks { get; init; }
    public required int UnmatchedElements { get; init; }
    public required IReadOnlyList<BimMapping> Mappings { get; init; }
    public IReadOnlyList<BimMapping>? NeedsReview { get; init; }
    public string? StorePath { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record BimSyncResult
{
    public required string Direction { get; init; }
    public required int Rows { get; init; }
    public string? OutputPath { get; init; }
    public IReadOnlyList<DurationProposal>? Proposals { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record DurationProposal
{
    public required int TaskUid { get; init; }
    public string? TaskName { get; init; }
    public double? Quantity { get; init; }
    public string? Unit { get; init; }
    public required double ProductivityPerDay { get; init; }
    public required double ProposedDurationDays { get; init; }
    public double? CurrentDurationDays { get; init; }
    public double? DeltaDays { get; init; }
}

/// <summary>4D row: one model element carrying the dates of the task that builds it.</summary>
public sealed record FourDRow
{
    public required string ElementId { get; init; }
    public required int TaskUid { get; init; }
    public string? TaskName { get; init; }
    public string? Code { get; init; }
    public string? PlannedStart { get; init; }
    public string? PlannedFinish { get; init; }
    public string? ActualStart { get; init; }
    public string? ActualFinish { get; init; }
    public double? PercentComplete { get; init; }
    public string? Status { get; init; }
}

/// <summary>
/// Ties the schedule to the model. Nobody else in this market does it, and it is the reason
/// a construction team would choose this server over the alternatives.
/// </summary>
public static class BimEngine
{
    /// <summary>
    /// Proposes task-to-element mappings by matching the schedule's code field against the code
    /// carried on each element. Low-confidence matches are separated out for review rather than
    /// written in silently — the same pattern used for keynote coding in Revit.
    /// </summary>
    public static BimLinkResult Suggest(
        ProjectFile project, IReadOnlyList<BimElement> elements, string codeField, double reviewBelow)
    {
        var slot = Dcma14.ParseTextSlot(codeField);
        var leaves = ScheduleAnalyzer.Leaves(project).ToList();

        var byCode = elements
            .Where(e => !string.IsNullOrWhiteSpace(e.Code))
            .GroupBy(e => Normalize(e.Code!), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var mappings = new List<BimMapping>();
        var review = new List<BimMapping>();
        var usedElements = new HashSet<string>();
        var unmatchedTasks = 0;

        foreach (var task in leaves)
        {
            var code = Dcma14.SafeGetText(task, slot);
            if (string.IsNullOrWhiteSpace(code))
            {
                unmatchedTasks++;
                continue;
            }

            var normalized = Normalize(code);
            List<BimElement>? matched = null;
            double confidence;
            string matchedOn;

            if (byCode.TryGetValue(normalized, out var exact))
            {
                matched = exact;
                confidence = 100;
                matchedOn = "exact_code";
            }
            else
            {
                // A task coded to a parent node legitimately covers every element beneath it.
                var prefixed = byCode
                    .Where(kv => kv.Key.StartsWith(normalized, StringComparison.Ordinal))
                    .SelectMany(kv => kv.Value)
                    .ToList();

                if (prefixed.Count > 0)
                {
                    matched = prefixed;
                    confidence = 70;
                    matchedOn = "code_prefix";
                }
                else
                {
                    unmatchedTasks++;
                    continue;
                }
            }

            foreach (var element in matched)
            {
                usedElements.Add(element.ElementId);
            }

            var quantities = matched.Where(e => e.Quantity is not null).ToList();
            var mapping = new BimMapping
            {
                TaskUid = task.UniqueID!.Value,
                TaskName = task.Name,
                Code = code,
                ElementIds = matched.Select(e => e.ElementId).ToList(),
                Quantity = quantities.Count == 0 ? null : Math.Round(quantities.Sum(e => e.Quantity!.Value), 3),
                Unit = quantities.FirstOrDefault()?.Unit,
                Confidence = confidence,
                MatchedOn = matchedOn,
            };

            if (confidence < reviewBelow)
            {
                review.Add(mapping);
            }
            else
            {
                mappings.Add(mapping);
            }
        }

        var notes = new List<string>();
        if (review.Count > 0)
        {
            notes.Add(
                $"{review.Count} mapping(s) matched only on a code prefix and are listed under needsReview "
                + "instead of being stored. Confirm them, then re-run with op='set'.");
        }

        if (unmatchedTasks > 0)
        {
            notes.Add(
                $"{unmatchedTasks} task(s) carry no code in {codeField}, or no element shares their code. "
                + "Run schedule_qa to find the tasks missing a budget code.");
        }

        return new BimLinkResult
        {
            Op = "suggest",
            Mapped = mappings.Count,
            UnmatchedTasks = unmatchedTasks,
            UnmatchedElements = elements.Count(e => !usedElements.Contains(e.ElementId)),
            Mappings = mappings,
            NeedsReview = review.Count == 0 ? null : review,
            Notes = notes,
        };
    }

    /// <summary>
    /// Model -> schedule. Turns measured quantities into duration proposals at a stated
    /// productivity rate. Proposals only — applying them is a separate, verified tasks_write.
    /// </summary>
    public static BimSyncResult PullQuantities(
        ProjectFile project, BimLinkSet links, IReadOnlyDictionary<string, double> productivityByUnit,
        double defaultProductivity)
    {
        var proposals = new List<DurationProposal>();
        var notes = new List<string>();

        foreach (var mapping in links.Mappings)
        {
            if (mapping.Quantity is not { } quantity || quantity <= 0)
            {
                continue;
            }

            var task = project.GetTaskByUniqueID(mapping.TaskUid);
            if (task is null)
            {
                continue;
            }

            var unit = mapping.Unit ?? "";
            var rate = productivityByUnit.TryGetValue(unit, out var r) && r > 0 ? r : defaultProductivity;
            if (rate <= 0)
            {
                continue;
            }

            var proposed = Math.Round(quantity / rate, 2);
            var current = MpxjMapper.Days(task.Duration);

            proposals.Add(new DurationProposal
            {
                TaskUid = mapping.TaskUid,
                TaskName = task.Name,
                Quantity = quantity,
                Unit = mapping.Unit,
                ProductivityPerDay = rate,
                ProposedDurationDays = proposed,
                CurrentDurationDays = current,
                DeltaDays = current is null ? null : Math.Round(proposed - current.Value, 2),
            });
        }

        notes.Add(
            "These are proposals, not writes. Apply the ones you accept with tasks_write, which verifies "
            + "each change by re-reading the model.");

        if (proposals.Count == 0)
        {
            notes.Add(
                "No proposal could be produced: the stored mappings carry no quantities. Re-export the "
                + "elements with a quantity column and re-run bim_link with op='suggest'.");
        }

        return new BimSyncResult
        {
            Direction = "model_to_schedule",
            Rows = proposals.Count,
            Proposals = proposals,
            Notes = notes,
        };
    }

    /// <summary>
    /// Schedule -> model. Emits one row per element carrying the dates and progress of the task
    /// that builds it. This is what drives 4D animation and the progress colour-coding in
    /// Navisworks and Power BI.
    /// </summary>
    public static BimSyncResult PushDates(
        ProjectFile project, BimLinkSet links, string outputPath, DateTime statusDate)
    {
        var rows = new List<FourDRow>();

        foreach (var mapping in links.Mappings)
        {
            var task = project.GetTaskByUniqueID(mapping.TaskUid);
            if (task is null)
            {
                continue;
            }

            var percent = task.PercentageComplete ?? 0;
            var status = percent >= 100 ? "complete"
                : percent > 0 ? "in_progress"
                : task.Start is not null && task.Start < statusDate ? "late_start"
                : "not_started";

            foreach (var elementId in mapping.ElementIds)
            {
                rows.Add(new FourDRow
                {
                    ElementId = elementId,
                    TaskUid = mapping.TaskUid,
                    TaskName = task.Name,
                    Code = mapping.Code,
                    PlannedStart = MpxjMapper.Iso(task.Start),
                    PlannedFinish = MpxjMapper.Iso(task.Finish),
                    ActualStart = MpxjMapper.Iso(task.ActualStart),
                    ActualFinish = MpxjMapper.Iso(task.ActualFinish),
                    PercentComplete = percent,
                    Status = status,
                });
            }
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (Path.GetExtension(outputPath).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("elementId,taskUid,taskName,code,plannedStart,plannedFinish,actualStart,actualFinish,percentComplete,status");
            foreach (var r in rows)
            {
                csv.AppendLine(string.Join(',',
                    Csv(r.ElementId), r.TaskUid, Csv(r.TaskName), Csv(r.Code),
                    Csv(r.PlannedStart), Csv(r.PlannedFinish), Csv(r.ActualStart), Csv(r.ActualFinish),
                    r.PercentComplete?.ToString("0.##") ?? "", Csv(r.Status)));
            }

            File.WriteAllText(outputPath, csv.ToString());
        }
        else
        {
            File.WriteAllText(outputPath,
                JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        }

        return new BimSyncResult
        {
            Direction = "schedule_to_model",
            Rows = rows.Count,
            OutputPath = outputPath,
            Notes = new[]
            {
                "One row per model element, carrying the dates of the task that builds it. Feed this to a "
                + "Revit add-in to write element parameters, to Navisworks for a 4D simulation, or straight "
                + "into Power BI for the progress dashboard.",
            },
        };
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private static string Normalize(string code) => code.Trim().ToUpperInvariant();
}
