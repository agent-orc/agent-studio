using AgentStudio.Shared;
using AgentStudio.Tokens;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: the fold compares only the explicit fixture interval and never reads the wall clock.

public sealed class CliWindowAttributionTests
{
    [Fact]
    public void Sum_UsesStoredCliAndExactQuotaWindow()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var entries = new[]
        {
            Entry(now.AddHours(-2), "codex", "Claude-looking model", 10),
            Entry(now.AddHours(-1), "claude", "gpt-looking model", 20),
            Entry(now.AddHours(-6), "codex", "gpt", 30),
            Entry(now.AddHours(-1), null, "gpt", 40),
        };
        var result = CliWindowAttributionFold.Sum(entries, "codex", now.AddHours(-5), now);
        Assert.Equal(10, result.TotalTokens);
        Assert.Equal(1, result.Calls);
    }

    private static OrchestratorLogEntry Entry(DateTime ts, string? cli, string model, int input) => new()
    {
        Ts = ts,
        TokenUsage = new OrchestratorTokenUsage { CliType = cli, Model = model, InputTokens = input },
    };
}
