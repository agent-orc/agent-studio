using System.Text.Json.Serialization;

namespace AgentStudio.TaskServer.Contracts;

// AGT-3063 (AGT-W73 OD-S1): typed decision grounds, the runner-hosted shadow
// model job, and the immutable per-occurrence decision receipt. The pure
// freeze, ledger, prompt and receipt rules live in Orchestration.Decisions;
// hosts collect facts and persist the records.

/// <summary>Closed AGT-W73 action union. A model may only choose from these.</summary>
public static class DecisionActionKinds
{
    public const string Observe = "observe";
    public const string Prepare = "prepare";
    public const string Defer = "defer";
    public const string Continue = "continue";
    public const string Adapt = "adapt";
    public const string Checkpoint = "checkpoint";
    public const string Retry = "retry";
    public const string Halt = "halt";
    public const string Ask = "ask";

    public static readonly IReadOnlyList<string> All =
        [Observe, Prepare, Defer, Continue, Adapt, Checkpoint, Retry, Halt, Ask];
}

/// <summary>Where the deciding model call runs and whose provider credential it uses.</summary>
public static class DecisionModelCustody
{
    /// <summary>Default (AGT-W63): a claimable job on a runner host, credentials stay on the host.</summary>
    public const string RunnerHostJob = "runner-host-job";
}

public static class DecisionPriceStatuses
{
    public const string Priced = "priced";
    public const string Partial = "partial";
    public const string UnknownModel = "unknown-model";
    public const string NoPriceForDate = "no-price-for-date";
    public const string NoUsage = "no-usage";
}

public static class DecisionCallStatuses
{
    public const string Completed = "completed";
    public const string Unavailable = "unavailable";
    public const string CapExhausted = "cap-exhausted";
    public const string InvalidResponse = "invalid-response";
    public const string IncompletePack = "incomplete-pack";
}

public static class DecisionJobStatuses
{
    public const string Pending = "pending";
    public const string Claimed = "claimed";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string CapExhausted = "cap-exhausted";

    public static bool IsTerminal(string status)
        => status is Completed or Failed or CapExhausted;
}

public static class DecisionJobClaimStatuses
{
    public const string Claimed = "claimed";
    public const string Empty = "empty";
}

public static class GroundsReliability
{
    public const string Reliable = "reliable";
    public const string Estimated = "estimated";
    public const string Unreliable = "unreliable";
}

public sealed record GroundsTriggerV1(string Kind, string Profile, DateTime ObservedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Cadence = null);

/// <summary>Card scope carries card ids; a periodic host scope leaves them null.</summary>
public sealed record GroundsScopeV1(
    string Kind,
    string? ProjectId,
    string? TaskId,
    string? AttemptId,
    long? Fence,
    long? Epoch,
    string? HostId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WorkspaceId = null);

public sealed record DecisionModelRouteV1(
    string Cli,
    string Model,
    string? ThinkingLevel,
    string Custody,
    string? AlternateCli = null,
    string? AlternateModel = null,
    string? AlternateThinkingLevel = null);

/// <summary>An immutable drill-down reference; opening it is a separate authorized read.</summary>
public sealed record GroundsSourceRefV1(
    string Kind,
    string Ref,
    string? Digest = null,
    string? Range = null,
    string AccessClass = "operator");

public sealed record GroundsFactV1(
    string Id,
    string Block,
    string Name,
    string Value,
    string? Unit,
    GroundsSourceRefV1 Source,
    DateTime ObservedAt,
    string Coverage,
    string Reliability,
    bool Required = false);

public sealed record GroundsAlternativeV1(
    string Action,
    string Target,
    string Reversibility,
    string RequiredApproval,
    bool Available,
    string? RejectionReason = null);

public sealed record GroundsPriorDecisionV1(
    string ReceiptId,
    string OccurrenceId,
    string ChosenAction,
    string? Prediction,
    string? Outcome,
    DateTime DecidedAt);

public sealed record GroundsOmissionV1(string Field, string Reason);

/// <summary>A spent loop budget blocks repeat admission and becomes a question for the engine.</summary>
public sealed record LoopExhaustionSignalV1(
    string LoopId,
    int MeasuredCount,
    int Limit,
    string Window,
    string Attempt,
    IReadOnlyList<string> CandidateActions)
{
    public string Name => $"loop.{LoopId}.exhausted";
    public bool ReentryBlocked => true;
}

