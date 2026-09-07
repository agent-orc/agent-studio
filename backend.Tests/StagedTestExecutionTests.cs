using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TestSelectionPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "staged-test-planner-" + Guid.NewGuid().ToString("N"));

    public TestSelectionPlannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void WorkPackage_SelectsReferencedDotNetTestProjectAndPreservesMachineBoundFilter()
    {
        Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup><ProjectReference Include="../../src/App/App.csproj" /></ItemGroup>
            </Project>
            """);
        Write("tests/Other.Tests/Other.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
            </Project>
            """);
        var fullCommand = "dotnet test --filter Category!=MachineBound";
        var verify = new VerifyPlan([
            new(VerifyEcosystem.DotNet, VerifyCommandKind.Build, "", "dotnet build"),
            new(VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", fullCommand),
        ], VerifyPlan.SourceBuildProfile);

        var result = TestSelectionPlanner.Plan(
            _root, verify, ["src/App/Service.cs"], policy: null,
            TaskStates.AutoReview, requiredLevel: null);

        Assert.Equal(TestExecutionLevels.WorkPackage, result.Audit.Level);
        Assert.Contains(result.Commands, command =>
            command.Command == $"dotnet test \"tests/App.Tests/App.Tests.csproj\" --filter \"{VerifyCommandPlanner.DefaultDotNetTestFilter}\"");
        Assert.DoesNotContain(result.Commands, command => command.Command.Contains("Other.Tests"));
        Assert.DoesNotContain(result.Commands, command => command.Command == fullCommand);
        Assert.Contains(fullCommand, result.Audit.OmittedTestCommands);
        Assert.False(result.Audit.FullSuiteRan);
    }

    [Fact]
    public void WorkPackage_GeneratedDotNetCommandExcludesMachineBoundRunnerCancellationFamilyByDefault()
    {
        Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup><ProjectReference Include="../../src/App/App.csproj" /></ItemGroup>
            </Project>
            """);
        var verify = new VerifyPlan([
            new(VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", "dotnet test"),
        ], VerifyPlan.SourceAutoDiscovery);

        var result = TestSelectionPlanner.Plan(
            _root, verify, ["src/App/Service.cs"], policy: null,
            TaskStates.Completed, TestExecutionLevels.WorkPackage);

        Assert.Contains(result.Commands, command =>
            command.Command ==
            $"dotnet test \"tests/App.Tests/App.Tests.csproj\" --filter \"{VerifyCommandPlanner.DefaultDotNetTestFilter}\"");
        Assert.DoesNotContain(result.Commands, command => command.Command == "dotnet test");
    }

    [Fact]
    public void WorkPackage_FrontendDiffSelectsTouchedFolderAndCollisionHotspotsWithoutFullSuite()
    {
        Write("frontend/package.json", """
            {
              "scripts": {
                "test": "ng test",
                "test:ci": "ng test frontend --watch=false --progress=false"
              }
            }
            """);
        Write("frontend/src/app/app.spec.ts", "// app barrel collision probe");
        Write("frontend/src/app/features/studio-shell/studio-shell.component.spec.ts", "// shell collision probe");
        Write("frontend/src/app/features/task-detail/task-detail.spec.ts", "// task-detail barrel probe");
        Write("frontend/src/app/features/project-detail/components/project-git-panel/project-git-panel.component.ts", "// changed");
        Write("frontend/src/app/features/project-detail/components/project-git-panel/project-git-panel.component.spec.ts", "// touched spec");
        Write("frontend/src/app/features/project-detail/components/project-git-panel/project-git-panel.model.spec.ts", "// touched spec");
        Write("frontend/src/app/features/project-detail/components/unrelated/unrelated.component.spec.ts", "// omitted");
        var fullDotNet = "dotnet test --filter Category!=MachineBound";
        var fullFrontend = "npm run test:ci";
        var profileFullFrontend = "npm --prefix frontend run test:ci";
        var verify = new VerifyPlan([
            new(VerifyEcosystem.DotNet, VerifyCommandKind.Build, "", "dotnet build"),
            new(VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", fullDotNet),
            new(VerifyEcosystem.Node, VerifyCommandKind.Test, "frontend", fullFrontend),
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", profileFullFrontend),
        ], VerifyPlan.SourceBuildProfile);

        var result = TestSelectionPlanner.Plan(
            _root,
            verify,
            ["frontend/src/app/features/project-detail/components/project-git-panel/project-git-panel.component.ts"],
            new TestExecutionPolicy
            {
                ContinuousCommands = [profileFullFrontend, "test-smoke"],
                ImpactRules =
                [
                    new TestImpactRule
                    {
                        PathPrefixes = ["frontend/"],
                        TestCommands = [profileFullFrontend],
                        Reason = "frontend impact",
                    },
                ],
            },
            TaskStates.Completed,
            TestExecutionLevels.WorkPackage);

        var frontend = Assert.Single(result.Commands, command =>
            command.Ecosystem == VerifyEcosystem.Node && command.Kind == VerifyCommandKind.Test);
        Assert.Equal("frontend", frontend.WorkingSubdir);
        Assert.Equal(TestExecutionLevels.WorkPackage, frontend.TestScope);
        Assert.True(frontend.BlocksWorkPackage);
        Assert.Contains("project-git-panel/*.spec.ts", frontend.Command);
        Assert.Contains("src/app/app.spec.ts", frontend.Command);
        Assert.Contains("studio-shell.component.spec.ts", frontend.Command);
        Assert.Contains("features/task-detail/task-detail.spec.ts", frontend.Command);
        Assert.DoesNotContain("unrelated.component.spec.ts", frontend.Command);
        Assert.DoesNotContain(result.Commands, command => command.Command == fullFrontend);
        Assert.DoesNotContain(result.Commands, command => command.Command == profileFullFrontend);
        Assert.DoesNotContain(result.Commands, command => command.Command == fullDotNet);
        Assert.Contains(result.Commands, command => command.Command == "test-smoke");
        Assert.DoesNotContain(result.Audit.Candidates,
            candidate => candidate.Command.Command == fullFrontend
                || candidate.Command.Command == profileFullFrontend);
        Assert.Contains(result.Audit.OmittedTestCommands, command => command.Contains(fullFrontend));
        Assert.Contains(result.Audit.OmittedTestCommands, command => command.Contains(profileFullFrontend));
        Assert.Contains(fullDotNet, result.Audit.OmittedTestCommands);
        Assert.False(result.Audit.FullSuiteRan);
    }

    [Fact]
    public void WorkPackage_NonFrontendDiffDoesNotAddFrontendCollisionSet()
    {
        Write("frontend/package.json", """
            { "scripts": { "test:ci": "ng test frontend --watch=false --progress=false" } }
            """);
        Write("frontend/src/app/app.spec.ts", "// collision probe");
        var verify = new VerifyPlan([
            new(VerifyEcosystem.Node, VerifyCommandKind.Test, "frontend", "npm run test:ci"),
        ], VerifyPlan.SourceBuildProfile);

        var result = TestSelectionPlanner.Plan(
            _root, verify, ["backend/Features/Pipeline/Worker.cs"], policy: null,
            TaskStates.Completed, TestExecutionLevels.WorkPackage);

        Assert.Empty(result.Commands);
        Assert.Empty(result.Audit.SelectedCommands);
    }

    [Theory]
    [InlineData(
        "frontend/src/app/features/project-detail/components/project-git-panel/project-git-panel.component.ts",
        "src/app/features/project-detail/components/project-git-panel/*.spec.ts")]
    [InlineData(
        "frontend/src/app/features/task-detail/components/concept-dossier-notice/concept-dossier-notice.component.ts",
        "src/app/features/task-detail/components/concept-dossier-notice/*.spec.ts")]
    [InlineData(
        "frontend/src/app/features/project-detail/components/workbench-overview/workbench-overview.component.ts",
        "src/app/features/project-detail/components/workbench-overview/*.spec.ts")]
    public void PromotionIncidentRetro_SelectsEachFailingComponentFolder(
        string changedPath,
        string expectedInclude)
    {
        Write("frontend/package.json", """
            { "scripts": { "test:ci": "ng test frontend --watch=false --progress=false" } }
            """);
        Write("frontend/src/app/app.spec.ts", "// app barrel collision probe");
        Write("frontend/src/app/features/studio-shell/studio-shell.component.spec.ts", "// shell collision probe");
        Write("frontend/src/app/features/task-detail/task-detail.spec.ts", "// task-detail barrel probe");
        var componentSpec = expectedInclude.Replace("/*.spec.ts", "/fixture.component.spec.ts");
        Write("frontend/" + componentSpec, "// affected folder spec");
        var verify = new VerifyPlan([
            new(VerifyEcosystem.Node, VerifyCommandKind.Test, "frontend", "npm run test:ci"),
        ], VerifyPlan.SourceBuildProfile);

        var result = TestSelectionPlanner.Plan(
            _root, verify, [changedPath], policy: null,
            TaskStates.Completed, TestExecutionLevels.WorkPackage);

        var command = Assert.Single(result.Commands);
        Assert.Contains(expectedInclude, command.Command);
        Assert.Contains("src/app/app.spec.ts", command.Command);
        Assert.Contains("studio-shell.component.spec.ts", command.Command);
        Assert.Contains("features/task-detail/task-detail.spec.ts", command.Command);
    }

    [Fact]
    public void PromotionIncidentRetro_StudioShellCycleSelectsTheCompleteHistoricalWorkPackage()
    {
        Write("frontend/package.json", """
            { "scripts": { "test:ci": "ng test frontend --watch=false --progress=false" } }
            """);
        Write("frontend/src/app/app.spec.ts", "// app barrel collision probe");
        Write("frontend/src/app/features/studio-shell/studio-shell.component.spec.ts", "// shell collision probe");
        Write("frontend/src/app/features/task-detail/task-detail.spec.ts", "// task-detail barrel probe");
        Write("frontend/src/app/features/studio-shell/services/studio-route.spec.ts", "// touched service spec");
        Write("frontend/src/app/features/task-detail/components/concept-dossier-notice/concept-dossier-notice.component.spec.ts", "// touched component spec");
        Write("frontend/src/app/services/project-identity.util.spec.ts", "// touched shared-service folder spec");
        var verify = new VerifyPlan([
            new(VerifyEcosystem.Node, VerifyCommandKind.Test, "frontend", "npm run test:ci"),
        ], VerifyPlan.SourceBuildProfile);

        var result = TestSelectionPlanner.Plan(
            _root,
            verify,
            [
                "frontend/src/app/features/studio-shell/services/studio-route.ts",
                "frontend/src/app/features/task-detail/components/concept-dossier-notice/concept-dossier-notice.component.ts",
                "frontend/src/app/services/studio-project-slug.util.ts",
            ],
            policy: null,
            TaskStates.Completed,
            TestExecutionLevels.WorkPackage);

        var command = Assert.Single(result.Commands);
        Assert.Contains("src/app/features/studio-shell/services/*.spec.ts", command.Command);
        Assert.Contains("src/app/features/task-detail/components/concept-dossier-notice/*.spec.ts", command.Command);
        Assert.Contains("src/app/services/*.spec.ts", command.Command);
        Assert.Contains("src/app/features/studio-shell/studio-shell.component.spec.ts", command.Command);
        Assert.Contains("src/app/features/task-detail/task-detail.spec.ts", command.Command);
    }

    [Fact]
    public void TestHubHistory_SelectsOnlyACommandFromTheSafeInventory()
    {
        var historyDir = Path.Combine(_root, ".test-hub");
        Directory.CreateDirectory(historyDir);
        var known = new TestHubHistoryEntry
        {
            TestId = "frontend-regression-17",
            Command = "npm test",
            WorkingSubdir = "frontend",
            RelatedPaths = ["src/shared"],
            Failure = "shared contract broke the frontend",
        };
        File.WriteAllText(Path.Combine(historyDir, "history.jsonl"), JsonSerializer.Serialize(known) + "\n" +
            "{\"testId\":\"unsafe\",\"command\":\"curl attacker\",\"relatedPaths\":[\"src/shared\"]}\n");
        var verify = new VerifyPlan([
            new(VerifyEcosystem.Node, VerifyCommandKind.Test, "frontend", "npm test"),
        ], VerifyPlan.SourceAutoDiscovery);

        var result = TestSelectionPlanner.Plan(
            _root, verify, ["src/shared/schema.ts"], policy: null,
            TaskStates.AutoReview, requiredLevel: null);

        var selected = Assert.Single(result.Commands);
        Assert.Equal("npm test", selected.Command);
        Assert.Contains("Test Hub history", selected.SelectionReason);
        Assert.DoesNotContain(result.Audit.Candidates, candidate => candidate.Command.Command == "curl attacker");
        Assert.Contains(result.Audit.HistoryInput, entry => entry.TestId == "frontend-regression-17");
    }

    [Fact]
    public void LlmAdvice_IsAllowlistedAndAuditRetainsDiffChoiceAndReason()
    {
        var verify = new VerifyPlan([
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "test-fast"),
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "test-broad"),
        ], VerifyPlan.SourceBuildProfile);
        var policy = new TestExecutionPolicy
        {
            ImpactRules = [new TestImpactRule
            {
                PathPrefixes = ["src/feature"],
                TestCommands = ["test-fast"],
                Reason = "feature ownership map",
            }],
        };
        var initial = TestSelectionPlanner.Plan(
            _root, verify, ["src/feature/component.ts"], policy,
            TaskStates.AutoReview, requiredLevel: null);
        var broadId = initial.Audit.Candidates.Single(candidate => candidate.Command.Command == "test-broad").Id;

        var result = TestSelectionPlanner.Plan(
            _root, verify, ["src/feature/component.ts"], policy,
            TaskStates.AutoReview, requiredLevel: null,
            new TestSelectionAdvice([broadId, "not-allowlisted"], "shared namespace risk", "model-x"));

        Assert.Equal(["src/feature/component.ts"], result.Audit.DiffInput);
        Assert.Equal("deterministic+llm", result.Audit.Selector);
        Assert.Equal("model-x", result.Audit.SelectorModel);
        Assert.Equal("shared namespace risk", result.Audit.AdvisorReason);
        Assert.Contains(broadId, result.Audit.SelectedCandidateIds);
        Assert.DoesNotContain("not-allowlisted", result.Audit.SelectedCandidateIds);
        Assert.Contains(result.Commands, command =>
            command.Command == "test-broad" && command.SelectionReason!.Contains("shared namespace risk"));
    }

    [Fact]
    public void RequiredFull_OverridesLaneAndIncludesBaselinePlusEveryDeclaredTest()
    {
        var verify = new VerifyPlan([
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "test-a"),
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "test-b"),
        ], VerifyPlan.SourceBuildProfile);
        var policy = new TestExecutionPolicy
        {
            LaneLevels = new() { [TaskStates.AutoReview] = TestExecutionLevels.Continuous },
            ContinuousCommands = ["test-smoke"],
        };

        var result = TestSelectionPlanner.Plan(
            _root, verify, ["src/file.cs"], policy,
            TaskStates.AutoReview, TestExecutionLevels.Full);

        Assert.Equal(TestExecutionLevels.Full, result.Audit.Level);
        Assert.True(result.Audit.FullSuiteRequired);
        Assert.False(result.Audit.FullSuiteRan);
        Assert.Equal(["test-smoke", "test-a", "test-b"],
            result.Commands.Select(command => command.Command));
        Assert.All(result.Commands, command => Assert.True(command.BlocksWorkPackage));
    }

    [Fact]
    public void SelectedNodeTest_IsNotRemovedByLegacyPackageDiffFilter()
    {
        var selectedFromHistoryOrLlm = new VerifyCommand(
            VerifyEcosystem.Node,
            VerifyCommandKind.Test,
            "frontend",
            "npm test")
        {
            TestScope = TestExecutionLevels.WorkPackage,
            SelectionReason = "Test Hub history selected a cross-package regression",
        };

        Assert.True(BuildTestGateRunner.ShouldRunForChange(
            selectedFromHistoryOrLlm,
            ["backend/Shared/Contract.cs"]));
    }

    [Fact]
    public void BaselineCommand_SelectedForDiff_IsBlockingAndRunsOnlyOnce()
    {
        var verify = new VerifyPlan([
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "test-fast"),
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "test-other"),
        ], VerifyPlan.SourceBuildProfile);
        var policy = new TestExecutionPolicy
        {
            ContinuousCommands = ["test-fast"],
            ImpactRules = [new TestImpactRule
            {
                PathPrefixes = ["src/feature"],
                TestCommands = ["test-fast"],
                Reason = "feature regression set",
            }],
        };

        var result = TestSelectionPlanner.Plan(
            _root, verify, ["src/feature/component.cs"], policy,
            TaskStates.AutoReview, requiredLevel: null);

        var selected = Assert.Single(result.Commands);
        Assert.Equal("test-fast", selected.Command);
        Assert.True(selected.BlocksWorkPackage);
        Assert.Equal(TestExecutionLevels.WorkPackage, selected.TestScope);
        Assert.Contains("feature regression set", selected.SelectionReason);
    }

    [Fact]
    public void LaneLevelResolution_IsCaseInsensitiveAfterSettingsDeserialization()
    {
        var policy = new TestExecutionPolicy
        {
            LaneLevels = new Dictionary<string, string>
            {
                ["4-AUTO-REVIEW"] = TestExecutionLevels.Continuous,
            },
        };

        var level = TestSelectionPlanner.ResolveLevel(
            policy, TaskStates.AutoReview, requiredLevel: null);

        Assert.Equal(TestExecutionLevels.Continuous, level);
    }

    private void Write(string relativePath, string contents)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}

public sealed class PreMainTestGateTests
{
    [Fact]
    public async Task RunAsync_ForcesFullFailClosedGateWithoutDiffReduction()
    {
        var runner = new CapturingGateRunner();
        var gate = new PreMainTestGate(runner);
        var request = new BuildTestGateRequest(
            "/repo", "abc", "release", RequireExactSubject: false)
        {
            Lane = TaskStates.Ready,
            RequiredTestLevel = TestExecutionLevels.Continuous,
        };

        var result = await gate.RunAsync(
            request, new BuildProfile(), TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.NotNull(runner.Request);
        Assert.Equal(TestExecutionLevels.Full, runner.Request!.RequiredTestLevel);
        Assert.True(runner.Request.RequireExactSubject);
        Assert.Null(runner.ChangedFiles);
        Assert.Equal(PostStepMode.Fail, runner.Mode);
        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
    }

    [Fact]
    public async Task RunAsync_RejectsGreenRunnerResultWithoutFullSuiteEvidence()
    {
        var runner = new CapturingGateRunner
        {
            Result = new BuildTestGateResult(
                BuildTestGateVerdict.Ok, 0, 1, "", "claimed green", false, false),
        };
        var gate = new PreMainTestGate(runner);

        var result = await gate.RunAsync(
            new BuildTestGateRequest("/repo", "abc", "release"),
            new BuildProfile(),
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Code, result.FailureKind);
        Assert.Contains("mandatory full-suite evidence is missing", result.Reason);
    }

    [Fact]
    public async Task PreDevelopRunAsync_FrontendDiffForcesExactBlockingWorkPackage()
    {
        var runner = new CapturingGateRunner();
        var gate = new PreDevelopBuildGate(runner);
        var changedFiles = new[]
        {
            "frontend/src/app/features/project-detail/components/workbench-overview/workbench-overview.component.ts",
        };

        await gate.RunAsync(
            new BuildTestGateRequest("/repo", "abc", "develop"),
            changedFiles,
            new BuildProfile { BuildCmds = ["build"] },
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.NotNull(runner.Request);
        Assert.True(runner.Request!.RequireExactSubject);
        Assert.Equal(TestExecutionLevels.WorkPackage, runner.Request.RequiredTestLevel);
        Assert.Equal(changedFiles, runner.ChangedFiles);
        Assert.Equal(PostStepMode.Fail, runner.Mode);
    }

    [Fact]
    public async Task PreDevelopRunAsync_NonFrontendDiffStaysBuildOnly()
    {
        var runner = new CapturingGateRunner();
        var gate = new PreDevelopBuildGate(runner);
        var changedFiles = new[] { "backend/Features/Pipeline/Worker.cs" };

        await gate.RunAsync(
            new BuildTestGateRequest("/repo", "abc", "develop"),
            changedFiles,
            new BuildProfile { BuildCmds = ["build"] },
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.Equal(TestExecutionLevels.BuildOnly, runner.Request!.RequiredTestLevel);
        Assert.Equal(changedFiles, runner.ChangedFiles);
    }

    private sealed class CapturingGateRunner : IBuildTestGateRunner
    {
        public BuildTestGateResult Result { get; init; } = new(
            BuildTestGateVerdict.Ok, 0, 1, "", "ok", false, false)
        {
            TestSelection = new TestSelectionAudit
            {
                Level = TestExecutionLevels.Full,
                FullSuiteRequired = true,
                FullSuiteRan = true,
            },
        };
        public BuildTestGateRequest? Request { get; private set; }
        public IReadOnlyList<string>? ChangedFiles { get; private set; }
        public PostStepMode Mode { get; private set; }

        public Task<BuildTestGateResult> RunAsync(
            BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles,
            BuildProfile? profile,
            PostStepMode mode,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Request = request;
            ChangedFiles = changedFiles;
            Mode = mode;
            return Task.FromResult(Result);
        }
    }
}

[Trait("Category", "MachineBound")]
public sealed class StagedBuildTestGateBehaviorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "staged-test-runner-" + Guid.NewGuid().ToString("N"));
    private readonly BuildTestGateRunner _runner = new(NullLogger<BuildTestGateRunner>.Instance);

    public StagedBuildTestGateBehaviorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task FailingContinuousTestBecomesSeparateFindingAndDoesNotBlockWorkPackage()
    {
        var marker = Path.Combine(_root, "work-package-ran.txt");
        var writeMarker = $"touch \"{marker}\"";
        var policy = new TestExecutionPolicy
        {
            ContinuousCommands = ["exit 9"],
            ImpactRules = [new TestImpactRule
            {
                PathPrefixes = ["src"],
                TestCommands = [writeMarker],
            }],
        };
        var request = new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false)
        {
            TestExecution = policy,
            Lane = TaskStates.AutoReview,
        };

        var result = await _runner.RunAsync(
            request, ["src/component.cs"],
            new BuildProfile { TestCmds = ["exit 0"] },
            PostStepMode.Fail, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Warn, result.Verdict);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("out-of-work-package-test-failure", finding.Kind);
        Assert.Equal(TestExecutionLevels.Continuous, finding.Scope);
        Assert.True(File.Exists(marker), "the selected work-package command must continue after the unrelated failure");
        Assert.Contains("full-suite=not-run", result.Reason);
    }

    [Fact]
    public async Task ContinuousBaselineStillRunsForDocumentationOnlyDiff()
    {
        var marker = Path.Combine(_root, "continuous-ran.txt");
        var writeMarker = $"touch \"{marker}\"";
        var request = new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false)
        {
            TestExecution = new TestExecutionPolicy
            {
                ContinuousCommands = [writeMarker],
            },
            Lane = TaskStates.AutoReview,
        };

        var result = await _runner.RunAsync(
            request, ["docs/contract.md"],
            new BuildProfile { TestCmds = ["exit 0"] },
            PostStepMode.Fail, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.True(File.Exists(marker));
        Assert.Equal(TestExecutionLevels.WorkPackage, result.TestSelection!.Level);
        Assert.Contains(writeMarker, result.TestSelection.SelectedCommands);
        Assert.Contains("full-suite=not-run", result.Reason);
    }

    [Fact]
    public async Task RequiredFullSuiteCannotBeSkippedByDocumentationOnlyDiff()
    {
        var marker = Path.Combine(_root, "full-ran.txt");
        var writeMarker = $"touch \"{marker}\"";
        var request = new BuildTestGateRequest(_root, null, "release", RequireExactSubject: false)
        {
            RequiredTestLevel = TestExecutionLevels.Full,
        };

        var result = await _runner.RunAsync(
            request, ["docs/release.md"],
            new BuildProfile { TestCmds = [writeMarker] },
            PostStepMode.Fail, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.True(File.Exists(marker));
        Assert.True(result.TestSelection!.FullSuiteRequired);
        Assert.True(result.TestSelection.FullSuiteRan);
        Assert.Contains("full-suite=required-and-run", result.Reason);
    }

    [Fact]
    public async Task IsolatedWorkPackageRunsMeasurablyFasterThanFullSuite()
    {
        var shortWait = WaitCommand(80);
        var longWait = WaitCommand(650);
        var policy = new TestExecutionPolicy
        {
            ContinuousCommands = [shortWait],
            ImpactRules = [new TestImpactRule
            {
                PathPrefixes = ["src/isolated"],
                TestCommands = [shortWait],
            }],
        };
        var profile = new BuildProfile { TestCmds = [shortWait, longWait] };
        var request = new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false)
        {
            TestExecution = policy,
            Lane = TaskStates.AutoReview,
        };

        var workPackage = await _runner.RunAsync(
            request, ["src/isolated/file.cs"], profile,
            PostStepMode.Fail, TimeSpan.FromSeconds(10), CancellationToken.None);
        var full = await _runner.RunAsync(
            request with { RequiredTestLevel = TestExecutionLevels.Full }, null, profile,
            PostStepMode.Fail, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Ok, workPackage.Verdict);
        Assert.Equal(BuildTestGateVerdict.Ok, full.Verdict);
        Assert.True(full.DurationMs - workPackage.DurationMs >= 350,
            $"expected isolated subset to save at least 350 ms, work={workPackage.DurationMs} full={full.DurationMs}");
        Assert.False(workPackage.TestSelection!.FullSuiteRan);
        Assert.True(full.TestSelection!.FullSuiteRan);
    }

    [Fact]
    public async Task FullSuiteEvidence_RemainsFalseWhenBuildStopsBeforeTests()
    {
        var testMarker = Path.Combine(_root, "full-test-ran.txt");
        var writeMarker = $"touch \"{testMarker}\"";
        var request = new BuildTestGateRequest(_root, null, "release", RequireExactSubject: false)
        {
            RequiredTestLevel = TestExecutionLevels.Full,
        };

        var result = await _runner.RunAsync(
            request,
            changedFiles: null,
            new BuildProfile
            {
                BuildCmds = ["exit 7"],
                TestCmds = [writeMarker],
            },
            PostStepMode.Fail,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.True(result.TestSelection!.FullSuiteRequired);
        Assert.False(result.TestSelection.FullSuiteRan);
        Assert.Contains(writeMarker, result.TestSelection.OmittedTestCommands);
        Assert.False(File.Exists(testMarker));
        Assert.Contains("full-suite=not-run", result.Reason);
    }

    private static string WaitCommand(int milliseconds)
        => FormattableString.Invariant($"sleep {milliseconds / 1000d:0.000}");
}
