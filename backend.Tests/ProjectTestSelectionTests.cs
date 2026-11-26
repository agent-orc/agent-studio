using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: fixed evidence timestamps are never compared with the current date.

public sealed class ProjectTestSelectionTests : IDisposable
{
    private const string LegacyDefinition = """
        schemaVersion: 1
        stack: [custom]
        commands:
          prepare: .agent-studio/prepare
          build: [echo build]
          test: [echo test]
          lint: []
        """;

    private readonly string Root = Path.Combine(Path.GetTempPath(), "selection-plan-" + Guid.NewGuid().ToString("N"));

    public ProjectTestSelectionTests()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".agent-studio"));
        File.Copy(Path.Combine(RepositoryRoot.Find(), "testdata/project-test-selection.project.yml"),
            Path.Combine(Root, ".agent-studio/project.yml"));
        foreach (var path in new[] { ".agent-studio/prepare", ".nvmrc", "global.json",
                     "backend/packages.lock.json", "frontend/package-lock.json" })
        {
            var target = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(RepositoryRoot.Find(), path), target);
        }
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
    private ProjectExecutionDefinition Definition => ProjectDefinitionReader.ReadWorkspace(Root).Definition!;

    [Theory]
    [InlineData("frontend/src/app/app.ts", "frontend-unit")]
    [InlineData("docs/system/domains/pipeline.md", "docs-check")]
    [InlineData("backend/Features/Pipeline/Worker.cs", "backend-routine")]
    public void SingleAreaSelectsOnlyItsSuiteAndCommands(string path, string suite)
    {
        var result = Plan(path);
        Assert.Equal([suite], result.Audit.SelectedSuites);
        Assert.False(result.Audit.FullSuiteRequired);
        Assert.Equal(Definition.TestSuites.Single(item => item.Id == suite).Command,
            Assert.Single(result.Commands.Where(command => command.Kind == VerifyCommandKind.Test)).Command);
        if (suite == "frontend-unit")
        {
            Assert.Equal([Definition.Commands.Build[2]], result.Commands.Where(command => command.Kind == VerifyCommandKind.Build)
                .Select(command => command.Command));
            Assert.Equal(Definition.Commands.Lint, result.Commands.Where(command => command.Kind == VerifyCommandKind.Lint)
                .Select(command => command.Command));
            Assert.DoesNotContain(result.Commands, command => command.Command.Contains("dotnet"));
        }
        if (suite == "docs-check") Assert.Single(result.Commands);
        if (suite == "backend-routine")
            Assert.DoesNotContain(result.Commands, command => command.Command.Contains("npm-frontend"));
        Assert.Equal(4, result.Audit.OmittedSuites.Count);
    }

    [Theory]
    [InlineData("contracts/TaskServer.Contracts/ReviewContracts.cs")]
    [InlineData(".agent-studio/project.yml")]
    [InlineData("Directory.Build.props")]
    [InlineData("new-source/Program.cs")]
    [InlineData("backend/OrchestratorApi.csproj")]
    public void BackendPlusSharedOrUnknownInputSelectsEverything(string path)
    {
        var result = Plan("backend/X.cs", path);
        Assert.True(result.Audit.FullSuiteRequired);
        Assert.Equal(VerifyCommandPlanner.Plan(Root, null).Commands.Select(command => command.Command),
            result.Commands.Select(command => command.Command));
        Assert.Empty(result.Audit.OmittedSuites);
    }

    [Theory]
    [InlineData("backend/Build.Common.props")]
    [InlineData("frontend/Build.Common.targets")]
    [InlineData("backend/build.proj")]
    [InlineData("frontend/affected.slnf")]
    public void ImportedBuildConfigurationSelectsAllSuitesEvenInsideAnAffectedArea(string path)
    {
        var result = Plan(path);
        Assert.True(result.Audit.FullSuiteRequired);
        Assert.Empty(result.Audit.OmittedSuites);
        Assert.Equal(VerifyCommandPlanner.Plan(Root, null).Commands.Select(command => command.Command),
            result.Commands.Select(command => command.Command));
        Assert.Contains(result.Audit.Reasons, reason => reason.Contains("Global build or project input changed"));
    }

    [Fact]
    public void MixedBatchIsTheUnionOfMemberRequirements()
    {
        var members = new[] { "backend/A.cs", "frontend/src/app/app.ts", "docs/a.md" };
        var union = members.SelectMany(path => Plan(path).Commands).Select(command => command.Command)
            .Distinct().Order().ToArray();
        Assert.Equal(union, Plan(members).Commands.Select(command => command.Command).Order().ToArray());
        Assert.Equal(["docs-check", "backend-routine", "frontend-unit"], Plan(members).Audit.SelectedSuites);
    }

    [Fact]
    public void EveryReviewStrategyUsesTheSameDeclaredSuites()
    {
        var verify = VerifyCommandPlanner.Plan(Root, null);
        var expected = Plan("frontend/src/app/app.ts").Commands.Select(command => command.Command).ToArray();
        var review = AgentStudio.Runner.V1ReviewPlaneEndpoints.FallbackPlan(Root, null, "develop",
            ["frontend/src/app/app.ts"]);
        Assert.Equal(expected, review.Commands.Select(command => command.Arguments.Last()));
        Assert.Equal(["frontend-unit"], review.SuiteSelection!.SelectedSuites);
        Assert.Contains("backend-routine", review.SuiteSelection.OmittedSuites);
        var perCard = PerCardVerifyPlanner.Plan(Root, verify, ["frontend/src/app/app.ts"], null, null, null);
        Assert.Equal(expected, perCard.Commands.Select(command => command.Command));
    }

    [Fact]
    public void AffectedGreenDoesNotBecomeAFullSuiteBaseVerdict()
    {
        var audit = Plan("frontend/src/app/app.ts").Audit;
        Assert.False(SuiteSelectionEvidence.SupportsFullBaseVerdict(audit));
        Assert.True(SuiteSelectionEvidence.SupportsFullBaseVerdict(audit with { FullSuiteRan = true }));
        Assert.True(SuiteSelectionEvidence.SupportsFullBaseVerdict(null));
    }

    [Fact]
    public void FullAndCompileStagesKeepTheirCoverageContracts()
    {
        var verify = VerifyCommandPlanner.Plan(Root, null);
        var full = DeterministicTestScope.Plan(Root, verify, ["docs/a.md"], null, null, "release", TestExecutionLevels.Full);
        Assert.True(full.Audit.FullSuiteRequired);
        Assert.Equal(verify.Commands.Select(command => command.Command), full.Commands.Select(command => command.Command));
        var compile = DeterministicTestScope.Plan(Root, verify, ["frontend/src/app/app.ts"], null, null,
            "integration", TestExecutionLevels.CompileOnly);
        Assert.Equal([Definition.Commands.Build[2]], compile.Commands.Select(command => command.Command));
        Assert.Contains("frontend-unit", compile.Audit.OmittedSuites);
    }

    [Theory]
    [InlineData("frontend/src/app/app.ts")]
    [InlineData("docs/a.md")]
    public void AdmissionPrecheckNeverBuildsOrTestsBackendForFrontendOrDocs(string path)
    {
        var precheck = DeterministicTestScope.Plan(Root, VerifyCommandPlanner.Plan(Root, null),
            [path], null, null, "pre-check", TestExecutionLevels.BatchPreCheck);
        Assert.DoesNotContain(precheck.Commands, command => command.Command.Contains("dotnet", StringComparison.Ordinal));
        Assert.DoesNotContain(precheck.PreparationCommands!, command => command.Contains("dotnet", StringComparison.Ordinal));
        if (path.StartsWith("docs/", StringComparison.Ordinal))
            Assert.Equal("node scripts/check-public-docs.mjs", Assert.Single(precheck.Commands).Command);
    }

    [Fact]
    public void IncompleteInventoryAndMissingDiffFailClosed()
    {
        var definition = Definition;
        var changed = definition with { Commands = definition.Commands with { Test = [.. definition.Commands.Test, "new test command"] } };
        Assert.True(ProjectTestSelectionPolicy.Select(changed, ["docs/a.md"]).Full);
        Assert.True(ProjectTestSelectionPolicy.Select(definition, null).Full);
        Assert.True(ProjectTestSelectionPolicy.Select(definition, []).Full);
    }

    [Fact]
    public void RestartReceiptRetainsSuiteCoverage()
    {
        var folder = Path.Combine(Path.GetTempPath(), "selection-receipt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sha = new string('a', 40);
            var audit = Plan("docs/a.md").Audit with { AttemptedSuites = ["docs-check"] };
            IntegrationGateReceipts.Record(folder, "integration", new(BuildTestGateVerdict.Ok, 0, 1, "", "ok", false, false)
            { ExpectedSha = sha, TestedSha = sha, TestSelection = audit });
            var recovered = IntegrationGateReceipts.ReadExact(folder, "integration", sha);
            Assert.Equal(["docs-check"], recovered!.TestSelection!.AttemptedSuites);
            Assert.Contains("backend-routine", recovered.TestSelection.OmittedSuites);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("suites: [missing]", "build: [0]")]
    [InlineData("suites: [backend]", "build: [20]")]
    [InlineData("suites: [backend]", "build: [-1]")]
    public void InvalidSelectionCannotBecomeAnExecutableDefinition(string suites, string build)
    {
        var read = ProjectDefinitionReader.Parse($$"""
            schemaVersion: 1
            stack: [custom]
            commands:
              prepare: .agent-studio/prepare
              build: [echo build]
              test: [echo test]
              lint: []
            testSuites:
              - id: backend
                category: routine
                expectedDurationSeconds: 1
                command: echo test
            testSelection:
              - paths: [backend/]
                {{suites}}
                {{build}}
            """);
        Assert.False(read.IsValid);
        Assert.Contains(read.Issues, issue => issue.Code == "selection-invalid");
    }

    [Fact]
    public void MissingMapKeepsTheLegacyGatePlanner()
    {
        var definitionPath = Path.Combine(Root, ".agent-studio/project.yml");
        File.WriteAllText(definitionPath, LegacyDefinition);
        var read = ProjectDefinitionReader.ReadWorkspace(Root);
        Assert.True(read.IsValid);
        Assert.Empty(read.Definition!.TestSelection);
        Assert.Null(ProjectAffectedVerifyPlanner.TryPlan(Root, VerifyCommandPlanner.Plan(Root, null),
            ["docs/a.md"], "integration", TestExecutionLevels.WorkPackage));
        var selection = ProjectTestSelectionPolicy.Select(read.Definition, ["frontend/a.ts"]);
        Assert.True(selection.Full);
        Assert.Empty(selection.OmittedSuites);
    }

    [Fact]
    public void SelectionSchemaDeclaresOnlySupportedRowFieldsAndKeepsUnknownFieldsClosed()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot.Find(),
            "docs/app/schemas/project-execution.schema.json")));
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        var selection = schema.RootElement.GetProperty("properties").GetProperty("testSelection");
        Assert.Equal("array", selection.GetProperty("type").GetString());
        var row = selection.GetProperty("items");
        Assert.False(row.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["paths", "suites"], row.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["build", "lint", "paths", "prepare", "suites"],
            row.GetProperty("properties").EnumerateObject().Select(item => item.Name).Order());
    }

    [Theory]
    [InlineData("testSelectionTypo: []", "testSelectionTypo")]
    [InlineData("testSelection:\n  - paths: [docs/]\n    suites: ['*']\n    typo: []", "testSelection")]
    public void NewFieldDoesNotRelaxUnknownFieldValidation(string addition, string issuePath)
    {
        var read = ProjectDefinitionReader.Parse(LegacyDefinition + "\n" + addition + "\n");
        Assert.False(read.IsValid);
        Assert.Contains(read.Issues, issue => issue.Code == "unknown-field" && issue.Path == issuePath);
    }

    [Theory]
    [InlineData("testSelection: invalid")]
    [InlineData("testSelection: {}")]
    [InlineData("testSelection:\n  - paths: docs/\n    suites: ['*']")]
    [InlineData("testSelection:\n  - paths: [docs/]\n    suites: ['*']\n    build: 0")]
    public void SelectionRequiresAnArrayOfRowsWithInlineLists(string addition)
    {
        var read = ProjectDefinitionReader.Parse(LegacyDefinition + "\n" + addition + "\n");
        Assert.False(read.IsValid);
        Assert.Contains(read.Issues, issue => issue.Code == "selection-shape-invalid");
    }

    private StagedVerifyPlan Plan(params string[] paths) => DeterministicTestScope.Plan(Root,
        VerifyCommandPlanner.Plan(Root, null), paths, null, null, "gate", TestExecutionLevels.WorkPackage);
}