/// <summary>Billed token categories. Uncached input never contains cached input.</summary>
public sealed record DecisionTokenUsageV1(long UncachedInput, long CachedInput, long CacheWrite, long Output)
{
    public static DecisionTokenUsageV1 Zero { get; } = new(0, 0, 0, 0);
    public long Total => UncachedInput + CachedInput + CacheWrite + Output;
    public DecisionTokenUsageV1 Add(DecisionTokenUsageV1 other)
        => new(UncachedInput + other.UncachedInput, CachedInput + other.CachedInput,
            CacheWrite + other.CacheWrite, Output + other.Output);
}

/// <summary>Historical catalog result. An unknown price keeps EstimatedUsd null, never zero.</summary>
public sealed record DecisionPriceV1(string Status, decimal? EstimatedUsd, string? Basis = null);

/// <summary>
/// One recorded usage row of a card. <paramref name="InputIncludesCached"/>
/// is true only when <paramref name="InputTokens"/> is a raw provider total
/// that still contains <paramref name="CachedInputTokens"/>.
/// </summary>
public sealed record RunLedgerRowV1(
    string? ProviderCallId,
    string? RunId,
    DateTime At,
    string? Model,
    string? ThinkingLevel,
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteTokens,
    long OutputTokens,
    bool InputIncludesCached,
    string? Participant = null);

public sealed record RunLedgerRoundInputV1(
    string AttemptId,
    long Fence,
    DateTime StartedAt,
    DateTime? EndedAt,
    string? Outcome,
    string? BaseSha,
    string? ResultSha,
    bool AcceptedDelivery,
    bool Exhaustion);

public sealed record RunLedgerRoundV1(
    string AttemptId,
    long Fence,
    DateTime StartedAt,
    DateTime? EndedAt,
    string? Outcome,
    string? BaseSha,
    string? ResultSha,
    bool Progress,
    string ProgressReason,
    DecisionTokenUsageV1 Tokens,
    decimal PricedUsd,
    int UnpricedRows);

/// <summary>AGT-W68 RB-S1 per-card ledger, deduplicated by provider call id before aggregation.</summary>
public sealed record CardRunLedgerV1(
    int RawRows,
    int CountedRows,
    int ExcludedDuplicateRows,
    DecisionTokenUsageV1 ExcludedDuplicateTokens,
    decimal ExcludedDuplicateUsd,
    DecisionTokenUsageV1 Tokens,
    decimal PricedSubtotalUsd,
    int UnpricedRows,
    long UnpricedTokens,
    IReadOnlyList<string> UnpricedModels,
    string PriceStatus,
    string DataQuality,
    IReadOnlyList<RunLedgerRoundV1> Rounds);

/// <summary>
/// AGT-W68 RB-S1 progress definition: a round made progress when its result
/// tree differs from its start tree or its result tree was newly verified.
/// </summary>
public sealed record ProgressDefinitionV1(
    string Version,
    string Definition,
    string? LastVerifiedTreeSha,
    int RoundsSinceAcceptedDelivery,
    int RoundsWithoutProgress,
    int ExhaustionsInDelivery);

/// <summary>Typed facts collected by the host when the occurrence is created.</summary>
public sealed record DecisionGroundsInputV1(
    string OccurrenceId,
    GroundsTriggerV1 Trigger,
    GroundsScopeV1 Scope,
    string? PriorStep,
    string? ProposedNextStep,
    string PolicyVersion,
    DecisionModelRouteV1 ModelRoute,
    IReadOnlyList<GroundsFactV1> Facts,
    IReadOnlyList<GroundsAlternativeV1> Alternatives,
    IReadOnlyList<GroundsPriorDecisionV1> PriorDecisions,
    CardRunLedgerV1 RunLedger,
    ProgressDefinitionV1 Progress,
    IReadOnlyList<GroundsOmissionV1> Omissions)
{
    public IReadOnlyList<LoopExhaustionSignalV1> LoopSignals { get; init; } = [];
}

/// <summary>
/// The frozen grounds plus the deterministic decision the host applies. The
/// deterministic choice stays outside the grounds and the model prompt so the
/// shadow comparison does not lead the model; the digest covers grounds only.
/// <c>AdmittedAt</c> is the receipt plane's clock when it admitted a host
/// occurrence; the host-day bucket of a waiting occurrence is keyed by it.
/// </summary>
public sealed record DecisionShadowInputV1(
    DecisionGroundsInputV1 Grounds,
    string DeterministicAction,
    string DeterministicTarget,
    string DeterministicReason,
    string Mode = "shadow",
    DateTime? AdmittedAt = null);

