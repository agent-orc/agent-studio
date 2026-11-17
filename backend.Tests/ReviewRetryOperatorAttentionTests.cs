using AgentStudio.Operator;
using AgentStudio.Runner;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewRetryOperatorAttentionTests
{
    private static readonly DateTime Due = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Overdue_retries_make_one_item_with_cards_count_and_watchdog_deadline_then_clear()
    {
        var retries = new List<ReviewInfrastructureRetryDue>
        {
            Retry("A-1"), Retry("A-2"),
        };
        var source = Source(() => retries, () => false);

        Assert.Empty(source.Collect(Due.AddSeconds(30))!);
        var item = Assert.Single(OperatorAttention.Project(source.Collect(Due.AddSeconds(31))!,
            OperatorDecisionReader.Everything).Items);
        Assert.Equal(OperatorDecisionTypes.ReviewRetrySchedulerStalled, item.Type);
        Assert.Equal(2, item.Cards.Count);
        Assert.Equal(["A-1", "A-2"], item.Cards);
        Assert.Contains("2", item.Reason);
        Assert.Contains("watchdog", item.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AutoReviewStrandingWatchdog.ParkOverdueRetryBy(Due,
            AutoReviewStrandingWatchdog.RetryOverdueLimit(Config())).ToString("yyyy-MM-dd HH:mm"), item.Reason);

        retries.Clear();
        Assert.Empty(source.Collect(Due.AddMinutes(1))!);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Disabled_or_failed_scheduler_reports_pending_retries_before_grace(bool enabled, bool failed)
    {
        var source = Source(() => [Retry("A-1")], () => failed, enabled);
        var item = Assert.Single(source.Collect(Due.AddSeconds(-5))!);
        Assert.Equal(["A-1"], item.Cards);
        Assert.Contains(enabled ? "failing" : "disabled", item.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static ReviewInfrastructureRetryDue Retry(string card)
        => new("review-" + card, card, 1, 3, "host unavailable", Due);

    private static IConfiguration Config(bool enabled = true) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
            { ["Runner:ReviewInfrastructureRetry:Enabled"] = enabled.ToString() }).Build();

    private static OperatorReviewRetryFactSource Source(Func<IReadOnlyList<ReviewInfrastructureRetryDue>> retries,
        Func<bool> failed, bool enabled = true) => new(retries, failed, Config(enabled));
}
