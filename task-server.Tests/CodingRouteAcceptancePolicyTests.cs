using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed class CodingRouteAcceptancePolicyTests
{
    private static readonly DateTime Now = DateTime.UnixEpoch.AddDays(1);

    [Fact]
    public void Effective_route_follows_the_runner_run_spec_order()
    {
        var mechanical = new MechanicalFreshRunRoute("codex", "gpt-6-sol", "high", "semantic-conflict");
        var fallback = new ProviderModelFallback("gpt-6-astra", "gpt-5.6-sol", "refused", "codex", "high");
        var intent = Intent(new ContinuationSelectionMask(Model: true, CliType: true, ThinkingLevel: true));

        Assert.Equal(new("codex", "gpt-6-sol"),
            CodingRouteAcceptancePolicy.EffectiveNamedRoute(mechanical, intent, fallback, "claude", "claude-opus-5"));
        Assert.Equal(new("codex", "gpt-6-luna"),
            CodingRouteAcceptancePolicy.EffectiveNamedRoute(null, intent, fallback, "claude", "claude-opus-5"));
        Assert.Equal(new("codex", "gpt-5.6-sol"),
            CodingRouteAcceptancePolicy.EffectiveNamedRoute(null, null, fallback, "claude", "claude-opus-5"));
        Assert.Equal(new("claude", "claude-opus-5"),
            CodingRouteAcceptancePolicy.EffectiveNamedRoute(null, null, null, null, "Claude-Opus-5"));
    }

    [Fact]
    public void Blank_routes_need_no_model_probe()
    {
        Assert.Null(CodingRouteAcceptancePolicy.EffectiveNamedRoute(null, null, null, "codex", " "));
        var thinkingOnly = Intent(new ContinuationSelectionMask(Model: false, CliType: false, ThinkingLevel: true));
        Assert.Null(CodingRouteAcceptancePolicy.EffectiveNamedRoute(null, thinkingOnly, null, "codex", "gpt-6-sol"));
        Assert.Null(CodingRouteAcceptancePolicy.Refusal(null, new(null, null, null, Now), Now));
    }

    public static TheoryData<string?, int?, int?, int?, string?> Evidence => new()
    {
        // status, fresh-until offset, watermark offset, rejection offset (seconds), refusal prefix
        { "ready", 60, null, null, null },
        { "ready", -1, null, null, "model-acceptance-unverified" },
        { "unavailable", 60, null, null, "model-acceptance-unverified" },
        { null, null, null, null, "model-acceptance-unverified" },
        { "ready", 60, null, -10, "model-capability-held" },
        { "ready", 60, -10, -10, "model-capability-held" },
        { "ready", 60, -20, -10, "model-capability-held" },
        { "ready", 60, -5, -10, null },
        { "unavailable", 60, -5, -10, "model-acceptance-unverified" },
    };

    [Theory]
    [MemberData(nameof(Evidence))]
    public void Refusal_requires_fresh_acceptance_and_a_server_ordered_recovery(
        string? status, int? freshOffset, int? watermarkOffset, int? rejectionOffset, string? expected)
    {
        var refusal = CodingRouteAcceptancePolicy.Refusal(
            new("codex", "gpt-6-sol"),
            new(status, At(freshOffset), At(watermarkOffset), At(rejectionOffset)),
            Now);

        if (expected is null) Assert.Null(refusal);
        else Assert.StartsWith(expected, refusal);
    }

    private static DateTime? At(int? seconds) => seconds is { } value ? Now.AddSeconds(value) : null;

    private static ContinuationIntentProjection Intent(ContinuationSelectionMask selection)
        => new(
            new ContinuationIntentReceipt("command-1", "project-1", "task-1", 1, 2, 1, 1, 1,
                "operator", "follow-up", Now, "policy-1", ExplicitSelection: true),
            "queued", "Continue.", "gpt-6-luna", "codex", "high", "continue", null, null, null, selection);
}
