using AgentStudio.ExecutionPreparation;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3428: one rule for a repository without verify commands, applied by the
/// review plan, the integration decision, the merge failure typing, the gate
/// failure router, the coding-run notice, and the onboarding proposal.
/// </summary>
public sealed class VerifyDefinitionPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "verify-definition-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly string _folder;

    public VerifyDefinitionPolicyTests()
    {
        _repo = Path.Combine(_root, "repo");
        _folder = Path.Combine(_root, "task");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_repo, "README.txt"), "no build system here\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    // ---- pure rule ----

    [Theory]
    [InlineData(VerifyPlan.SourceNone, false, VerifyDefinitionState.Missing)]
    [InlineData(VerifyPlan.SourceProjectDefinition, false, VerifyDefinitionState.DeclaredEmpty)]
    [InlineData(VerifyPlan.SourceProjectDefinition, true, VerifyDefinitionState.Derived)]
    [InlineData(VerifyPlan.SourceAutoDiscovery, true, VerifyDefinitionState.Derived)]
    [InlineData(VerifyPlan.SourceBuildProfile, true, VerifyDefinitionState.Derived)]
    public void Classify_Matrix(string source, bool hasCommand, VerifyDefinitionState expected)
    {
        IReadOnlyList<VerifyCommand> commands = hasCommand
            ? [new VerifyCommand(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "make test")]
            : [];
        Assert.Equal(expected, VerifyDefinitionPolicy.Classify(new VerifyPlan(commands, source)));
    }

    [Theory]
    [InlineData(BuildTestGateVerdict.Ok, "", true)]
    [InlineData(BuildTestGateVerdict.NotApplicable, VerifyDefinitionPolicy.DeclaredEmptyReason, true)]
    [InlineData(BuildTestGateVerdict.NotApplicable, VerifyDefinitionPolicy.NoVerifyCommandsReason, false)]
    [InlineData(BuildTestGateVerdict.NotApplicable, "anything else", false)]
    [InlineData(BuildTestGateVerdict.Fail, "tests failed", false)]
    [InlineData(BuildTestGateVerdict.Skipped, "mode=off", false)]
    public void AdmitsMerge_Matrix(BuildTestGateVerdict verdict, string reason, bool admits)
    {
        var gate = new BuildTestGateResult(verdict, null, 0, string.Empty, reason, false, false);
        Assert.Equal(admits, VerifyDefinitionPolicy.AdmitsMerge(gate));
        Assert.Equal(verdict == BuildTestGateVerdict.NotApplicable && !admits,
            VerifyDefinitionPolicy.IsMissingDefinition(gate));
    }

    [Theory]
    [InlineData("go.mod", "go")]
    [InlineData("pyproject.toml", "python")]
    [InlineData("requirements.txt", "python")]
    [InlineData("Cargo.toml", "rust")]
    [InlineData("pom.xml", "java")]
    [InlineData("Makefile", "make")]
    [InlineData("src/App.csproj", "dotnet")]
    [InlineData("web/package.json", "node")]
    [InlineData(null, "custom")]
    public void ExampleFor_DetectsStackAndProducesAValidDefinition(string? marker, string stack)
    {
        if (marker is not null)
        {
            var path = Path.Combine(_repo, marker);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, marker.EndsWith(".json", StringComparison.Ordinal) ? "{}" : "x\n");
        }

        var example = VerifyDefinitionPolicy.ExampleFor(_repo);
        Assert.Equal(stack, example.Stack);
        Assert.NotEmpty(example.Test);

        // The example validates and yields a non-empty verify plan.
        var studio = Path.Combine(_repo, ".agent-studio");
        Directory.CreateDirectory(studio);
        File.WriteAllText(Path.Combine(studio, "project.yml"), example.Definition);
        File.WriteAllText(Path.Combine(studio, "prepare"), example.PrepareScript);
        var read = Contract.ProjectDefinitionReader.ReadWorkspace(_repo);
        Assert.True(read.IsValid, string.Join("; ", read.Issues.Select(issue => issue.Message)));
        Assert.Equal(VerifyDefinitionState.Derived, VerifyDefinitionPolicy.Inspect(_repo, null).State);
    }

    [Fact]
    public void Inspect_RepositoryWithoutDefinition_NamesTheFileAndShowsAnExample()
    {
        File.WriteAllText(Path.Combine(_repo, "go.mod"), "module example.com/x\n");

        var report = VerifyDefinitionPolicy.Inspect(_repo, null);

        Assert.True(report.IsMissing);
        Assert.Equal(VerifyPlan.SourceNone, report.PlanSource);
        Assert.StartsWith(VerifyDefinitionPolicy.MessagePrefix, report.Message);
        Assert.Contains("`.agent-studio/project.yml` is missing", report.Message);
        Assert.Contains("`.agent-studio/prepare`", report.Message);
        Assert.Contains("```yaml", report.Message);
        Assert.Contains("go test ./...", report.Message);
    }

    [Fact]
    public void Inspect_InvalidDefinition_SaysWhyItIsInvalid()
    {
        WriteDefinition("schemaVersion: 1\nstack: [custom]\ncommands:\n  prepare: .agent-studio/prepare\n  test: [make test]\n",
            prepare: false);

        var report = VerifyDefinitionPolicy.Inspect(_repo, null);

        Assert.True(report.IsMissing);
        Assert.Contains("is invalid (commands.prepare", report.Message);
    }

    [Fact]
    public void Inspect_DeclaredEmptyDefinition_IsNotMissing()
    {
        WriteDefinition("schemaVersion: 1\nstack: [custom]\ncommands:\n  prepare: .agent-studio/prepare\n"
                        + "  build: []\n  test: []\n  lint: []\n");

        var report = VerifyDefinitionPolicy.Inspect(_repo, null);

        Assert.Equal(VerifyDefinitionState.DeclaredEmpty, report.State);
        Assert.Null(report.Message);
        Assert.Equal(VerifyDefinitionPolicy.DeclaredEmptyReason,
            VerifyDefinitionPolicy.NotApplicableReason(VerifyCommandPlanner.Plan(_repo, null)));
    }

    // ---- review plan ----

    [Fact]
    public void ReviewPlan_RepositoryWithoutVerifyCommands_StatesItAtReviewStart()
    {
        var plan = Plan(["main.go"]);

        Assert.StartsWith(VerifyDefinitionPolicy.MessagePrefix, plan.VerifyDefinitionMissing);
        var skipped = Assert.Single(plan.SkippedAspects!, item => item.Aspect == "build-tests");
        Assert.Equal(VerifyDefinitionPolicy.Summary, skipped.Reason);
    }

    [Theory]
    [InlineData(".agent-studio/project.yml")]
    [InlineData("docs/notes.md")]
    public void ReviewPlan_DeliveryAddingTheDefinitionOrDocsOnly_IsNotFlagged(string changed)
        => Assert.Null(Plan([changed]).VerifyDefinitionMissing);

    [Fact]
    public void ReviewPlan_DeclaredEmptyDefinition_IsNotFlagged()
    {
        WriteDefinition("schemaVersion: 1\nstack: [custom]\ncommands:\n  prepare: .agent-studio/prepare\n"
                        + "  build: []\n  test: []\n  lint: []\n");

        Assert.Null(Plan(["main.go"]).VerifyDefinitionMissing);
    }

    [Fact]
    public void ReviewPlan_WithVerifyCommands_IsNotFlagged()
    {
        WriteDefinition("schemaVersion: 1\nstack: [custom]\ncommands:\n  prepare: .agent-studio/prepare\n"
                        + "  test:\n    - make test\n");

        var plan = Plan(["main.go"]);

        Assert.Null(plan.VerifyDefinitionMissing);
        Assert.Contains(plan.Commands, command => command.Aspect == "build-tests");
    }

    [Fact]
    public void IntegrationDecision_FlaggedPlan_RefusesWithTheMessageInsteadOfIntegrating()
    {
        var plan = Plan(["main.go"]);

        var decision = RemoteDeliveryIntegrationPolicy.Decide(true, "Pass", plan, []);

        Assert.False(decision.ShouldIntegrate);
        Assert.True(decision.VerifyDefinitionMissing);
        Assert.Equal(RemoteBuildTestGateClass.NotApplicable, decision.BuildTestGate);
        Assert.Equal(plan.VerifyDefinitionMissing, decision.Reason);
    }

    [Fact]
    public void IntegrationDecision_UnflaggedEmptyPlan_StillIntegrates()
    {
        var plan = new Contract.ReviewPlanDto(
            [new Contract.ReviewCommandDto("verify-subject", "completion", "git", ["rev-parse", "HEAD"])],
            ["completion"]);

        var decision = RemoteDeliveryIntegrationPolicy.Decide(true, "Pass", plan, []);

        Assert.True(decision.ShouldIntegrate);
        Assert.False(decision.VerifyDefinitionMissing);
    }

    // ---- merge failure typing and router ----

    [Theory]
    [InlineData(AcceptedIntegrationFailureCodes.BuildGateFailed,
        "Pre-main full suite blocked the merge: no verify commands derivable")]
    [InlineData(AcceptedIntegrationFailureCodes.DeliveryGateFailed,
        VerifyDefinitionPolicy.MessagePrefix + " `.agent-studio/project.yml` is missing")]
    [InlineData(AcceptedIntegrationFailureCodes.VerifyDefinitionMissing, "anything")]
    public void FailurePolicy_MissingDefinition_IsTypedAndNotRoutedAsARedGate(string persisted, string reason)
    {
        var failure = AcceptedIntegrationFailurePolicy.Classify(
            PipelineStepStatus.Failed, "gate-failed", reason, null, persisted);

        Assert.Equal(AcceptedIntegrationFailureCodes.VerifyDefinitionMissing, failure!.Code);
        Assert.Contains(".agent-studio/project.yml", failure.Reason);
        // A legacy reason without the message gets the generic example.
        if (!reason.Contains(VerifyDefinitionPolicy.MessagePrefix, StringComparison.Ordinal))
            Assert.Contains("```yaml", failure.Reason);
        Assert.False(GateFailureRouter.Handles(new TaskIntegrationStatus
        {
            Failure = new TaskIntegrationFailure { Code = failure.Code, Reason = failure.Reason },
        }));
    }

    [Fact]
    public void FailurePolicy_RealRedGate_StaysABuildGateFailure()
    {
        var failure = AcceptedIntegrationFailurePolicy.Classify(
            PipelineStepStatus.Failed, "gate-failed", "Pre-main full suite blocked the merge: 3 tests failed",
            null, AcceptedIntegrationFailureCodes.BuildGateFailed);

        Assert.Equal(AcceptedIntegrationFailureCodes.BuildGateFailed, failure!.Code);
        Assert.True(GateFailureRouter.Handles(new TaskIntegrationStatus
        {
            Failure = new TaskIntegrationFailure { Code = failure.Code },
        }));
    }

    // ---- coding run and onboarding ----

    [Fact]
    public void CodingRunNotice_NamesTheFileAndCarriesTheExample()
    {
        File.WriteAllText(Path.Combine(_repo, "go.mod"), "module example.com/x\n");

        var notice = PromptEnrichmentService.VerifyDefinitionNotice(Task(), _repo, null);

        Assert.NotNull(notice);
        Assert.StartsWith($"### No verify commands defined (`{VerifyDefinitionPolicy.EnrichmentBlockId}`)", notice);
        Assert.Contains(".agent-studio/project.yml", notice);
        Assert.Contains("go test ./...", notice);
        Assert.Contains("## Prompt enrichment", PromptEnrichmentService.RenderLaunchContext(
            new IntakeEnrichmentManifest(), notice));
    }

    [Fact]
    public void CodingRunNotice_AbsentWhenCommandsExistOrModeIsNotCoding()
    {
        Assert.Null(PromptEnrichmentService.VerifyDefinitionNotice(Task() with { Mode = TaskModes.Concept }, _repo, null));
        WriteDefinition("schemaVersion: 1\nstack: [custom]\ncommands:\n  prepare: .agent-studio/prepare\n"
                        + "  test:\n    - make test\n");
        Assert.Null(PromptEnrichmentService.VerifyDefinitionNotice(Task(), _repo, null));
    }

    [Fact]
    public void OnboardingProposal_NeverDeclaresAnEmptyDefinitionByAccident()
    {
        var proposal = ProjectDefinitionGenerator.Generate(_repo);

        Assert.Contains("./scripts/test.sh", proposal.Definition);
    }

    private Contract.ReviewPlanDto Plan(IReadOnlyList<string> changed)
        => Builder().Build(Task(), _repo, new ProjectSettings(), "develop", new string('b', 40), changed,
            "refs/heads/agent-studio/results/task");

    private TaskInfo Task() => new()
    {
        Id = "task", TaskKey = "AOW-15", ProjectName = "project",
        WatchPath = _repo, FolderPath = _folder, State = TaskStates.AutoReview, Mode = TaskModes.Coding,
    };

    private void WriteDefinition(string yaml, bool prepare = true)
    {
        var studio = Path.Combine(_repo, ".agent-studio");
        Directory.CreateDirectory(studio);
        File.WriteAllText(Path.Combine(studio, "project.yml"), yaml);
        if (prepare) File.WriteAllText(Path.Combine(studio, "prepare"), "#!/bin/sh\nset -eu\n");
    }

    private static RemoteReviewPlanBuilder Builder()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        return new RemoteReviewPlanBuilder(aspects, config, git, new FakeTimeProvider());
    }
}
