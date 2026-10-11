using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewTargetedRerunPolicyTests
{
    [Fact]
    public void Direct_dotnet_test_keeps_original_category_filter_and_selects_failed_classes()
    {
        var command = new ReviewCommandDto("verify-3", "build-tests", "dotnet",
            ["test", "backend.Tests", "--no-build", "--filter", "Category!=MachineBound"]);

        var selected = ReviewTargetedRerunPolicy.Select(command,
            ["AgentStudio.Tests.FooTests.Fails(input: 1)", "AgentStudio.Tests.FooTests.Other", "AgentStudio.Tests.BarTests.Fails"]);

        Assert.Equal("Category!=MachineBound", command.Arguments[^1]);
        Assert.Equal("(Category!=MachineBound)&(FullyQualifiedName~AgentStudio.Tests.BarTests.|FullyQualifiedName~AgentStudio.Tests.FooTests.)",
            selected.Arguments[^1]);
        Assert.Equal("--no-build", selected.Arguments[2]);
    }

    [Fact]
    public void Shell_test_keeps_coverage_and_logger_and_limits_repeat_to_class()
    {
        var command = new ReviewCommandDto("verify-3", "build-tests", "sh",
            ["-lc", "dotnet test backend.Tests --no-build --filter Category!=MachineBound --collect \"Code Coverage;Format=Cobertura\" --logger \"console;verbosity=normal\""]);

        var selected = ReviewTargetedRerunPolicy.Select(command, ["AgentStudio.Tests.FooTests.Fails"]);

        Assert.Contains("--collect \"Code Coverage;Format=Cobertura\"", selected.Arguments[1]);
        Assert.Contains("--logger \"console;verbosity=normal\"", selected.Arguments[1]);
        Assert.EndsWith("--filter \"(Category!=MachineBound)&(FullyQualifiedName~AgentStudio.Tests.FooTests.)\"",
            selected.Arguments[1]);
    }

    [Theory]
    [InlineData("Failed to restore packages")]
    [InlineData("Foo.Bar-Baz")]
    public void Unfilterable_failure_keeps_full_repeat(string failure)
    {
        var command = new ReviewCommandDto("verify-3", "build-tests", "dotnet", ["test", "backend.Tests"]);

        Assert.Same(command, ReviewTargetedRerunPolicy.Select(command, [failure]));
    }

    [Fact]
    public void Chained_shell_command_is_not_rewritten()
    {
        var command = new ReviewCommandDto("verify-3", "build-tests", "sh",
            ["-lc", "dotnet test backend.Tests && npm test"]);

        Assert.Same(command, ReviewTargetedRerunPolicy.Select(command, ["AgentStudio.Tests.FooTests.Fails"]));
    }

    [Theory]
    [InlineData("Test Run Aborted.")]
    [InlineData("The active test run was aborted. Reason: Test host crashed")]
    [InlineData("The test host crashed")]
    public void Aborted_test_run_gets_a_full_repeat(string output)
    {
        var command = new ReviewCommandDto("verify-3", "build-tests", "dotnet", ["test", "backend.Tests"]);
        Assert.Same(command, ReviewTargetedRerunPolicy.Select(
            command, ["AgentStudio.Tests.FooTests.Fails"], output));
    }

    [Fact]
    public void A_signal_gets_a_full_repeat()
    {
        var command = new ReviewCommandDto("verify-3", "build-tests", "dotnet", ["test", "backend.Tests"]);
        Assert.Same(command, ReviewTargetedRerunPolicy.Select(
            command, ["AgentStudio.Tests.FooTests.Fails"], signal: "timeout"));
    }

    [Fact]
    public void Only_passed_test_lines_prove_the_selected_failure_ran()
    {
        var observed = ReviewTargetedRerunPolicy.ObservedPassingFailures(
            "No test matches the given testcase filter\nPassed AgentStudio.Tests.FooTests.Other [1 ms]",
            ["AgentStudio.Tests.FooTests.Fails(input: 1)"]);
        Assert.Empty(observed);
        observed = ReviewTargetedRerunPolicy.ObservedPassingFailures(
            "Passed AgentStudio.Tests.FooTests.Fails(input: 1) [12 ms]",
            ["AgentStudio.Tests.FooTests.Fails(input: 1)"]);
        Assert.Equal(["AgentStudio.Tests.FooTests.Fails"], observed);
    }

    [Fact]
    public void Fresh_clone_replays_the_frozen_build_before_no_build_test()
    {
        var build = new ReviewCommandDto("verify-1", "build-tests", "sh",
            ["-lc", "dotnet build agent-taskboard.sln --no-restore"]);
        var test = new ReviewCommandDto("verify-3", "build-tests", "sh",
            ["-lc", "dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build"]);
        var plan = new ReviewPlanDto([build, test], ["build-tests"]);

        Assert.Same(build, ReviewTargetedRerunPolicy.PrecedingBuild(plan, test));
        Assert.Null(ReviewTargetedRerunPolicy.PrecedingBuild(
            new ReviewPlanDto([test], ["build-tests"]), test));
    }
}
