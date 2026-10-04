using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2987: an unclaimable review queue used to look like an idle one on the
/// runner too. The daemon now logs the missing keys, bounded per key set.
/// </summary>
public sealed class ReviewClaimEmptyWarningTests
{
    private static readonly DateTime Now = new(2026, 9, 27, 15, 8, 0, DateTimeKind.Utc);

    [Fact]
    public void Unclaimable_empty_claim_warns_with_the_missing_keys_and_the_oldest_attempt()
    {
        var warning = new ReviewClaimEmptyWarning();

        var line = warning.Next(Unclaimable(CapabilityProtocol.DotNet, CapabilityProtocol.Node), Now);

        Assert.NotNull(line);
        Assert.Contains("reason=unclaimable-plan-requirements", line, StringComparison.Ordinal);
        Assert.Contains("missing=toolchain:dotnet,toolchain:node", line, StringComparison.Ordinal);
        Assert.Contains("oldestAttempt=review-1", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_keys_repeat_only_after_the_interval_while_a_changed_key_set_warns_at_once()
    {
        var warning = new ReviewClaimEmptyWarning();
        Assert.NotNull(warning.Next(Unclaimable(CapabilityProtocol.DotNet), Now));

        Assert.Null(warning.Next(Unclaimable(CapabilityProtocol.DotNet), Now.AddMinutes(5)));
        Assert.NotNull(warning.Next(Unclaimable(CapabilityProtocol.Node), Now.AddMinutes(6)));
        Assert.NotNull(warning.Next(
            Unclaimable(CapabilityProtocol.Node),
            Now.AddMinutes(6).Add(ReviewClaimEmptyWarning.RepeatInterval)));
    }

    [Theory]
    [InlineData("empty", ReviewClaimEmptyReasons.QueueEmpty)]
    [InlineData("empty", null)]
    [InlineData("claimed", null)]
    public void Other_claim_answers_stay_quiet(string status, string? reason)
    {
        var warning = new ReviewClaimEmptyWarning();

        Assert.Null(warning.Next(new ReviewClaimResponse(status, Reason: reason), Now));
    }

    private static ReviewClaimResponse Unclaimable(params string[] missing)
        => ReviewClaimEmptyResponses.ForQueue(
            [new ReviewUnclaimableAttemptDto("review-1", "AGT-1", Now.AddHours(-1), missing)],
            ReviewClaimEmptyReasons.QueueEmpty,
            "No current immutable ReviewAttempt is queued.");
}
