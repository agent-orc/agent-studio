using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3232: the pure checks that keep a shard from passing without proof.
/// An aborted run never passes, a shard must have run exactly its partition,
/// quarantine and re-run act only on failures console and TRX agree on, and a
/// re-run is green only when its report shows every targeted test passed.
/// </summary>
public sealed class GateShardRunChecksTests
{
    private static readonly GateShardPlan Plan = new("backend", 3, GateShardCountSources.Profile, GateShardStrategies.Hash,
    [
        new(0, ["Ns.AlphaTests", "Ns.BetaTests"], 0),
        new(1, ["Ns.GammaTests"], 0),
        new(2, ["Ns.DeltaTests"], 0),
    ], "digest");

    private static GateTestReport Report(GateTestCounts counts, IReadOnlyList<string>? failed = null,
        IReadOnlyCollection<string>? classes = null, string? aborted = null)
        => new(counts, failed ?? []) { Classes = classes ?? [], AbortedOutcome = aborted };

    [Theory]
    [InlineData("Aborted", "", "TRX run outcome Aborted")]
    [InlineData(null, "Test Run Aborted.", "console reports \"Test Run Aborted\"")]
    [InlineData(null, "The active test run was aborted. Reason: Test host process crashed", "console reports \"The active test run was aborted\"")]
    [InlineData(null, "  Failed Ns.AlphaTests.Red [3 ms]\nTotal tests: 4", null)]
    public void An_aborted_run_is_read_from_the_report_or_the_console(string? trxOutcome, string output, string? expected)
        => Assert.Equal(expected, GateShardRunChecks.Abort(Report(GateTestCounts.None, aborted: trxOutcome), output));

    [Fact]
    public void A_listed_shard_may_only_hold_its_own_classes_and_the_complement_none_of_the_others()
    {
        Assert.Empty(GateShardRunChecks.ForeignClasses(Plan, 0, Report(GateTestCounts.None, classes: ["Ns.AlphaTests"])));
        Assert.Equal(["Ns.GammaTests"],
            GateShardRunChecks.ForeignClasses(Plan, 0, Report(GateTestCounts.None, classes: ["Ns.AlphaTests", "Ns.GammaTests"])));
        // The complement also runs classes the scan missed, such as nested ones.
        Assert.Empty(GateShardRunChecks.ForeignClasses(Plan, 2,
            Report(GateTestCounts.None, classes: ["Ns.DeltaTests", "Ns.AlphaTests+Nested", "Ns.UnscannedTests"])));
        Assert.Equal(["Ns.BetaTests"],
            GateShardRunChecks.ForeignClasses(Plan, 2, Report(GateTestCounts.None, classes: ["Ns.DeltaTests", "Ns.BetaTests"])));
    }

    [Fact]
    public void Failures_are_confirmed_only_when_console_and_report_name_the_same_tests()
    {
        var report = Report(new GateTestCounts(3, 2, 1, 0), ["Ns.AlphaTests.Red"]);

        Assert.True(GateShardRunChecks.FailuresConfirmed(["Ns.AlphaTests.Red"], report));
        Assert.False(GateShardRunChecks.FailuresConfirmed(["Ns.AlphaTests.Red", "Ns.BetaTests.Red"], report));
        Assert.False(GateShardRunChecks.FailuresConfirmed(["Ns.AlphaTests.Red"], Report(new GateTestCounts(3, 3, 0, 0))));
        Assert.False(GateShardRunChecks.FailuresConfirmed(["Ns.AlphaTests.Red"], null));
        Assert.False(GateShardRunChecks.FailuresConfirmed([], Report(GateTestCounts.None)));
    }

    [Theory]
    [InlineData(true, 2, 2, 0, 0, null, "", true)]
    [InlineData(true, 0, 0, 0, 0, null, "", false)]
    [InlineData(true, 1, 1, 0, 0, null, "", false)]
    [InlineData(true, 2, 1, 0, 1, null, "", false)]
    [InlineData(true, 2, 1, 1, 0, null, "", false)]
    [InlineData(false, 2, 2, 0, 0, null, "", false)]
    [InlineData(true, 2, 2, 0, 0, "Aborted", "", false)]
    [InlineData(true, 2, 2, 0, 0, null, "Test Run Aborted.", false)]
    public void A_rerun_is_green_only_with_every_targeted_test_passed_in_its_report(
        bool exitZero, int total, int passed, int failed, int skipped, string? aborted, string output, bool green)
        => Assert.Equal(green, GateShardRunChecks.RerunConfirmed(
            exitZero, Report(new GateTestCounts(total, passed, failed, skipped), aborted: aborted), output, targeted: 2));

    [Fact]
    public void A_rerun_without_a_report_is_not_green()
        => Assert.False(GateShardRunChecks.RerunConfirmed(true, null, "", targeted: 1));
}
