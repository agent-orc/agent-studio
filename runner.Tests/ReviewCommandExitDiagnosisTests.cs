using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewCommandExitDiagnosisTests
{
    [Theory]
    [InlineData(0, 1, 1, true)]
    [InlineData(0, 1, 2, true)]
    [InlineData(1, 1, 1, false)]
    [InlineData(0, 1, 0, false)]
    [InlineData(0, 127, 127, false)]
    [InlineData(0, 1, 127, false)]
    [InlineData(0, 124, 124, false)]
    public void Only_repeated_candidate_command_failures_on_a_green_baseline_charge_the_card(
        int baseline, int candidate, int repeat, bool expected)
        => Assert.Equal(expected,
            ReviewCommandExitDiagnosis.Reproduced(baseline, candidate, repeat));

    [Fact]
    public void A_timed_out_repeat_does_not_prove_a_product_failure()
        => Assert.False(ReviewCommandExitDiagnosis.Reproduced(0, 1, 1, repeatSignal: "timeout"));

    [Fact]
    public void Evidence_is_bounded_and_scrubs_secret_shaped_values()
    {
        var credential = "token=" + new string('s', 50);
        var evidence = ReviewCommandExitDiagnosis.Evidence(
            "npm --prefix frontend run build", new string('a', 40), new string('b', 40),
            0, 1, 1, new string('x', 4000) + credential + "\nerror: compile failed", "");

        Assert.Contains("candidate-output-tail:", evidence);
        Assert.Contains("error: compile failed", evidence);
        Assert.Contains("[redacted]", evidence);
        Assert.DoesNotContain(credential, evidence);
        Assert.True(evidence.Length < 2400);
    }
}
