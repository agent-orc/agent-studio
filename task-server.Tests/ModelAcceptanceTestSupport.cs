using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;

namespace TaskServer.Tests;

/// <summary>
/// Coding claims require fresh host acceptance of the effective named model.
/// Tests that are not about model acceptance advertise every routed model.
/// </summary>
internal static class ModelAcceptanceTestSupport
{
    private static readonly (string Provider, string Model)[] RoutedModels =
    [
        ("codex", "gpt-6-sol"), ("codex", "gpt-6-luna"), ("codex", "gpt-5.6-sol"),
        ("codex", "gpt-5.6-terra"), ("codex", "gpt-5.6-luna"), ("codex", "gpt-5.5"),
        ("claude", "claude-opus-5"), ("claude", "claude-sonnet-5"), ("claude", "claude-haiku-4-5"),
        ("claude", "claude-opus-4-8"), ("claude", "claude-sonnet-4-6"),
    ];

    /// <param name="serverTime">A server-issued timestamp, such as a task's
    /// creation time, so the test does not read the wall clock.</param>
    public static Task AcceptRoutedModelsAsync(
        TaskServerStore store,
        DateTime serverTime,
        string runnerId = "runner-a",
        string instanceId = "instance-a",
        long generation = 1)
        => store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            runnerId, instanceId, CapabilityProtocol.CurrentSchemaVersion, serverTime, 900, generation,
            RoutedModels.Select(route => new AdvertisedCapabilityDto(
                CapabilityProtocol.ModelAcceptance(route.Provider, route.Model), "model-acceptance", "ready",
                CredentialGeneration: "login-1", CredentialObservedAt: serverTime,
                ProbeSequence: generation)).ToArray()), runnerId, default);
}
