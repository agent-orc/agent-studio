using AgentStudio.Cli;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class QuotaFallbackMarkerTests
{
    [Fact]
    public void CardStatus_ExplainsEffectiveAndConfiguredRouteWithQuotaWindow()
    {
        var status = QuotaFallbackMarker.ToStatus(new QuotaFallbackRecord
        {
            CliType = CliTypes.Codex,
            Model = ModelIds.Gpt56Sol,
            ModelFallback = new ModelFallbackInfo(
                "claude-opus-5 high",
                "gpt-5.6-sol high",
                "quota-cap",
                CliTypes.Codex,
                "high",
                "Weekly",
                98,
                "TokenEconomy 0.3.4; routing 2026-07-24"),
        });

        Assert.Equal(
            "ran on gpt-5.6-sol high instead of claude-opus-5 high (Claude weekly 98 %)",
            status?.Reason);
    }
}
