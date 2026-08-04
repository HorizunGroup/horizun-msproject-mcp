using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Horizun.ProjectMcp.Analysis;
using Horizun.ProjectMcp.Backends;
using Horizun.ProjectMcp.Bim;
using Horizun.ProjectMcp.Model;
using ModelContextProtocol.Server;

namespace Horizun.ProjectMcp.Tools;

public sealed record ExportResult
{
    public required string Format { get; init; }
    public required string Path { get; init; }
    public required int Rows { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record ImportPlan
{
    public required string Source { get; init; }
    public required int RowsRead { get; init; }
    public required int Matched { get; init; }
    public required int Unmatched { get; init; }
    public required IReadOnlyList<ImportChange> Changes { get; init; }
    public required bool Applied { get; init; }
    public WriteResult? WriteResult { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record ImportChange
{
    public required int Uid { get; init; }
    public string? TaskName { get; init; }
    public required string Field { get; init; }
    public string? Current { get; init; }
    public string? Incoming { get; init; }
}

[McpServerToolType]
public static class InteropTools
{
    [McpServerTool(Name = "project_export")]
    [Description(
        "Export the schedule for another tool. 'mspdi' and 'mpx' write real schedule files; 'csv' and "
        + "'json' write a flat task table; 'pbip_dataset' writes the shaped dataset our Power BI report "
        + "consumes — tasks, assignments, earned value and the S-curve — rather than a raw dump.")]
    public static ExportResult ProjectExport(
        [Description("Document handle from project_open.")] string handle,
        [Description("Output file path.")] string path,
        [Description(
            "csv, json or pbip_dataset for a flat table; mspdi, mpx, mpp, xer, pmxml, planner "
            + "or sdef for a real schedule file.")]
        string format = "csv",
        [Description("Status date used for the earned-value block in pbip_dataset, yyyy-MM-dd.")]
        string? statusDate = null)
    {
        var session = SessionStore.Get(handle);
        var project = session.File;
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        switch (format.Trim().ToLowerInvariant())
        {
            case "mspdi":
            case "mpx":
            case "mpp":
            case "xer":
            case "pmxml":
            case "planner":
            case "sdef":
                MpxjBackend.Write(project, full, format);
                return new ExportResult { Format = format, Path = full, Rows = project.Tasks.Count };

            case "csv":
            {
                var tasks = ScheduleAnalyzer.Leaves(project).Select(t => MpxjMapper.ToDto(t, true)).ToList();
                var csv = new StringBuilder();
                csv.AppendLine("uid,wbs,name,start,finish,durationDays,percentComplete,critical,totalFloatDays,"
                               + "baselineStart,baselineFinish,workHours,cost");
                foreach (var t in tasks)
                {
                    csv.AppendLine(string.Join(',',
                        t.Uid, Csv(t.Wbs), Csv(t.Name), Csv(t.Start), Csv(t.Finish),
                        Csv(t.Duration), t.PercentComplete?.ToString("0.##") ?? "",
                        t.Critical, t.TotalFloatDays?.ToString("0.##") ?? "",
                        Csv(t.BaselineStart), Csv(t.BaselineFinish),
                        t.WorkHours?.ToString("0.##") ?? "", t.Cost?.ToString("0.##") ?? ""));
                }

                File.WriteAllText(full, csv.ToString());
                return new ExportResult { Format = "csv", Path = full, Rows = tasks.Count };
            }

            case "json":
            {
                var tasks = ScheduleAnalyzer.Leaves(project).Select(t => MpxjMapper.ToDto(t, true)).ToList();
                File.WriteAllText(full, JsonSerializer.Serialize(tasks,
                    new JsonSerializerOptions { WriteIndented = true }));
                return new ExportResult { Format = "json", Path = full, Rows = tasks.Count };
            }

            case "pbip_dataset":
            {
                var effective = QueryTools.ParseDate(statusDate)
                                ?? project.ProjectProperties.StatusDate ?? DateTime.Today;

                var dataset = new
                {
                    generatedUtc = DateTime.UtcNow,
                    project = new
                    {
                        name = project.ProjectProperties.Name,
                        start = MpxjMapper.Iso(project.ProjectProperties.StartDate),
                        finish = MpxjMapper.Iso(MpxjBackend.ProjectFinish(project)),
                        statusDate = effective.ToString("yyyy-MM-dd"),
                    },
                    tasks = ScheduleAnalyzer.Leaves(project).Select(t => MpxjMapper.ToDto(t, true)).ToList(),
                    assignments = project.ResourceAssignments.Select(MpxjMapper.ToDto).ToList(),
                    resources = project.Resources
                        .Where(r => r.UniqueID is not null)
                        .Select(r => MpxjMapper.ToDto(r))
                        .ToList(),
                    earnedValue = EarnedValue.Compare(project, effective, 0, byBranch: true),
                    quality = Dcma14.Run(project, effective, canRecalculate: false),
                };

                File.WriteAllText(full, JsonSerializer.Serialize(dataset,
                    new JsonSerializerOptions { WriteIndented = true }));

                return new ExportResult
                {
                    Format = "pbip_dataset",
                    Path = full,
                    Rows = project.Tasks.Count,
                    Notes = new[]
                    {
                        "Shaped for the Power BI report: tasks, assignments, resources, the earned-value "
                        + "block and the DCMA findings in one file. Point Power Query at this path.",
                    },
                };
            }

            default:
                throw new McpToolException(
                    $"Unknown format '{format}'. Use csv, json, pbip_dataset, or any format project_save "
                    + "writes: mspdi, mpx, mpp, xer, pmxml, planner, sdef.");
        }
    }

    [McpServerTool(Name = "project_import")]
    [Description(
        "Bring updates in from a CSV or JSON file — typically progress collected in the field. "
        + "Runs plan-first: by default it matches rows to tasks by uid and returns the field-by-field "
        + "diff without changing anything. Pass apply=true once you have read the plan, and the changes "
        + "go through tasks_write so every one of them is verified by re-reading the model.")]
    public static ImportPlan ProjectImport(
        [Description("Document handle from project_open.")] string handle,
        [Description("Path to the CSV or JSON file to import.")] string path,
        [Description("Apply the plan. Defaults to false — plan only.")] bool apply = false)
    {
        var session = SessionStore.Get(handle);
        var project = session.File;

        if (!File.Exists(path))
        {
            throw new McpToolException($"No import file at '{path}'.");
        }

        var rows = ReadRows(path);
        var changes = new List<ImportChange>();
        var ops = new List<TaskOp>();
        var unmatched = 0;

        foreach (var row in rows)
        {
            if (!row.TryGetValue("uid", out var uidText) || !int.TryParse(uidText, out var uid))
            {
                unmatched++;
                continue;
            }

            var task = project.GetTaskByUniqueID(uid);
            if (task is null)
            {
                unmatched++;
                continue;
            }

            var op = new TaskOp { Op = "update", Uid = uid };
            var touched = false;

            if (row.TryGetValue("percentcomplete", out var pc)
                && double.TryParse(pc, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var percent))
            {
                if (Math.Abs((task.PercentageComplete ?? 0) - percent) > 0.001)
                {
                    changes.Add(new ImportChange
                    {
                        Uid = uid, TaskName = task.Name, Field = "percentComplete",
                        Current = task.PercentageComplete?.ToString("0.##"), Incoming = percent.ToString("0.##"),
                    });
                    op = op with { PercentComplete = percent };
                    touched = true;
                }
            }

            foreach (var (key, field) in new[]
                     {
                         ("actualstart", "actualStart"), ("actualfinish", "actualFinish"),
                     })
            {
                if (!row.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var current = field == "actualStart" ? task.ActualStart : task.ActualFinish;
                var incoming = QueryTools.ParseDate(raw);
                if (incoming is null || current == incoming)
                {
                    continue;
                }

                changes.Add(new ImportChange
                {
                    Uid = uid, TaskName = task.Name, Field = field,
                    Current = MpxjMapper.Iso(current), Incoming = MpxjMapper.Iso(incoming),
                });

                op = field == "actualStart"
                    ? op with { ActualStart = raw }
                    : op with { ActualFinish = raw };
                touched = true;
            }

            if (touched)
            {
                ops.Add(op);
            }
        }

        var notes = new List<string>();
        WriteResult? result = null;

        if (apply && ops.Count > 0)
        {
            result = WriteTools.TasksWrite(handle, ops.ToArray(), dryRun: false);
        }
        else if (!apply)
        {
            notes.Add(
                ops.Count == 0
                    ? "Nothing to change — the file matches the schedule as it stands."
                    : $"Plan only. {ops.Count} task(s) would change. Re-run with apply=true to write them; "
                      + "each change is verified by re-reading the model.");
        }

        if (unmatched > 0)
        {
            notes.Add($"{unmatched} row(s) had no usable uid or matched no task, and were skipped.");
        }

        return new ImportPlan
        {
            Source = Path.GetFullPath(path),
            RowsRead = rows.Count,
            Matched = ops.Count,
            Unmatched = unmatched,
            Changes = changes,
            Applied = apply && ops.Count > 0,
            WriteResult = result,
            Notes = notes,
        };
    }

    [McpServerTool(Name = "bim_link")]
    [Description(
        "Tie schedule tasks to the model elements they build, matching on a shared code (HRZ_COD_PRES, "
        + "a keynote, a budget code). op='suggest' proposes the mapping from an element export and "
        + "separates out anything that matched only on a code prefix for you to confirm; 'set' stores it; "
        + "'get' reads it back; 'clear' removes it. The mapping lives in a sidecar next to the schedule, "
        + "so re-exporting the model never touches the planner's file.")]
    public static BimLinkResult BimLink(
        [Description("Document handle from project_open.")] string handle,
        [Description("suggest, set, get, or clear.")] string op = "get",
        [Description("Path to the element export (JSON array or CSV with elementId, code, quantity, unit). Required for suggest.")]
        string? elementsPath = null,
        [Description("Task custom field holding the code, e.g. 'Text1'. Defaults to Text1.")]
        string codeField = "Text1",
        [Description("Mappings below this confidence are listed for review instead of stored. Defaults to 100.")]
        double reviewBelow = 100)
    {
        var session = SessionStore.Get(handle);

        switch (op.Trim().ToLowerInvariant())
        {
            case "suggest":
            {
                if (string.IsNullOrWhiteSpace(elementsPath))
                {
                    throw new McpToolException(
                        "suggest needs elementsPath — the element export from Revit, Navisworks, or an IFC extract.");
                }

                var elements = BimLinkStore.LoadElements(Path.GetFullPath(elementsPath));
                return BimEngine.Suggest(session.File, elements, codeField, reviewBelow);
            }

            case "set":
            {
                if (string.IsNullOrWhiteSpace(elementsPath))
                {
                    throw new McpToolException("set needs elementsPath so the mapping can be rebuilt and stored.");
                }

                var elements = BimLinkStore.LoadElements(Path.GetFullPath(elementsPath));
                var suggestion = BimEngine.Suggest(session.File, elements, codeField, reviewBelow);

                var stored = BimLinkStore.Save(session.Path, new BimLinkSet
                {
                    CodeField = codeField,
                    Mappings = suggestion.Mappings,
                });

                return suggestion with { Op = "set", StorePath = stored };
            }

            case "get":
            {
                var set = BimLinkStore.Load(session.Path);
                return new BimLinkResult
                {
                    Op = "get",
                    Mapped = set.Mappings.Count,
                    UnmatchedTasks = 0,
                    UnmatchedElements = 0,
                    Mappings = set.Mappings,
                    StorePath = BimLinkStore.PathFor(session.Path),
                    Notes = set.Mappings.Count == 0
                        ? new[] { "No mapping is stored yet. Run bim_link with op='suggest' against an element export." }
                        : Array.Empty<string>(),
                };
            }

            case "clear":
            {
                var storePath = BimLinkStore.PathFor(session.Path);
                if (File.Exists(storePath))
                {
                    File.Delete(storePath);
                }

                return new BimLinkResult
                {
                    Op = "clear",
                    Mapped = 0,
                    UnmatchedTasks = 0,
                    UnmatchedElements = 0,
                    Mappings = Array.Empty<BimMapping>(),
                    StorePath = storePath,
                    Notes = new[] { "The stored mapping was deleted. The schedule itself was not touched." },
                };
            }

            default:
                throw new McpToolException($"Unknown op '{op}'. Use suggest, set, get, or clear.");
        }
    }

    [McpServerTool(Name = "bim_sync")]
    [Description(
        "Move data across the schedule/model boundary, in either direction. "
        + "direction='model_to_schedule' turns measured quantities into duration proposals at a stated "
        + "productivity rate — proposals only, which you then apply with tasks_write. "
        + "direction='schedule_to_model' writes one row per element carrying its task's dates and "
        + "progress: that file drives 4D animation in Navisworks, element parameters in Revit, and the "
        + "progress dashboard in Power BI.")]
    public static BimSyncResult BimSync(
        [Description("Document handle from project_open.")] string handle,
        [Description("model_to_schedule or schedule_to_model.")] string direction,
        [Description("Output path for schedule_to_model (.csv or .json).")] string? outputPath = null,
        [Description("Productivity per day per unit, e.g. {\"m3\": 25, \"m2\": 120}. Used by model_to_schedule.")]
        Dictionary<string, double>? productivity = null,
        [Description("Productivity to use when a unit is not listed. Defaults to 1.")]
        double defaultProductivity = 1,
        [Description("Status date used to classify element progress, yyyy-MM-dd.")] string? statusDate = null)
    {
        var session = SessionStore.Get(handle);
        var links = BimLinkStore.Load(session.Path);

        if (links.Mappings.Count == 0)
        {
            throw new McpToolException(
                "No task-to-element mapping is stored for this schedule. Run bim_link with op='set' first — " +
                "without it there is nothing tying the schedule to the model.");
        }

        var effective = QueryTools.ParseDate(statusDate)
                        ?? session.File.ProjectProperties.StatusDate ?? DateTime.Today;

        return direction.Trim().ToLowerInvariant() switch
        {
            "model_to_schedule" => BimEngine.PullQuantities(
                session.File, links,
                productivity ?? new Dictionary<string, double>(),
                defaultProductivity),

            "schedule_to_model" => BimEngine.PushDates(
                session.File, links,
                Path.GetFullPath(outputPath ?? throw new McpToolException(
                    "schedule_to_model needs outputPath — the .csv or .json to write the 4D rows to.")),
                effective),

            _ => throw new McpToolException(
                $"Unknown direction '{direction}'. Use model_to_schedule or schedule_to_model."),
        };
    }

    private static List<Dictionary<string, string>> ReadRows(string path)
    {
        var text = File.ReadAllText(path);

        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(text)
                         ?? new List<Dictionary<string, JsonElement>>();
            return parsed
                .Select(d => d.ToDictionary(
                    kv => kv.Key.ToLowerInvariant(),
                    kv => kv.Value.ValueKind == JsonValueKind.String
                        ? kv.Value.GetString() ?? ""
                        : kv.Value.ToString()))
                .ToList();
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count < 2)
        {
            return new List<Dictionary<string, string>>();
        }

        var separator = lines[0].Contains(';') ? ';' : ',';
        var header = lines[0].Split(separator).Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToList();

        return lines.Skip(1)
            .Select(line =>
            {
                var cells = line.Split(separator);
                var row = new Dictionary<string, string>();
                for (var i = 0; i < header.Count && i < cells.Length; i++)
                {
                    row[header[i]] = cells[i].Trim().Trim('"');
                }

                return row;
            })
            .ToList();
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
}
