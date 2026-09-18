namespace AgentStudio.Git;

/// <summary>
/// One attempted stage of the staged integration strategy. The values
/// are deliberately structured so card copy never has to expose Git stderr.
/// </summary>
public sealed record IntegrationConflictStageReport
{
    public string Stage { get; init; } = "";
    public string Outcome { get; init; } = "";
    public int ConflictedFileCount { get; init; }
    public string? StoppedCommitSha { get; init; }
    public int? StoppedCommitNumber { get; init; }
    public int? TotalCommits { get; init; }
}

/// <summary>
/// Durable evidence for a delivery that exhausted direct merge, mechanical
/// merge, and the cardinality-preserving rebase fallback.
/// </summary>
public sealed record IntegrationConflictReport
{
    public const int MaxReportedFiles = 20;

    public string IntegrationBranch { get; init; } = "develop";
    public string IntegrationTipSha { get; init; } = "";
    public string DeliverySha { get; init; } = "";
    public List<IntegrationConflictStageReport> Stages { get; init; } = [];
    public int ConflictedFileCount { get; init; }
    public List<string> ConflictedFiles { get; init; } = [];
    public bool ConflictedFilesTruncated { get; init; }

    /// <summary>
    /// Compact card-safe projection. The persisted report retains the typed
    /// stage fields and exact SHAs; this projection stays at three readable
    /// lines and never carries Git's interactive conflict hints.
    /// </summary>
    public string RenderDetail()
    {
        var stages = string.Join("; ", Stages.Select(RenderStage));
        var files = ConflictedFiles.Count == 0
            ? "none recorded"
            : string.Join(", ", ConflictedFiles);
        var count = ConflictedFilesTruncated
            ? $"{ConflictedFileCount} total, showing {ConflictedFiles.Count}"
            : $"{ConflictedFileCount} total";
        return $"Merge into {IntegrationBranch} conflicted (direct merge, mechanical merge and rebase fallback all failed)\n"
               + $"Stages: {stages}.\n"
               + $"Conflicted files ({count}): {files}. Integration tip {Short(IntegrationTipSha)}; delivery {Short(DeliverySha)}.";
    }

    private static string RenderStage(IntegrationConflictStageReport stage)
    {
        return stage.Stage switch
        {
            "direct-merge" => $"direct merge: conflict in {stage.ConflictedFileCount} {Files(stage.ConflictedFileCount)}",
            "mechanical-merge" => $"mechanical merge: {stage.ConflictedFileCount} {Files(stage.ConflictedFileCount)} left",
            "rebase-fallback" when stage.StoppedCommitNumber is { } number
                                   && stage.TotalCommits is { } total
                                   && !string.IsNullOrWhiteSpace(stage.StoppedCommitSha) =>
                $"rebase fallback: stopped at commit {Short(stage.StoppedCommitSha)} ({number} of {total})",
            "rebase-fallback" when stage.TotalCommits is { } total =>
                $"rebase fallback: {stage.Outcome} after {total} {Commits(total)}",
            _ => $"{stage.Stage}: {stage.Outcome}",
        };
    }

    private static string Files(int count) => count == 1 ? "file" : "files";
    private static string Commits(int count) => count == 1 ? "commit" : "commits";
    private static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;
}
