namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the Studio P2 "global CLI/quota settings, crash
/// recovery, watch paths" bundle (Group G4). None of these routes are
/// project-scoped: CLI economy mode and quota policy are process-wide
/// settings, the crash-recovery queue is the global boot-time recovery
/// backlog, and watch paths name arbitrary directories the standalone
/// runner should observe.
/// </summary>
public sealed record SetEconomyModeRequest(bool Enabled);

public sealed record SetQuotaCapsRequest(string CapsJson);

public sealed record SetQuotaModelRoutesRequest(string ModelRoutesJson);

public sealed record SetCliQuotaWaitPolicyRequest(string WaitPolicyJson);

/// <summary>
/// The single global row of CLI/quota settings. Each PUT route updates one
/// column and returns the full row so a client always sees the current
/// state of every setting, not just the one it just changed.
/// </summary>
public sealed record CliSettingsDto(
    bool EconomyMode,
    string? QuotaCapsJson,
    string? QuotaModelRoutesJson,
    string? QuotaWaitPolicyJson,
    DateTime UpdatedAt);

/// <summary>
/// Crash-recovery status values. <c>Pending</c> items await an explicit
/// operator commit or dismiss; both are terminal.
/// </summary>
public static class CrashRecoveryPendingStatuses
{
    public const string Pending = "pending";
    public const string Committed = "committed";
    public const string Dismissed = "dismissed";
}

public sealed record CrashRecoveryPendingItemDto(
    string Id,
    string? ProjectId,
    string? TaskId,
    DateTime DetectedAt,
    string DetailJson,
    string Status);

public sealed record CreateWatchPathRequest(string Name, string? ProjectId, string Pattern);

public sealed record WatchPathDto(string Name, string? ProjectId, string Pattern, DateTime CreatedAt);
