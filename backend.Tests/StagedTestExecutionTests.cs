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

    /// <summary>
    /// AGT-2839: the compile-only stage keeps the build commands and drops both
    /// the test and the lint commands, and both omissions stay in the audit.
    /// </summary>
    [Fact]
    public void CompileOnly_KeepsBuildCommandsAndOmitsBothTestsAndLint()
    {
        var verify = new VerifyPlan([
            new(VerifyEcosystem.DotNet, VerifyCommandKind.Build, "", "dotnet build"),
            new(VerifyEcosystem.Node, VerifyCommandKind.Lint, "frontend", "npm run lint"),
            new(VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", "dotnet test"),
        ], VerifyPlan.SourceBuildProfile);

        var result = TestSelectionPlanner.Plan(
            _root, verify, ["backend/Features/Pipeline/Worker.cs"], policy: null,
            TaskStates.Completed, TestExecutionLevels.CompileOnly);

        Assert.Equal(TestExecutionLevels.CompileOnly, result.Audit.Level);
        Assert.Equal(["dotnet build"], result.Commands.Select(command => command.Command));
        Assert.Contains("dotnet test", result.Audit.OmittedTestCommands.Single(
            entry => entry.Contains("dotnet test", StringComparison.Ordinal)));
        Assert.Contains("npm run lint", result.Audit.OmittedTestCommands.Single(
            entry => entry.Contains("npm run lint", StringComparison.Ordinal)));
        Assert.False(result.Audit.FullSuiteRan);
        Assert.False(result.Audit.FullSuiteRequired);
    }

    /// <summary>
    /// AGT-2839: the build-only stage is unchanged - it still runs lint. Only a
    /// reused remote review verdict drops it.
    /// </summary>
    [Fact]
    public void BuildOnly_StillRunsLint()
    {
        var verify = new VerifyPlan([
            new(VerifyEcosystem.DotNet, VerifyCommandKind.Build, "", "dotnet build"),
            new(VerifyEcosystem.Node, VerifyCommandKind.Lint, "frontend", "npm run lint"),
            new(VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", "dotnet test"),
        ], VerifyPlan.SourceBuildProfile);

        var result = TestSelectionPlanner.Plan(
            _root, verify, ["backend/Features/Pipeline/Worker.cs"], policy: null,
            TaskStates.Completed, TestExecutionLevels.BuildOnly);

        Assert.Equal(["dotnet build", "npm run lint"], result.Commands.Select(command => command.Command));
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
            command.Command == "dotnet test \"tests/App.Tests/App.Tests.csproj\" --filter Category!=MachineBound");
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
            "dotnet test \"tests/App.Tests/App.Tests.csproj\" --filter Category!=MachineBound");
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

    /// <summary>
    /// AGT-2854 regression: AGT-2853 landed a Windows-only red test through the
    /// pre-develop gate because a backend-only diff was pinned to build-only. The
    /// changed test file must produce a filtered command that names the classes
    /// it declares, so the merge result runs them on the gate host.
    /// </summary>
    [Fact]
    public void PreDevelopWorkPackage_ChangedTestFileSelectsItsOwnTestClasses()
    {
        BackendRepository();
        Write("backend.Tests/GateFlakyRerunTests.cs", """
            namespace AgentStudio.Tests;
            public sealed class GateFlakyRerunPolicyTests { }
            public sealed class GateFlakyRerunBehaviorTests : IDisposable { }
            public sealed class GateFlakyRerunReceiptsTests : IDisposable { }
            """);
        var verify = BackendVerifyPlan();
        string[] changedFiles = ["backend.Tests/GateFlakyRerunTests.cs"];

        var level = PreDevelopBuildGate.ResolveTestLevel(changedFiles);
        var result = TestSelectionPlanner.Plan(
            _root, verify, changedFiles, policy: null, TaskStates.Completed, level);

        Assert.Equal(TestExecutionLevels.WorkPackage, level);
        var command = Assert.Single(
            result.Commands,
            item => item.Command.StartsWith("dotnet test", StringComparison.Ordinal));
        Assert.Equal(
            "dotnet test \"backend.Tests/OrchestratorApi.Tests.csproj\" --filter " +
            "\"(Category!=MachineBound)&(FullyQualifiedName~GateFlakyRerunBehaviorTests" +
            "|FullyQualifiedName~GateFlakyRerunPolicyTests" +
            "|FullyQualifiedName~GateFlakyRerunReceiptsTests)\"",
            command.Command);
        Assert.Equal(
            ["GateFlakyRerunBehaviorTests", "GateFlakyRerunPolicyTests", "GateFlakyRerunReceiptsTests"],
            result.Audit.SelectedTestClasses);
        Assert.Contains(result.Audit.Reasons, reason =>
            reason.Contains("work-package test classes", StringComparison.Ordinal)
            && reason.Contains("GateFlakyRerunBehaviorTests", StringComparison.Ordinal));
        Assert.Contains(result.Audit.Candidates, candidate =>
            candidate.TestClasses.Contains("GateFlakyRerunBehaviorTests")
            && candidate.Reasons.Any(reason =>
                reason.Contains("selected from the changed files", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The composed class filter must not cost the AGT-2853 targeted re-run: its
    /// own <c>&amp;</c> and <c>|</c> operators are filter syntax, not shell
    /// structure, so the red command stays targetable.
    /// </summary>
    [Fact]
    public void PreDevelopWorkPackage_ClassFilteredCommandStaysTargetableByTheFlakyRerun()
    {
        BackendRepository();
        Write("backend.Tests/GateFlakyRerunTests.cs", """
            namespace AgentStudio.Tests;
            public sealed class GateFlakyRerunBehaviorTests { }
            """);
        var verify = BackendVerifyPlan();
        string[] changedFiles = ["backend.Tests/GateFlakyRerunTests.cs"];

        var result = TestSelectionPlanner.Plan(
            _root, verify, changedFiles, policy: null,
            TaskStates.Completed, PreDevelopBuildGate.ResolveTestLevel(changedFiles));
        var command = Assert.Single(
            result.Commands,
            item => item.Command.StartsWith("dotnet test", StringComparison.Ordinal));
        var decision = GateFlakyRerunPolicy.Decide(
            VerifyCommandKind.Test,
            BuildTestGateFailureKind.Code,
            command.Command,
            "  Failed AgentStudio.Tests.GateFlakyRerunBehaviorTests.Reruns [1 ms]",
            TimeSpan.FromMinutes(10));

        Assert.True(decision.ShouldRerun);
        Assert.DoesNotContain("MachineBound", decision.Command);
        Assert.Contains(
            "FullyQualifiedName=AgentStudio.Tests.GateFlakyRerunBehaviorTests.Reruns",
            decision.Command);
    }

    [Fact]
    public void PreDevelopWorkPackage_BackendProductionDiffKeepsTheWholeImpactedTestProject()
    {
        BackendRepository();
        Write("backend.Tests/GateFlakyRerunTests.cs", "public sealed class GateFlakyRerunBehaviorTests { }");
        var verify = BackendVerifyPlan();
        string[] changedFiles = ["backend/Features/Pipeline/PreDevelopBuildGate.cs"];

        var result = TestSelectionPlanner.Plan(
            _root, verify, changedFiles, policy: null,
            TaskStates.Completed, PreDevelopBuildGate.ResolveTestLevel(changedFiles));

        // No convention maps a production type to its covering test classes, so
        // the bounded slice must not pretend to cover the project.
        Assert.Contains(result.Commands, command =>
            command.Command ==
            "dotnet test \"backend.Tests/OrchestratorApi.Tests.csproj\" --filter Category!=MachineBound");
        Assert.Empty(result.Audit.SelectedTestClasses);
    }

    [Fact]
    public void PreDevelopWorkPackage_ChangedSharedFixtureKeepsTheWholeTestProject()
    {
        BackendRepository();
        Write("backend.Tests/Fixtures/TempRepository.cs", "internal sealed class TempRepository { }");
        var verify = BackendVerifyPlan();
        string[] changedFiles = ["backend.Tests/Fixtures/TempRepository.cs"];

        var result = TestSelectionPlanner.Plan(
            _root, verify, changedFiles, policy: null,
            TaskStates.Completed, PreDevelopBuildGate.ResolveTestLevel(changedFiles));

        Assert.Contains(result.Commands, command =>
            command.Command ==
            "dotnet test \"backend.Tests/OrchestratorApi.Tests.csproj\" --filter Category!=MachineBound");
        Assert.Empty(result.Audit.SelectedTestClasses);
    }

    [Fact]
    public void PreDevelopWorkPackage_ChangedTestFileWithASharedBaseTypeKeepsTheWholeTestProject()
    {
        BackendRepository();
        Write("backend.Tests/GateFlakyRerunTests.cs", """
            namespace AgentStudio.Tests;
            public abstract class GateFixtureBase { }
            public sealed class GateFlakyRerunBehaviorTests : GateFixtureBase { }
            """);
        var verify = BackendVerifyPlan();
        string[] changedFiles = ["backend.Tests/GateFlakyRerunTests.cs"];

        var result = TestSelectionPlanner.Plan(
            _root, verify, changedFiles, policy: null,
            TaskStates.Completed, PreDevelopBuildGate.ResolveTestLevel(changedFiles));

        // A base type can carry tests in other files, so the slice must not
        // narrow away from the whole project here.
        Assert.Contains(result.Commands, command =>
            command.Command ==
            "dotnet test \"backend.Tests/OrchestratorApi.Tests.csproj\" --filter Category!=MachineBound");
        Assert.Empty(result.Audit.SelectedTestClasses);
    }

    [Fact]
    public void PreDevelopWorkPackage_ChangedTestFileAlsoSelectsItsDirectorySiblings()
    {
        BackendRepository();
        Write("backend.Tests/Projection/AlphaProjectionTests.cs", "public sealed class AlphaProjectionTests { }");
        Write("backend.Tests/Projection/BetaProjectionTests.cs", "public sealed class BetaProjectionTests { }");
        Write("backend.Tests/UnrelatedTests.cs", "public sealed class UnrelatedTests { }");
        var verify = BackendVerifyPlan();
        string[] changedFiles = ["backend.Tests/Projection/AlphaProjectionTests.cs"];

        var result = TestSelectionPlanner.Plan(
            _root, verify, changedFiles, policy: null,
            TaskStates.Completed, PreDevelopBuildGate.ResolveTestLevel(changedFiles));

        Assert.Equal(["AlphaProjectionTests", "BetaProjectionTests"], result.Audit.SelectedTestClasses);
        Assert.DoesNotContain(result.Commands, command => command.Command.Contains("UnrelatedTests"));
    }

    [Fact]
    public void PreDevelopWorkPackage_FrontendOnlyDiffSelectsNoDotNetTestCommand()
    {
        BackendRepository();
        Write("backend.Tests/GateFlakyRerunTests.cs", "public sealed class GateFlakyRerunBehaviorTests { }");
        Write("frontend/package.json", """
            { "scripts": { "test:ci": "ng test frontend --watch=false --progress=false" } }
            """);
        Write("frontend/src/app/app.spec.ts", "// app barrel collision probe");
        Write("frontend/src/app/features/board/board.component.ts", "// changed");
        Write("frontend/src/app/features/board/board.component.spec.ts", "// touched spec");
        var verify = BackendVerifyPlan();
        string[] changedFiles = ["frontend/src/app/features/board/board.component.ts"];

        var result = TestSelectionPlanner.Plan(
            _root, verify, changedFiles, policy: null,
            TaskStates.Completed, PreDevelopBuildGate.ResolveTestLevel(changedFiles));

        Assert.DoesNotContain(result.Commands, command =>
            command.Kind == VerifyCommandKind.Test
            && command.Command.Contains("dotnet test", StringComparison.Ordinal));
        Assert.Contains(result.Commands, command =>
            command.Command.Contains("src/app/features/board/*.spec.ts", StringComparison.Ordinal));
        // The declared frontend suite is replaced by the bounded include slice,
        // and the lints keep running as non-test commands.
        Assert.Contains("npm --prefix frontend run test:ci", result.Audit.OmittedTestCommands);
        Assert.Contains(result.Commands, command =>
            command.Command == "npm --prefix frontend run lint");
    }

    [Fact]
    public void PreDevelopWorkPackage_MixedDiffSelectsBothStacks()
    {
        BackendRepository();
        Write("backend.Tests/GateFlakyRerunTests.cs", "public sealed class GateFlakyRerunBehaviorTests { }");
        Write("frontend/package.json", """
            { "scripts": { "test:ci": "ng test frontend --watch=false --progress=false" } }
            """);
        Write("frontend/src/app/app.spec.ts", "// app barrel collision probe");
        Write("frontend/src/app/features/board/board.component.ts", "// changed");
        Write("frontend/src/app/features/board/board.component.spec.ts", "// touched spec");
        var verify = BackendVerifyPlan();
        string[] changedFiles =
        [
            "backend.Tests/GateFlakyRerunTests.cs",
            "frontend/src/app/features/board/board.component.ts",
        ];

        var result = TestSelectionPlanner.Plan(
            _root, verify, changedFiles, policy: null,
            TaskStates.Completed, PreDevelopBuildGate.ResolveTestLevel(changedFiles));

        Assert.Contains(result.Commands, command =>
            command.Command.Contains("FullyQualifiedName~GateFlakyRerunBehaviorTests", StringComparison.Ordinal));
        Assert.Contains(result.Commands, command =>
            command.Command.Contains("src/app/features/board/*.spec.ts", StringComparison.Ordinal));
    }

    [Fact]
    public void PreDevelopBuildOnly_DocsOnlyDiffKeepsTheCompileOnlyStage()
    {
        BackendRepository();
        Write("backend.Tests/GateFlakyRerunTests.cs", "public sealed class GateFlakyRerunBehaviorTests { }");
        var verify = BackendVerifyPlan();
        string[] changedFiles = ["docs/system/domains/pipeline.md", "README.md"];

        var level = PreDevelopBuildGate.ResolveTestLevel(changedFiles);
        var result = TestSelectionPlanner.Plan(
            _root, verify, changedFiles, policy: null, TaskStates.Completed, level);

        Assert.Equal(TestExecutionLevels.BuildOnly, level);
        Assert.DoesNotContain(result.Commands, command => command.Kind == VerifyCommandKind.Test);
        Assert.Equal("build-only", result.Audit.Selector);
    }

    /// <summary>
    /// The Studio layout: one production project, one flat test project, and the
    /// declared project.yml commands the gate derives its filter from.
    /// </summary>
    private void BackendRepository()
    {
        Write("backend/OrchestratorApi.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("backend.Tests/OrchestratorApi.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup><ProjectReference Include="../backend/OrchestratorApi.csproj" /></ItemGroup>
            </Project>
            """);
    }

    private static VerifyPlan BackendVerifyPlan()
        => new([
            new(VerifyEcosystem.Custom, VerifyCommandKind.Build, "", "dotnet build agent-taskboard.sln --no-restore"),
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "",
                "dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter Category!=MachineBound"),
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "npm --prefix frontend run test:ci"),
            new(VerifyEcosystem.Custom, VerifyCommandKind.Lint, "", "npm --prefix frontend run lint"),
        ], VerifyPlan.SourceProjectDefinition);

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

    /// <summary>
    /// AGT-2839: only the merge runner's reuse decision selects compile-only;
    /// the ordinary pre-develop levels stay on <c>ResolveTestLevel</c>'s matrix,
    /// including the AGT-2854 work package for managed sources.
    /// </summary>
    [Fact]
    public void PreDevelopLevel_DropsToCompileOnlyOnlyWhenTheReviewVerdictIsReused()
    {
        string[] frontend = ["frontend/src/app/app.ts"];
        string[] backend = ["backend/Features/Pipeline/Worker.cs"];
        string[] docs = ["docs/system/domains/pipeline.md"];

        Assert.Equal(
            TestExecutionLevels.CompileOnly,
            PreDevelopBuildGate.LevelFor(frontend, reuseRemoteReviewVerdict: true));
        Assert.Equal(
            TestExecutionLevels.CompileOnly,
            PreDevelopBuildGate.LevelFor(backend, reuseRemoteReviewVerdict: true));
        Assert.Equal(
            TestExecutionLevels.CompileOnly,
            PreDevelopBuildGate.LevelFor(docs, reuseRemoteReviewVerdict: true));
        Assert.Equal(
            TestExecutionLevels.WorkPackage,
            PreDevelopBuildGate.LevelFor(frontend, reuseRemoteReviewVerdict: false));
        Assert.Equal(
            TestExecutionLevels.WorkPackage,
            PreDevelopBuildGate.LevelFor(backend, reuseRemoteReviewVerdict: false));
        Assert.Equal(
            TestExecutionLevels.BuildOnly,
            PreDevelopBuildGate.LevelFor(docs, reuseRemoteReviewVerdict: false));
    }

    /// <summary>
    /// AGT-2854: a backend-only delivery used to reach <c>develop</c> with the
    /// compile-only stage, so a Windows-only red test was never executed before
    /// the merge. Managed sources now force the same blocking work package the
    /// frontend already had.
    /// </summary>
    [Fact]
    public async Task PreDevelopRunAsync_BackendDiffForcesExactBlockingWorkPackage()
    {
        var runner = new CapturingGateRunner();
        var gate = new PreDevelopBuildGate(runner);
        var changedFiles = new[] { "backend.Tests/GateFlakyRerunTests.cs" };

        await gate.RunAsync(
            new BuildTestGateRequest("/repo", "abc", "develop"),
            changedFiles,
            new BuildProfile { BuildCmds = ["build"] },
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.True(runner.Request!.RequireExactSubject);
        Assert.Equal(TestExecutionLevels.WorkPackage, runner.Request.RequiredTestLevel);
        Assert.Equal(changedFiles, runner.ChangedFiles);
        Assert.Equal(PostStepMode.Fail, runner.Mode);
    }

    [Fact]
    public async Task PreDevelopRunAsync_DocsOnlyDiffStaysBuildOnly()
    {
        var runner = new CapturingGateRunner();
        var gate = new PreDevelopBuildGate(runner);
        var changedFiles = new[] { "docs/system/domains/pipeline.md" };

        await gate.RunAsync(
            new BuildTestGateRequest("/repo", "abc", "develop"),
            changedFiles,
            new BuildProfile { BuildCmds = ["build"] },
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.Equal(TestExecutionLevels.BuildOnly, runner.Request!.RequiredTestLevel);
        Assert.Equal(changedFiles, runner.ChangedFiles);
    }

    /// <summary>
    /// The documented pre-develop matrix: backend-only, frontend-only, both, and
    /// neither. Direct matrix test of the pure level policy.
    /// </summary>
    [Theory]
    [InlineData(TestExecutionLevels.WorkPackage, "backend/Features/Pipeline/PreDevelopBuildGate.cs")]
    [InlineData(TestExecutionLevels.WorkPackage, "backend.Tests/GateFlakyRerunTests.cs")]
    [InlineData(TestExecutionLevels.WorkPackage, "backend/OrchestratorApi.csproj")]
    [InlineData(TestExecutionLevels.WorkPackage, "frontend/src/app/app.component.ts")]
    [InlineData(TestExecutionLevels.WorkPackage,
        "backend.Tests/GateFlakyRerunTests.cs", "frontend/src/app/app.component.ts")]
    [InlineData(TestExecutionLevels.BuildOnly, "docs/system/domains/pipeline.md", "README.md")]
    [InlineData(TestExecutionLevels.BuildOnly, "scripts/release.sh")]
    public void PreDevelopResolveTestLevel_MatchesTheDocumentedMatrix(
        string expected,
        params string[] changedFiles)
        => Assert.Equal(expected, PreDevelopBuildGate.ResolveTestLevel(changedFiles));

    [Fact]
    public void PreDevelopResolveTestLevel_UnavailableDiffNeverBecomesBuildOnly()
        => Assert.Equal(
            TestExecutionLevels.WorkPackage, PreDevelopBuildGate.ResolveTestLevel(null));

    [Theory]
    [InlineData(true, "backend/Features/Pipeline/PreDevelopBuildGate.cs")]
    [InlineData(true, "frontend/src/app/app.component.ts")]
    [InlineData(false, "docs/system/domains/pipeline.md")]
    public void PreDevelopAppliesTo_CoversBothWorkPackageStacksWithoutABuildProfile(
        bool expected,
        string changedFile)
        => Assert.Equal(expected, PreDevelopBuildGate.AppliesTo(new BuildProfile(), [changedFile]));

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
