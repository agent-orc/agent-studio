using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2831 policy matrix. The isolation set is what keeps four concurrent
/// review attempts off one host-shared .NET build server, so each variable is
/// asserted by value rather than by presence.
/// </summary>
public sealed class ReviewBuildServerIsolationTests
{
    [Fact]
    public void Isolation_disables_every_host_shared_dotnet_build_server()
    {
        var variables = ReviewBuildServerIsolation.Variables("/attempt/tmp");

        Assert.Equal("1", variables[ReviewBuildServerIsolation.DisableNodeReuse]);
        Assert.Equal("0", variables[ReviewBuildServerIsolation.DisableMsBuildServer]);
        Assert.Equal("false", variables[ReviewBuildServerIsolation.DisableSharedCompilation]);
        Assert.Equal("/attempt/tmp", variables[ReviewBuildServerIsolation.MsBuildDebugPath]);
    }

    [Fact]
    public void Two_attempts_get_distinct_msbuild_debug_paths()
    {
        var first = ReviewBuildServerIsolation.Variables("/attempt-a/tmp");
        var second = ReviewBuildServerIsolation.Variables("/attempt-b/tmp");

        Assert.NotEqual(
            first[ReviewBuildServerIsolation.MsBuildDebugPath],
            second[ReviewBuildServerIsolation.MsBuildDebugPath]);
    }

    [Fact]
    public void Applying_isolation_overrides_a_value_that_would_re_enable_a_shared_server()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ReviewBuildServerIsolation.DisableNodeReuse] = "0",
            [ReviewBuildServerIsolation.DisableSharedCompilation] = "true",
            ["HOME"] = "/attempt/home",
        };

        ReviewBuildServerIsolation.ApplyTo(environment, "/attempt/tmp");

        Assert.Equal("1", environment[ReviewBuildServerIsolation.DisableNodeReuse]);
        Assert.Equal("false", environment[ReviewBuildServerIsolation.DisableSharedCompilation]);
        Assert.Equal("/attempt/home", environment["HOME"]);
        Assert.True(ReviewBuildServerIsolation.IsIsolated(environment));
    }

    [Theory]
    [InlineData("0", "0", "false")]
    [InlineData("1", "1", "false")]
    [InlineData("1", "0", "true")]
    public void An_environment_missing_any_part_of_the_fence_is_not_isolated(
        string nodeReuse,
        string msbuildServer,
        string sharedCompilation)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ReviewBuildServerIsolation.DisableNodeReuse] = nodeReuse,
            [ReviewBuildServerIsolation.DisableMsBuildServer] = msbuildServer,
            [ReviewBuildServerIsolation.DisableSharedCompilation] = sharedCompilation,
        };

        Assert.False(ReviewBuildServerIsolation.IsIsolated(environment));
    }

    [Fact]
    public void An_empty_environment_is_not_isolated()
        => Assert.False(ReviewBuildServerIsolation.IsIsolated(
            new Dictionary<string, string?>(StringComparer.Ordinal)));

    [Fact]
    public void Isolation_requires_an_attempt_local_temp_directory()
        => Assert.Throws<ArgumentException>(() => ReviewBuildServerIsolation.Variables("  "));
}
