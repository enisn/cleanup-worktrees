namespace CleanupWorktrees.Models;

public enum WorktreeGitStatus
{
    Clean,
    Dirty,
    Missing,
    Error
}

public sealed record WorktreeInfo
{
    public required int Index { get; init; }
    public required string Path { get; init; }
    public required string Branch { get; init; }
    public string HeadSha { get; init; } = "";
    public bool Exists { get; init; }
    public bool IsMain { get; init; }
    public bool IsCurrent { get; init; }
    public bool IsBare { get; init; }
    public bool IsLocked { get; init; }
    public string? LockReason { get; init; }
    public bool IsPrunable { get; init; }
    public string? PrunableReason { get; init; }
    public WorktreeGitStatus Status { get; init; }
    public string? StatusError { get; init; }
    public DateTimeOffset? LastCommitAt { get; init; }
    public DateTimeOffset? LastModifiedAt { get; init; }
    public long SizeBytes { get; init; }
    public bool SizeCalculated { get; init; }
    public bool IsProtected => IsMain || IsCurrent || IsBare;
    public bool IsExcluded { get; init; }
    public bool IsRemovable => !IsProtected && !IsExcluded;
    public bool IsPreSelected { get; init; }
    public string Reason { get; init; } = "";
}

public enum RemovalOutcome
{
    Removed,
    Pruned,
    PartiallyRemoved,
    Failed
}

public sealed record RemovalResult(
    WorktreeInfo Worktree,
    RemovalOutcome Outcome,
    long ReclaimedBytes,
    string? ErrorMessage);

public sealed record CleanupReport(
    IReadOnlyList<RemovalResult> Results,
    bool PruneAttempted,
    bool PruneSucceeded,
    string? PruneError)
{
    public int RemovedCount => Results.Count(r => r.Outcome == RemovalOutcome.Removed);
    public int PrunedCount => Results.Count(r => r.Outcome == RemovalOutcome.Pruned);
    public int PartialCount => Results.Count(r => r.Outcome == RemovalOutcome.PartiallyRemoved);
    public int FailedCount => Results.Count(r => r.Outcome == RemovalOutcome.Failed);
    public long TotalReclaimedBytes => Results.Sum(r => r.ReclaimedBytes);
    public bool HasFailures => FailedCount > 0 || PartialCount > 0 || (PruneAttempted && !PruneSucceeded);
}
