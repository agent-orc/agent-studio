using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3232: one verdict per gate run. All shards must pass, a red test is a
/// code failure wherever it ran, and an infrastructure failure next to green
/// shards is an infrastructure verdict, never a code failure.
/// </summary>
public sealed class GateShardVerdictPolicyTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static GateShardEvidence Shard(int index, string outcome,
        BuildTestGateFailureKind kind = BuildTestGateFailureKind.None, string host = "kori-gate")
        => new(index, host, 0, 10, 1000, 1000, outcome == GateShardOutcomes.Passed ? 0 : 1, outcome,
            new GateTestCounts(100, outcome == GateShardOutcomes.Failed ? 99 : 100, outcome == GateShardOutcomes.Failed ? 1 : 0, 0),
            outcome == GateShardOutcomes.Failed ? [$"P.Shard{index}Tests.Red"] : [])
        {
            FailureKind = kind,
        };

    [Fact]
    public void Green_flaky_and_quarantined_shards_pass_together()
        => Assert.Equal(BuildTestGateFailureKind.None, GateShardVerdictPolicy.MergeShards(
        [
            Shard(0, GateShardOutcomes.Passed),
            Shard(1, GateShardOutcomes.FlakyPassed),
            Shard(2, GateShardOutcomes.QuarantinePassed),
            Shard(3, GateShardOutcomes.Empty),
        ]));

    [Fact]
    public void A_red_shard_is_a_code_failure_even_next_to_an_infrastructure_failure()
        => Assert.Equal(BuildTestGateFailureKind.Code, GateShardVerdictPolicy.MergeShards(
        [
            Shard(0, GateShardOutcomes.Infrastructure, BuildTestGateFailureKind.OutOfMemory),
            Shard(1, GateShardOutcomes.Failed, BuildTestGateFailureKind.Code),
            Shard(2, GateShardOutcomes.NotRun),
        ]));

    [Fact]
    public void An_infrastructure_shard_among_green_shards_is_an_infrastructure_verdict()
    {
        var kind = GateShardVerdictPolicy.MergeShards(
        [
            Shard(0, GateShardOutcomes.Passed),
            Shard(2, GateShardOutcomes.Infrastructure, BuildTestGateFailureKind.Timeout),
            Shard(1, GateShardOutcomes.Infrastructure, BuildTestGateFailureKind.OutOfMemory),
        ]);

        Assert.Equal(BuildTestGateFailureKind.OutOfMemory, kind);
    }

    [Fact]
    public void An_infrastructure_shard_without_a_kind_is_environmental()
        => Assert.Equal(BuildTestGateFailureKind.Environment,
            GateShardVerdictPolicy.MergeShards([Shard(0, GateShardOutcomes.Infrastructure)]));

    private static GateShardSuiteEvidence Suite(string host, int count, params GateShardEvidence[] shards)
        => new("backend-routine", "dotnet test B.csproj", count, GateShardCountSources.Profile,
            GateShardStrategies.Timings, "plan-1", 2, 5000, shards)
        {
            Hosts = [host],
            SerialWallMs = shards.Sum(shard => shard.DurationMs),
            ClassDurationsMs = new Dictionary<string, double> { [$"{host}Tests"] = 10 },
        };

    private static BuildTestGateResult Green(string host, GateShardSuiteEvidence suite, long durationMs = 1000)
        => new(BuildTestGateVerdict.Ok, 0, durationMs, $"{host} output", $"verify gate passed on {host}", true, false)
        {
            TestedSha = Sha,
            ExpectedSha = Sha,
            Executor = "ssh:" + host,
            TestShards = [suite],
        };

    private static BuildTestGateResult Red(string host, GateShardSuiteEvidence suite)
        => Green(host, suite) with
        {
            Verdict = BuildTestGateVerdict.Fail,
            FailureKind = BuildTestGateFailureKind.Code,
            ExitCode = 1,
            Reason = "dotnet test exit 1",
        };

    [Fact]
    public void Green_hosts_merge_into_one_pass_with_every_shard_and_host()
    {
        var merged = GateShardVerdictPolicy.MergeHosts(
        [
            new("agent-runner-01", new GateShardPart(1, 2), Green("agent-runner-01",
                Suite("agent-runner-01", 4, Shard(1, GateShardOutcomes.Passed, host: "agent-runner-01"),
                    Shard(3, GateShardOutcomes.Passed, host: "agent-runner-01")), durationMs: 9000)),
            new("kori-gate", new GateShardPart(0, 2), Green("kori-gate",
                Suite("kori-gate", 4, Shard(0, GateShardOutcomes.Passed), Shard(2, GateShardOutcomes.FlakyPassed)))),
        ]);

        Assert.Equal(BuildTestGateVerdict.Ok, merged.Verdict);
        Assert.Equal(BuildTestGateFailureKind.None, merged.FailureKind);
        Assert.Equal(9000, merged.DurationMs);
        Assert.Equal("sharded:ssh:kori-gate,ssh:agent-runner-01", merged.Executor);
        var suite = Assert.Single(merged.TestShards);
        Assert.Equal([0, 1, 2, 3], suite.Shards.Select(shard => shard.Index));
        Assert.Equal(["kori-gate", "agent-runner-01"], suite.Hosts);
        Assert.Equal(new GateTestCounts(400, 400, 0, 0), suite.Tests);
        Assert.Equal(4, suite.Lanes);
        Assert.Equal(4000, suite.SerialWallMs);
        Assert.Equal(2, suite.ClassDurationsMs.Count);
        Assert.StartsWith("# ===== gate host kori-gate (host part 1/2): Ok =====", merged.Output);
        Assert.Contains("sharded across 2 host(s): kori-gate part 1/2=Ok, agent-runner-01 part 2/2=Ok", merged.Reason);
    }

    [Fact]
    public void A_red_test_on_one_host_fails_the_run_as_code_and_names_the_host()
    {
        var merged = GateShardVerdictPolicy.MergeHosts(
        [
            new("kori-gate", new GateShardPart(0, 2), Green("kori-gate",
                Suite("kori-gate", 2, Shard(0, GateShardOutcomes.Passed)))),
            new("agent-runner-01", new GateShardPart(1, 2), Red("agent-runner-01",
                Suite("agent-runner-01", 2, Shard(1, GateShardOutcomes.Failed, BuildTestGateFailureKind.Code, "agent-runner-01")))),
        ]);

        Assert.Equal(BuildTestGateVerdict.Fail, merged.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Code, merged.FailureKind);
        Assert.False(merged.IsInfrastructureFailure);
        Assert.StartsWith("dotnet test exit 1 (on agent-runner-01, host part 2/2)", merged.Reason);
        Assert.Equal(["P.Shard1Tests.Red"], merged.TestShards.Single().Shards.Single(shard => shard.Index == 1).FailedTests);
    }

    [Fact]
    public void A_partial_infrastructure_failure_is_an_infrastructure_verdict_not_a_code_failure()
    {
        var merged = GateShardVerdictPolicy.MergeHosts(
        [
            new("kori-gate", new GateShardPart(0, 2), Green("kori-gate",
                Suite("kori-gate", 2, Shard(0, GateShardOutcomes.Passed)))),
            new("agent-runner-01", new GateShardPart(1, 2), new BuildTestGateResult(
                BuildTestGateVerdict.Fail, null, 0, "ssh: connect refused", "Remote gate transport failure", false, false)
            {
                FailureKind = BuildTestGateFailureKind.Environment,
                ExpectedSha = Sha,
            }),
        ]);

        Assert.Equal(BuildTestGateVerdict.Fail, merged.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Environment, merged.FailureKind);
        Assert.True(merged.IsInfrastructureFailure);
        Assert.StartsWith("partial infrastructure failure on agent-runner-01 (host part 2/2): Remote gate transport failure; other hosts: kori-gate=Ok",
            merged.Reason);
        Assert.Equal(Sha, merged.TestedSha);
    }

    [Fact]
    public void A_red_test_wins_over_a_sibling_stopped_because_of_it()
    {
        var merged = GateShardVerdictPolicy.MergeHosts(
        [
            new("kori-gate", new GateShardPart(0, 2), new BuildTestGateResult(
                BuildTestGateVerdict.Fail, null, 0, "", "stopped", false, false)
            {
                FailureKind = BuildTestGateFailureKind.Cancellation,
            }),
            new("agent-runner-01", new GateShardPart(1, 2), Red("agent-runner-01",
                Suite("agent-runner-01", 2, Shard(1, GateShardOutcomes.Failed, BuildTestGateFailureKind.Code)))),
        ]);

        Assert.Equal(BuildTestGateFailureKind.Code, merged.FailureKind);
    }

    [Fact]
    public void The_cause_leads_an_infrastructure_verdict_not_the_sibling_it_stopped()
    {
        var merged = GateShardVerdictPolicy.MergeHosts(
        [
            new("kori-gate", new GateShardPart(0, 2), new BuildTestGateResult(
                BuildTestGateVerdict.Fail, null, 0, "", "stopped because another shard host failed", false, false)
            {
                FailureKind = BuildTestGateFailureKind.Cancellation,
            }),
            new("agent-runner-01", new GateShardPart(1, 2), new BuildTestGateResult(
                BuildTestGateVerdict.Fail, 137, 0, "", "test host killed", false, false)
            {
                FailureKind = BuildTestGateFailureKind.OutOfMemory,
            }),
        ]);

        Assert.Equal(BuildTestGateFailureKind.OutOfMemory, merged.FailureKind);
        Assert.Contains("on agent-runner-01", merged.Reason);
    }

    [Fact]
    public void Green_hosts_that_left_a_shard_unrun_do_not_pass()
    {
        var merged = GateShardVerdictPolicy.MergeHosts(
        [
            new("kori-gate", new GateShardPart(0, 2), Green("kori-gate",
                Suite("kori-gate", 3, Shard(0, GateShardOutcomes.Passed), Shard(2, GateShardOutcomes.Passed)))),
            new("agent-runner-01", new GateShardPart(1, 2), Green("agent-runner-01",
                Suite("agent-runner-01", 3))),
        ]);

        Assert.Equal(BuildTestGateVerdict.Fail, merged.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Environment, merged.FailureKind);
        Assert.StartsWith("shard evidence incomplete: suite backend-routine shard(s) 2 of 3 ran on no host", merged.Reason);
    }

    [Fact]
    public void Hosts_that_planned_differently_do_not_pass()
    {
        var other = Suite("agent-runner-01", 2, Shard(1, GateShardOutcomes.Passed)) with { PlanDigest = "plan-2" };
        var merged = GateShardVerdictPolicy.MergeHosts(
        [
            new("kori-gate", new GateShardPart(0, 2), Green("kori-gate", Suite("kori-gate", 2, Shard(0, GateShardOutcomes.Passed)))),
            new("agent-runner-01", new GateShardPart(1, 2), Green("agent-runner-01", other)),
        ]);

        Assert.True(merged.IsInfrastructureFailure);
        Assert.Contains("different shard plans", merged.Reason);
    }

    [Fact]
    public void A_host_that_did_not_plan_the_suite_does_not_pass()
    {
        var merged = GateShardVerdictPolicy.MergeHosts(
        [
            new("kori-gate", new GateShardPart(0, 2), Green("kori-gate", Suite("kori-gate", 1, Shard(0, GateShardOutcomes.Passed)))),
            new("agent-runner-01", new GateShardPart(1, 2), Green("agent-runner-01", Suite("x", 1)) with { TestShards = [] }),
        ]);

        Assert.True(merged.IsInfrastructureFailure);
        Assert.Contains("was not planned on agent-runner-01", merged.Reason);
    }

    [Theory]
    [InlineData(BuildTestGateFailureKind.None, BuildTestGateFailureKind.Environment)]
    [InlineData(BuildTestGateFailureKind.Timeout, BuildTestGateFailureKind.Timeout)]
    public void A_failed_share_without_a_code_red_fails_the_merge(
        BuildTestGateFailureKind shareKind, BuildTestGateFailureKind mergedKind)
    {
        // Shard evidence alone would read green; the share's own verdict says it did not pass.
        var merged = GateShardVerdictPolicy.MergeHosts(
        [
            new("kori-gate", new GateShardPart(0, 2), Green("kori-gate", Suite("kori-gate", 2, Shard(0, GateShardOutcomes.Passed)))),
            new("agent-runner-01", new GateShardPart(1, 2), Green("agent-runner-01",
                Suite("agent-runner-01", 2, Shard(1, GateShardOutcomes.Passed, host: "agent-runner-01"))) with
            {
                Verdict = BuildTestGateVerdict.Fail,
                FailureKind = shareKind,
                Reason = "share did not finish",
            }),
        ]);

        Assert.Equal(BuildTestGateVerdict.Fail, merged.Verdict);
        Assert.Equal(mergedKind, merged.FailureKind);
        Assert.Contains("share did not finish", merged.Reason);
    }
}
