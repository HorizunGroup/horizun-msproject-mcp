using System.Text.RegularExpressions;
using Horizun.ProjectMcp.Backends;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Analysis;

public sealed record ProposedLink
{
    public required int FromUid { get; init; }
    public required int ToUid { get; init; }
    public string? FromName { get; init; }
    public string? ToName { get; init; }
    public required string Group { get; init; }
    public required string Type { get; init; }
    public required double GapDays { get; init; }
    public required string Evidence { get; init; }
}

public sealed record SequenceReport
{
    public required string GroupBy { get; init; }
    public required int Groups { get; init; }
    public required int TasksConsidered { get; init; }
    public required int AlreadySequenced { get; init; }
    public required int Proposed { get; init; }
    public required IReadOnlyList<string> InferredOrder { get; init; }
    public required IReadOnlyList<ProposedLink> Links { get; init; }
    public double OrderConfidence { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Recovers the logic a schedule is missing by reading the sequence its own dates already state.
/// </summary>
/// <remarks>
/// A very common defect, in every industry and every tool: the planner laid the work out on the
/// bar chart in the right order but never linked it, so the network is a set of parallel chains
/// that do not know about each other. DCMA check 1 flags it; nothing fixes it, because the fix
/// looks like it needs domain knowledge.
/// <para>
/// It does not. The dates are the domain knowledge — somebody already decided that plastering
/// follows masonry when they placed the bars. This reads that decision back out, checks the same
/// order holds across every repetition rather than trusting one, and proposes the links. It never
/// invents an order the dates do not state, and it never proposes a link where a path already
/// exists.
/// </para>
/// </remarks>
public static class SequenceInferrer
{
    // Trailing identifiers: unit numbers, floor numbers, block letters. Whatever varies between
    // repetitions of the same work is the unit; whatever stays is the activity.
    private static readonly Regex UnitToken = new(@"[-–—#]?\s*\b(\d{1,4}[A-Za-z]?)\b\s*$", RegexOptions.Compiled);

    public static SequenceReport Infer(
        ProjectFile project, string groupBy, double minAgreement, int maxLinks, int groupDepth)
    {
        var notes = new List<string>();
        var leaves = ScheduleAnalyzer.Leaves(project)
            .Where(t => t.Start is not null && !t.Milestone)
            .ToList();

        var byGroup = groupBy.Trim().ToLowerInvariant() == "parent"
            ? GroupByParent(leaves)
            : GroupByUnit(leaves, groupDepth);

        if (byGroup.Count == 0)
        {
            return new SequenceReport
            {
                GroupBy = groupBy,
                Groups = 0,
                TasksConsidered = leaves.Count,
                AlreadySequenced = 0,
                Proposed = 0,
                InferredOrder = Array.Empty<string>(),
                Links = Array.Empty<ProposedLink>(),
                Notes = new[]
                {
                    "No repeating groups were found, so there is no repetition to read a common order "
                    + "out of. Try groupBy='parent' to use the WBS grouping instead, or link the work "
                    + "by hand with links_write.",
                },
            };
        }

        // The order each group states, then the order they agree on. One group is an anecdote;
        // agreement across many is the plan.
        var pairVotes = new Dictionary<(string Before, string After), int>();
        var activitySeen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (_, tasks) in byGroup)
        {
            var ordered = tasks
                .OrderBy(t => t.Start)
                .ThenBy(t => t.Finish)
                .Select(t => (Key: ScheduleLibrary.Normalise(t.Name), Task: t))
                .Where(x => x.Key.Length > 0)
                .DistinctBy(x => x.Key)
                .ToList();

            foreach (var (key, _) in ordered)
            {
                activitySeen[key] = activitySeen.GetValueOrDefault(key) + 1;
            }

            for (var i = 0; i + 1 < ordered.Count; i++)
            {
                var pair = (ordered[i].Key, ordered[i + 1].Key);
                pairVotes[pair] = pairVotes.GetValueOrDefault(pair) + 1;
            }
        }

        // Keep only orderings the repetitions overwhelmingly agree on. A pair that appears one way
        // in half the units and the other way in the rest states nothing.
        var agreed = new Dictionary<(string, string), double>();
        foreach (var ((before, after), votes) in pairVotes)
        {
            var opposite = pairVotes.GetValueOrDefault((after, before));
            var total = votes + opposite;
            if (total == 0)
            {
                continue;
            }

            var share = (double)votes / total;
            if (share >= minAgreement && votes >= 2)
            {
                agreed[(before, after)] = share;
            }
        }

        var order = TopologicalOrder(agreed, activitySeen);
        var confidence = agreed.Count == 0 ? 0 : Math.Round(agreed.Values.Average() * 100, 1);

        // Propose the links, group by group, only where nothing already connects the two.
        var links = new List<ProposedLink>();
        var alreadySequenced = 0;
        var calendar = new WorkingCalendar(project);

        foreach (var (groupName, tasks) in byGroup)
        {
            var ordered = tasks
                .OrderBy(t => t.Start)
                .Select(t => (Key: ScheduleLibrary.Normalise(t.Name), Task: t))
                .Where(x => x.Key.Length > 0)
                .ToList();

            for (var i = 0; i + 1 < ordered.Count; i++)
            {
                var (fromKey, from) = ordered[i];
                var (toKey, to) = ordered[i + 1];

                if (fromKey == toKey || !agreed.ContainsKey((fromKey, toKey)))
                {
                    continue;
                }

                if (Reachable(project, from.UniqueID!.Value, to.UniqueID!.Value))
                {
                    alreadySequenced++;
                    continue;
                }

                var gap = from.Finish is null || to.Start is null
                    ? 0
                    : calendar.WorkingDaysBetween(from.Finish.Value, to.Start.Value) - 1;

                links.Add(new ProposedLink
                {
                    FromUid = from.UniqueID!.Value,
                    ToUid = to.UniqueID!.Value,
                    FromName = from.Name,
                    ToName = to.Name,
                    Group = groupName,
                    Type = "FS",
                    GapDays = Math.Round(Math.Max(gap, 0), 1),
                    Evidence = $"{agreed[(fromKey, toKey)] * 100:0}% of the {byGroup.Count} group(s) "
                               + "run these two in this order.",
                });

                if (links.Count >= maxLinks)
                {
                    notes.Add(
                        $"Stopped at {maxLinks} proposals. Raise maxLinks, or apply these and run again.");
                    break;
                }
            }

            if (links.Count >= maxLinks)
            {
                break;
            }
        }

        notes.Add(
            $"The order comes from the dates already in the schedule, not from any assumption about "
            + $"the work: {byGroup.Count} repetition(s) were compared and only orderings at least "
            + $"{minAgreement * 100:0}% of them agree on were kept.");

        if (links.Count == 0 && agreed.Count > 0)
        {
            notes.Add("Every pair the dates agree on is already connected. There is nothing to add.");
        }

        if (agreed.Count == 0)
        {
            notes.Add(
                "The repetitions do not agree on any ordering, so nothing can be inferred. That "
                + "usually means the groups are not really repetitions of the same work — check "
                + "groupBy, or lower minAgreement if the variation is genuine.");
        }

        return new SequenceReport
        {
            GroupBy = groupBy,
            Groups = byGroup.Count,
            TasksConsidered = leaves.Count,
            AlreadySequenced = alreadySequenced,
            Proposed = links.Count,
            InferredOrder = order,
            Links = links,
            OrderConfidence = confidence,
            Notes = notes,
        };
    }

    /// <summary>
    /// Groups by the identifier that varies between repetitions — the unit number at the end of a
    /// task name. Language-independent: it keys on the digits, not on any word.
    /// </summary>
    private static List<(string Name, List<MPXJ.Net.Task> Tasks)> GroupByUnit(
        IReadOnlyList<MPXJ.Net.Task> leaves, int groupDepth)
    {
        var groups = new Dictionary<string, List<MPXJ.Net.Task>>(StringComparer.Ordinal);

        foreach (var task in leaves)
        {
            var match = UnitToken.Match(task.Name ?? string.Empty);
            if (!match.Success)
            {
                continue;
            }

            // The unit alone is ambiguous across blocks, so qualify it with the branch it sits in —
            // but a HIGH branch, the tower or the phase. Qualifying with the immediate parent is
            // what separates the very trades this needs to compare: in a schedule organised by
            // trade, apartment 101's structure and its masonry sit under different parents and
            // would never meet.
            var key = $"{Ancestor(task, groupDepth)}#{match.Groups[1].Value}";

            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = new List<MPXJ.Net.Task>();
            }

            list.Add(task);
        }

        // A group of one states no ordering.
        return groups.Where(g => g.Value.Count > 2)
            .Select(g => (g.Key, g.Value))
            .ToList();
    }

