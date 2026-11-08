using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewTestReportPolicyTests
{
    [Fact]
    public void Vitest_test_files_summary_is_a_parseable_report()
    {
        var command = new ReviewCommandDto("verify-9", "build-tests", "sh", ["-c", "ng test"],
            CompareToBaseline: true);
        Assert.False(ReviewTestReportPolicy.Missing(command,
            new ProcessResult(0, "Test Files  1 passed (1)", string.Empty)));
        Assert.True(ReviewTestReportPolicy.Missing(command,
            new ProcessResult(0, "Application bundle generation complete.", string.Empty)));
        Assert.Equal("Angular bundle completed but Vitest did not report a run",
            ReviewTestReportPolicy.MissingPhase(new ProcessResult(
                0, "Application bundle generation complete.", string.Empty)));
    }

    [Theory]
    [InlineData("M\tfrontend/vitest.config.ts", true)]
    [InlineData("A\tfrontend/src/new.spec.ts", true)]
    [InlineData("M\tfrontend/src/existing.spec.ts", false)]
    [InlineData("M\tfrontend/src/component.ts", false)]
    public void Setup_change_requires_configuration_or_spec_list_change(string line, bool expected)
        => Assert.Equal(expected, ReviewTestReportPolicy.SetupChangeReason([line]) is not null);
}
