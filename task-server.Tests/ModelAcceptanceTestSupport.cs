using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;

namespace TaskServer.Tests;

/// <summary>
/// Advertises model acceptance the way the runner's model probe does. Every
/// time passed in is one the store itself issued (its clock or a response's
/// LastSeenAt), as a real host only learns server time from responses.
/// </summary>
internal static class ModelAcceptanceTestSupport
{
    /// <summary>Codex routes the mechanical and provider-fallback policies can name.</summary>
    public static readonly (string Cli, string Model)[] PolicyRoutes =
    [
        ("codex", "gpt-6-sol"), ("codex", "gpt-6.1-sol"), ("codex", "gpt-5.6-sol"),
        ("codex", "gpt-5.6-terra"), ("codex", "gpt-5.6-luna"), ("codex", "gpt-6-luna"), ("codex", "gpt-5.5"),
    ];

    public static Task<RunnerCapabilitySnapshotDto> AcceptModelsAsync(
        TaskServerStore store,
        DateTime serverTime,
        string runnerId,
        string instanceId,
        long generation,
        params (string Cli, string Model)[] routes)
        => AdvertiseAsync(store, runnerId, instanceId, generation, serverTime, routes.Select(route =>
            Capability(route.Cli, route.Model, "ready", serverTime, serverTime)).ToArray());

    /// <summary>
    /// One model verdict. <paramref name="hostTime"/> is the host's own clock
    /// and <paramref name="watermark"/> the server time it had received before
    /// the probe started.
    /// </summary>
    public static Task<RunnerCapabilitySnapshotDto> AdvertiseModelAsync(
        TaskServerStore store,
        string runnerId,
        string instanceId,
        long generation,
        string cli,
        string model,
        string status,
        DateTime hostTime,
        DateTime? watermark)
        => AdvertiseAsync(store, runnerId, instanceId, generation, hostTime,
            [Capability(cli, model, status, hostTime, watermark)]);

    /// <summary>The Codex ChatGPT-account refusal of a named model.</summary>
    public static ExecutionOutcomeDecision ModelRefusalDecision(string runId, string model)
        => ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            runId, ExecutionAttemptKind.Coding,
            ProviderTerminalEvent: $$$"""
                {"type":"turn.failed","error":{"type":"invalid_request_error","message":"The {{{model}}} model is not supported when using Codex with a ChatGPT account","status":400}}
                """,
            ExitCode: 1, DurableOutputState: DurableOutputState.Missing,
            EffectiveCliType: "codex", EffectiveModel: model, EffectiveThinkingLevel: "medium"));

    private static Task<RunnerCapabilitySnapshotDto> AdvertiseAsync(
        TaskServerStore store,
        string runnerId,
        string instanceId,
        long generation,
        DateTime advertisedAt,
        AdvertisedCapabilityDto[] capabilities)
        => store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            runnerId, instanceId, CapabilityProtocol.CurrentSchemaVersion,
            advertisedAt.ToUniversalTime(), 180, generation, capabilities), runnerId, default);

    private static AdvertisedCapabilityDto Capability(
        string cli, string model, string status, DateTime observedAt, DateTime? watermark)
        => new(CapabilityProtocol.ModelAcceptance(cli, model), "model-acceptance", status,
            CredentialGeneration: "login-test",
            CredentialObservedAt: observedAt.ToUniversalTime(),
            ProbeServerWatermark: watermark?.ToUniversalTime());
}
