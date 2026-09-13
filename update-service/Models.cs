namespace AgentTaskboard.UpdateService;

/// <summary>
/// What the update service tells the frontend on every poll. Designed to be
/// stable across versions of the main backend so the FE banner keeps
/// working even when /api/* is unreachable mid-update.
///
/// Phase vocabulary (additive across versions; FE must tolerate unknown
/// strings): idle | preparing | pausing-runners | pulling | building |
/// restarting | verifying-after-restart | resuming | rolling-back | done |
/// failed.
/// </summary>
public sealed record UpdateStatus(
    string Phase,
    string? PhaseLabel,              // human-readable gerund for the FE; ADR-0031.
    string? Message,                 // human-readable detail for the current phase
    string? CurrentRunId,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string HeadLocal,                // short SHA, current
    string? HeadOrigin,              // short SHA from last fetch, null until first fetch
    int BehindBy,
    IReadOnlyList<CommitInfo> PendingCommits,
    DateTime? LastFetchAt,
    DateTime? LastUpdateAt,
    DateTime? LastSuccessAt,
    DateTime? LastRunFinishedAt,     // ADR-0031: FE renders the green completion toast for 60 s after this.
    string? LastRunHeadBefore,       // SHA before the most recent run (for hard-reload toast).
    string? LastRunHeadAfter,        // SHA after the most recent run.
    bool IsRunning,                  // true for every in-flight phase incl. verifying / rolling-back.
    bool BackendReachable,
    string ServiceVersion,
    string ProductVersion,
    string Mode,                     // "manual" | "scheduled"
    IReadOnlyList<VerificationFailure>? VerificationFailures, // populated when phase=failed after verifying.
    bool AutoRollbackEnabled,        // mirrors UpdateServiceOptions.AutoRollback so the FE can show the right copy.
    RuntimeVersion? RunningVersion = null,
    BranchVersion? MainVersion = null,
    BranchVersion? DevelopVersion = null,
    ReleaseComparison? ReleaseComparison = null
);

public sealed record RuntimeVersion(
    string Version,
    string Commit,
    DateTime? DeployedAt,
    string? Tag = null,
    bool Dirty = false,
    DateTimeOffset? BuiltAt = null,
    string? Integrity = null,
    ReleaseArtifact? CodingAgentRunner = null,
    ReleaseArtifact? CodingAgentChat = null,
    bool Legacy = false);
public sealed record BranchVersion(string Branch, string Commit, DateTime? CommitAt, int AheadBy, int BehindBy);

public sealed record CommitInfo(string Sha, string Subject, string Author, DateTime AuthorDate);

/// <summary>
/// One historical record per completed update attempt, append-only on disk.
/// Backwards-compat note (ADR-0031): older readers may only know about
/// (RunId, StartedAt, FinishedAt, Status, HeadBefore, HeadAfter,
/// DurationSeconds, Error, Trigger). New optional fields go at the end.
/// </summary>
public sealed record UpdateHistoryEntry(
    string RunId,
    DateTime StartedAt,
    DateTime? FinishedAt,
    string Status,                   // ok | failed | aborted
    string HeadBefore,
    string HeadAfter,
    int DurationSeconds,
    string? Error,
    string Trigger,                  // "manual" | "scheduled" | "api"
    IReadOnlyList<VerificationFailure>? VerificationFailures = null,
    string? RollbackStatus = null,   // "ok" | "failed" | null (no rollback ran)
    string? RunFolder = null,
    string? IntendedTag = null,
    string? ObservedTag = null,
    string? ReleaseDirection = null,
    string? ManifestIntegrity = null,
    ReleaseManifest? IntendedRelease = null,
    ReleaseManifest? ObservedRelease = null,
    // ADR-0031 incident follow-up: the cold-compile health wait and the
    // frontend restart step both record how long they actually took, so
    // operators can tell a slow-but-fine run from a stuck one after the fact.
    int? BackendStartupSeconds = null,
    int? FrontendStartupSeconds = null
);

public sealed record TriggerRequest(string? Reason, bool Force);

public sealed record TriggerResponse(string RunId, string Phase, string Message);

public sealed record RollbackRequest(string RunId);

public sealed record RollbackResponse(string RunId, string Phase, string Message);

// ─── Run-folder artefact shapes (ADR-0031) ────────────────────────────────

/// <summary>
/// Snapshot of state captured before phase 2 (pre) and again at phase 8
/// (post). Schema: docs/app/schemas/update-run-snapshot.schema.json.
/// </summary>
public sealed record UpdateRunSnapshot(
    string Kind,                       // "pre" | "post"
    string RunId,
    DateTime CapturedAt,
    string Head,                       // short SHA
    Dictionary<string, string> ProjectModes,
    bool HealthzOk,
    string? HealthzBody,
    int? RunnerStatusHttp,
    int? JobsRecentCount,
    int? ClientsCount,
    bool CliQuotaReachable
);

/// <summary>One row in <run-folder>/verification.jsonl. Schema: update-run-verification.schema.json.</summary>
public sealed record VerificationCheck(
    string RunId,
    string Step,                       // healthz-stable | runner-status | jobs-grouped | clients | cli-quota | db-touch
    bool Ok,
    string? Observed,
    string? Expected,
    DateTime At,
    int DurationMs
);

/// <summary>Compact failure summary surfaced on UpdateStatus and UpdateHistoryEntry.</summary>
public sealed record VerificationFailure(string Step, string? Observed, string? Expected);

public sealed record RollbackResult(
    string RunId,
    string Status,                     // "ok" | "failed"
    string HeadBefore,
    string HeadAfter,
    DateTime StartedAt,
    DateTime FinishedAt,
    string? Error,
    // ADR-0031 reissue-2026-05-11: the rollback path re-runs phases 5+6+7
    // (stop+reset+start + 6-check matrix + resume). Verification failures
    // are surfaced here so the run-folder artefacts and history rows make
    // the strict-bar visible without re-parsing rollback-verification.jsonl.
    IReadOnlyList<VerificationFailure>? VerificationFailures = null
);
