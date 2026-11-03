using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3428: a repository with no verify commands. The real gate runs on the
/// exact subject and returns NotApplicable; the merge path applies the one rule
/// instead of treating that verdict as a red gate.
/// </summary>
public sealed partial class MergeIntoDevelopRunnerTests
{
    [Fact]
    public async Task RunAsync_MainTarget_WithoutVerifyDefinition_RefusesWithTypedCodeAndExample()
    {
        var repo = SeedRepo("runner-main-no-definition");
        RunGit(repo, "checkout -q -b task/61");
        File.WriteAllText(Path.Combine(repo, "go.mod"), "module example.com/fixture\n");
        File.WriteAllText(Path.Combine(repo, "main.go"), "package main\n");
        Commit(repo, "feat: unverifiable work");
        var mainBefore = RunGit(repo, "rev-parse main").Out.Trim();
        RunGit(repo, "checkout -q main");

        var (git, log, settings) = BuildWithSettings(repo);
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(HermeticGateRunner()),
            preMainTimeout: TimeSpan.FromSeconds(30));
        var jobFolder = BeginRun(log, repo, jobId: "61");

        var outcome = await runner.RunAsync("Fixture", "61", jobFolder, repo, "main", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Error, outcome.Outcome);
        Assert.Equal(AcceptedIntegrationFailureCodes.VerifyDefinitionMissing, outcome.FailureCode);
        Assert.StartsWith(VerifyDefinitionPolicy.MessagePrefix, outcome.Error);
        Assert.Contains(ProjectPreparationPaths.Definition, outcome.Error);
        Assert.Contains("```yaml", outcome.Error);
        Assert.DoesNotContain("Pre-main full suite blocked", outcome.Error);
        Assert.Equal(mainBefore, RunGit(repo, "rev-parse main").Out.Trim());

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
        Assert.Equal(AcceptedIntegrationFailureCodes.VerifyDefinitionMissing, step.FailureCode);
        var failure = AcceptedIntegrationFailurePolicy.Classify(
            step.Status, step.Verdict, step.Reason, step.VerdictSummary, step.FailureCode);
        Assert.Equal(AcceptedIntegrationFailureCodes.VerifyDefinitionMissing, failure!.Code);
        Assert.False(GateFailureRouter.Handles(new TaskIntegrationStatus
        {
            Failure = new TaskIntegrationFailure { Code = failure.Code, Reason = failure.Reason },
        }));
    }

    [Fact]
    public async Task RunAsync_MainTarget_DeclaredEmptyDefinition_MergesOnNotApplicableGate()
    {
        var repo = SeedRepo("runner-main-declared-empty");
        RunGit(repo, "checkout -q -b task/62");
        Directory.CreateDirectory(Path.Combine(repo, ".agent-studio"));
        File.WriteAllText(Path.Combine(repo, ".agent-studio", "project.yml"),
            "schemaVersion: 1\nstack: [custom]\ncommands:\n  prepare: .agent-studio/prepare\n"
            + "  build: []\n  test: []\n  lint: []\n");
        File.WriteAllText(Path.Combine(repo, ".agent-studio", "prepare"), "#!/bin/sh\nset -eu\n");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "declared without checks");
        Commit(repo, "feat: declared without checks");
        var taskSha = RunGit(repo, "rev-parse task/62").Out.Trim();
        RunGit(repo, "checkout -q main");

        var (git, log, settings) = BuildWithSettings(repo);
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.NotApplicable, null, 0, string.Empty,
            VerifyDefinitionPolicy.DeclaredEmptyReason, false, false));
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner));
        var jobFolder = BeginRun(log, repo, jobId: "62");

        var outcome = await runner.RunAsync("Fixture", "62", jobFolder, repo, "main", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.Equal(taskSha, Lane(repo, "main"));
        Assert.Equal(1, gateRunner.Invocations);
    }

    [Fact]
    public async Task RunAsync_MainTarget_LegacyNotApplicableGate_IsNeverABuildGateFailure()
    {
        var repo = SeedRepo("runner-main-legacy-na");
        RunGit(repo, "checkout -q -b task/63");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "work");
        Commit(repo, "feat: work");
        RunGit(repo, "checkout -q main");

        var (git, log, settings) = BuildWithSettings(repo);
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.NotApplicable, null, 0, string.Empty,
            VerifyDefinitionPolicy.NoVerifyCommandsReason, false, false));
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner));
        var jobFolder = BeginRun(log, repo, jobId: "63");

        var outcome = await runner.RunAsync("Fixture", "63", jobFolder, repo, "main", CancellationToken.None);

        Assert.Equal(AcceptedIntegrationFailureCodes.VerifyDefinitionMissing, outcome.FailureCode);
        Assert.Contains("stack: [custom]", outcome.Error);
    }
}
