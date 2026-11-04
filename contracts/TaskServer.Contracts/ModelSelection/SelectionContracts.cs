using AgentStudio.Cli;
using AgentStudio.Pipeline;
using AgentStudio.Shared;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>Consumer classes that ask the selection service for a route (Dossier model-selection-and-fallback).</summary>
public static class SelectionConsumers
{
    public const string Coding = "coding";
    public const string ReviewAspect = "review-aspect";
    public const string ReviewDeep = "review-deep";
    public const string ReviewGrade = "review-grade";
    public const string ConceptFit = "concept-fit";
    public const string Chat = "chat";
    public const string MergeResolver = "merge-resolver";
    public const string CardCreation = "card-creation";
    public const string OneShotPrefix = "one-shot:";

    public static string OneShot(string kind) => OneShotPrefix + kind;

    /// <summary>Review consumers accept only a fallback of the same capability class (D4).</summary>
    public static bool IsReview(string? consumer)
        => consumer is ReviewAspect or ReviewDeep or ReviewGrade or ConceptFit;
}

/// <summary>Rule ids a selection result names; the first rule that decides wins.</summary>
public static class SelectionRules
{
    public const string Pin = "pin";
    public const string ConsumerDefault = "consumer-default";
    public const string QuotaCap = "quota-cap";
    public const string OperatorSwitch = "operator-switch";
    public const string Capacity = "capacity";
    public const string Unauthorized = "unauthorized";
    public const string ModelUnavailable = "model-unavailable";
    public const string Floor = "floor";
    public const string CostGuard = "cost-guard";
    public const string HostCapability = "host-capability";
}

public enum SelectionOutcome
{
    /// <summary>Launch the requested (primary) route.</summary>
    Primary,
    /// <summary>Launch an equivalent route instead of the primary.</summary>
    Fallback,
    /// <summary>Do not launch now; <see cref="SelectionResult.RetryAt"/> says when to ask again.</summary>
    Wait,
    /// <summary>Hold this launch to reduce parallelism while another slot is busy.</summary>
    Throttle,
    /// <summary>This host cannot run the route; another host may.</summary>
    Refused,
}

/// <summary>
/// How a card or step pin binds. <see cref="Preference"/> is the default (D2):
/// a quota or health rule may switch away from it and the switch is logged.
/// A <see cref="Hard"/> pin runs as pinned or waits.
/// </summary>
public enum SelectionPinMode
{
    Preference,
    Hard,
}

public sealed record SelectionRoute(string CliType, string? Model, string? ThinkingLevel);

public sealed record SelectionPins(
    string? CliType = null,
    string? Model = null,
    string? ThinkingLevel = null,
    SelectionPinMode Mode = SelectionPinMode.Preference);

/// <summary>
/// Capabilities the route must offer. <see cref="SameClassOnly"/> is applied
/// today; the other flags are carried for the selection ledger until the
/// capability rules of MS-6 read them.
/// </summary>
public sealed record SelectionRequirements(
    bool SameClassOnly = false,
    bool Tools = false,
    bool ImageInput = false,
    bool LongContext = false);

/// <summary>The claiming host of a remote decision.</summary>
public sealed record SelectionHost(
    string? HostId = null,
    IReadOnlyList<string>? AcceptedModels = null,
    IReadOnlyDictionary<string, string>? CliVersions = null);

/// <summary>A same-session resume keeps the session's CLI and model.</summary>
public sealed record SelectionContinuation(string? SessionCli = null, string? SessionModel = null);

/// <summary>One consumer's question: which CLI, model and thinking level runs this work now.</summary>
public sealed record SelectionRequest
{
    public string Consumer { get; init; } = SelectionConsumers.Coding;
    public string? Project { get; init; }
    public string? PipelineType { get; init; }
    public string? StepId { get; init; }
    public string? TaskKey { get; init; }
    public string? AttemptId { get; init; }
    public int? Round { get; init; }
    public SelectionPins Pins { get; init; } = new();
    public bool IsExplicitPin { get; init; }
    public string? TaskType { get; init; }
    /// <summary>The card's correctness floor; a fallback below it waits instead (rule 7).</summary>
    public ModelRoutingTier? CorrectnessFloor { get; init; }
    public SelectionRequirements Required { get; init; } = new();
    public SelectionHost? Host { get; init; }
    public SelectionContinuation? Continuation { get; init; }
}

public sealed record SelectionEquivalence(
    string? CapabilityClass,
    string? CatalogueVersion,
    string? ThinkingMapping,
    string? Source);

/// <summary>The deciding quota numbers: the primary window behind a switch and the worst projection.</summary>
public sealed record SelectionQuota(
    string? Window,
    double? UsedPct,
    QuotaProjection? Projection,
    QuotaProjectionWarning? ProjectionWarning);

