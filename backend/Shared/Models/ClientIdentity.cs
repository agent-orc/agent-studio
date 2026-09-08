namespace AgentStudio.Shared;

/// <summary>
/// Identity record for one client of the Task Access Layer. A "client" is
/// anything that talks to the API: a human-driven UI session, a CLI agent
/// instance, an external tool, or a backend service.
///
/// Stored as JSON files under <c>&lt;TaskRepository&gt;/identities/&lt;id&gt;.json</c>.
/// Loaded on backend boot. Mutations through <c>/api/clients/*</c> are the
/// single writer; no other service writes identity files directly.
///
/// Pairs with <c>docs/app/schemas/client-identity.schema.json</c>.
///
/// This legacy identity is attribution only. The local profile uses it as a
/// lightweight registration boundary. The networked profile authenticates
/// human sessions and Runner service credentials separately.
/// </summary>
public record ClientIdentity
{
    /// <summary>Stable, immutable, kebab-case slug. Assigned by the API on first registration.</summary>
    public string Id { get; init; } = "";

    /// <summary>Free-form human-readable name. Re-registering the same value is idempotent.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Optional single grapheme used as a visual marker on cards and chips.</summary>
    public string? Emoji { get; init; }

    /// <summary>Optional CSS hex colour applied to the chip background.</summary>
    public string? Colour { get; init; }

    /// <summary>What kind of caller this identity represents. Soft-delete flips this to "retired".</summary>
    public ClientIdentityKind Kind { get; init; } = ClientIdentityKind.Human;

    /// <summary>UTC timestamp the identity was first registered. Immutable.</summary>
    public DateTime RegisteredAt { get; init; }

    /// <summary>UTC timestamp of the most recent request bearing this clientId. Updated on every authenticated read or write.</summary>
    public DateTime? LastSeenAt { get; init; }

    /// <summary>Optional monthly token cap. Null disables the cap.</summary>
    public long? TokenBudgetMonthly { get; init; }

    /// <summary>Free-form operator notes.</summary>
    public string? Notes { get; init; }

    /// <summary>
    /// User's preferred CLI for new tasks ("claude", "codex", "gemini").
    /// The orchestrator reads this on every chat turn so a
    /// "create me three tasks" request lands on the user's actual default
    /// instead of the hardcoded "claude" fallback. Null until first set.
    /// </summary>
    public string? DefaultCliType { get; init; }

    /// <summary>
    /// User's preferred model id for new tasks (e.g. "claude-haiku-4-5").
    /// Surfaced into the per-turn orchestrator prompt. Null until first set.
    /// </summary>
    public string? DefaultModel { get; init; }

    /// <summary>
    /// User's preferred thinking / reasoning level for new tasks. Null means
    /// use the selected model's default level.
    /// </summary>
    public string? DefaultThinkingLevel { get; init; }

    /// <summary>Latest daemon startup proof: <c>ready</c>, <c>ready-no-workflow-scope</c>, or <c>read-only</c>; null for legacy clients.</summary>
    public string? RunnerGitStatus { get; init; }
    public string? RunnerGitDetail { get; init; }
    public DateTime? RunnerGitCheckedAt { get; init; }

    /// <summary>
    /// Last delivery-path proof for each remotely assigned project. The
    /// registration fingerprint makes a cached result unusable as soon as the
    /// project's repository registration changes.
    /// </summary>
    public IReadOnlyList<RunnerProjectPreflight> RunnerProjectPreflights { get; init; } = [];

    /// <summary>Persisted operator lifecycle. A drain blocks claims; retire completes after active slots reach zero.</summary>
    public DateTime? DrainRequestedAt { get; init; }
    public DateTime? RetireRequestedAt { get; init; }

    /// <summary>Latest daemon activity projection, reported on every claim poll.</summary>
    public string? RunnerDaemonState { get; init; }
    public DateTime? RunnerLastClaimAt { get; init; }
    public int? RunnerActiveSlots { get; init; }

    /// <summary>
    /// Free slots below the host ceiling. Derived from the ceiling minus the
    /// server's own lease count, never from the daemon's breathing observation,
    /// so a slot ledger reads as a stable capacity rather than "active + 1".
    /// </summary>
    public int? RunnerAvailableSlots { get; init; }

    /// <summary>
    /// Central host capacity targets (AGT-2302 / AGT-2376): the hard ceiling on
    /// concurrent runs, the CPU load the host aims to stay under, and how fast
    /// concurrency may grow. These are the one source of truth for capacity;
    /// per-project <c>maxParallelism</c> is deprecated. Seeded on first contact
    /// from the daemon's <c>RUNNER_MAX_PARALLELISM</c> (and, transitionally, the
    /// project values being migrated), then owned by the operator.
    /// </summary>
    public int? RunnerDesiredMaxParallelism { get; init; }
    public int? RunnerTargetLoadPercent { get; init; }
    public string? RunnerRampStrategy { get; init; }

