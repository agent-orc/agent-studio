using System.Text.Json.Serialization;

namespace AgentStudio.Shared;

/// <summary>
/// Snapshot of the commit a job produced when transitioning from progress to
/// review. Cached in <c>job.json</c> so the board card and detail view can
/// render file count + SHA without re-running git per render.
///
/// <para>
/// Commit-attribution metadata (<see cref="Attribution"/> + <see cref="Confidence"/>)
/// is populated by the deterministic post-execution attribution step (ADR
/// "Commit-Attribution-Regel"). Legacy entries without an explicit
/// <see cref="Attribution"/> are treated as <see cref="CommitAttributionKinds.Legacy"/>
/// at render time so the UI distinguishes "we know this came from the rule
/// engine" from "this was stamped before attribution existed".
/// </para>
/// </summary>
public record TaskCommitInfo
{
    public string Sha { get; init; } = "";
    public string ShortSha { get; init; } = "";
    public string Message { get; init; } = "";
    /// <summary>
    /// Stable repository identity for this commit. New attribution writes the
    /// registered repository id when one is available, otherwise its remote
    /// URL. Legacy records are backfilled once from the informational
    /// <c>[repository]</c> message prefix.
    /// </summary>
    public string? Repository { get; init; }
    /// <summary>
    /// Delivery branch that carried this attributed commit. Remote attribution
    /// persists the exact runner/result ref here so acceptance can resolve the
    /// reviewed source from card data without reconstructing it from the task
    /// folder slug. Null on legacy and local commits that pre-date this field.
    /// </summary>
    public string? Branch { get; init; }
    /// <summary>
    /// Fenced run attempt that first produced this commit. Remote attribution
    /// stamps this together with <see cref="RunnerId"/> and
    /// <see cref="ResultSha"/> so a later generation can reuse the commit
    /// without claiming that it produced it. Null on legacy and local entries.
    /// </summary>
    public string? RunAttemptId { get; init; }
    /// <summary>Runner that owned <see cref="RunAttemptId"/>.</summary>
    public string? RunnerId { get; init; }
    /// <summary>
    /// Verified result tip whose delivery range proved this commit reachable.
    /// </summary>
    public string? ResultSha { get; init; }
    /// <summary>
    /// Exact replacement object produced when the platform replayed this commit
    /// mechanically onto a newer integration base. The historical entry remains
    /// readable but no longer participates in integration completeness checks.
    /// </summary>
    [JsonPropertyName("supersededBySha")]
    public string? SupersededBySha { get; init; }
    /// <summary>
    /// Run attempt that replaced this commit's delivery generation. A non-null
    /// value keeps the commit as readable history while removing it from the
    /// current integration expectation. <see cref="TaskCommitSupersession.PendingAttempt"/>
    /// is used between an explicit requeue and publication of the replacement
    /// attempt, then resolved to the new fenced run attempt id.
    /// </summary>
    [JsonPropertyName("supersededByAttempt")]
    public string? SupersededByAttempt { get; init; }
    public int FilesChanged { get; init; }
    public List<string> Files { get; init; } = [];
    public DateTime At { get; init; }
    /// <summary>
    /// How the commit got attributed to this task. One of
    /// <see cref="CommitAttributionKinds"/>. Null on legacy job.json entries
    /// that pre-date the attribution step; the reader treats null as
    /// <see cref="CommitAttributionKinds.Legacy"/>.
    /// </summary>
    public string? Attribution { get; init; }
    /// <summary>
    /// Confidence of an automatic attribution (0..1). Null for legacy
    /// entries. The frontend renders a small badge when this is present so
    /// the operator can see where the system was uncertain.
    /// </summary>
    public double? Confidence { get; init; }
}