public sealed class DocsOnlyIntegrationSelectionTests
{
    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task DocsOnlyIntegrationExecutesDocumentationCheckWithoutBackendOrBroadPreparation()
    {
        var root = Path.Combine(Path.GetTempPath(), "docs-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".agent-studio"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".agent-studio/prepare"), "#!/bin/sh\nexit 72\n");
            File.WriteAllText(Path.Combine(root, ".agent-studio/project.yml"), """
                schemaVersion: 1
                stack: [custom]
                commands:
                  prepare: .agent-studio/prepare
                  build: [echo backend-build-must-not-start]
                  test: [echo backend-test-must-not-start, echo docs-checked]
                  lint: []
                testSuites:
                  - id: backend
                    category: routine
                    expectedDurationSeconds: 1
                    command: echo backend-test-must-not-start
                  - id: docs
                    category: routine
                    expectedDurationSeconds: 1
                    command: echo docs-checked
                testSelection:
                  - paths: [backend/]
                    suites: [backend]
                    build: [0]
                  - paths: [docs/]
                    suites: [docs]
                """);
            Assert.True(PreDevelopBuildGate.AppliesTo(null, ["docs/a.md"], root));
            var runner = new BuildTestGateRunner(NullLogger<BuildTestGateRunner>.Instance,
                BuildTestMachineGateMode.BypassForHermeticTest, Path.Combine(root, "cache"));
            var result = await runner.RunAsync(new BuildTestGateRequest(root, null, "integration", RequireExactSubject: false)
                { RequiredTestLevel = PreDevelopBuildGate.ResolveTestLevel(["docs/a.md"], root) },
                ["docs/a.md"], null, PostStepMode.Fail, TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
            Assert.Equal("echo docs-checked", Assert.Single(result.Processes).Command);
            Assert.Equal(["docs"], result.TestSelection!.AttemptedSuites);
            Assert.Equal(["backend"], result.TestSelection.OmittedSuites);
            IntegrationGateReceipts.Record(root, "integration", result);
            var receipt = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(root, "post-steps"))));
            Assert.Contains("suites-ran=docs", receipt);
            Assert.Contains("suites-omitted=backend", receipt);
            var card = BuildTestGateStepProjection.Create(PipelineStepStatus.Passed, result.DurationMs,
                "pass", result.Reason, result, new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc));
            Assert.Contains("suites-ran=docs", card.Reason);
            Assert.Contains("suites-omitted=backend", card.Reason);
        }
        finally { Directory.Delete(root, true); }
    }
}
