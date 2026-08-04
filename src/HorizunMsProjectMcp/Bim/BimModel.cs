using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horizun.ProjectMcp.Bim;

/// <summary>One model element as exported from Revit, Navisworks, or an IFC extract.</summary>
public sealed record BimElement
{
    public required string ElementId { get; init; }

    /// <summary>The shared code that ties the element to a schedule task (e.g. HRZ_COD_PRES).</summary>
    public string? Code { get; init; }

    public string? Category { get; init; }
    public string? Family { get; init; }
    public string? Type { get; init; }
    public string? Level { get; init; }
    public double? Quantity { get; init; }
    public string? Unit { get; init; }
}

/// <summary>A task tied to the model elements it builds.</summary>
public sealed record BimMapping
{
    public required int TaskUid { get; init; }
    public string? TaskName { get; init; }
    public string? Code { get; init; }
    public required IReadOnlyList<string> ElementIds { get; init; }
    public double? Quantity { get; init; }
    public string? Unit { get; init; }

    /// <summary>0-100. Anything below the review threshold is surfaced instead of applied.</summary>
    public double Confidence { get; init; } = 100;

    public string? MatchedOn { get; init; }
}

public sealed record BimLinkSet
{
    public string Version { get; init; } = "1";
    public string? ProjectPath { get; init; }
    public string? CodeField { get; init; }
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<BimMapping> Mappings { get; init; } = Array.Empty<BimMapping>();
}

/// <summary>
/// Persists task-to-element mappings in a sidecar next to the schedule.
/// </summary>
/// <remarks>
/// Deliberately a sidecar rather than a custom field inside the .mpp: the mapping is Horizun's
/// data, it changes on a different cadence from the schedule, and writing it into the file would
/// mean touching a document the planner owns every time the model is re-exported.
/// </remarks>
public static class BimLinkStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string projectPath) =>
        System.IO.Path.ChangeExtension(projectPath, ".hzbim.json");

    public static BimLinkSet Load(string projectPath)
    {
        var path = PathFor(projectPath);
        if (!File.Exists(path))
        {
            return new BimLinkSet { ProjectPath = projectPath };
        }

        try
        {
            return JsonSerializer.Deserialize<BimLinkSet>(File.ReadAllText(path), Json)
                   ?? new BimLinkSet { ProjectPath = projectPath };
        }
        catch (Exception ex)
        {
            throw new Backends.McpToolException(
                $"The BIM link file '{path}' could not be read: {ex.Message}. " +
                "Fix or delete it, then run bim_link with op='suggest' to rebuild it.");
        }
    }

    public static string Save(string projectPath, BimLinkSet set)
    {
        var path = PathFor(projectPath);
        File.WriteAllText(path, JsonSerializer.Serialize(set with
        {
            ProjectPath = projectPath,
            UpdatedUtc = DateTime.UtcNow,
        }, Json));
        return path;
    }

    /// <summary>
    /// Reads an element export. Accepts the JSON array a Revit add-in emits, or a CSV whose
    /// header names the columns.
    /// </summary>
    public static IReadOnlyList<BimElement> LoadElements(string path)
    {
        if (!File.Exists(path))
        {
            throw new Backends.McpToolException($"No element export at '{path}'.");
        }

        var text = File.ReadAllText(path);
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();

        if (extension == ".json")
        {
            try
            {
                return JsonSerializer.Deserialize<List<BimElement>>(text,
                           new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? new List<BimElement>();
            }
            catch (Exception ex)
            {
                throw new Backends.McpToolException(
                    $"'{path}' is not a readable element export: {ex.Message}. Expected a JSON array of " +
                    "objects with elementId, code, category, quantity, and unit.");
            }
        }

        return ParseCsv(text, path);
    }

    private static IReadOnlyList<BimElement> ParseCsv(string text, string path)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count < 2)
        {
            throw new Backends.McpToolException($"'{path}' has a header but no rows.");
        }

        var separator = lines[0].Contains(';') ? ';' : ',';
        var header = lines[0].Split(separator).Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToList();

        int Index(params string[] names)
        {
            foreach (var name in names)
            {
                var i = header.IndexOf(name);
                if (i >= 0)
                {
                    return i;
                }
            }

            return -1;
        }

        var idIdx = Index("elementid", "element_id", "id");
        var codeIdx = Index("code", "hrz_cod_pres", "keynote", "budgetcode", "codigo");
        var catIdx = Index("category", "categoria");
        var famIdx = Index("family", "familia");
        var typeIdx = Index("type", "tipo");
        var levelIdx = Index("level", "nivel");
        var qtyIdx = Index("quantity", "qty", "cantidad");
        var unitIdx = Index("unit", "unidad", "uom");

        if (idIdx < 0)
        {
            throw new Backends.McpToolException(
                $"'{path}' has no element-id column. Expected one of: elementId, element_id, id. " +
                $"Found: {string.Join(", ", header)}.");
        }

        var elements = new List<BimElement>();
        foreach (var line in lines.Skip(1))
        {
            var cells = SplitCsvLine(line, separator);
            string? Cell(int i) => i >= 0 && i < cells.Count && !string.IsNullOrWhiteSpace(cells[i]) ? cells[i] : null;

            var elementId = Cell(idIdx);
            if (elementId is null)
            {
                continue;
            }

            double? quantity = null;
            var rawQty = Cell(qtyIdx);
            if (rawQty is not null && double.TryParse(rawQty.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                quantity = parsed;
            }

            elements.Add(new BimElement
            {
                ElementId = elementId,
                Code = Cell(codeIdx),
                Category = Cell(catIdx),
                Family = Cell(famIdx),
                Type = Cell(typeIdx),
                Level = Cell(levelIdx),
                Quantity = quantity,
                Unit = Cell(unitIdx),
            });
        }

        return elements;
    }

    private static List<string> SplitCsvLine(string line, char separator)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == separator && !quoted)
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString().Trim());
        return cells;
    }
}