/// <summary>The route a consumer starts, and why.</summary>
public sealed record SelectionResult
{
    public required SelectionOutcome Outcome { get; init; }
    public required SelectionRoute Route { get; init; }
    /// <summary>What would run without the deciding rule.</summary>
    public required SelectionRoute Primary { get; init; }
    public required string Rule { get; init; }
    public required string Reason { get; init; }
    public SelectionEquivalence? Equivalence { get; init; }
    public SelectionQuota? Quota { get; init; }
    public DateTime? RetryAt { get; init; }
    public DateTime? ReturnToPrimaryAt { get; init; }
    /// <summary>True when a capped primary waits for a nearby reset instead of switching.</summary>
    public bool NearbyResetWait { get; init; }
    /// <summary>Run-scoped fallback receipt, shared with provider-rejection continuations.</summary>
    public ModelFallbackInfo? Fallback { get; init; }
    public string? LedgerId { get; init; }

    public bool IsFallback => Outcome == SelectionOutcome.Fallback;
    public bool ShouldLaunch => Outcome is SelectionOutcome.Primary or SelectionOutcome.Fallback;
}

/// <summary>
/// The routing map the planner may switch through: the operator profile per
/// CLI (<c>cli-model-routing.json</c>) and the Token Economy catalogue.
/// </summary>
public sealed record SelectionRoutingFacts(
    Func<string, CliModelRouteProfile?> Profile,
    IModelEquivalenceCatalog? Equivalence,
    Func<string, string?, IReadOnlyList<string>> ThinkingLevelsFor);

/// <summary>
/// Everything the pure planner reads. The selection service in each host
/// collects these facts; the planner never reads a clock, file or network.
/// </summary>
public sealed record SelectionFacts
{
    public required DateTime NowUtc { get; init; }
    public required IQuotaCapTable Caps { get; init; }
    /// <summary>Cached quota snapshot per CLI; null when none was probed.</summary>
    public Func<string?, QuotaSnapshot?> SnapshotFor { get; init; } = _ => null;
    /// <summary>Null when no routing map is configured: the planner then never switches.</summary>
    public SelectionRoutingFacts? Routing { get; init; }
    /// <summary>Ordered consumer or project routes, tried before catalogue routing.</summary>
    public IReadOnlyList<SelectionRoute>? ConfiguredFallbacks { get; init; }
    public int OccupiedSlots { get; init; }
    public ResolvedCliQuotaWaitPolicy? WaitPolicy { get; init; }
    /// <summary>The operator switch ("conserve provider X until its reset") for the requested CLI.</summary>
    public CliFallbackPreference? OperatorSwitch { get; init; }
    /// <summary>False for a provider the operator conserves; such a provider is never a fallback target.</summary>
    public Func<string, bool>? FallbackProviderAllowed { get; init; }
    /// <summary>Host acceptance of the primary route; null when no host claims (local runs).</summary>
    public Func<string, string?, bool>? HostAccepts { get; init; }
    /// <summary>Host acceptance of a fallback route; defaults to <see cref="HostAccepts"/>.</summary>
    public Func<string, string?, bool>? HostAcceptsFallback { get; init; }
    /// <summary>Policy tiers for the correctness-floor rule.</summary>
    public IReadOnlyList<ModelRoutingTier> Tiers { get; init; } = [];
    public Func<string?, string> NormalizeModelId { get; init; } = id => id?.Trim() ?? "";
    /// <summary>
    /// Lazy correctness floor when the request carries none. It is read only
    /// when a fallback has to clear it, so the claim path reads a card prompt
    /// only for the cards that switch.
    /// </summary>
    public Func<ModelRoutingTier?>? CorrectnessFloor { get; init; }
}

/// <summary>Ledger kinds of a selection event.</summary>
public static class SelectionLedgerKinds
{
    public const string Switched = "switched";
    public const string Waiting = "waiting";
    public const string Refused = "refused";
}

/// <summary>
/// One selection-ledger event: every result that is not a plain primary
/// launch. MS-2 stores these entries; MS-5 writes them through the ledger
/// seam of the selection service.
/// </summary>
public sealed record SelectionLedgerEntry(
    string Id,
    DateTime At,
    string Kind,
    string Consumer,
    string? Host,
    string? Project,
    string? PipelineType,
    string? StepId,
    string? TaskKey,
    string? AttemptId,
    int? Round,
    string ExecutionPath,
    SelectionRoute From,
    SelectionRoute To,
    string Rule,
    string Reason,
    SelectionQuota? Quota,
    SelectionEquivalence? Equivalence,
    DateTime? RetryAt,
    DateTime? ReturnToPrimaryAt)
{
    /// <summary>The ledger event for a result, or null for a plain primary launch.</summary>
    public static SelectionLedgerEntry? For(
        SelectionRequest request, SelectionResult result, string executionPath, string id, DateTime at)
    {
        var kind = result.Outcome switch
        {
            SelectionOutcome.Fallback => SelectionLedgerKinds.Switched,
            SelectionOutcome.Wait or SelectionOutcome.Throttle => SelectionLedgerKinds.Waiting,
            SelectionOutcome.Refused => SelectionLedgerKinds.Refused,
            _ => null,
        };
        if (kind is null) return null;
        return new SelectionLedgerEntry(
            id, at, kind, request.Consumer, request.Host?.HostId, request.Project, request.PipelineType,
            request.StepId, request.TaskKey, request.AttemptId, request.Round, executionPath,
            result.Primary, result.Route, result.Rule, result.Reason, result.Quota, result.Equivalence,
            result.RetryAt, result.ReturnToPrimaryAt);
    }
}