/// <summary>
/// Engine-created host occurrence. Studio's receipt plane freezes it with the
/// workspace rule pin; <c>Cadence</c> (scheduled or event) is required.
/// </summary>
public sealed record HostDecisionOccurrenceRequestV1(
    string OccurrenceId, string WorkspaceId, string HostId, string TriggerKind,
    string Profile, DateTime ObservedAt, IReadOnlyList<GroundsFactV1> Facts,
    IReadOnlyList<GroundsAlternativeV1> Alternatives,
    IReadOnlyList<GroundsOmissionV1> Omissions,
    string? Cadence = null);

public sealed record GroundsPackV1(
    int Bytes,
    int EstimatedTokens,
    int MaxBytes,
    int MaxTokens,
    bool Truncated,
    bool Incomplete,
    IReadOnlyList<string> TruncationMarkers);

/// <summary>Canonical immutable decision input. <see cref="ContentDigest"/> covers every other field.</summary>
public sealed record DecisionGroundsV1(
    string SchemaVersion,
    string OccurrenceId,
    GroundsTriggerV1 Trigger,
    GroundsScopeV1 Scope,
    string? PriorStep,
    string? ProposedNextStep,
    string PolicyVersion,
    DecisionModelRouteV1 ModelRoute,
    IReadOnlyList<GroundsFactV1> Facts,
    IReadOnlyList<GroundsAlternativeV1> Alternatives,
    IReadOnlyList<GroundsPriorDecisionV1> PriorDecisions,
    CardRunLedgerV1 RunLedger,
    ProgressDefinitionV1 Progress,
    IReadOnlyList<GroundsOmissionV1> Omissions,
    GroundsPackV1 Pack,
    string ContentDigest);

/// <summary>One provider usage frame reported by the runner host for a decision call.</summary>
public sealed record DecisionUsageFrameV1(
    string? ProviderCallId,
    string? Model,
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteTokens,
    long OutputTokens,
    bool InputIncludesCached);

public sealed record OrchestratorDecisionJobRequest(
    string OccurrenceId,
    string ProjectId,
    string TaskId,
    int Attempt,
    string GroundsDigest,
    string Prompt,
    string Cli,
    string Model,
    string? ThinkingLevel,
    int MaxInputTokens,
    int MaxOutputTokens);

public sealed record OrchestratorDecisionJobResult(
    bool Success,
    string? ResponseText,
    string? Provider,
    string? Model,
    string? ThinkingLevel,
    DecisionTokenUsageV1 Tokens,
    DecisionPriceV1 Price,
    IReadOnlyList<string> ProviderCallIds,
    int ExcludedDuplicateFrames,
    DateTime StartedAt,
    DateTime EndedAt,
    string? HostId,
    string? Error,
    bool TransportFailure);

public sealed record OrchestratorDecisionJobDto(
    string JobId,
    string OccurrenceId,
    string ProjectId,
    string TaskId,
    int Attempt,
    string Status,
    string Custody,
    string Cli,
    string Model,
    string? ThinkingLevel,
    DateTime CreatedAt,
    DateTime? ClaimedAt,
    string? RunnerId,
    OrchestratorDecisionJobResult? Result,
    string? Error);

public sealed record OrchestratorDecisionJobClaimRequest(
    string RunnerId,
    string RunnerName,
    string? CapabilityInstanceId = null);

public sealed record OrchestratorDecisionJobWork(
    string JobId,
    string ClaimToken,
    string Prompt,
    string Cli,
    string Model,
    string? ThinkingLevel,
    int TimeoutSeconds,
    DateTime ClaimExpiresAt,
    int MaxInputTokens = 0,
    int MaxOutputTokens = 0);

public sealed record OrchestratorDecisionJobClaimResponse(
    string Status,
    OrchestratorDecisionJobWork? Work = null);

public sealed record OrchestratorDecisionJobCompletion(
    string JobId,
    string ClaimToken,
    string RunnerId,
    bool Success,
    string? ResponseText,
    string? Model,
    IReadOnlyList<DecisionUsageFrameV1> Usage,
    DateTime StartedAt,
    DateTime EndedAt,
    string? Error,
    bool TransportFailure);

public sealed record DecisionChoiceV1(string Action, string Target, string Outcome);

public sealed record DecisionAlternativeConsideredV1(string Action, string RejectionReason);

