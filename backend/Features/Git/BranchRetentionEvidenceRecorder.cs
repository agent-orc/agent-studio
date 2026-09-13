namespace AgentStudio.Git;

/// <summary>
/// Records branch retention deletions to evidence reports and task history.
/// Per-project, per-date reports preserve deletion proof for audit trail and recovery.
/// </summary>
public sealed record BranchRetentionDeletionProof(
    string Ref,
    string Sha,
    BranchNamespace Namespace,
    string Reason,
    string? TaskKey,
    DateTimeOffset DeletedAtUtc,
    string ProofOfReachability);

public sealed class BranchRetentionEvidenceRecorder
{
    private readonly ILogger<BranchRetentionEvidenceRecorder> _logger;

    public BranchRetentionEvidenceRecorder(ILogger<BranchRetentionEvidenceRecorder> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Records a branch deletion to the evidence report. Called before actual deletion
    /// to preserve proof that the commit is reachable via ancestry.
    /// </summary>
    public async Task RecordDeletionAsync(
        string projectName,
        BranchRetentionDeletionProof proof,
        string workspaceReportsPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var reportDir = Path.Combine(workspaceReportsPath, "branch-reclaim", projectName);
            Directory.CreateDirectory(reportDir);

            var dateStr = proof.DeletedAtUtc.UtcDateTime.ToString("yyyy-MM-dd");
            var reportPath = Path.Combine(reportDir, $"{dateStr}.jsonl");

            var jsonLine = System.Text.Json.JsonSerializer.Serialize(new
            {
                proof.Ref,
                proof.Sha,
                Namespace = proof.Namespace.ToString(),
                proof.Reason,
                proof.TaskKey,
                proof.DeletedAtUtc,
                proof.ProofOfReachability,
            });

            await File.AppendAllTextAsync(reportPath, jsonLine + Environment.NewLine, cancellationToken);

            _logger.LogInformation(
                "branch-retention-evidence recorded project={Project} ref={Ref} sha={Sha} taskKey={TaskKey}",
                projectName, proof.Ref, proof.Sha, proof.TaskKey ?? "none");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "branch-retention-evidence recording failed project={Project} ref={Ref}",
                projectName, proof.Ref);
        }
    }

    /// <summary>
    /// Creates deletion proof for a results ref. Should be called before deletion.
    /// </summary>
    public static BranchRetentionDeletionProof CreateResultsRefProof(
        BranchRetentionAction action,
        string proofOfReachability)
        => new(
            Ref: action.Branch,
            Sha: action.TipSha,
            Namespace: BranchNamespace.Results,
            Reason: action.Reason,
            TaskKey: null,
            DeletedAtUtc: DateTimeOffset.UtcNow,
            ProofOfReachability: proofOfReachability);

    /// <summary>
    /// Creates deletion proof for a task-related ref (task, runner, delivery, salvage, quarantine).
    /// </summary>
    public static BranchRetentionDeletionProof CreateTaskRefProof(
        BranchRetentionAction action,
        BranchNamespace ns,
        string? taskKey,
        string proofOfReachability)
        => new(
            Ref: action.Branch,
            Sha: action.TipSha,
            Namespace: ns,
            Reason: action.Reason,
            TaskKey: taskKey,
            DeletedAtUtc: DateTimeOffset.UtcNow,
            ProofOfReachability: proofOfReachability);
}
