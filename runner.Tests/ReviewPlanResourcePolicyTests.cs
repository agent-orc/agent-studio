using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewPlanResourcePolicyTests
{
    [Fact]
    public void Shell_dotnet_tests_receive_cpu_and_collection_parallelism_limits()
    {
        var plan = new ReviewPlanDto(
            [new ReviewCommandDto(
                "verify-2",
                "build-tests",
                "sh",
                ["-lc", "cd -- backend && dotnet test --filter Category!=MachineBound"],
                CompareToBaseline: true)],
            ["build-tests"]);

        var limited = ReviewPlanResourcePolicy.Apply(plan, dotNetMaxCpuCount: 2);

        Assert.Equal(
            "cd -- backend && dotnet test -maxcpucount:2 -p:ParallelizeTestCollections=false --filter \"Category!=MachineBound&Category!=LiveCli\"",
            Assert.Single(limited.Commands).Arguments[1]);
    }

    [Fact]
    public void Direct_dotnet_tests_are_limited_without_changing_non_test_commands()
    {
        var test = new ReviewCommandDto(
            "verify-test", "build-tests", "dotnet", ["test", "runner.Tests"]);
        var build = new ReviewCommandDto(
            "verify-build", "build-tests", "dotnet", ["build", "runner"]);
        var plan = new ReviewPlanDto([test, build], ["build-tests"]);

        var limited = ReviewPlanResourcePolicy.Apply(plan, dotNetMaxCpuCount: 2);

        Assert.Equal(
            ["test", "-maxcpucount:2", "-p:ParallelizeTestCollections=false", "runner.Tests", "--filter", ReviewPlanResourcePolicy.DefaultGateTestFilter],
            limited.Commands[0].Arguments);
        Assert.Equal(build, limited.Commands[1]);
    }

    [Fact]
    public void Existing_unbounded_values_are_replaced_idempotently()
    {
        var plan = new ReviewPlanDto(
            [new ReviewCommandDto(
                "verify-2",
                "build-tests",
                "bash",
                ["-lc", "dotnet test -maxcpucount:12 -p:ParallelizeTestCollections=true"],
                CompareToBaseline: true)],
            ["build-tests"]);

        var first = ReviewPlanResourcePolicy.Apply(plan, dotNetMaxCpuCount: 2);
        var second = ReviewPlanResourcePolicy.Apply(first, dotNetMaxCpuCount: 2);

        Assert.Equal(first, second);
        Assert.Equal(
            $"dotnet test -maxcpucount:2 -p:ParallelizeTestCollections=false --filter \"{ReviewPlanResourcePolicy.DefaultGateTestFilter}\"",
            Assert.Single(first.Commands).Arguments[1]);
    }

    [Fact]
    public void Shell_normalization_preserves_quoted_whitespace()
    {
        var plan = new ReviewPlanDto(
            [new ReviewCommandDto(
                "verify-2",
                "build-tests",
                "sh",
                ["-lc", "dotnet test -maxcpucount:12 --filter \"Name~two  spaces\""],
                CompareToBaseline: true)],
            ["build-tests"]);

        var limited = ReviewPlanResourcePolicy.Apply(plan, dotNetMaxCpuCount: 2);

        Assert.Equal(
            "dotnet test -maxcpucount:2 -p:ParallelizeTestCollections=false --filter \"Name~two  spaces&Category!=MachineBound&Category!=LiveCli\"",
            Assert.Single(limited.Commands).Arguments[1]);
    }

    [Fact]
    public void Preparation_and_verification_share_the_same_dotnet_resource_policy()
    {
        var plan = new ReviewPlanDto(
            [new ReviewCommandDto("verify", "build-tests", "git", ["status"])],
            ["build-tests"],
            Preparation:
            [
                new ReviewPreparationCommandDto(
                    "prepare",
                    "bash",
                    ["-lc", "dotnet test --filter Category!=MachineBound && npm ci"]),
            ]);

        var limited = ReviewPlanResourcePolicy.Apply(plan, dotNetMaxCpuCount: 2);

        Assert.Equal(
            "dotnet test -maxcpucount:2 -p:ParallelizeTestCollections=false --filter \"Category!=MachineBound&Category!=LiveCli\" && npm ci",
            Assert.Single(limited.Preparation!).Arguments[1]);
    }
}
