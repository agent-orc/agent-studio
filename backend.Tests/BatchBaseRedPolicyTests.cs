using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchBaseRedPolicyTests
{
    [Fact]
    public void Contended_red_is_provisional_until_an_idle_verdict()
    {
        var red = Result("Product.Tests.Base") with { GateCollisionDetected = true };
        Assert.True(BatchBaseRedPolicy.Provisional(red));
        Assert.False(BatchBaseRedPolicy.Provisional(Result("Product.Tests.Base")));
        Assert.True(BatchBaseRedPolicy.Provisional(Result("Product.Tests.Base") with
        {
            Processes = [new BuildTestGateProcessEvidence
            {
                ExitCode = 1,
                Resources = new GateResourceEvidence(1000, 100, 90, 95, 500, 500, 100,
                    1, true, AverageExternalCpuPercent: 58),
            }],
        }));
        var baseline = new BatchGateBaseVerdict("project", "base", "profile", "fail", null,
            "red", "evidence", DateTimeOffset.UnixEpoch, Provisional: true,
            FailedTests: ["Product.Tests.Base"]);
        Assert.False(BatchBaseRedPolicy.HoldsCandidate(Result("Product.Tests.Base"), baseline));
        Assert.True(BatchBaseRedPolicy.HoldsCandidate(Result("Product.Tests.Base"),
            baseline with { Provisional = false }));
    }

    [Fact]
    public void Distinct_failed_tests_do_not_hold_the_candidate()
    {
        var baseline = new BatchGateBaseVerdict("project", "base", "profile", "fail", null,
            "red", "evidence", DateTimeOffset.UnixEpoch, FailedTests: ["Product.Tests.Base"]);
        Assert.False(BatchBaseRedPolicy.HoldsCandidate(Result("Product.Tests.Member"), baseline));
        Assert.True(BatchBaseRedPolicy.HoldsCandidate(Result("Product.Tests.Base"), baseline));
        var mixed = Result("Product.Tests.Member") with
        {
            FlakyQuarantinedFailures = ["Product.Tests.Flaky"],
            Processes = [new BuildTestGateProcessEvidence
            {
                ExitCode = 1,
                StandardOutput = "  Failed Product.Tests.Flaky [1 ms]\n"
                    + "  Failed Product.Tests.Member [1 ms]",
            }],
        };
        Assert.Equal(["Product.Tests.Member"], BatchBaseRedPolicy.FailedTests(mixed));
    }

    private static BuildTestGateResult Result(string failed)
        => new(BuildTestGateVerdict.Fail, 1, 1, "red", "red", false, false)
        {
            FailureKind = BuildTestGateFailureKind.Code,
            Processes = [new BuildTestGateProcessEvidence
            {
                ExitCode = 1, StandardOutput = $"  Failed {failed} [1 ms]",
            }],
        };
}
