using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelPinAdmissionPolicyTests
{
    [Fact]
    public void Local_pickup_rejects_Claude_pin_below_registry_minimum()
    {
        var decision = ModelPinAdmissionPolicy.Evaluate(
            CliTypes.Claude,
            "claude-opus-5-5",
            explicitlyPinned: true,
            installedCliVersion: "2.1.270 (Claude Code)");

        Assert.False(decision.IsAllowed);
        Assert.Equal("model-unsupported", decision.Code);
        Assert.Equal(
            "model unsupported by installed CLI 2.1.270 (minimum 2.1.281)",
            decision.Reason);
    }

    [Fact]
    public void Local_pickup_allows_Claude_pin_at_registry_minimum()
    {
        var decision = ModelPinAdmissionPolicy.Evaluate(
            CliTypes.Claude,
            "claude-opus-5-5",
            explicitlyPinned: true,
            installedCliVersion: "2.1.281");

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void Remote_pickup_rejects_Codex_pin_missing_from_advertised_live_catalogue()
    {
        var decision = ModelPinAdmissionPolicy.Evaluate(
            CliTypes.Codex,
            "gpt-6-astra",
            explicitlyPinned: true,
            installedCliVersion: "0.152.0",
            supportedModels: ["gpt-5.6-sol", "gpt-5.5"]);

        Assert.False(decision.IsAllowed);
        Assert.Contains("not offered by live catalogue", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_pickup_allows_Codex_pin_in_advertised_live_catalogue()
    {
        var decision = ModelPinAdmissionPolicy.Evaluate(
            CliTypes.Codex,
            "gpt-6-astra",
            explicitlyPinned: true,
            installedCliVersion: "0.153.0",
            supportedModels: ["gpt-6-astra", "gpt-5.6-sol"]);

        Assert.True(decision.IsAllowed);
    }
}
