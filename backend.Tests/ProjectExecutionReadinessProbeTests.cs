using AgentStudio.Projects;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectExecutionReadinessProbeTests
{
    private static readonly DateTime ObservedAt = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Ready_preflight_on_main_overrides_missing_generic_git_capability()
    {
        var result = ProjectExecutionReadinessProbe.Evaluate(
            "main", ["node"], ReadyPreflight("main"),
            [Capability("executor:coding"), Capability("toolchain:node"), Capability("cli-execution:codex")],
            "active");

        Assert.All(result, check => Assert.True(check.Passed, check.Detail));
        Assert.Equal("Integration branch", result[1].Label);
        Assert.Contains("main", result[1].Detail);
    }

    [Fact]
    public void Checks_every_detected_stack_runtime()
    {
        var result = ProjectExecutionReadinessProbe.Evaluate(
            "main", ["node", "dotnet"], ReadyPreflight("main"),
            [Capability("executor:coding"), Capability("toolchain:node"), Capability("cli-execution:codex")],
            "online");

        Assert.True(result[0].Passed);
        Assert.True(result[1].Passed);
        Assert.False(result[2].Passed);
        Assert.Contains("dotnet", result[2].Detail);
    }

    [Fact]
    public void Preflight_for_another_branch_cannot_pass()
    {
        var result = ProjectExecutionReadinessProbe.Evaluate(
            "main", ["node"], ReadyPreflight("develop"),
            [Capability("executor:coding"), Capability("toolchain:node"), Capability("cli-execution:codex")],
            "online");

        Assert.False(result[0].Passed);
        Assert.False(result[1].Passed);
    }

    private static RunnerProjectPreflight ReadyPreflight(string branch) => new()
    {
        TargetBranch = branch, Status = "ready", Detail = "write probe created and removed", CheckedAt = ObservedAt,
    };

    private static CapabilityHealthDto Capability(string key) => new(
        key, "test", "ready", "healthy", null, ObservedAt, ObservedAt.AddMinutes(1),
        true, null, null, null, null, 0, null, null, null, [], []);
}