    /// <summary>
    /// The WBS of the ancestor at <paramref name="depth"/> outline levels down from the root —
    /// the tower, phase or building that a unit number is unique within.
    /// </summary>
    private static string Ancestor(MPXJ.Net.Task task, int depth)
    {
        var current = task;
        var chain = new List<MPXJ.Net.Task>();

        while (current is not null)
        {
            chain.Add(current);
            current = current.ParentTask;
        }

        // A task with no ancestors belongs to the only branch there is. Returning its own WBS would
        // give every task a unique branch and dissolve every group — which is exactly what happens
        // on a flat schedule, and flat schedules are common on small jobs.
        if (chain.Count <= 1)
        {
            return string.Empty;
        }

        chain.Reverse();
        var index = Math.Clamp(depth - 1, 0, chain.Count - 2);
        return chain[index].WBS ?? string.Empty;
    }

    private static List<(string Name, List<MPXJ.Net.Task> Tasks)> GroupByParent(
        IReadOnlyList<MPXJ.Net.Task> leaves)
    {
        return leaves
            .Where(t => t.ParentTask is not null)
            .GroupBy(t => t.ParentTask!.UniqueID ?? -1)
            .Where(g => g.Count() > 2)
            .Select(g => (g.First().ParentTask!.Name ?? $"uid {g.Key}", g.ToList()))
            .ToList();
    }