public sealed record DecisionSignalReadV1(string FactId, string Value, DateTime ObservedAt, string Reliability);

public sealed record DecisionModelCallV1(
    string Status,
    string Custody,
    string? JobId,
    string? HostId,
    string? Provider,
    string? Model,
    string? ThinkingLevel,
    int Attempts,
    bool AlternateUsed,
    DecisionTokenUsageV1 Tokens,
    DecisionPriceV1 Price,
    IReadOnlyList<string> ProviderCallIds,
    int ExcludedDuplicateFrames,
    bool CallTokenCapExceeded,
    string? Error);

/// <summary>
/// Immutable record of one decision occurrence (AGT-W73 visibility contract).
/// The first stored receipt of an occurrence wins; a replay returns it.
/// </summary>
public sealed record OrchestratorDecisionReceiptV1(
    string SchemaVersion,
    string ReceiptId,
    string OccurrenceId,
    string Mode,
    GroundsTriggerV1 Trigger,
    GroundsScopeV1 Scope,
    string? PriorStep,
    string? NextStep,
    string GroundsDigest,
    IReadOnlyList<DecisionSignalReadV1> Signals,
    IReadOnlyList<GroundsAlternativeV1> Offered,
    IReadOnlyList<GroundsAlternativeV1> FloorRejected,
    IReadOnlyList<DecisionChoiceV1> Chosen,
    IReadOnlyList<DecisionAlternativeConsideredV1> AlternativesConsidered,
    double? Confidence,
    string? Reasoning,
    IReadOnlyList<string> CitedFacts,
    string? Prediction,
    string? Uncertainty,
    string DeterministicAction,
    string DeterministicReason,
    bool MatchesDeterministic,
    string PolicyVersion,
    string PromptVersion,
    DecisionModelRouteV1 ModelRoute,
    DecisionModelCallV1 Call,
    string DispatchOutcome,
    string EvidenceRef,
    string OutcomeLink,
    DateTime StartedAt,
    DateTime EndedAt,
    DecisionGroundsV1 Grounds,
    string? ResponseContent,
    string? Assessment = null,
    string? Report = null);

/// <summary>Compact Studio row; <see cref="EvidenceRef"/> opens the full receipt in one click.</summary>
public sealed record OrchestratorDecisionReceiptSummary(
    string ReceiptId,
    string OccurrenceId,
    string? TaskId,
    string Profile,
    string Mode,
    string ChosenAction,
    string DeterministicAction,
    bool MatchesDeterministic,
    string CallStatus,
    string? Model,
    string? ThinkingLevel,
    string Custody,
    long TotalTokens,
    string PriceStatus,
    decimal? EstimatedUsd,
    string EvidenceRef,
    DateTime EndedAt,
    int LateCalls = 0,
    string? ProjectId = null,
    string Actor = "decision-step",
    long InputTokens = 0,
    long OutputTokens = 0,
    int UnknownPriceCalls = 0,
    string? HostId = null,
    string? WorkspaceId = null);

public sealed record OrchestratorDecisionSpendSummary(
    string Scope,
    string? ScopeId,
    int Occurrences,
    long InputTokens,
    long OutputTokens,
    decimal PricedUsd,
    int UnknownPriceCalls);

/// <summary>Human Operator response to an ask receipt; used as grounds for the next decision.</summary>
public sealed record DecisionOperatorIntentV1(
    string ReceiptId,
    string TaskId,
    string Action,
    string Reason,
    int? RequestedEmergencyHours,
    DateTime RecordedAt,
    DecisionChoiceV1? TypedChoice = null)
{
    public string Actor { get; init; } = "Human Operator";
    public int? RequestedEmergencyMinutes { get; init; }
    public string Outcome { get; init; } = "recorded, not applied";
    public DateTime? AppliedAt { get; init; }
}

public sealed record DecisionOperatorIntentRequest(
    string Action,
    string Reason,
    int? RequestedEmergencyHours = null,
    int? RequestedEmergencyMinutes = null);

/// <summary>
/// Usage of a decision call that a runner reported after the receipt of its
/// occurrence was stored. The receipt stays immutable; spend views add this.
/// </summary>
public sealed record DecisionLateCallV1(
    string JobId,
    string? HostId,
    string? Model,
    DecisionTokenUsageV1 Tokens,
    DecisionPriceV1 Price,
    IReadOnlyList<string> ProviderCallIds,
    DateTime EndedAt,
    string? Error);
