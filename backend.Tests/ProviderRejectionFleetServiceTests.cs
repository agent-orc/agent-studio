using AgentStudio.Management;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProviderRejectionFleetServiceTests
{
    [Fact]
    public void Aggregate_counts_refusals_per_model_per_utc_day()
    {
        var events = new[]
        {
            Refusal(new DateTime(2026, 9, 18, 10, 4, 0, DateTimeKind.Utc), "gpt-6-astra"),
            Refusal(new DateTime(2026, 9, 18, 11, 4, 0, DateTimeKind.Utc), "gpt-6-astra"),
            Refusal(new DateTime(2026, 9, 18, 12, 4, 0, DateTimeKind.Utc), "claude-opus-5"),
            Refusal(new DateTime(2026, 9, 17, 23, 4, 0, DateTimeKind.Utc), "gpt-6-astra"),
            new TimelineEvent { Ts = new DateTime(2026, 9, 18, 13, 0, 0, DateTimeKind.Utc), Kind = TimelineEventKinds.AgentRunFinished },
        };

        var counts = ProviderRejectionFleetService.Aggregate(
            events,
            new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, counts.Count);
        Assert.Equal(2, counts.Single(item => item.Model == "gpt-6-astra").Count);
        Assert.Equal("unsupported_parameter access_programs.cyber", counts[0].Refusals.Single());
    }

    private static TimelineEvent Refusal(DateTime at, string model) => new()
    {
        Ts = at,
        Kind = TimelineEventKinds.AgentRunFinished,
        Details = new Dictionary<string, string>
        {
            ["typedOutcome"] = ExecutionOutcomeKind.ProviderRejectedRequest.ToString(),
            ["providerRejectionModel"] = model,
            ["providerRejectionCode"] = "unsupported_parameter",
            ["providerRejectionParam"] = "access_programs.cyber",
        },
    };
}
