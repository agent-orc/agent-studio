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
/// Either the occurrence that now waits for an engine receipt, or, on a test
/// host in the Monolith decision mode only, the in-process decision.
/// </summary>
public sealed record AiOperatorSubmission(OrchestrationRunDto? Occurrence, AiOperatorDecision? TestHostDecision)
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
/// decision policy. The engine decides and the store applies the receipt
/// through <see cref="IAiOperatorEffect"/>.
/// </summary>
public sealed class AiOperatorDecisionGateway
{
    /// <summary>Transition profile key; only test hosts may set it to Monolith.</summary>
    public const string Transition = "OperatorDecision";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IConfiguration _configuration;
    private readonly MonolithOrchestrationStore? _store;

    public AiOperatorDecisionGateway(IConfiguration configuration, MonolithOrchestrationStore? store = null)
    {
        _configuration = configuration;
        _store = store;
    }

    /// <summary>
    /// The test-host gateway for directly constructed services. Its default
    /// decision mode is Monolith because the configuration is not engine-only.
    /// </summary>
    public static AiOperatorDecisionGateway ForTestHost(IConfiguration configuration) => new(configuration);

    public bool EngineDecides => MonolithTransitionSwitch.For(_configuration, Transition)
        != OrchestrationExecutionMode.Monolith;

    public AiOperatorSubmission Submit(AiOperatorDecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EngineDecides)
            return new(null, MonolithTransitionAdapter.Operator(request.Payload));
        if (_store is null)
            throw new InvalidOperationException("The orchestration authority is unavailable.");
        var run = _store.CreateOperatorDecision(request.ProjectId, request.TaskId, request.Payload.Decider,
            JsonSerializer.Serialize(request.Payload, Json), request.Payload.SubjectKey,
            request.ExpectedSourceState, request.AuthorityAttemptId ?? request.TaskId,
            request.AuthorityFence, request.AuthorityEpoch, request.Period);
        return new(run, null);
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
