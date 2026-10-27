using AgentStudio.Pipeline;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3310 acceptance on this repository: the folder-to-test-project map is
/// generated from the real <c>.csproj</c> graph and the real
/// <c>.agent-studio/project.yml</c>, then the per-card plan selects from it.
/// </summary>
public sealed class GeneratedTestProjectMapTests
{
    private const string BackendTests = "backend.Tests/OrchestratorApi.Tests.csproj";
    private const string RunnerTests = "runner.Tests/AgentRunner.Tests.csproj";
    private const string TaskServerTests = "task-server.Tests/TaskServer.Tests.csproj";
    private const string SetupTests = "setup.Tests/AgentOrchestratorSetup.Tests.csproj";
    private const string FrontendUnit = "bash scripts/npm-frontend.sh run test:ci";

    private static readonly string Root = RepositoryRoot.Find();

    [Fact]
    public void SetupOnlyDiffSelectsSetupTestsAndNotBackendTests()
    {
        var plan = Plan("setup/Program.cs");

        Assert.False(plan.Audit.FullSuiteRequired);
        Assert.Equal("generated-project-map", plan.Audit.Selector);
        var test = Assert.Single(Tests(plan));
        Assert.Equal(
            $"dotnet test \"{SetupTests}\" --no-build --filter Category!=MachineBound", test.Command);
        Assert.DoesNotContain(plan.Commands, command => command.Command.Contains(BackendTests));
        Assert.Contains(plan.Audit.OmittedTestCommands, command => command.Contains(BackendTests));
    }

    [Fact]
    public void RunnerDiffSelectsBackendRunnerAndTaskServerTests()
    {
        var plan = Plan("runner/Program.cs");

        Assert.False(plan.Audit.FullSuiteRequired);
        var tests = Tests(plan).Select(command => command.Command).ToList();
        // The declared commands are reused as written.
        Assert.Contains($"dotnet test {BackendTests} --no-build --filter Category!=MachineBound", tests);
        Assert.Contains($"dotnet test {RunnerTests} --no-build --filter Category!=MachineBound", tests);
        Assert.Contains($"dotnet test {TaskServerTests} --no-build --filter Category!=MachineBound", tests);
        // The engine tests reference runner/ as well; the graph selects them.
        Assert.Contains(tests, command => command.Contains("orchestrator-engine.Tests/OrchestratorEngine.Tests.csproj"));
        Assert.DoesNotContain(tests, command => command.Contains(SetupTests));
        Assert.DoesNotContain(FrontendUnit, tests);
    }

    [Theory]
    [InlineData("global.json", "global.json")]
    [InlineData("Directory.Build.props", "Directory.Build.*")]
    [InlineData(".agent-studio/project.yml", ".agent-studio/**")]
    [InlineData("testsupport/RepositoryRoot.cs", "testsupport/**")]
    public void GlobalInputRunsTheFullDeclaredSet(string path, string rule)
    {
        var verify = VerifyCommandPlanner.Plan(Root, null);
        var plan = Plan("setup/Program.cs", path);

        Assert.True(plan.Audit.FullSuiteRequired);
        Assert.Equal("deterministic-map-full-fallback", plan.Audit.Selector);
        Assert.Contains($"global input changed: {path} ({rule})", plan.Audit.Reasons);
        Assert.Equal(
            verify.Commands.Where(command => command.Kind == VerifyCommandKind.Test).Select(command => command.Command),
            Tests(plan).Select(command => command.Command));
    }

    [Fact]
    public void FrontendDiffSelectsFrontendUnitAndTheTypeCheck()
    {
        var plan = Plan("frontend/src/app/app.ts");

        Assert.False(plan.Audit.FullSuiteRequired);
        var test = Assert.Single(Tests(plan));
        Assert.Equal(FrontendUnit, test.Command);
        Assert.Contains(plan.Commands, command => command.Kind == VerifyCommandKind.Lint
            && command.Command == "bash scripts/npm-frontend.sh run typecheck");
        Assert.Contains(plan.Commands, command => command.Kind == VerifyCommandKind.Lint
            && command.Command == "bash scripts/npm-frontend.sh run lint");
        Assert.Contains("fast check format: frontend/ declares no 'format:check' script", plan.Audit.Reasons);
    }

    [Fact]
    public void ChangeAcrossModulesRunsTheUnionInsteadOfTheFullSuite()
    {
        var plan = Plan("setup/Program.cs", "frontend/src/app/app.ts");

        Assert.False(plan.Audit.FullSuiteRequired);
        Assert.Equal(
            [FrontendUnit, $"dotnet test \"{SetupTests}\" --no-build --filter Category!=MachineBound"],
            Tests(plan).Select(command => command.Command).Order(StringComparer.Ordinal));
        Assert.Contains(plan.Audit.Reasons, reason => reason.StartsWith("change spans modules:", StringComparison.Ordinal));
    }

    [Fact]
    public void UnmappedPathAndProjectGraphChangeRunTheFullSuite()
    {
        var unmapped = Plan("docs/start/README.md");
        Assert.True(unmapped.Audit.FullSuiteRequired);
        Assert.Contains("unmapped changed path: docs/start/README.md", unmapped.Audit.Reasons);

        var stale = Plan("setup/AgentOrchestratorSetup.csproj");
        Assert.True(stale.Audit.FullSuiteRequired);
        Assert.Contains(stale.Audit.Reasons, reason => reason.StartsWith("stale generated project map", StringComparison.Ordinal));

        var missing = PerCardVerifyPlanner.Plan(Root, VerifyCommandPlanner.Plan(Root, null), null, null, null, null);
        Assert.True(missing.Audit.FullSuiteRequired);
        Assert.Contains("changed-file input is unavailable or empty", missing.Audit.Reasons);
    }

    [Fact]
    public void MapDigestIsRecordedAndJoinsTheSelectionDigest()
    {
        var verify = VerifyCommandPlanner.Plan(Root, null);
        var map = GeneratedTestProjectMap.Generate(Root, verify);
        var plan = Plan("setup/Program.cs");

        Assert.Null(map.Failure);
        Assert.Equal(64, map.Digest.Length);
        Assert.Equal(map.Digest, GeneratedTestProjectMap.Generate(Root, verify).Digest);
        Assert.Equal(map.Digest, plan.Audit.ProjectMapDigest);
        var withoutMap = DeterministicTestScope.WithDigest(plan with { Audit = plan.Audit with { ProjectMapDigest = null } });
        Assert.NotEqual(plan.Audit.Digest, withoutMap.Audit.Digest);
        Assert.Contains(map.Rows, row => row.Folder == "frontend" && row.TestProjects.SequenceEqual(["frontend-unit"]));
        Assert.Contains(map.Rows, row => row.Folder == "setup" && row.TestProjects.SequenceEqual([SetupTests]));
    }

    private static StagedVerifyPlan Plan(params string[] changed)
        => PerCardVerifyPlanner.Plan(Root, VerifyCommandPlanner.Plan(Root, null), changed, null, null, null);

    private static IReadOnlyList<VerifyCommand> Tests(StagedVerifyPlan plan)
        => plan.Commands.Where(command => command.Kind == VerifyCommandKind.Test).ToList();
}
