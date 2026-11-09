using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Security;

/// <summary>Severity names of a posture finding, most severe first.</summary>
public static class SecurityPostureSeverities
{
    public const string Critical = "critical";
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";
    public const string Info = "info";

    public static readonly IReadOnlyList<string> All = [Critical, High, Medium, Low, Info];

    /// <summary>The severity band a score falls into: 90 and above is critical, 70 high, 40 medium, 20 low.</summary>
    public static string ForScore(int score) => score switch
    {
        >= 90 => Critical,
        >= 70 => High,
        >= 40 => Medium,
        >= 20 => Low,
        _ => Info,
    };
}

/// <summary>The fact area a finding belongs to; the UI groups and links by it.</summary>
public static class SecurityPostureAreas
{
    public const string Exposure = "exposure";
    public const string Credentials = "credentials";
    public const string SecondFactor = "second-factor";
    public const string Versions = "versions";
    public const string Audit = "audit";
    public const string Cards = "cards";
}

/// <summary>
/// One ranked posture entry. <see cref="Score"/> (0 to 100) orders the list and
/// decides <see cref="Severity"/>; <see cref="Rank"/> is the 1-based position.
/// <see cref="ActionSoon"/> marks what needs an operator step within 14 days.
/// </summary>
public sealed record SecurityPostureFinding(
    string Id,
    int Rank,
    int Score,
    string Severity,
    string Area,
    string Title,
    string Detail,
    bool ActionSoon,
    DateTime? DueAt,
    string? Subject);

public sealed record SecurityPostureSummary(
    int Total, int Critical, int High, int Medium, int Low, int Info, int ActionSoon);

/// <summary>A fact the API cannot observe from inside the container, named so it is not read as healthy.</summary>
public sealed record SecurityPostureExternalFact(string Id, string Label, string State, string Detail);

public sealed record SecurityPostureExposure(
    string Profile,
    bool HttpsRequired,
    bool CloudflareEdge,
    int TrustedProxyCount,
    bool LoginAddressHealthy,
    IReadOnlyList<string> AnonymousRoutes,
    IReadOnlyList<SecurityPostureExternalFact> External);

/// <summary>An API token or a service-account token: metadata only, never a value, hash, or prefix.</summary>
public sealed record SecurityPostureToken(
    string Id,
    string Holder,
    string Kind,
    string Role,
    IReadOnlyList<string> Projects,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    DateTime? LastUsedAt,
    string State);

public sealed record SecurityPostureOperatorAgent(
    string Id, string Name, bool Disabled, int LiveSessions, DateTime? NextSessionExpiry);

/// <summary>A credential from the host-side metadata inventory (GitHub App, host tokens, provider sign-ins, runner credentials).</summary>
public sealed record SecurityPostureInventoryItem(
    string Id,
    string Category,
    string Kind,
    string Label,
    string HostId,
    DateTime? ExpiresAt,
    DateTime? LastUseAt,
    string State,
    string Detail);

public sealed record SecurityPostureCredentials(
    IReadOnlyList<SecurityPostureToken> Tokens,
    IReadOnlyList<SecurityPostureOperatorAgent> OperatorAgents,
    IReadOnlyList<SecurityPostureInventoryItem> Inventory,
    string InventorySource,
    string? InventoryProblem);

public sealed record SecurityPostureUser(
    string Id,
    string Username,
    string DisplayName,
    string Role,
    bool Disabled,
    bool Totp,
    int Passkeys,
    int RecoveryCodes,
    bool Required,
    string State);

public sealed record SecurityPostureSecondFactor(
    int ActiveUsers, int Covered, IReadOnlyList<SecurityPostureUser> Users);

public sealed record SecurityPostureCli(string Name, string Version, string? TargetVersion, bool BelowTarget);

public sealed record SecurityPostureHost(
    string RunnerId,
    string Name,
    string HostId,
    string Status,
    string RunnerVersion,
    string? ReleaseVersion,
    string ReleaseState,
    string? OperatingSystem,
    DateTime LastSeenAt,
    IReadOnlyList<SecurityPostureCli> Clis,
    IReadOnlyList<SecurityPostureImage> Containers);

public sealed record SecurityPostureImage(string Name, string? ImageDigest, string State);

public sealed record SecurityPostureServerVersion(
    string Version, string Tag, string Commit, DateTimeOffset? BuiltAt, bool Known);

public sealed record SecurityPostureVersions(
    SecurityPostureServerVersion Server,
    string HostSource,
    string? HostProblem,
    IReadOnlyList<SecurityPostureHost> Hosts);