    /// <summary>When an operator last changed the central targets.</summary>
    public DateTime? RunnerCapacityUpdatedAt { get; init; }

    /// <summary>Ceiling the live daemon reports as adopted. Telemetry, not policy.</summary>
    public int? RunnerEffectiveMaxParallelism { get; init; }
    public DateTime? RunnerEffectiveMaxParallelismAppliedAt { get; init; }
}

/// <summary>
/// How fast a host may grow its concurrency once work is already running.
/// Paired with <see cref="HostCapacityPolicy.RampInterval"/>.
/// </summary>
public static class RunnerRampStrategies
{
    public const string Conservative = "conservative";
    public const string Balanced = "balanced";
    public const string Aggressive = "aggressive";

    public static readonly IReadOnlyList<string> All = [Conservative, Balanced, Aggressive];

    public static bool IsValid(string? value)
        => value?.Trim().ToLowerInvariant() is Conservative or Balanced or Aggressive;

    /// <summary>Normalise to a known strategy; anything unknown becomes balanced.</summary>
    public static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            Conservative => Conservative,
            Aggressive => Aggressive,
            _ => Balanced,
        };
}

/// <summary>
/// Body for <c>PUT /api/clients/{id}/runner-capacity</c>. Every field is
/// optional; omitting one leaves that target untouched.
/// </summary>
public record SetRunnerCapacityRequest
{
    public int? MaxParallelism { get; init; }
    public int? TargetLoadPercent { get; init; }
    public string? RampStrategy { get; init; }
}

public enum ClientIdentityKind
{
    Human,
    AgentInstance,
    ExternalTool,
    Service,
    Retired
}

public static class ClientIdentityKinds
{
    public const string Human = "human";
    public const string AgentInstance = "agent-instance";
    public const string ExternalTool = "external-tool";
    public const string Service = "service";
    public const string Retired = "retired";

    public static ClientIdentityKind Parse(string? value) => value?.ToLowerInvariant() switch
    {
        AgentInstance => ClientIdentityKind.AgentInstance,
        ExternalTool => ClientIdentityKind.ExternalTool,
        Service => ClientIdentityKind.Service,
        Retired => ClientIdentityKind.Retired,
        _ => ClientIdentityKind.Human
    };
}

/// <summary>
/// Bootstrap convention: a default identity exists on every backend so
/// historical jobs (no <c>ownerClientId</c> on disk) get a non-null
/// attribution on first migration. Configured via
/// <c>Environment:DefaultIdentityName</c> and <c>Environment:DefaultIdentityEmoji</c>
/// in <c>appsettings.Local.json</c>.
/// </summary>
public static class DefaultClientIdentity
{
    public const string Id = "local-default";
}

public record RegisterClientRequest
{
    public string DisplayName { get; init; } = "";
    public string? Emoji { get; init; }
    public string? Colour { get; init; }
    public string? Kind { get; init; }
    public long? TokenBudgetMonthly { get; init; }
    public string? Notes { get; init; }
}

public record ClientSummary
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? Emoji { get; init; }
    public string? Colour { get; init; }
    public string Kind { get; init; } = ClientIdentityKinds.Human;
    public DateTime RegisteredAt { get; init; }
    public DateTime? LastSeenAt { get; init; }
    public long? TokenBudgetMonthly { get; init; }
    public string? Notes { get; init; }
    public string? DefaultCliType { get; init; }
    public string? DefaultModel { get; init; }
    public string? DefaultThinkingLevel { get; init; }
    public string? RunnerGitStatus { get; init; }
    public string? RunnerGitDetail { get; init; }
    public DateTime? RunnerGitCheckedAt { get; init; }
    public IReadOnlyList<RunnerProjectPreflight> RunnerProjectPreflights { get; init; } = [];
    public DateTime? DrainRequestedAt { get; init; }
    public DateTime? RetireRequestedAt { get; init; }
    public string? RunnerDaemonState { get; init; }
    public DateTime? RunnerLastClaimAt { get; init; }
    public int? RunnerActiveSlots { get; init; }
    public int? RunnerAvailableSlots { get; init; }
    public int? RunnerDesiredMaxParallelism { get; init; }
    public int? RunnerTargetLoadPercent { get; init; }
    public string? RunnerRampStrategy { get; init; }
    public DateTime? RunnerCapacityUpdatedAt { get; init; }
    public int? RunnerEffectiveMaxParallelism { get; init; }
    public DateTime? RunnerEffectiveMaxParallelismAppliedAt { get; init; }
    /// <summary>Present only for a synthetic registry row representing an unreadable identity file.</summary>
    public string? IdentityFileError { get; init; }
    public string? IdentityFileName { get; init; }
    public DateTime? IdentityFileModifiedAt { get; init; }
    public long? IdentityFileSizeBytes { get; init; }
    public string? IdentityRestoreHint { get; init; }

    public static ClientSummary From(ClientIdentity i) => new()
    {
        Id = i.Id,
        DisplayName = i.DisplayName,
        Emoji = i.Emoji,
        Colour = i.Colour,
        Kind = i.Kind switch
        {
            ClientIdentityKind.AgentInstance => ClientIdentityKinds.AgentInstance,
            ClientIdentityKind.ExternalTool => ClientIdentityKinds.ExternalTool,
            ClientIdentityKind.Service => ClientIdentityKinds.Service,
            ClientIdentityKind.Retired => ClientIdentityKinds.Retired,
            _ => ClientIdentityKinds.Human
        },
        RegisteredAt = i.RegisteredAt,
        LastSeenAt = i.LastSeenAt,
        TokenBudgetMonthly = i.TokenBudgetMonthly,
        Notes = i.Notes,
        DefaultCliType = i.DefaultCliType,
        DefaultModel = i.DefaultModel,
        DefaultThinkingLevel = i.DefaultThinkingLevel,
        RunnerGitStatus = i.RunnerGitStatus,
        RunnerGitDetail = i.RunnerGitDetail,
        RunnerGitCheckedAt = i.RunnerGitCheckedAt,
        RunnerProjectPreflights = i.RunnerProjectPreflights,
        DrainRequestedAt = i.DrainRequestedAt,
        RetireRequestedAt = i.RetireRequestedAt,
        RunnerDaemonState = i.RunnerDaemonState,
        RunnerLastClaimAt = i.RunnerLastClaimAt,
        RunnerActiveSlots = i.RunnerActiveSlots,
        RunnerAvailableSlots = i.RunnerAvailableSlots,
        RunnerDesiredMaxParallelism = i.RunnerDesiredMaxParallelism,
        RunnerTargetLoadPercent = i.RunnerTargetLoadPercent,
        RunnerRampStrategy = i.RunnerRampStrategy,
        RunnerCapacityUpdatedAt = i.RunnerCapacityUpdatedAt,
        RunnerEffectiveMaxParallelism = i.RunnerEffectiveMaxParallelism,
        RunnerEffectiveMaxParallelismAppliedAt = i.RunnerEffectiveMaxParallelismAppliedAt
    };
}

