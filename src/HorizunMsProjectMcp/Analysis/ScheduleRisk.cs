using Horizun.ProjectMcp.Backends;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Analysis;

public sealed record RiskRange
{
    public required double Optimistic { get; init; }
    public required double MostLikely { get; init; }
    public required double Pessimistic { get; init; }
}

public sealed record RiskDriver
{
    public required int Uid { get; init; }
    public string? Name { get; init; }

    /// <summary>Share of the runs in which the task was on the path that set the finish.</summary>
    public required double CriticalityPercent { get; init; }

    /// <summary>Correlation between the task's sampled duration and the finish: how much its
    /// uncertainty moves the end date. The ranking a risk register should start from.</summary>
    public required double Sensitivity { get; init; }

    public required string Range { get; init; }
}

public sealed record RiskReport
{
    public required int Iterations { get; init; }
    public required string DataDate { get; init; }
    public required string DeterministicFinish { get; init; }
    public string? ScheduledFinish { get; init; }
    public required IReadOnlyDictionary<string, string> Percentiles { get; init; }
    public string? TargetDate { get; init; }
    public double? ProbabilityOfTarget { get; init; }
    public string? BaselineFinish { get; init; }
    public double? ProbabilityOfBaseline { get; init; }

    /// <summary>Working days of contingency between the deterministic finish and P80 — what to hold
    /// back to promise a date with 80% confidence.</summary>
    public required double ContingencyP80Days { get; init; }