public sealed record SecurityPostureAuditEvent(
    DateTime At, string Kind, string Stream, string Actor, string Operation, string Outcome, int Count);

public sealed record SecurityPostureAudit(
    string IntegrityState,
    DateTime? IntegrityCheckedAt,
    bool SinkConfigured,
    long PendingForward,
    bool SinkAlert,
    IReadOnlyList<string> OpenAlerts,
    DateTime? LastKillAt,
    DateTime? LastDrillAt,
    bool? LastDrillPassed,
    int ActiveBreakGlassWindows,
    int WindowDays,
    IReadOnlyList<SecurityPostureAuditEvent> Anomalies);

public sealed record SecurityPostureCard(
    string Key, string Title, string Project, string State, DateTime CreatedAt);

/// <summary>
/// The installation security view (GET /api/security/posture): ranked findings
/// first, then the facts they were derived from. Metadata only; no secret
/// value, token hash, prefix, TOTP secret, or recovery code leaves the API.
/// </summary>
public sealed record SecurityPostureResponse(
    DateTime GeneratedAt,
    SecurityPostureSummary Summary,
    IReadOnlyList<SecurityPostureFinding> Findings,
    SecurityPostureExposure Exposure,
    SecurityPostureCredentials Credentials,
    SecurityPostureSecondFactor SecondFactor,
    SecurityPostureVersions Versions,
    SecurityPostureAudit Audit,
    IReadOnlyList<SecurityPostureCard> Cards);

/// <summary>Where a source's rows came from: this API, the standalone Task Server, or nowhere this time.</summary>
public static class SecurityPostureSources
{
    public const string Local = "local";
    public const string TaskServer = "task-server";
    public const string Unavailable = "unavailable";
}

/// <summary>Everything the projection reads. Built by <see cref="SecurityPostureService"/>, pure input for tests.</summary>
public sealed record SecurityPostureFacts
{
    public required DateTime Now { get; init; }
    public required string Profile { get; init; }
    public bool CloudflareEdge { get; init; }
    public int TrustedProxyCount { get; init; }
    public bool LoginAddressHealthy { get; init; } = true;
    public IReadOnlyList<string> AnonymousRoutes { get; init; } = [];
    public IReadOnlyList<StudioApiToken> ApiTokens { get; init; } = [];
    public IReadOnlyList<ServiceAccountFact> ServiceAccounts { get; init; } = [];
    public IReadOnlyList<OperatorAgentFact> OperatorAgents { get; init; } = [];
    public IReadOnlyList<CredentialInventoryItemDto> Inventory { get; init; } = [];
    public string InventorySource { get; init; } = SecurityPostureSources.Local;
    public string? InventoryProblem { get; init; }
    public IReadOnlyList<StudioUser> Users { get; init; } = [];
    public required SecurityPostureServerVersion Server { get; init; }
    public IReadOnlyList<RunnerCapabilitySnapshotDto> Hosts { get; init; } = [];
    public string HostSource { get; init; } = SecurityPostureSources.Local;
    public string? HostProblem { get; init; }
    public AgentStudio.Runner.StableReleaseIdentity? Stable { get; init; }
    public required ControlPlaneAuditIntegrity Integrity { get; init; }
    public required ControlPlaneAuditForwarding Forwarding { get; init; }
    public IReadOnlyList<string> OpenAuditAlerts { get; init; } = [];
    public IReadOnlyList<ControlPlaneAuditEntry> RecentAudit { get; init; } = [];
    public IReadOnlyList<AgentStudio.Secrets.SecretAuditEntry> RecentSecretAudit { get; init; } = [];
    public IReadOnlyList<OperatorKillRecord> Kills { get; init; } = [];
    public IReadOnlyList<OperatorKillDrillResult> Drills { get; init; } = [];
    public int DrillIntervalHours { get; init; }
    public IReadOnlyList<BreakGlassWindow> BreakGlassWindows { get; init; } = [];
    public IReadOnlyList<AgentStudio.Shared.TaskInfo> SecurityCards { get; init; } = [];
}

/// <summary>A service account created in the Access area and the metadata of its tokens.</summary>
public sealed record ServiceAccountFact(
    string Id, string Name, string Role, IReadOnlyList<string> Projects, bool Disabled,
    IReadOnlyList<ServiceAccountToken> Tokens);

public sealed record OperatorAgentFact(
    string Id, string Name, bool Disabled, IReadOnlyList<OperatorAgentSession> Sessions);
