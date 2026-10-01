using Horizun.ProjectMcp.Backends;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Writes;

/// <summary>What a task costs, as Microsoft Project adds it up: its fixed cost plus its resources.</summary>
/// <remarks>
/// MPXJ stores a task's cost; it does not compute it. A fixed cost written with <c>tasks_write</c> left
/// the stored cost at 0, so a baseline saved afterwards recorded a budget of 0, and every scenario
/// cost 0 on a schedule carrying 4,000 M in fixed costs. Totals go through here instead.
/// </remarks>
public static class Costs
{
    public static double Of(MPXJ.Net.Task task)
    {
        var computed = Convert.ToDouble(task.FixedCost ?? 0)
                       + task.ResourceAssignments.Sum(OfAssignment);
        // Nothing to add up (an imported file whose costs only Project knows): the stored figure.
        return computed > 0 ? Math.Round(computed, 2) : Convert.ToDouble(task.Cost ?? 0);
    }

    /// <summary>Keeps the stored cost in step after a fixed cost or an assignment changes, so every
    /// reader of the field — queries, exports, Project itself — sees the same figure.</summary>
    public static void Refresh(MPXJ.Net.Task task)
    {
        if (!task.Summary)
        {
            task.Cost = Of(task);
        }
    }

    private static double OfAssignment(ResourceAssignment assignment)
    {
        if (assignment.Cost is { } stored && Convert.ToDouble(stored) > 0)
        {
            return Convert.ToDouble(stored);
        }

        var resource = assignment.Resource;
        if (resource is null)
        {
            return 0;
        }

        var perUse = Convert.ToDouble(resource.CostPerUse ?? 0);
        var rate = resource.StandardRate;
        if (rate is null || rate.Amount <= 0)
        {
            return perUse;
        }

        if (resource.Type == ResourceType.Material)
        {
            // A material's rate is per unit; its quantity is held ×100, as percentages are.
            return perUse + rate.Amount * Convert.ToDouble(assignment.Units ?? 0) / 100.0;
        }

        var hours = MpxjMapper.Hours(assignment.Work) ?? 0;
        var perHour = rate.Units switch
        {
            TimeUnit.Minutes => rate.Amount * 60,
            TimeUnit.Days => rate.Amount / MpxjMapper.HoursPerDay,
            TimeUnit.Weeks => rate.Amount / (MpxjMapper.HoursPerDay * 5),
            TimeUnit.Months => rate.Amount / (MpxjMapper.HoursPerDay * 20),
            TimeUnit.Years => rate.Amount / (MpxjMapper.HoursPerDay * 260),
            _ => rate.Amount,
        };
        return perUse + hours * perHour;
    }
}
