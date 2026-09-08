using AgentStudio.TestSupport.Scenario;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// The `inproc` target of the deployment regression scenario (AGT-2739): boots
/// the distributed Task Server / Runner topology as sibling processes with a
/// fake CLI, drives the ordered steps in
/// <c>testsupport/scenario/steps.json</c>, and writes a JUnit + Markdown report.
/// Invoked by <c>scripts/scenario.sh --target inproc --level smoke|full</c>.
/// </summary>
[Trait("Category", "MachineBound")]
[Trait("Category", "ReviewFlaky")]
public sealed class ScenarioTests
{
    [Fact(Timeout = 170_000)]
    public Task Deployment_regression_scenario_smoke() => RunAsync("smoke");

    [Fact(Timeout = 300_000)]
    public Task Deployment_regression_scenario_full() => RunAsync("full");

    private static async Task RunAsync(string level)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = ProtocolTests.RepositoryRoot();
        var manifest = ScenarioManifest.Load(Path.Combine(root, "testsupport", "scenario", "steps.json"));
        var fixture = ScenarioFixture.Load(Path.Combine(root, "testsupport", "scenario", "fixture.json"));
        var steps = manifest.StepsFor(level);

        using var context = await ScenarioContext.CreateAsync(root, fixture);
        var result = await ScenarioRunner.RunAsync(steps, level, step => context.ExecuteAsync(step.Id));

        var reportDirectory = Environment.GetEnvironmentVariable("SCENARIO_REPORT_DIR")
            ?? Path.Combine(root, "artifacts", "scenario-reports");
        var junitPath = ScenarioReportWriter.WriteJUnit(result, "inproc", reportDirectory);
        var markdownPath = ScenarioReportWriter.WriteMarkdown(result, "inproc", reportDirectory);

        Assert.True(
            result.Success,
            $"Deployment regression scenario ({level}) failed. Report: {markdownPath} / {junitPath}" +
            string.Concat(result.Steps
                .Where(step => step.Status != ScenarioStepStatus.Passed)
                .Select(step => $"{Environment.NewLine}- {step.Id}: {step.Status}{(step.FailureMessage is null ? "" : $" - {step.FailureMessage.Split(Environment.NewLine)[0]}")}")));
    }
}
