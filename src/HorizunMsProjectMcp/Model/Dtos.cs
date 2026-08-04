namespace Horizun.ProjectMcp.Model;

/// <summary>A task as the agent sees it. Identity is always <see cref="Uid"/>, never <see cref="Id"/>.</summary>
public sealed record TaskDto
{
    public required int Uid { get; init; }

    /// <summary>Row number. Display only — it shifts when tasks are inserted or deleted.</summary>
    public int? Id { get; init; }

    public string? Wbs { get; init; }
    public string? Name { get; init; }
    public int? OutlineLevel { get; init; }
    public bool Summary { get; init; }
    public bool Milestone { get; init; }
    public bool Active { get; init; } = true;
    public string? Start { get; init; }
    public string? Finish { get; init; }
    public string? Duration { get; init; }
    public double? PercentComplete { get; init; }
    public bool Critical { get; init; }
    public double? TotalFloatDays { get; init; }
    public double? FreeFloatDays { get; init; }

    /// <summary>Earliest the logic allows. Together with the late dates this is the float, shown.</summary>
    public string? EarlyStart { get; init; }

    public string? EarlyFinish { get; init; }
    public string? LateStart { get; init; }
    public string? LateFinish { get; init; }
    public string? Deadline { get; init; }
    public string? ConstraintType { get; init; }
    public string? ConstraintDate { get; init; }
    public string? ActualStart { get; init; }
    public string? ActualFinish { get; init; }
    public double? WorkHours { get; init; }
    public double? Cost { get; init; }
    public string? BaselineStart { get; init; }
    public string? BaselineFinish { get; init; }
    public int? ParentUid { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyDictionary<string, string>? Custom { get; init; }
}

public sealed record LinkDto
{
    public required int FromUid { get; init; }
    public required int ToUid { get; init; }
    public required string Type { get; init; }
    public double LagDays { get; init; }
    public string? FromName { get; init; }
    public string? ToName { get; init; }
    public bool Driving { get; init; }
}

public sealed record ResourceDto
{
    public required int Uid { get; init; }
    public int? Id { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
    public double? MaxUnits { get; init; }
    public double? StandardRate { get; init; }
    public double? CostTotal { get; init; }
    public double? WorkHours { get; init; }
    public bool Overallocated { get; init; }
    public IReadOnlyList<AssignmentDto>? Assignments { get; init; }
}

public sealed record AssignmentDto
{
    public required int TaskUid { get; init; }
    public required int ResourceUid { get; init; }
    public string? TaskName { get; init; }
    public string? ResourceName { get; init; }
    public double? Units { get; init; }
    public double? WorkHours { get; init; }
    public double? Cost { get; init; }
    public string? Start { get; init; }
    public string? Finish { get; init; }
}

/// <summary>Standard envelope for every paginated query.</summary>
public sealed record Page<T>
{
    public required int Total { get; init; }
    public required int Returned { get; init; }
    public int? NextCursor { get; init; }
    public required IReadOnlyList<T> Items { get; init; }
}

/// <summary>
/// The result of any write. <see cref="Applied"/> is counted by re-reading the model
/// after the commit — never by "the call did not throw".
/// </summary>
public sealed record WriteResult
{
    public required bool DryRun { get; init; }

    /// <summary>Operations that fully succeeded. A half-landed operation counts as rejected.</summary>
    public required int Applied { get; init; }

    /// <summary>Individual field-level checks that passed. Diagnostic detail behind <see cref="Applied"/>.</summary>
    public int FieldsVerified { get; init; }

    public required IReadOnlyList<RejectedWrite> Rejected { get; init; }
    public required ImpactReport Impact { get; init; }
    public string VerifiedBy { get; init; } = "reread";
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record RejectedWrite
{
    public int? Uid { get; init; }
    public string? Field { get; init; }
    public string? Requested { get; init; }
    public string? Actual { get; init; }
    public required string Reason { get; init; }
}

public sealed record ImpactReport
{
    public int TasksMoved { get; init; }
    public string? ProjectFinishBefore { get; init; }
    public string? ProjectFinishAfter { get; init; }
    public double ProjectFinishDeltaDays { get; init; }
    public bool CriticalPathChanged { get; init; }
    public int NewNegativeFloat { get; init; }
}

public sealed record Finding
{
    public required string Rule { get; init; }
    public required string Severity { get; init; }
    public required string Summary { get; init; }
    public double? Measured { get; init; }
    public double? Threshold { get; init; }

    /// <summary>
    /// False when the check could not be run at all (no baseline stored, no scheduling engine).
    /// Explicit rather than inferred from a null <see cref="Passed"/>, so "could not tell" is never
    /// mistaken for "passed" by a serializer that drops nulls.
    /// </summary>
    public bool Evaluated { get; init; } = true;

    public bool? Passed { get; init; }
    public IReadOnlyList<int> Uids { get; init; } = Array.Empty<int>();
    public string? FixHint { get; init; }
}
