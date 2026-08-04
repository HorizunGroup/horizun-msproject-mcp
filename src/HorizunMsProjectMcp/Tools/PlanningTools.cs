using System.ComponentModel;
using Horizun.ProjectMcp.Analysis;
using Horizun.ProjectMcp.Backends;
using ModelContextProtocol.Server;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Tools;

public sealed record GenerateResult
{
    public required string Handle { get; init; }
    public required string Path { get; init; }
    public required int TasksCreated { get; init; }
    public required int LinksCreated { get; init; }
    public required string DurationBasis { get; init; }
    public string? Start { get; init; }
    public string? Finish { get; init; }
    public required IReadOnlyList<string> Unmatched { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

[McpServerToolType]
public static class PlanningTools
{
    [McpServerTool(Name = "schedule_recovery")]
    [Description(
        "Work out what is late, what it is costing the finish date, and which changes to the logic "
        + "would actually recover time. Lists the late work ordered by how much of the schedule is "
        + "stuck behind it, then measures recovery levers — removing lag on the driving chain, "
        + "overlapping finish-to-start hand-offs, compressing the longest critical tasks. "
        + "Every option is applied to a throwaway copy, rescheduled, and reported with the finish "
        + "date it genuinely produces; an option that recovers nothing says so rather than being "
        + "offered as advice. Nothing is written to the open document.")]
    public static RecoveryReport ScheduleRecovery(
        [Description("Document handle from project_open.")] string handle,
        [Description("Status date, yyyy-MM-dd. Defaults to the project's own, then today.")]
        string? statusDate = null,
        [Description("The date you need to finish by, yyyy-MM-dd. Adds the gap to the report.")]
        string? targetFinish = null,
        [Description("How many recovery options to return. Defaults to 5.")] int maxOptions = 5)
    {
        var session = SessionStore.Get(handle);
        var effective = QueryTools.ParseDate(statusDate)
                        ?? session.File.ProjectProperties.StatusDate
                        ?? DateTime.Today;

        return RecoveryPlanner.Analyse(
            session.File, effective, QueryTools.ParseDate(targetFinish), maxOptions);
    }

    [McpServerTool(Name = "schedule_target")]
    [Description(
        "Test a schedule against a date you have been given. Reports whether the target is "
        + "achievable, how many working days short it falls, the driving chain with the dates each "
        + "task would have to hit, and every task the network does not actually hold in place — "
        + "work with no predecessor or no successor, which never moves when its neighbours do and "
        + "makes any target date arithmetic rather than a plan. Read-only.")]
    public static TargetReport ScheduleTarget(
        [Description("Document handle from project_open.")] string handle,
        [Description("The target date, yyyy-MM-dd.")] string target,
        [Description("'finish' (default) to test a completion date, or 'start' to test a start date.")]
        string anchor = "finish")
    {
        var session = SessionStore.Get(handle);
        var date = QueryTools.ParseDate(target)
                   ?? throw new McpToolException("A target date is required, as yyyy-MM-dd.");

        var byFinish = !anchor.Trim().Equals("start", StringComparison.OrdinalIgnoreCase);
        return TargetPlanner.Analyse(session.File, date, byFinish);
    }

    [McpServerTool(Name = "schedule_learn")]
    [Description(
        "Mine finished schedules for how long each kind of activity actually takes and what usually "
        + "comes before and after it. Reads any number of .mpp, MSPDI or Primavera files, normalises "
        + "task names so the same activity matches across projects and units, and reports each "
        + "activity's duration distribution — median, 80th percentile, range — together with its "
        + "typical predecessors and successors and the lag between them. "
        + "Save the result and hand it to schedule_generate to build a new schedule from experience "
        + "rather than from a blank page.")]
    public static ActivityLibrary ScheduleLearn(
        [Description("Paths of past schedules to learn from.")] string[] paths,
        [Description("Where to save the library as JSON. Omit to return it without saving.")]
        string? savePath = null,
        [Description("Custom field holding the budget code, e.g. 'Text1', to carry into the library.")]
        string? codeField = null,
        [Description("Ignore activities seen fewer times than this. Defaults to 3.")]
        int minOccurrences = 3,
        [Description(
            "Element exports (the same CSV or JSON bim_link reads) carrying measured quantities per "
            + "code. Supply them together with codeField and the library reports a productivity rate "
            + "per activity — how much a crew actually got through in a day — instead of only how "
            + "long the task was typed as taking.")]
        string[]? quantitySources = null)
    {
        if (paths.Length == 0)
        {
            throw new McpToolException("Give at least one schedule to learn from.");
        }

        if (quantitySources is { Length: > 0 } && string.IsNullOrWhiteSpace(codeField))
        {
            throw new McpToolException(
                "Quantities join to tasks on a shared code, so codeField is required alongside "
                + "quantitySources — pass the custom field the schedules carry it in, e.g. 'Text1'.");
        }

        var library = ScheduleLibrary.Learn(paths, codeField, minOccurrences, quantitySources);

        if (!string.IsNullOrWhiteSpace(savePath))
        {
            var saved = ScheduleLibrary.Save(library, savePath);
            library = library with
            {
                Notes = library.Notes.Append($"Saved to '{saved}'.").ToList(),
            };
        }

        return library;
    }

    [McpServerTool(Name = "schedule_generate")]
    [Description(
        "Build a new schedule from an activity library produced by schedule_learn. Each activity "
        + "gets the duration its history says it takes, and the dependencies that usually surround "
        + "it are recreated between the activities you asked for. "
        + "Returns an open handle, so run schedule_qa on it before trusting it and adjust with "
        + "tasks_write — this is a first draft grounded in what actually happened on past projects, "
        + "not a finished programme.")]
    public static GenerateResult ScheduleGenerate(
        [Description("Path to the library JSON from schedule_learn.")] string libraryPath,
        [Description("Path for the new schedule. Refuses to overwrite an existing file.")]
        string outputPath,
        [Description("Project start date, yyyy-MM-dd.")] string startDate,
        [Description(
            "Activity keys to include, as listed in the library. Omit to use every activity in it.")]
        string[]? activities = null,
        [Description("Name for the new project.")] string? name = null,
        [Description("'median' (default) for a normal run, or 'p80' to plan with contingency.")]
        string durationBasis = "median",
        [Description(
            "How many units to build — apartments, floors, blocks. Activities the library found to "
            + "repeat unit by unit are created once per unit and chained, which is how construction "
            + "schedules are actually shaped. Defaults to 1.")]
        int units = 1,
        [Description("Word naming a unit, used in the task names: 'apto', 'piso', 'torre'. Defaults to 'unidad'.")]
        string unitLabel = "unidad",
        [Description(
            "Measured quantities per activity key, e.g. {\"VACIADO LOSA\": 340}. Where the library "
            + "learned a productivity rate for that activity, the duration is sized from the quantity "
            + "instead of copied from history — the quantity is what changes between projects.")]
        Dictionary<string, double>? quantities = null)
    {
        var library = ScheduleLibrary.Load(libraryPath);
        var start = QueryTools.ParseDate(startDate)
                    ?? throw new McpToolException("A start date is required, as yyyy-MM-dd.");

        var byKey = library.Activities.ToDictionary(a => a.Key, StringComparer.Ordinal);
        var unmatched = new List<string>();

        List<ActivityEntry> chosen;
        if (activities is { Length: > 0 })
        {
            chosen = new List<ActivityEntry>();
            foreach (var wanted in activities)
            {
                var key = ScheduleLibrary.Normalise(wanted);
                if (byKey.TryGetValue(key, out var entry))
                {
                    chosen.Add(entry);
                }
                else
                {
                    unmatched.Add(wanted);
                }
            }
        }
        else
        {
            chosen = library.Activities.ToList();
        }

        if (chosen.Count == 0)
        {
            throw new McpToolException(
                "None of the requested activities are in the library. Call schedule_learn first, or "
                + "check the keys against the library's own list.");
        }

        var opened = SessionTools.ProjectOpen(
            outputPath, mode: "readwrite", create: true,
            name: name ?? Path.GetFileNameWithoutExtension(outputPath), startDate: startDate);

        var session = SessionStore.Get(opened.Handle);
        var project = session.File;
        var usePercentile80 = durationBasis.Trim().Equals("p80", StringComparison.OrdinalIgnoreCase);

        // Create the activities first, so the logic can be wired between them afterwards.
        // Work the library found to repeat unit by unit gets one task per unit; everything else
        // gets a single instance.
        var repeatCount = Math.Max(units, 1);
        var uidByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var repeatChains = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var created = 0;

        MPXJ.Net.Task NewTask(string label, double days)
        {
            var task = project.AddTask();
            if (task.UniqueID is not > 0)
            {
                task.UniqueID = project.Tasks.Select(t => t.UniqueID ?? 0).DefaultIfEmpty(0).Max() + 1;
            }

            task.Name = label;
            task.Duration = MPXJ.Net.Duration.GetInstance(Math.Max(days, 0.5), TimeUnit.Days);
            created++;
            return task;
        }

        var byQuantity = new List<string>();
        var normalisedQuantities = quantities is null
            ? new Dictionary<string, double>(StringComparer.Ordinal)
            : quantities.ToDictionary(
                kv => ScheduleLibrary.Normalise(kv.Key), kv => kv.Value, StringComparer.Ordinal);

        foreach (var entry in chosen)
        {
            var days = usePercentile80 ? entry.P80DurationDays : entry.MedianDurationDays;

            // A measured quantity beats a remembered duration: the rate is what carries over
            // between projects, the quantity is what changes.
            if (normalisedQuantities.TryGetValue(entry.Key, out var quantity)
                && entry.ProductivityPerDay is > 0 && quantity > 0)
            {
                days = Math.Round(quantity / entry.ProductivityPerDay.Value, 2);
                byQuantity.Add($"{entry.Label ?? entry.Key}: {quantity:0.##} {entry.Unit ?? "units"} "
                               + $"at {entry.ProductivityPerDay:0.##}/day = {days:0.##}d");
            }

            var stem = StripUnitSuffix(entry.Label ?? entry.Key);

            // Per-unit work is anything the history saw several times in a single project —
            // not only the activities that chain to themselves. A trade that follows another
            // trade through the building repeats just as much, it simply never links to itself.
            var perUnit = entry.RepeatsInSequence || entry.TypicalRepetitions > 1;

            if (perUnit && repeatCount > 1)
            {
                var chain = new List<int>();
                for (var unit = 1; unit <= repeatCount; unit++)
                {
                    var task = NewTask($"{stem} {unitLabel} {unit}", days);
                    chain.Add(task.UniqueID!.Value);
                }

                repeatChains[entry.Key] = chain;
                uidByKey[entry.Key] = chain[0];
            }
            else
            {
                var task = NewTask(stem, days);
                uidByKey[entry.Key] = task.UniqueID!.Value;
            }
        }

        // Chain through the units only where the history shows the work handing over to itself.
        // A trade that follows another trade through the building is sequenced by that other
        // trade, not by its own previous unit; chaining it as well would double the logic.
        var chainLinks = 0;
        foreach (var (key, chain) in repeatChains)
        {
            var entry = chosen.First(a => a.Key == key);
            if (!entry.RepeatsInSequence)
            {
                continue;
            }

            for (var i = 1; i < chain.Count; i++)
            {
                var predecessor = project.GetTaskByUniqueID(chain[i - 1]);
                var successor = project.GetTaskByUniqueID(chain[i]);
                if (predecessor is null || successor is null)
                {
                    continue;
                }

                try
                {
                    successor.AddPredecessor(new Relation.Builder(project)
                        .PredecessorTask(predecessor)
                        .SuccessorTask(successor)
                        .Type(RelationType.FinishStart)
                        .Lag(MPXJ.Net.Duration.GetInstance(Math.Max(entry.RepeatLagDays, 0), TimeUnit.Days)));
                    chainLinks++;
                }
                catch
                {
                    // A relation the backend refuses simply does not make it into the draft.
                }
            }
        }

        // Recreate the dependencies that usually surround each activity, but only between the
        // activities actually being built, and never one that would close a loop.
        var links = chainLinks;
        foreach (var entry in chosen)
        {
            var successorChain = repeatChains.GetValueOrDefault(entry.Key)
                                 ?? new List<int> { uidByKey[entry.Key] };

            foreach (var predecessor in entry.TypicalPredecessors)
            {
                // An activity that follows itself is the repeat chain, already wired above.
                if (predecessor.Key == entry.Key)
                {
                    continue;
                }

                var predChain = repeatChains.GetValueOrDefault(predecessor.Key);
                if (predChain is null && !uidByKey.ContainsKey(predecessor.Key))
                {
                    continue;
                }

                predChain ??= new List<int> { uidByKey[predecessor.Key] };

                // Match unit to unit where both repeat: the tiler follows the plasterer through
                // the building, rather than every apartment waiting on apartment one.
                for (var i = 0; i < successorChain.Count; i++)
                {
                    var successorUid = successorChain[i];
                    var predUid = predChain[Math.Min(i, predChain.Count - 1)];
                    WireLink(project, predUid, successorUid, predecessor, ref links);
                }
            }
        }

        static void WireLink(
            ProjectFile project, int predUid, int successorUid, LinkedActivity predecessor, ref int links)
        {
            {
                var successor = project.GetTaskByUniqueID(successorUid);
                if (successor is null || predUid == successorUid)
                {
                    return;
                }

                var predTask = project.GetTaskByUniqueID(predUid);
                if (predTask is null || WouldLoop(project, predUid, successorUid))
                {
                    return;
                }

                if (successor.Predecessors.Any(r => r.PredecessorTask?.UniqueID == predUid))
                {
                    return;
                }

                try
                {
                    successor.AddPredecessor(new Relation.Builder(project)
                        .PredecessorTask(predTask)
                        .SuccessorTask(successor)
                        .Type(MpxjMapper.ParseRelationType(predecessor.Type))
                        .Lag(MPXJ.Net.Duration.GetInstance(predecessor.MedianLagDays, TimeUnit.Days)));
                    links++;
                }
                catch
                {
                    // A relation the backend refuses simply does not make it into the draft.
                }
            }
        }

        var report = CpmScheduler.Run(project);
        session.Dirty = true;

        var notes = new List<string>
        {
            "A first draft grounded in the durations and logic of past projects. Run schedule_qa on it "
            + "and fix what it finds before treating it as a programme.",
            $"Durations taken from the {(usePercentile80 ? "80th percentile" : "median")} of "
            + $"{library.TasksRead} task(s) across {library.Sources.Count} source schedule(s).",
            "Nothing is on disk yet — call project_save when the draft is worth keeping.",
        };

        if (unmatched.Count > 0)
        {
            notes.Add($"{unmatched.Count} requested activity(ies) are not in the library and were skipped.");
        }

        if (byQuantity.Count > 0)
        {
            notes.Add($"{byQuantity.Count} activity(ies) sized from measured quantity and learned "
                      + $"productivity rather than from a remembered duration: "
                      + string.Join("; ", byQuantity.Take(6)));
        }
        else if (quantities is { Count: > 0 })
        {
            notes.Add(
                "None of the supplied quantities matched an activity carrying a learned productivity "
                + "rate, so every duration came from history. Run schedule_learn with quantitySources "
                + "and codeField to teach it the rates first.");
        }

        if (report.Warnings.Count > 0)
        {
            notes.AddRange(report.Warnings);
        }

        return new GenerateResult
        {
            Handle = opened.Handle,
            Path = opened.Path,
            TasksCreated = created,
            LinksCreated = links,
            DurationBasis = usePercentile80 ? "p80" : "median",
            Start = report.ProjectStart,
            Finish = report.ProjectFinish,
            Unmatched = unmatched,
            Notes = notes,
        };
    }

    /// <summary>
    /// Drops the unit identifier off a learned label so it can be renumbered. The library keyed
    /// "Estructura apto 101" and "Estructura apto 214" as one activity, but kept the first label
    /// it saw; reusing that verbatim would name every apartment 101.
    /// </summary>
    private static string StripUnitSuffix(string label)
    {
        var words = label.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        while (words.Count > 1 && words[^1].All(c => char.IsDigit(c) || c is '-' or '.'))
        {
            words.RemoveAt(words.Count - 1);
        }

        // The word naming the unit goes too — the caller supplies its own.
        if (words.Count > 1 && words[^1].Length <= 8 &&
            new[] { "apto", "apt", "piso", "torre", "casa", "nivel", "bloque", "unidad" }
                .Contains(words[^1].ToLowerInvariant()))
        {
            words.RemoveAt(words.Count - 1);
        }

        return words.Count == 0 ? label.Trim() : string.Join(' ', words);
    }

    /// <summary>True when linking from -> to would close a cycle.</summary>
    private static bool WouldLoop(ProjectFile project, int from, int to)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(to);

        while (stack.Count > 0)
        {
            var uid = stack.Pop();
            if (uid == from)
            {
                return true;
            }

            if (!seen.Add(uid))
            {
                continue;
            }

            var task = project.GetTaskByUniqueID(uid);
            if (task is null)
            {
                continue;
            }

            foreach (var relation in task.Successors)
            {
                var next = relation.SuccessorTask?.UniqueID;
                if (next is not null)
                {
                    stack.Push(next.Value);
                }
            }
        }

        return false;
    }
}
