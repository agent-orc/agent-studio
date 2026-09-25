using AgentStudio.CliHosting;

namespace AgentRunner;

/// <summary>Pure admission decision for the one allowed rebase-only session continuation.</summary>
public static class MechanicalResumePolicy
{
    public const long TokenCeiling = 1_211_213;
    public const int DurationCeilingSeconds = 300;

    public static MechanicalResumeDecision Decide(
        MechanicalResumeCandidateDto? candidate,
        string taskKey,
        string provider,
        string? contextMode,
        string? repositoryId,
        string? repositoryUrl,
        string worktreePath,
        string branch,
        string? preparedBaseSha,
        DateTime nowUtc,
        bool sessionExists)
    {
        if (candidate is null) return new(false, "no-candidate");
        if (!string.Equals(candidate.TaskKey, taskKey, StringComparison.Ordinal)) return new(false, "task-key-mismatch");
        if (candidate.PriorResumeDecision is "resumed" or "fallback-fresh") return new(false, "continuation-already-used");
        if (!string.Equals(candidate.Provider, provider, StringComparison.Ordinal)) return new(false, "provider-changed");
        if (contextMode is not null && !string.Equals(contextMode, "clean", StringComparison.OrdinalIgnoreCase))
            return new(false, "clean-context-changed");
        if (!string.Equals(candidate.CleanContextKey, taskKey, StringComparison.Ordinal)) return new(false, "clean-context-mismatch");
        if (!string.Equals(candidate.RepositoryId, repositoryId, StringComparison.Ordinal)
            || !string.Equals(candidate.RepositoryUrl, repositoryUrl, StringComparison.Ordinal))
            return new(false, "repository-mismatch");
        if (!string.Equals(Path.GetFullPath(candidate.WorktreePath), Path.GetFullPath(worktreePath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return new(false, "worktree-path-mismatch");
        if (!string.Equals(candidate.Branch, branch, StringComparison.Ordinal)
            || !string.Equals(candidate.ResultSha, preparedBaseSha, StringComparison.OrdinalIgnoreCase))
            return new(false, "branch-lineage-mismatch");
        if (candidate.CapturedAtUtc == default
            || candidate.CapturedAtUtc > nowUtc
            || nowUtc - candidate.CapturedAtUtc > TaskCleanContextStore.DefaultRetention)
            return new(false, "stale-session");
        if (string.IsNullOrWhiteSpace(candidate.SessionId)) return new(false, "missing-session");
        if (!Guid.TryParse(candidate.SessionId, out _)) return new(false, "invalid-session-id");
        if (!sessionExists) return new(false, "missing-session");
        return new(true, "lineage-matched");
    }
}

public sealed record MechanicalResumeDecision(bool Resume, string Reason);