/// <summary>Compatibility parser for the historical <c>[repository]</c> subject prefix.</summary>
public static class TaskCommitRepository
{
    public static string? FromMessagePrefix(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || message[0] != '[') return null;
        var close = message.IndexOf(']');
        if (close is <= 1 or > 160) return null;
        var value = message[1..close].Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static TaskCommitInfo NormalizeLegacy(TaskCommitInfo commit)
        => !string.IsNullOrWhiteSpace(commit.Repository)
            ? commit
            : commit with { Repository = FromMessagePrefix(commit.Message) };
}

/// <summary>Durable values used by commit-generation supersession.</summary>
public static class TaskCommitSupersession
{
    /// <summary>
    /// Replacement identity used after an integration-failure requeue but
    /// before the next remote delivery has supplied its run attempt id.
    /// </summary>
    public const string PendingAttempt = "next-attempt";

    public static bool IsSuperseded(TaskCommitInfo commit)
        => !string.IsNullOrWhiteSpace(commit.SupersededBySha)
            || !string.IsNullOrWhiteSpace(commit.SupersededByAttempt);
}

/// <summary>
/// One commit that the deterministic attribution rule subtracted from a
/// task's commit set (see ADR "Commit-Attribution-Regel"). An internal
/// rule-engine value: the engine emits these so the post-step can log how
/// many commits it withheld and why. Not persisted onto <see cref="TaskInfo"/>
/// and not surfaced in the UI.
/// </summary>
public record TaskExcludedCommitInfo
{
    public string Sha { get; init; } = "";
    public string ShortSha { get; init; } = "";
    /// <summary>One of <see cref="CommitExclusionReasons"/>. Free-form on read.</summary>
    public string Reason { get; init; } = CommitExclusionReasons.Other;
    /// <summary>Commit subject (first line). Optional.</summary>
    public string? Subject { get; init; }
    public DateTime At { get; init; }
}

/// <summary>
/// String constants for <see cref="TaskCommitInfo.Attribution"/>. Kept as
/// constants (not an enum) so the wire format stays a literal string and
/// hand-written job.json files remain readable.
/// </summary>
public static class CommitAttributionKinds
{
    /// <summary>The deterministic rule engine attributed this commit.</summary>
    public const string Automatic = "automatic";
    /// <summary>
    /// An operator attached an already-existing integration commit through the
    /// API-owned integration backfill route. This is deliberately narrower
    /// than the retired generic include/exclude override surface.
    /// </summary>
    public const string Manual = "manual";
    /// <summary>
    /// Legacy entry without explicit attribution (job.json pre-dates the
    /// attribution step). Treated as "trust the existing stamp" by readers.
    /// </summary>
    public const string Legacy = "legacy";

    public static readonly string[] All = [Automatic, Manual, Legacy];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Legacy;
        var v = value.Trim();
        foreach (var k in All)
            if (string.Equals(k, v, StringComparison.OrdinalIgnoreCase)) return k;
        return Legacy;
    }
}

/// <summary>
/// String constants for <see cref="TaskExcludedCommitInfo.Reason"/>. The
/// rule engine writes one of these; the UI maps each to a human-friendly
/// hover label.
/// </summary>
public static class CommitExclusionReasons
{
    /// <summary>Crash-recovery commit that names another task in its message.</summary>
    public const string CrashRecoveryOfOtherTask = "crash-recovery-of-other-task";
    /// <summary>Submodule / stable update commits that don't belong to any one task.</summary>
    public const string UpdateStableBump = "update-stable-bump";
    /// <summary>git pull merge commits produced by the update-stable workflow.</summary>
    public const string MergeCommit = "merge-commit";
    /// <summary>Commit landed before the task's first start; outside the window.</summary>
    public const string OutsideTaskWindow = "outside-task-window";
    /// <summary>Unrecognized exclusion reason.</summary>
    public const string Other = "other";

    public static readonly string[] All =
        [CrashRecoveryOfOtherTask, UpdateStableBump, MergeCommit, OutsideTaskWindow, Other];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Other;
        var v = value.Trim();
        foreach (var r in All)
            if (string.Equals(r, v, StringComparison.OrdinalIgnoreCase)) return r;
        return Other;
    }
}
