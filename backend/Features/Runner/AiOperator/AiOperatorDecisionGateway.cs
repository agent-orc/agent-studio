using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>Frozen facts of one card and decider that the authority asks the AI Operator about.</summary>
/// <param name="Period">Cadence of a periodic decider; null for an event-triggered one.</param>
public sealed record AiOperatorDecisionRequest(
    string ProjectId,
    string TaskId,
    string ExpectedSourceState,
    AiOperatorDecisionPayload Payload,
    TimeSpan? Period,
    string? AuthorityAttemptId = null,
    long AuthorityFence = 0,
    long AuthorityEpoch = 0);

/// <summary>
/// Either the occurrence that now waits for an engine receipt (switch on
/// Engine), or the decision the monolith took in process through the shared
/// decision library (switch on Monolith, the default).
/// </summary>
public sealed record AiOperatorSubmission(OrchestrationRunDto? Occurrence, AiOperatorDecision? MonolithDecision)
{
    public bool WaitsForEngine => Occurrence is not null;
}

/// <summary>Applies one engine receipt for the decider that owns the effect.</summary>
public interface IAiOperatorEffect
{
    string Decider { get; }

    /// <summary>
    /// Applies an Act or Ask receipt. Re-validates the card against
    /// <see cref="AiOperatorApplication.ExpectedSourceState"/> and treats a card
    /// that already left the lane as settled. Must be idempotent per
    /// <see cref="AiOperatorApplication.WriteKey"/>.
    /// </summary>
    Task ApplyAsync(AiOperatorApplication application, CancellationToken ct);
}

/// <summary>
/// An effect that also records the decider's A2 proposals itself (AGT-3207).
/// Without it, an engine Propose receipt is only written to the card's feed.
/// </summary>
public interface IAiOperatorProposalEffect : IAiOperatorEffect;

public sealed record AiOperatorApplication(
    string RunId,
    string ProjectId,
    string TaskId,
    string ExpectedSourceState,
    string WriteKey,
    AiOperatorDecisionPayload Payload,
    AiOperatorDecision Decision,
    string AuthorityAttemptId,
    long AuthorityFence,
    long AuthorityEpoch);

/// <summary>
/// AGT-3060 (I18): the monolith's only seam to the AI Operator. Background
/// services collect typed facts and submit them here; they never call a
/// decision policy. The per-transition switch <see cref="Transition"/> is the
/// single selector of who decides (operator decision 15.10.2026): on Monolith
/// (the default) the monolith decides in process through the shared decision
/// library and the caller applies the decision at once; on Engine the engine
/// decides and the store applies the receipt through
/// <see cref="IAiOperatorEffect"/>. Both run the same
/// <c>AiOperatorDecisionPolicy</c>, so switching back is the rollback.
/// </summary>
public sealed class AiOperatorDecisionGateway
{
    /// <summary>Transition profile key: <c>Orchestration:Transitions:OperatorDecision</c>.</summary>
    public const string Transition = "OperatorDecision";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IConfiguration _configuration;
    private readonly Func<MonolithOrchestrationStore?> _store;

    public AiOperatorDecisionGateway(IConfiguration configuration, MonolithOrchestrationStore? store = null)
        : this(configuration, () => store) { }

    private AiOperatorDecisionGateway(IConfiguration configuration, Func<MonolithOrchestrationStore?> store)
    {
        _configuration = configuration;
        _store = store;
    }

    /// <summary>
    /// The host gateway. The occurrence store is resolved on the first engine
    /// submission: the store's applier reaches services that decide through
    /// this gateway (the acceptance rail via the merge runner), so resolving
    /// it eagerly would be a dependency cycle.
    /// </summary>
    public static AiOperatorDecisionGateway Deferred(IConfiguration configuration,
        Func<MonolithOrchestrationStore> store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var resolved = new Lazy<MonolithOrchestrationStore>(store);
        return new AiOperatorDecisionGateway(configuration, () => resolved.Value);
    }

    /// <summary>
    /// The gateway for directly constructed services without an occurrence
    /// store. It decides in process; selecting Engine without a store fails.
    /// </summary>
    public static AiOperatorDecisionGateway ForTestHost(IConfiguration configuration) => new(configuration);

    public bool EngineDecides => MonolithTransitionSwitch.For(_configuration, Transition)
        != OrchestrationExecutionMode.Monolith;

    public AiOperatorSubmission Submit(AiOperatorDecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EngineDecides)
            return new(null, MonolithTransitionAdapter.Operator(request.Payload));
        var store = _store()
            ?? throw new InvalidOperationException("The orchestration authority is unavailable.");
        var run = store.CreateOperatorDecision(request.ProjectId, request.TaskId, request.Payload.Decider,
            JsonSerializer.Serialize(request.Payload, Json), request.Payload.SubjectKey,
            request.ExpectedSourceState, request.AuthorityAttemptId ?? request.TaskId,
            request.AuthorityFence, request.AuthorityEpoch, request.Period);
        return new(run, null);
    }

    /// <summary>
    /// A subject key that names the exact facts: unchanged facts map to the
    /// same occurrence, so an engine Hold is not asked again until a fact
    /// changes, and the effect can tell that the card moved on.
    /// </summary>
    public static string FactsSubjectKey<TFacts>(string prefix, string taskId, TFacts facts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var hash = System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(facts, Json));
        return $"{prefix}:{taskId}:{Convert.ToHexStringLower(hash.AsSpan(0, 8))}";
    }

    public static AiOperatorDecisionPayload ReadPayload(string payloadJson)
        => JsonSerializer.Deserialize<AiOperatorDecisionPayload>(payloadJson, Json)
            ?? throw new InvalidDataException("AI Operator occurrence payload is missing.");

    public static AiOperatorDecision ReadDecision(string outputJson)
    {
        using var output = JsonDocument.Parse(outputJson);
        if (!output.RootElement.TryGetProperty("operatorDecision", out var element))
            throw new InvalidDataException("The engine AI Operator decision is missing.");
        return element.Deserialize<AiOperatorDecision>(Json)
            ?? throw new InvalidDataException("The engine AI Operator decision is invalid.");
    }
}
