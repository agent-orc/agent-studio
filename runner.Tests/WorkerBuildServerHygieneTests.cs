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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Detached_workers_drop_task_server_and_browser_edge_credentials(bool review)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["RUNNER_AUTH_TOKEN"] = "runner-secret",
            ["RUNNER_AUTH_TOKEN_FILE"] = "runner-secret-path",
            ["STUDIO_COOKIE"] = "browser-cookie",
            ["STUDIO_CSRF"] = "browser-csrf",
            ["CONNECTOR_SESSION_SECRET"] = "connector-secret",
            ["PATH"] = "tool-path",
        };

        if (review) DurableReviewProcess.ApplyWorkerEnvironment(environment);
        else DurableAgentProcess.ApplyWorkerEnvironment(environment, "codex");

        Assert.Equal("tool-path", environment["PATH"]);
        Assert.DoesNotContain(environment.Keys, key => key is "RUNNER_AUTH_TOKEN"
            or "RUNNER_AUTH_TOKEN_FILE" or "STUDIO_COOKIE" or "STUDIO_CSRF"
            or "CONNECTOR_SESSION_SECRET");
    }

    [Fact]
    public void Explicit_preparation_environment_cannot_put_edge_credentials_back_in_the_worker_spec()
    {
        var options = new RunnerOptions
        {
            ServerUrl = "http://localhost",
            RunnerId = "runner-test",
            RunnerName = "runner-test",
            Hostname = "host-test",
            BackendName = "test",
            WorkDir = Path.GetTempPath(),
            BaseBranch = "main",
            CliBin = "codex",
            CliArgs = "",
        };
        var spec = DurableAgentProcess.BuildSpec(
            options, Path.GetTempPath(), "prompt", Path.GetTempPath(),
            environment: new Dictionary<string, string>
            {
                ["CONNECTOR_SESSION_SECRET"] = "browser-secret",
                ["RUNNER_AUTH_TOKEN"] = "runner-secret",
                ["NUGET_PACKAGES"] = "cache-path",
            });

        Assert.Equal("cache-path", spec.Environment!["NUGET_PACKAGES"]);
        Assert.False(spec.Environment.ContainsKey("CONNECTOR_SESSION_SECRET"));
        Assert.False(spec.Environment.ContainsKey("RUNNER_AUTH_TOKEN"));
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
