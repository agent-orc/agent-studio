using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2868 deliverable 1. The seven 1.7-day-old MSBuild nodes that blocked
/// cgroup delegation on <c>agent-runner-review</c> on 18.09.2026 were started by
/// builds the runner never sees the command line of, so the only place the fence
/// can be installed is the environment the detached worker itself is launched
/// with. Both roles are asserted, because the coding unit carried the same
/// leftovers and only the coding launch had half of the fence.
/// </summary>
public sealed class WorkerBuildServerHygieneTests
{
    [Fact]
    public void The_fence_names_both_msbuild_servers_with_their_no_server_value()
    {
        Assert.Equal("1", WorkerBuildServerHygiene.Variables["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("0", WorkerBuildServerHygiene.Variables["DOTNET_CLI_USE_MSBUILD_SERVER"]);
        Assert.Equal(2, WorkerBuildServerHygiene.Variables.Count);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData(null)]
    public void A_coding_worker_is_started_without_reusable_build_servers(string? cliType)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);

        DurableAgentProcess.ApplyWorkerEnvironment(environment, cliType);

        Assert.True(WorkerBuildServerHygiene.IsFenced(environment));
    }

    [Fact]
    public void A_review_worker_is_started_without_reusable_build_servers()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);

        DurableReviewProcess.ApplyWorkerEnvironment(environment);

        Assert.True(WorkerBuildServerHygiene.IsFenced(environment));
    }

    /// <summary>
    /// A worker inherits the daemon's environment. An operator or a drop-in that
    /// re-enabled node reuse for the daemon must not be able to hand a build farm
    /// back to every run on the host.
    /// </summary>
    [Fact]
    public void An_inherited_value_that_would_re_enable_a_build_server_is_overridden()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["MSBUILDDISABLENODEREUSE"] = "0",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "1",
            ["HOME"] = "/var/lib/agent-runner",
        };

        DurableReviewProcess.ApplyWorkerEnvironment(environment);

        Assert.Equal("1", environment["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("0", environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
        Assert.Equal("/var/lib/agent-runner", environment["HOME"]);
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    public void An_environment_missing_either_half_of_the_fence_is_not_fenced(
        string nodeReuse,
        string msbuildServer)
        => Assert.False(WorkerBuildServerHygiene.IsFenced(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["MSBUILDDISABLENODEREUSE"] = nodeReuse,
                ["DOTNET_CLI_USE_MSBUILD_SERVER"] = msbuildServer,
            }));

    [Fact]
    public void An_empty_environment_is_not_fenced()
        => Assert.False(WorkerBuildServerHygiene.IsFenced(
            new Dictionary<string, string?>(StringComparer.Ordinal)));

    /// <summary>
    /// The worker-launch fence and the stricter per-attempt review fence must
    /// keep spelling the same two variables the same way; a divergence would
    /// leave one of the two layers silently ineffective.
    /// </summary>
    [Fact]
    public void The_worker_fence_agrees_with_the_per_attempt_review_isolation()
    {
        var attempt = ReviewBuildServerIsolation.Variables("/attempt/tmp");

        foreach (var (key, value) in WorkerBuildServerHygiene.Variables)
            Assert.Equal(value, attempt[key]);
    }
}
