using Horizun.ProjectMcp.Backends;
using Horizun.ProjectMcp.Model;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Analysis;

public sealed record ScheduleAnalysis
{
    public CriticalPathReport? CriticalPath { get; init; }
    public FloatDistribution? FloatDistribution { get; init; }
    public IReadOnlyList<LinkDto>? LongestPath { get; init; }
    public IReadOnlyList<OverallocationWindow>? Overallocation { get; init; }
    public IReadOnlyList<MilestoneStatus>? Milestones { get; init; }
    public DependencyHealth? DependencyHealth { get; init; }
}

public sealed record CriticalPathReport
{
    public required int Count { get; init; }
    public required double TotalDurationDays { get; init; }
    public string? ProjectFinish { get; init; }
    public required IReadOnlyList<TaskDto> Chain { get; init; }
}

public sealed record FloatDistribution
{
    public required int Negative { get; init; }
    public required int Zero { get; init; }
    public required int UpTo5Days { get; init; }
    public required int UpTo20Days { get; init; }
    public required int UpTo44Days { get; init; }
    public required int Over44Days { get; init; }
    public double? MedianDays { get; init; }
}

public sealed record MilestoneStatus
{
    public required int Uid { get; init; }
    public string? Name { get; init; }
    public string? Finish { get; init; }
    public string? Deadline { get; init; }
    public string? BaselineFinish { get; init; }
    public double? SlipDays { get; init; }
    public required string Status { get; init; }
}

public sealed record DependencyHealth
{
    public required int Tasks { get; init; }
    public required int LeafTasks { get; init; }
    public required int MissingPredecessor { get; init; }
    public required int MissingSuccessor { get; init; }
    public required int FinishStart { get; init; }
    public required int StartStart { get; init; }
    public required int FinishFinish { get; init; }
    public required int StartFinish { get; init; }
    public required int PositiveLags { get; init; }
    public required int NegativeLags { get; init; }
}

/// <summary>
/// Server-side schedule analysis. The point is that the agent asks a question and gets an
/// answer, instead of pulling two thousand tasks into context to work it out itself.
/// </summary>
public static class ScheduleAnalyzer
{
    public static IEnumerable<MPXJ.Net.Task> Leaves(ProjectFile project) =>
        project.Tasks.Where(t => !t.Summary && t.UniqueID is not null and not 0);

    public static ScheduleAnalysis Analyze(ProjectFile project, IReadOnlyCollection<string> aspects)
    {
        var want = aspects.Count == 0
            ? new HashSet<string> { "critical_path", "float_distribution", "overallocation", "milestones", "dependency_health" }
            : aspects.Select(a => a.Trim().ToLowerInvariant()).ToHashSet();

        return new ScheduleAnalysis
        {
            CriticalPath = want.Contains("critical_path") ? BuildCriticalPath(project) : null,
            FloatDistribution = want.Contains("float_distribution") ? BuildFloatDistribution(project) : null,
            LongestPath = want.Contains("longest_path") ? BuildLongestPath(project) : null,
            Overallocation = want.Contains("overallocation") ? ResourceAnalyzer.Find(project) : null,
            Milestones = want.Contains("milestones") ? BuildMilestones(project) : null,
            DependencyHealth = want.Contains("dependency_health") ? BuildDependencyHealth(project) : null,
        };
    }

    public static CriticalPathReport BuildCriticalPath(ProjectFile project)
    {
        var chain = Leaves(project)
            .Where(t => t.Critical)
            .OrderBy(t => t.Start ?? DateTime.MaxValue)
            .ToList();

        return new CriticalPathReport
        {
            Count = chain.Count,
            TotalDurationDays = Math.Round(chain.Sum(t => MpxjMapper.Days(t.Duration) ?? 0), 2),
            ProjectFinish = MpxjMapper.Iso(MpxjBackend.ProjectFinish(project)),
            Chain = chain.Select(t => MpxjMapper.ToDto(t)).ToList(),
        };
    }

