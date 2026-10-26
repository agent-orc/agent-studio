using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewStatisticsFoldTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MedianAndP90_AreComputedFromDeliveryRounds()
    {
        var values = new long[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
        var rows = values.Select((tokens, index) => Row("P", $"card-{index}", $"a-{index}", "requirement-fit",
            "gpt-6-sol", "medium", tokens, 0.01m * (index + 1))).ToList();

        var result = ReviewStatisticsFold.Build(rows, new Dictionary<string, long>(), new HashSet<string>(), Now, 30);

        Assert.Equal(55, result.DeliveryTokens.Median);
        Assert.Equal(90, result.DeliveryTokens.P90);
        Assert.Equal(0.055, result.DeliveryUsd.Median!.Value, 3);
        Assert.Equal(10, result.DeliveryTokens.Samples);
        Assert.Equal(30, result.DeliveryTokens.Last30d.Count);
    }

    [Fact]
    public void ModelAndReasoningFilter_PreservesReviewAttributionAndAcceptedCardMath()
    {
        var rows = new[]
        {
            Row("P", "A", "one", "requirement-fit", "gpt-6-sol", "medium", 100, 1m),
            Row("P", "A", "one", "grade", "gpt-6-sol", "medium", 20, 0.2m),
            Row("P", "A", "two", "code-quality", "gpt-6-sol", "xhigh", 80, 0.8m),
            Row("P", "B", "one", "grade", "claude-opus-5.5", "low", 200, null),
        };
        var totals = new Dictionary<string, long> { ["P/A"] = 1_000, ["P/B"] = 500 };
        var accepted = new HashSet<string> { "P/A" };

        var all = ReviewStatisticsFold.Build(rows, totals, accepted, Now, 30);
        var medium = ReviewStatisticsFold.Build(rows, totals, accepted, Now, 30,
            model: "gpt-6-sol", level: "medium");

        Assert.Equal(2, all.RoundsUntilAcceptance.Median);
        Assert.Equal(20, all.ReviewSharePercent.Median);
        Assert.Equal(200, all.TokensPerAcceptedCard.Median);
        Assert.Equal(2, all.DeliveryUsd.Samples); // The unpriced delivery is excluded from USD.
        Assert.Equal(1, medium.RoundsUntilAcceptance.Median);
        Assert.Equal(12, medium.ReviewSharePercent.Median);
        Assert.Equal(120, medium.DeliveryTokens.Median);
        Assert.Equal(110, all.ByStepTokens["grade"].Median);
        Assert.Equal(80, all.ByModelLevelTokens["gpt-6-sol / xhigh"].Median);
        Assert.Equal(120, medium.ByModelLevelTokens["gpt-6-sol / medium"].Median);
        Assert.Equal(120, medium.ByModelLevelTokens["gpt-6-sol / medium"].P90);
        Assert.Equal(1.2, medium.ByModelLevelUsd["gpt-6-sol / medium"].Median);
    }

    [Theory]
    [InlineData("support:remote-review:attempt-1:step:aspect-code-quality", "code-quality", "attempt-1")]
    [InlineData("support:remote-review:attempt-1:step:post-code-review-grade", "grade", "attempt-1")]
    [InlineData("support:remote-review:attempt-1:step:aspect-code-quality:usage:attempt-1:0", "code-quality", "attempt-1")]
    [InlineData("support:remote-review:attempt-1", "review, step unknown", "attempt-1")]
    [InlineData("support:review-attempt:3:step:post-code-review-grade", "grade", "3")]
    public void ParticipantIdentity_ResolvesStepAndAttempt(string participant, string step, string attempt)
    {
        Assert.Equal(step, ReviewStatisticsFold.Step(participant));
        Assert.Equal(attempt, ReviewStatisticsFold.Attempt(participant, Now));
    }

    [Fact]
    public void LegacyRemoteReviewWithoutHost_IsNotAttributedToLocalHost()
    {
        Assert.Equal(TokenUsageHost.UnrecordedRemote,
            TokenUsageHost.Resolve(null, "support:remote-review:attempt-1"));
    }

    private static ReviewUsageRow Row(string project, string card, string attempt, string step,
        string model, string level, long tokens, decimal? usd)
        => new(project, card, attempt, step, Now.AddDays(-1), model, level, "review-host", tokens, usd);
}
