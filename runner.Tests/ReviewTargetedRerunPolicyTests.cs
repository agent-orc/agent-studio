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
}
