namespace AgentStudio.TaskServer.Contracts;

/// <summary>A single-use integration recovery instruction, separate from the original task prompt.</summary>
public sealed record MechanicalRoundDelta(
    string BaseSha,
    string DeliveryRef,
    string DeliverySha,
    IReadOnlyList<string> ConflictPaths,
    string Steer,
    string VerificationPlan);

/// <summary>A policy-qualified route for a fresh mechanical recovery attempt.</summary>
public sealed record MechanicalFreshRunRoute(
    string CliType,
    string Model,
    string ThinkingLevel,
    string Reason);

/// <summary>Durable, fenced evidence for one coding generation on a task.</summary>
public sealed record SessionContinuationLedgerEntry(
    string AttemptId,
    string TaskKey,
    string Provider,
    string Repository,
    string WorktreePath,
    string Branch,
    string? ResultRef,
    string? ResultSha,
    string? CleanContextIdentity,
    string? InputSessionId,
    string? CapturedSessionId,
    string ResumeDecision,
    string? RejectionReason,
    bool MechanicalRound,
    int MechanicalResumesUsed,
    long? InputTokens,
    long? OutputTokens,
    long? CachedTokens,
    long? TotalTokens,
    double? DurationSeconds,
    DateTime RecordedAtUtc,
    string? FallbackReason = null);

/// <summary>Pure admission decision. An unknown lineage or session always starts fresh.</summary>
public static class MechanicalSessionResumePolicy
{
    public const long TokenCeiling = 1_211_213;
    public const int DurationCeilingSeconds = 300;

    public static string? RejectionReason(
        SessionContinuationLedgerEntry? previous,
        MechanicalRoundDelta? delta,
        string taskKey,
        string provider,
        string repository,
        string worktreePath,
        string branch,
        string? cleanContextIdentity,
        bool sessionPresent)
    {
        if (delta is null) return "not-mechanical-recovery";
        if (previous is null) return "missing-prior-generation";
        if (previous.MechanicalResumesUsed >= 1) return "continuation-limit-reached";
        if (!string.Equals(previous.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)) return "task-key-mismatch";
        if (!string.Equals(previous.Provider, provider, StringComparison.OrdinalIgnoreCase)) return "provider-change";
        if (!string.Equals(previous.Repository, repository, StringComparison.Ordinal)) return "repository-mismatch";
        if (!string.Equals(previous.WorktreePath, worktreePath, StringComparison.Ordinal)) return "worktree-path-mismatch";
        if (!string.Equals(previous.Branch, branch, StringComparison.Ordinal)) return "branch-mismatch";
        if (string.IsNullOrWhiteSpace(previous.ResultRef) || string.IsNullOrWhiteSpace(previous.ResultSha)
            || !string.Equals(previous.ResultRef, delta.DeliveryRef, StringComparison.Ordinal)
            || !string.Equals(previous.ResultSha, delta.DeliverySha, StringComparison.OrdinalIgnoreCase))
            return "branch-lineage-mismatch";
        if (string.IsNullOrWhiteSpace(previous.CleanContextIdentity)
            || !string.Equals(previous.CleanContextIdentity, cleanContextIdentity, StringComparison.Ordinal))
            return "clean-context-mismatch";
        if (string.IsNullOrWhiteSpace(previous.CapturedSessionId)) return "missing-session";
        if (!sessionPresent) return "stale-session";
        return null;
    }
}