/// <summary>Persisted per-host proof that one registered project can be delivered.</summary>
public sealed record RunnerProjectPreflight
{
    public string ProjectId { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string RegistrationFingerprint { get; init; } = "";
    public string RepositoryUrl { get; init; } = "";
    public string FetchUrl { get; init; } = "";
    public string PushUrl { get; init; } = "";
    public string TargetBranch { get; init; } = "";
    public string Status { get; init; } = "failed";
    public string Detail { get; init; } = "";
    public DateTime CheckedAt { get; init; }
}

public record RunnerGitCapabilityRequest
{
    public string Status { get; init; } = "";
    public string? Detail { get; init; }
    public DateTime CheckedAt { get; init; }
}
/// Body for <c>PUT /api/clients/{id}/defaults</c>. Each field is independent;
/// omit a field to leave it untouched. Set a field to an empty string to
/// clear that side.
/// </summary>
public record SetClientDefaultsRequest
{
    public string? DefaultCliType { get; init; }
    public string? DefaultModel { get; init; }
    public string? DefaultThinkingLevel { get; init; }
}

/// <summary>
/// Read-side shape of <c>GET /api/clients/{id}/defaults</c>. Always returns
/// the (possibly null) current values for the identity.
/// </summary>
public record ClientDefaultsResponse
{
    public string Id { get; init; } = "";
    public string? DefaultCliType { get; init; }
    public string? DefaultModel { get; init; }
    public string? DefaultThinkingLevel { get; init; }
}

public record ClientDetail
{
    public ClientSummary Identity { get; init; } = new();
    /// <summary>Number of jobs (across all states) currently attributed to this client.</summary>
    public int OwnedJobCount { get; init; }
    /// <summary>Up to ten most recent job ids attributed to this client (newest first).</summary>
    public List<string> RecentJobIds { get; init; } = [];
}

/// <summary>Body for <c>POST /api/clients/retired/purge</c>.</summary>
public record PurgeRetiredClientsRequest
{
    /// <summary>Case-insensitive id/display-name prefix filter (e.g. "e2e-"). Null or empty matches every retired client.</summary>
    public string? Prefix { get; init; }

    /// <summary>True (the default) only previews the candidates; false actually deletes the eligible ones.</summary>
    public bool DryRun { get; init; } = true;
}

/// <summary>One retired identity considered by a purge sweep, with its eligibility.</summary>
public record PurgeRetiredClientCandidate
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool Eligible { get; init; }
    public string? RefusalReason { get; init; }
    /// <summary>True only on an applied (non-dry-run) purge that deleted this candidate.</summary>
    public bool Deleted { get; init; }
}

/// <summary>Result of <c>POST /api/clients/retired/purge</c>, dry-run or applied.</summary>
public record PurgeRetiredClientsResponse
{
    public bool DryRun { get; init; }
    public IReadOnlyList<PurgeRetiredClientCandidate> Candidates { get; init; } = [];
    public int DeletedCount { get; init; }
}