    public required IReadOnlyList<RiskDriver> Drivers { get; init; }
    public required IReadOnlyList<HistogramBin> Histogram { get; init; }
    public required string Assumptions { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record HistogramBin
{
    public required string Finish { get; init; }
    public required int Runs { get; init; }
    public required double CumulativePercent { get; init; }
}

/// <summary>
/// Quantitative schedule risk analysis by Monte Carlo simulation (PMBOK: perform quantitative risk
/// analysis). Each unfinished task's remaining duration is sampled from a three-point range, the
/// network is rescheduled forward in working days, and the distribution of the finish date is reported
/// with the probability of meeting a date and the tasks driving the uncertainty.
/// </summary>
/// <remarks>
/// The simulation is a forward pass over the schedule's own logic — all four relation types and their
/// lags, in working days of the project calendar — from the status date. It does not re-run Microsoft
/// Project a thousand times; it is calibrated instead: the run with every task at its most likely
/// duration is reported beside the schedule's own finish, so any difference between the two (hard
/// constraints, calendars per task) is visible rather than hidden in the percentiles.
/// </remarks>
public static class ScheduleRisk
{
    private sealed class Node
    {
        public required MPXJ.Net.Task Task { get; init; }
        public required int Uid { get; init; }
        public double Floor { get; set; }
        public double Done;
        public RiskRange Range = null!;
        public List<(int From, RelationType Type, double Lag)> Preds { get; } = new();
    }

    public static RiskReport Analyse(
        ProjectFile project, DateTime dataDate, int iterations, double optimisticPct, double pessimisticPct,
        IReadOnlyDictionary<int, RiskRange>? ranges, DateTime? target, string distribution, int? seed)
    {
        iterations = Math.Clamp(iterations, 100, 20000);
        var calendar = new WorkingCalendar(project);
        var hours = project.ProjectProperties.MinutesPerDay is > 0
            ? Convert.ToDouble(project.ProjectProperties.MinutesPerDay) / 60.0 : MpxjMapper.HoursPerDay;
        var notes = new List<string>();

        var leaves = ScheduleAnalyzer.Leaves(project).Where(t => t.UniqueID is not null).ToList();
        var nodes = new Dictionary<int, Node>();
        var ranged = 0;
        foreach (var t in leaves)
        {
            var uid = t.UniqueID!.Value;
            var percent = Convert.ToDouble(t.PercentageComplete ?? 0);
            var total = MpxjMapper.Days(t.Duration, hours) ?? 0;
            var remaining = percent >= 100 ? 0 : MpxjMapper.Days(t.RemainingDuration, hours) ?? total * (1 - percent / 100);
            var node = new Node { Task = t, Uid = uid, Done = percent >= 100 ? 1 : 0 };

            if (ranges is not null && ranges.TryGetValue(uid, out var given))
            {
                node.Range = given;
                ranged++;
            }
            else
            {
                node.Range = new RiskRange
                {
                    Optimistic = Math.Max(0, remaining * (1 + optimisticPct / 100)),
                    MostLikely = remaining,
                    Pessimistic = remaining * (1 + pessimisticPct / 100),
                };
            }

            // Where the task cannot start before: its current start, for work with nothing driving it.
            node.Floor = t.Start is { } start && start > dataDate ? calendar.WorkingDaysBetween(dataDate, start) : 0;
            nodes[uid] = node;
        }

        foreach (var node in nodes.Values)
        {
            foreach (var r in node.Task.Predecessors)
            {
                if (r.PredecessorTask?.UniqueID is int from && nodes.ContainsKey(from))
                {
                    node.Preds.Add((from, r.Type ?? RelationType.FinishStart, MpxjMapper.Days(r.Lag, hours) ?? 0));
                }
            }
        }

        // Tasks with predecessors start where the logic puts them, not at their current date.
        foreach (var node in nodes.Values.Where(n => n.Preds.Count > 0))
        {
            node.Floor = 0;
        }

        var order = TopologicalOrder(nodes);
        var rng = seed is null ? new Random() : new Random(seed.Value);
        var pert = distribution.Trim().Equals("pert", StringComparison.OrdinalIgnoreCase);

        var finishes = new double[iterations];
        var sampled = nodes.Keys.ToDictionary(k => k, _ => new double[iterations]);
        var critical = nodes.Keys.ToDictionary(k => k, _ => 0);

        double deterministic = Pass(nodes, order, n => n.Range.MostLikely, null, out _);

        for (var i = 0; i < iterations; i++)
        {
            var draw = new Dictionary<int, double>(nodes.Count);
            foreach (var n in nodes.Values)
            {
                draw[n.Uid] = n.Done >= 1 ? 0 : Sample(n.Range, rng, pert);
                sampled[n.Uid][i] = draw[n.Uid];
            }

            finishes[i] = Pass(nodes, order, n => draw[n.Uid], draw, out var path);
            foreach (var uid in path)
            {
                critical[uid]++;
            }
        }

        var sorted = finishes.OrderBy(f => f).ToArray();
        double Percentile(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        string DateAt(double offset) => calendar.AddWorkingDays(dataDate, offset).ToString("yyyy-MM-dd");

        var percentiles = new Dictionary<string, string>
        {
            ["P10"] = DateAt(Percentile(0.10)),
            ["P50"] = DateAt(Percentile(0.50)),
            ["P80"] = DateAt(Percentile(0.80)),
            ["P90"] = DateAt(Percentile(0.90)),
        };

        double? Probability(DateTime? date)
        {
            if (date is null)
            {
                return null;
            }

            var limit = date.Value <= dataDate ? 0 : calendar.WorkingDaysBetween(dataDate, date.Value);
            return Math.Round(100.0 * finishes.Count(f => f <= limit + 1e-9) / finishes.Length, 1);
        }

        var baselineFinish = leaves.Select(t => t.BaselineFinish).Where(d => d is not null).DefaultIfEmpty().Max();

        var drivers = nodes.Values
            .Where(n => n.Done < 1 && n.Range.Pessimistic > n.Range.Optimistic)
            .Select(n => new RiskDriver
            {
                Uid = n.Uid,
                Name = n.Task.Name,
                CriticalityPercent = Math.Round(100.0 * critical[n.Uid] / iterations, 1),
                Sensitivity = Math.Round(Correlation(sampled[n.Uid], finishes), 3),
                Range = $"{n.Range.Optimistic:0.#} / {n.Range.MostLikely:0.#} / {n.Range.Pessimistic:0.#} d",
            })
            .OrderByDescending(d => d.Sensitivity)
            .ThenByDescending(d => d.CriticalityPercent)
            .Take(15)
            .ToList();

        var bins = new List<HistogramBin>();
        if (sorted.Length > 0)
        {
            var low = sorted[0];
            var high = sorted[^1];
            var width = Math.Max((high - low) / 12, 1);
            var running = 0;
            for (var b = low; b <= high + 1e-9; b += width)
            {
                var count = sorted.Count(f => f >= b && f < b + width || b + width > high && f >= b);
                running += count;
                bins.Add(new HistogramBin { Finish = DateAt(b + width), Runs = count, CumulativePercent = Math.Round(100.0 * Math.Min(running, sorted.Length) / sorted.Length, 1) });
                if (running >= sorted.Length)
                {
                    break;
                }
            }
        }

        var scheduled = MpxjBackend.ProjectFinish(project);
        if (scheduled is not null)
        {
            var gap = calendar.WorkingDaysBetween(calendar.AddWorkingDays(dataDate, deterministic), scheduled.Value);
            if (Math.Abs(gap) > 2)
            {
                notes.Add($"With every task at its most likely duration the simulation finishes {Math.Abs(gap):0.#} working "
                          + $"days {(gap > 0 ? "earlier" : "later")} than the schedule itself. Constraints, deadlines or task "
                          + "calendars the forward pass does not model account for the difference; read the percentiles "
                          + "relative to the deterministic finish.");
            }
        }

        if (ranged == 0)
        {
            notes.Add($"No three-point estimates were given, so every remaining duration ranges from {optimisticPct:+0;-0}% to "
                      + $"{pessimisticPct:+0;-0}% of its current value. Give the tasks you know best their own ranges "
                      + "('ranges') — a flat band is a starting point, not a risk assessment.");
        }

        return new RiskReport
        {
            Iterations = iterations,
            DataDate = dataDate.ToString("yyyy-MM-dd"),
            DeterministicFinish = DateAt(deterministic),
            ScheduledFinish = MpxjMapper.Iso(scheduled),
            Percentiles = percentiles,
            TargetDate = target?.ToString("yyyy-MM-dd"),
            ProbabilityOfTarget = Probability(target),
            BaselineFinish = MpxjMapper.Iso(baselineFinish),
            ProbabilityOfBaseline = Probability(baselineFinish),
            ContingencyP80Days = Math.Round(Math.Max(0, Percentile(0.80) - deterministic), 1),
            Drivers = drivers,
            Histogram = bins,
            Assumptions = $"{(pert ? "PERT" : "Triangular")} distribution on remaining durations; {ranged} task(s) with "
                          + $"their own range, the rest {optimisticPct:+0;-0}%/{pessimisticPct:+0;-0}%; durations independent; "
                          + "logic, relation types and lags as scheduled; working days of the project calendar from the data date.",
            Notes = notes,
        };
    }

    private static double Pass(
        Dictionary<int, Node> nodes, IReadOnlyList<int> order, Func<Node, double> duration,
        Dictionary<int, double>? draw, out List<int> path)
    {
        var es = new Dictionary<int, double>(nodes.Count);
        var ef = new Dictionary<int, double>(nodes.Count);
        var driver = new Dictionary<int, int?>(nodes.Count);
        foreach (var uid in order)
        {
            var n = nodes[uid];
            var d = duration(n);
            var start = n.Floor;
            int? by = null;
            foreach (var (from, type, lag) in n.Preds)
            {
                var candidate = type switch
                {
                    RelationType.StartStart => es[from] + lag,
                    RelationType.FinishFinish => ef[from] + lag - d,
                    RelationType.StartFinish => es[from] + lag - d,
                    _ => ef[from] + lag,
                };
                if (candidate > start)
                {
                    start = candidate;
                    by = from;
                }
            }

            es[uid] = Math.Max(0, start);
            ef[uid] = es[uid] + d;
            driver[uid] = by;
        }

        var last = ef.OrderByDescending(kv => kv.Value).First();
        path = new List<int>();
        int? at = last.Key;
        while (at is int u && path.Count < nodes.Count)
        {
            path.Add(u);
            at = driver[u];
        }

        return last.Value;
    }

    private static List<int> TopologicalOrder(Dictionary<int, Node> nodes)
    {
        var indegree = nodes.ToDictionary(kv => kv.Key, kv => kv.Value.Preds.Count);
        var next = nodes.Values.GroupBy(n => n.Uid).ToDictionary(g => g.Key, _ => new List<int>());
        foreach (var n in nodes.Values)
        {
            foreach (var (from, _, _) in n.Preds)
            {
                next[from].Add(n.Uid);
            }
        }

        var queue = new Queue<int>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var order = new List<int>(nodes.Count);
        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            order.Add(u);
            foreach (var v in next[u])
            {
                if (--indegree[v] == 0)
                {
                    queue.Enqueue(v);
                }
            }
        }

        if (order.Count != nodes.Count)
        {
            throw new McpToolException("The schedule's logic contains a cycle, so it cannot be simulated. schedule_qa names it.");
        }

        return order;
    }

    private static double Sample(RiskRange r, Random rng, bool pert)
    {
        double a = r.Optimistic, m = r.MostLikely, b = r.Pessimistic;
        if (b <= a)
        {
            return m;
        }

        if (pert)
        {
            // Beta-PERT via two gamma draws.
            var alpha = 1 + 4 * (m - a) / (b - a);
            var beta = 1 + 4 * (b - m) / (b - a);
            var x = Gamma(alpha, rng);
            var y = Gamma(beta, rng);
            return a + (b - a) * x / (x + y);
        }

        var u = rng.NextDouble();
        var c = (m - a) / (b - a);
        return u < c ? a + Math.Sqrt(u * (b - a) * (m - a)) : b - Math.Sqrt((1 - u) * (b - a) * (b - m));
    }

    private static double Gamma(double shape, Random rng)
    {
        if (shape < 1)
        {
            return Gamma(shape + 1, rng) * Math.Pow(rng.NextDouble(), 1 / shape);
        }

        var d = shape - 1.0 / 3;
        var c = 1 / Math.Sqrt(9 * d);
        while (true)
        {
            double x, v;
            do
            {
                var u1 = 1 - rng.NextDouble();
                var u2 = rng.NextDouble();
                x = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                v = 1 + c * x;
            }
            while (v <= 0);

            v = v * v * v;
            var u = rng.NextDouble();
            if (u < 1 - 0.0331 * x * x * x * x || Math.Log(u) < 0.5 * x * x + d * (1 - v + Math.Log(v)))
            {
                return d * v;
            }
        }
    }

    private static double Correlation(double[] x, double[] y)
    {
        var n = x.Length;
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            sxy += (x[i] - mx) * (y[i] - my);
            sxx += (x[i] - mx) * (x[i] - mx);
            syy += (y[i] - my) * (y[i] - my);
        }

        return sxx <= 0 || syy <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }
}