    /// <summary>The activity order implied by the agreed pairs, most-constrained first.</summary>
    private static List<string> TopologicalOrder(
        Dictionary<(string Before, string After), double> agreed, Dictionary<string, int> seen)
    {
        var nodes = agreed.Keys.SelectMany(k => new[] { k.Before, k.After }).ToHashSet(StringComparer.Ordinal);
        var inDegree = nodes.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);

        foreach (var (_, after) in agreed.Keys)
        {
            inDegree[after]++;
        }

        var ready = new List<string>(nodes.Where(n => inDegree[n] == 0));
        var order = new List<string>();

        while (ready.Count > 0)
        {
            // Break ties by how often the activity was seen, so the backbone comes first.
            ready.Sort((a, b) => seen.GetValueOrDefault(b).CompareTo(seen.GetValueOrDefault(a)));
            var next = ready[0];
            ready.RemoveAt(0);
            order.Add(next);

            foreach (var (before, after) in agreed.Keys.Where(k => k.Before == next))
            {
                if (--inDegree[after] == 0)
                {
                    ready.Add(after);
                }
            }
        }

        // Anything left sits in a disagreement loop; report it rather than dropping it silently.
        order.AddRange(nodes.Except(order, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal));
        return order;
    }

    /// <summary>Is <paramref name="to"/> already downstream of <paramref name="from"/>?</summary>
    private static bool Reachable(ProjectFile project, int from, int to)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(from);

        while (stack.Count > 0 && seen.Count < 4000)
        {
            var uid = stack.Pop();
            if (uid == to)
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