    public static FloatDistribution BuildFloatDistribution(ProjectFile project)
    {
        var floats = Leaves(project)
            .Select(t => MpxjMapper.Days(t.TotalSlack))
            .Where(f => f is not null)
            .Select(f => f!.Value)
            .OrderBy(f => f)
            .ToList();

        double? median = floats.Count == 0
            ? null
            : Math.Round(floats[floats.Count / 2], 2);

        return new FloatDistribution
        {
            Negative = floats.Count(f => f < 0),
            Zero = floats.Count(f => Math.Abs(f) < 0.001),
            UpTo5Days = floats.Count(f => f > 0.001 && f <= 5),
            UpTo20Days = floats.Count(f => f > 5 && f <= 20),
            UpTo44Days = floats.Count(f => f > 20 && f <= 44),
            Over44Days = floats.Count(f => f > 44),
            MedianDays = median,
        };
    }

    /// <summary>
    /// The driving chain — the links that actually push the finish date, walked backwards
    /// from the last-finishing task through zero-float predecessors.
    /// </summary>
    public static IReadOnlyList<LinkDto> BuildLongestPath(ProjectFile project)
    {
        var last = Leaves(project)
            .Where(t => t.Finish is not null)
            .OrderByDescending(t => t.Finish)
            .FirstOrDefault();

        if (last is null)
        {
            return Array.Empty<LinkDto>();
        }

        var path = new List<LinkDto>();
        var visited = new HashSet<int>();
        var current = last;

        while (current is not null && current.UniqueID is not null && visited.Add(current.UniqueID.Value))
        {
            var driver = current.Predecessors
                .Where(r => r.PredecessorTask is not null)
                .OrderBy(r => MpxjMapper.Days(r.PredecessorTask!.TotalSlack) ?? double.MaxValue)
                .ThenByDescending(r => r.PredecessorTask!.Finish)
                .FirstOrDefault();

            if (driver is null)
            {
                break;
            }

            path.Add(MpxjMapper.ToDto(driver, driving: true));
            current = driver.PredecessorTask;
        }

        path.Reverse();
        return path;
    }

    public static IReadOnlyList<MilestoneStatus> BuildMilestones(ProjectFile project)
    {
        var result = new List<MilestoneStatus>();

        foreach (var task in project.Tasks.Where(t => t.Milestone && t.UniqueID is not null))
        {
            double? slip = null;
            if (task.Finish is not null && task.BaselineFinish is not null)
            {
                slip = Math.Round((task.Finish.Value - task.BaselineFinish.Value).TotalDays, 2);
            }

            var status = "on_track";
            if (task.PercentageComplete >= 100)
            {
                status = "complete";
            }
            else if (task.Deadline is not null && task.Finish is not null && task.Finish > task.Deadline)
            {
                status = "misses_deadline";
            }
            else if (slip is > 0)
            {
                status = "behind_baseline";
            }

            result.Add(new MilestoneStatus
            {
                Uid = task.UniqueID!.Value,
                Name = task.Name,
                Finish = MpxjMapper.Iso(task.Finish),
                Deadline = MpxjMapper.Iso(task.Deadline),
                BaselineFinish = MpxjMapper.Iso(task.BaselineFinish),
                SlipDays = slip,
                Status = status,
            });
        }

        return result.OrderBy(m => m.Finish).ToList();
    }

    public static DependencyHealth BuildDependencyHealth(ProjectFile project)
    {
        var leaves = Leaves(project).ToList();
        int fs = 0, ss = 0, ff = 0, sf = 0, positive = 0, negative = 0;

        foreach (var task in leaves)
        {
            foreach (var relation in task.Predecessors)
            {
                switch (relation.Type)
                {
                    case RelationType.FinishStart: fs++; break;
                    case RelationType.StartStart: ss++; break;
                    case RelationType.FinishFinish: ff++; break;
                    case RelationType.StartFinish: sf++; break;
                }

                var lag = MpxjMapper.Days(relation.Lag) ?? 0;
                if (lag > 0.001) positive++;
                else if (lag < -0.001) negative++;
            }
        }

        return new DependencyHealth
        {
            Tasks = project.Tasks.Count,
            LeafTasks = leaves.Count,
            MissingPredecessor = leaves.Count(t => t.Predecessors.Count == 0),
            MissingSuccessor = leaves.Count(t => t.Successors.Count == 0),
            FinishStart = fs,
            StartStart = ss,
            FinishFinish = ff,
            StartFinish = sf,
            PositiveLags = positive,
            NegativeLags = negative,
        };
    }
}
