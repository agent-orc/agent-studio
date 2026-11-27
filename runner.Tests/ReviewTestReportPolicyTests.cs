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
        Assert.False(ReviewTestReportPolicy.NeedsSetupCheck(command,
            new ProcessResult(0, "Application bundle generation complete.", string.Empty)));
        Assert.Equal("Angular bundle completed but Vitest did not report a run",
            ReviewTestReportPolicy.MissingPhase(new ProcessResult(
                0, "Application bundle generation complete.", string.Empty)));
        Assert.False(ReviewTestReportPolicy.Missing(command,
            new ProcessResult(0, "custom check passed", string.Empty)));
    }

    [Theory]
    [InlineData("dotnet", "test backend.Tests.csproj", "No test is available in backend.Tests.dll")]
    [InlineData("node", "--test", "# tests 0")]
    [InlineData("npx", "jest --passWithNoTests", "No tests found, exiting with code 0")]
    [InlineData("npx", "vitest run", "Test Files  0 passed (0)")]
    [InlineData("sh", "-c printf", "No test is available in backend.Tests.dll")]
    [InlineData("sh", "-c printf", "# tests 0")]
    [InlineData("sh", "-c printf", "No tests found, exiting with code 0")]
    [InlineData("sh", "-c printf", "Test Files  0 passed (0)")]
    public void Zero_tests_from_recognized_runner_is_missing(string fileName, string arguments, string output)
    {
        var command = new ReviewCommandDto("verify-9", "build-tests", fileName,
            arguments.Split(' '), CompareToBaseline: true);
        Assert.True(ReviewTestReportPolicy.Missing(command, new ProcessResult(0, output, string.Empty)));
    }

    [Fact]
    public void Changed_test_setup_requires_a_report_even_for_an_unrecognized_command()
    {
        var command = new ReviewCommandDto("verify-9", "build-tests", "sh",
            ["-c", "./scripts/check.sh"], CompareToBaseline: true);
        var result = new ProcessResult(0, "custom check passed", string.Empty);
        Assert.True(ReviewTestReportPolicy.NeedsSetupCheck(command, result));
        Assert.False(ReviewTestReportPolicy.Missing(command, result));
        Assert.True(ReviewTestReportPolicy.Missing(command, result, setupChanged: true));
    }

    [Fact]
    public void Playwright_summary_is_a_parseable_report_when_the_spec_file_list_changed()
    {
        // AGT-4239: the product-start base suite ends with a Playwright run.
        var command = new ReviewCommandDto("verify-8", "build-tests", "sh",
            ["-lc", "bash scripts/base-product-start.sh"], CompareToBaseline: true);
        var passed = new ProcessResult(0, "Running 1 test using 1 worker\n\n  1 passed (24.1s)\n", string.Empty);
        Assert.False(ReviewTestReportPolicy.Missing(command, passed, setupChanged: true));
        Assert.False(ReviewTestReportPolicy.Missing(command, new ProcessResult(0,
            "\u001b[32m  1 passed\u001b[39m\u001b[2m (24.1s)\u001b[22m\n", string.Empty), setupChanged: true));
        Assert.True(ReviewTestReportPolicy.Missing(command,
            new ProcessResult(0, "  0 passed (0.1s)\n", string.Empty), setupChanged: true));
    }

    [Fact]
    public void Missing_vitest_file_report_path_is_named_in_diagnostic()
    {
        var result = new ProcessResult(2, string.Empty,
            "Vitest completed without a readable report at /tmp/review-vite/report.json: ENOENT");
        Assert.Equal("/tmp/review-vite/report.json",
            ReviewTestReportPolicy.MissingReportPath(result, "/review/artifacts/verify.stdout.log"));
        Assert.True(ReviewTestReportPolicy.HasExplicitMissingReport(result));
        Assert.Equal("/review/artifacts/verify.stdout.log",
            ReviewTestReportPolicy.MissingReportPath(new ProcessResult(0, "bundle complete", ""),
                "/review/artifacts/verify.stdout.log"));
        Assert.False(ReviewTestReportPolicy.HasExplicitMissingReport(
            new ProcessResult(1, "", "ordinary test crash")));
    }

    [Theory]
    [InlineData("M\tfrontend/vitest.config.ts", true)]
    [InlineData("A\tfrontend/src/new.spec.ts", true)]
    [InlineData("M\tfrontend/src/existing.spec.ts", false)]
    [InlineData("M\tfrontend/src/component.ts", false)]
    [InlineData("R100\tfrontend/src/old.spec.ts\tfrontend/src/helper.ts", true)]
    [InlineData("R100\tfrontend/src/helper.ts\tfrontend/src/new.spec.ts", true)]
    [InlineData("R100\tfrontend/vitest.config.ts\tfrontend/old-config.ts", true)]
    [InlineData("R100\tfrontend/src/old.ts\tfrontend/src/new.ts", false)]
    public void Setup_change_requires_configuration_or_spec_list_change(string line, bool expected)
        => Assert.Equal(expected, ReviewTestReportPolicy.SetupChangeReason([line]) is not null);

    [Fact]
    public void Renaming_a_spec_to_a_non_spec_cites_the_removed_spec()
        => Assert.Equal("spec file list changed: frontend/src/old.spec.ts",
            ReviewTestReportPolicy.SetupChangeReason(
                ["R100\tfrontend/src/old.spec.ts\tfrontend/src/helper.ts"]));
}
