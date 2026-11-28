using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Abstractions;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3310: <c>testing.perCard</c> on the Remote Review plan, derived from
/// <c>testing.strategy</c> since AGT-3965. Strict (full) and balanced T1 fallback
/// keep the full plan; fast fallback keeps build and lint; fast and balanced (affected, the default) run
/// build, lint, the touched-area fast checks and the tests the generated
/// project map selects.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class PerCardReviewPlanTests : IDisposable
{
    private const string ResultRef = "refs/heads/agent-studio/results/task";
    private const string BackendTest =
        "dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter Category!=MachineBound";
    private const string FrontendUnit = "bash scripts/npm-frontend.sh run test:ci";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "per-card-plan-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly string _folder;
    private readonly ITestOutputHelper _output;

    public PerCardReviewPlanTests(ITestOutputHelper output)
    {
        _output = output;
        _repo = Path.Combine(_root, "repo");
        _folder = Path.Combine(_root, "task");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Change the backend.\n");
        Repository();
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("backend/FooService.cs")]
    [InlineData("global.json")]
    public void StrictAndBalancedFallbackKeepTheFullPlan(string changed)
    {
        var resultSha = Commit(changed);
        // AGT-3965: the plan before testing.strategy existed is the plan
        // without per-card selection; strict reproduces it byte for byte.
        var today = Json(Build(null, resultSha, changed));

        Assert.Equal(today, Json(Build(Settings(TestingStrategies.Strict), resultSha, changed)));
        if (changed == "global.json")
        {
            Assert.Equal(today, Json(Build(Settings(TestingStrategies.Balanced), resultSha, changed)));
            foreach (var settings in new[] { Settings(TestingStrategies.Fast), new ProjectSettings() })
            {
                var plan = Build(settings, resultSha, changed);
                Assert.Equal(TestExecutionLevels.BuildOnly, plan.TestLevel);
                Assert.DoesNotContain(plan.Commands, command => command.Arguments.Contains(BackendTest)
                    || command.Arguments.Contains(FrontendUnit));
                Assert.Contains(plan.Commands, command => command.Arguments.Contains("echo build"));
            }
        }
        else
            Assert.NotEqual(today, Json(Build(new ProjectSettings(), resultSha, changed)));
    }

    [Theory]
    [InlineData("fast", "frontend/src/app.ts", "frontend", "work-package")]
    [InlineData("balanced", "frontend/src/app.ts", "frontend", "work-package")]
    [InlineData("strict", "frontend/src/app.ts", "frontend", "work-package")]
    [InlineData("fast", "docs/notes.md", "docs", "work-package")]
    [InlineData("balanced", "docs/notes.md", "docs", "work-package")]
    [InlineData("strict", "docs/notes.md", "docs", "work-package")]
    [InlineData("fast", "global.json", "all", "full")]
    [InlineData("balanced", "global.json", "all", "full")]
    [InlineData("strict", "global.json", "all", "full")]
    public void DeclaredImpactMapControlsCoverageAndLevelInEveryStrategy(
        string strategy, string changed, string expectedSuite, string expectedLevel)
    {
        Write(".agent-studio/project.yml", """
            schemaVersion: 1
            stack: [custom]
            commands:
              prepare: .agent-studio/prepare
              build: [echo backend-build, echo frontend-build]
              test: [echo backend-test, echo frontend-test, echo docs-test]
              lint: [echo frontend-lint]
            testSuites:
              - id: backend
                category: routine
                expectedDurationSeconds: 1
                command: echo backend-test
              - id: frontend
                category: routine
                expectedDurationSeconds: 1
                command: echo frontend-test
              - id: docs
                category: routine
                expectedDurationSeconds: 1
                command: echo docs-test
            testSelection:
              - paths: [backend/]
                suites: [backend]
                build: [0]
              - paths: [frontend/]
                suites: [frontend]
                build: [1]
                lint: [0]
              - paths: [docs/]
                suites: [docs]
            """);
        Write("docs/notes.md", "Notes.\n");
        Git("add", ".");
        Git("commit", "-qm", "impact map baseline");
        var sha = Commit(changed);

        var plan = Build(Settings(strategy), sha, changed);

        Assert.Equal(expectedLevel, plan.TestLevel);
        Assert.NotNull(plan.SuiteSelection);
        Assert.Equal(expectedSuite == "all" ? ["backend", "frontend", "docs"] : new[] { expectedSuite },
            plan.SuiteSelection.SelectedSuites);
        foreach (var suite in plan.SuiteSelection.SelectedSuites)
            Assert.Contains(plan.Commands, command => command.Arguments.Contains($"echo {suite}-test"));
        foreach (var suite in plan.SuiteSelection.OmittedSuites)
            Assert.DoesNotContain(plan.Commands, command => command.Arguments.Contains($"echo {suite}-test"));
        if (expectedSuite == "docs")
            Assert.DoesNotContain(plan.Commands, command => command.Arguments.Any(argument =>
                argument.Contains("backend-build", StringComparison.Ordinal)
                || argument.Contains("frontend-build", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fast_defers_a_mapped_whole_test_project_command_to_after_integration(bool fixedChecks)
    {
        if (fixedChecks)
        {
            ReviewBaseInventory(mapped: false);
            Git("add", ".");
            Git("commit", "-qm", "fixed review inventory");
        }
        var resultSha = Commit("backend/FooService.cs");
        var settings = Settings(TestingStrategies.Fast) with
        {
            TestExecution = new TestExecutionPolicy
            {
                FolderToTestProjects = [new TestFolderMapping
                {
                    Folder = "backend", Module = "backend",
                    TestProjects = ["backend.Tests/OrchestratorApi.Tests.csproj"],
                }],
            },
        };
        var plan = Build(settings, resultSha, "backend/FooService.cs");
        Assert.DoesNotContain(plan.Commands, command => command.Arguments.Contains(BackendTest));
        Assert.Contains(plan.Commands, command => command.Arguments.Contains("echo build"));
        Assert.Equal(fixedChecks, plan.Commands.Any(command => command.Arguments.Any(argument =>
            argument.Contains("Category=ReviewBase", StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData(false, "backend/FooService.cs", false)]
    [InlineData(true, "backend/FooService.cs", false)]
    [InlineData(false, "backend/Program.cs", false)]
    [InlineData(true, "unmapped/Unknown.cs", false)]
    [InlineData(false, "runner/Changed.cs", true)]
    [InlineData(true, "contracts/Changed.cs", true)]
    [InlineData(true, "orchestrator-engine/Changed.cs", true)]
    [InlineData(false, "orchestration-decisions/Changed.cs", true)]
    [InlineData(true, "task-server/Changed.cs", true)]
    public void Fast_review_keeps_fixed_checks_through_mapped_legacy_and_broad_fallback(
        bool mapped, string changed, bool cutover)
    {
        ReviewBaseInventory(mapped);
        if (changed != "backend/FooService.cs") Write(changed, "// composition or unmapped input\n");
        Git("add", ".");
        Git("commit", "-qm", "fixed review inventory");
        var sha = Commit(changed);

        var plan = Build(Settings(TestingStrategies.Fast), sha, changed);
        var shells = plan.Commands.SelectMany(command => command.Arguments).ToArray();
        _output.WriteLine("fixed-review-selection: " + JsonSerializer.Serialize(new
        {
            mapped, changed,
            selected = shells.Where(command => command.StartsWith("dotnet test", StringComparison.Ordinal)),
            attempted = Array.Empty<string>(),
            note = "Planner evidence only; test execution receipts establish attempted and missing evidence.",
        }));
        var fixedCommand = Assert.Single(shells, command => command.Contains("Category=ReviewBase", StringComparison.Ordinal));
        Assert.Contains("(Category!=MachineBound)&(FullyQualifiedName~.Architecture.|Category=Guard|Category=ReviewBase)", fixedCommand);
        Assert.Contains("dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build", fixedCommand);
        // No second guard invocation for the backend: both RDG profiles remain Guard.
        Assert.Single(shells, command => command.Contains("backend.Tests/OrchestratorApi.Tests.csproj", StringComparison.Ordinal)
            && command.Contains("Category=Guard", StringComparison.Ordinal));
        Assert.Equal(cutover, shells.Any(command => command.Contains("FullyQualifiedName~Example.CutoverTests", StringComparison.Ordinal)));
        Assert.DoesNotContain(BackendTest, shells);
        Assert.DoesNotContain("dotnet test engine.Tests/Engine.Tests.csproj --no-build --filter Category!=MachineBound", shells);
        Assert.NotNull(plan.TestSelectionDigest);
        if (mapped)
        {
            Assert.NotNull(plan.SuiteSelection);
            Assert.DoesNotContain("backend-routine", plan.SuiteSelection.SelectedSuites);
            Assert.Contains("backend-routine", plan.SuiteSelection.OmittedSuites);
        }
    }

    [Theory]
    [InlineData("docs/notes.md", null)]
    [InlineData("backend/FooService.cs", "research")]
    public void Fixed_checks_leave_documentation_and_report_only_reviews_unchanged(string changed, string? mode)
    {
        ReviewBaseInventory(mapped: false);
        if (changed.StartsWith("docs/", StringComparison.Ordinal)) Write(changed, "Notes.\n");
        Git("add", ".");
        Git("commit", "-qm", "fixed review inventory");
        var sha = Commit(changed);

        var plan = Build(Settings(TestingStrategies.Fast), sha, changed, mode);

        Assert.DoesNotContain(plan.Commands, command => command.Arguments.Any(argument =>
            argument.Contains("Category=ReviewBase", StringComparison.Ordinal)
            || argument.Contains("Example.CutoverTests", StringComparison.Ordinal)));
    }

    [Fact]
    public void Fixed_check_audit_distinguishes_selection_from_execution_and_leaves_full_plans_alone()
    {
        ReviewBaseInventory(mapped: false);
        var verify = VerifyCommandPlanner.Plan(_repo, null);
        var ordinary = PerCardVerifyPlanner.Plan(_repo, verify, ["backend/Program.cs"], null, null, null);
        var selected = PerCardVerifyPlanner.WithReviewBase(_repo, verify, ordinary, ["backend/Program.cs"]);

        Assert.Contains(selected.Audit.SelectedCommands, command => command.Contains("Category=ReviewBase", StringComparison.Ordinal));
        Assert.Empty(selected.Audit.AttemptedTestCommands);
        Assert.Contains(selected.Audit.Reasons, reason => reason.Contains("not attempted", StringComparison.Ordinal));
        Assert.DoesNotContain(ordinary.Commands, command => command.Command.Contains("Category=ReviewBase", StringComparison.Ordinal));
        Assert.Contains(selected.Audit.OmittedTestCommands, command => command.Contains(BackendTest, StringComparison.Ordinal));
        Assert.NotEqual(ordinary.Audit.Digest, selected.Audit.Digest);
    }

    [Theory]
    [InlineData("strict")]
    [InlineData("balanced")]
    public void Fixed_inventory_does_not_change_other_review_strategies(string strategy)
    {
        ReviewBaseInventory(mapped: false);
        Git("add", ".");
        Git("commit", "-qm", "fixed review inventory");
        var sha = Commit("backend/FooService.cs");
        var plan = Build(Settings(strategy), sha, "backend/FooService.cs");

        Assert.DoesNotContain(plan.Commands, command => command.Arguments.Any(argument =>
            argument.Contains("Category=ReviewBase", StringComparison.Ordinal)
            || argument.Contains("Example.CutoverTests", StringComparison.Ordinal)));
    }

    [Fact]
    public void Deferred_fast_review_also_keeps_fixed_checks()
    {
        ReviewBaseInventory(mapped: false);
        Git("add", ".");
        Git("commit", "-qm", "fixed review inventory");
        var sha = Commit("backend/FooService.cs");
        var settings = Settings(TestingStrategies.Fast) with
        {
            BatchGate = new(Enabled: true, Scope: "code", ReviewTestLevel: "build-only"),
        };

        var plan = Build(settings, sha, "backend/FooService.cs");

        Assert.Contains(plan.Commands, command => command.Arguments.Any(argument =>
            argument.Contains("Category=ReviewBase", StringComparison.Ordinal)));
    }

    [Fact]
    public void Repository_inventory_selects_the_four_methods_and_keeps_Rdg_in_Guard()
    {
        var expected = new[]
        {
            (typeof(TaskCoreEndpointTests), "DurableMutations_AreVisibleToTheNextDetailRequest", 1),
            (typeof(BoardReadMutationTests), "Mutations_replace_cached_board_bytes_and_validator", 14),
            (typeof(BatchGatePilotRecoveryTests), "Batch_deferred_actionable_concern_waits_for_a_fix_before_admission", 1),
            (typeof(MonolithOrchestrationEndpointTests), "Engine_switch_holds_coding_and_review_reports_until_each_decision_is_applied", 4),
        };
        var tagged = typeof(TaskCoreEndpointTests).Assembly.GetTypes().SelectMany(type => type.GetMethods())
            .Where(method => HasCategory(method, "ReviewBase")).ToArray();
        Assert.Equal(expected.Select(item => item.Item2).Order(), tagged.Select(method => method.Name).Order());
        foreach (var (type, name, rows) in expected)
        {
            var method = type.GetMethod(name)!;
            Assert.Equal(rows, Math.Max(1, method.GetCustomAttributes(typeof(InlineDataAttribute), false).Length));
        }
        var rdg = typeof(RequestDelegateGeneratorCoverageTests);
        Assert.True(HasCategory(rdg, "Guard"));
        Assert.False(HasCategory(rdg, "ReviewBase"));

        var root = RepositoryRoot.Find();
        var verify = VerifyCommandPlanner.Plan(root, null);
        var selection = new StagedVerifyPlan([], new TestSelectionAudit());
        var plan = PerCardVerifyPlanner.WithReviewBase(root, verify, selection, ["contracts/TaskServer.Contracts/ReviewPlan.cs"]);
        Assert.Single(plan.Commands, command => command.Command.Contains("Category=ReviewBase", StringComparison.Ordinal));
        Assert.Single(plan.Commands, command => command.Command.Contains(
            "FullyQualifiedName~OrchestratorEngine.Tests.StandaloneReviewLoopCutoverTests", StringComparison.Ordinal));
        Assert.Contains("OrchestratorEngine.Tests.StandaloneReviewLoopCutoverTests", plan.Audit.SelectedTestClasses);
        Assert.All(plan.Commands, command => Assert.Contains("Category!=MachineBound", command.Command));

        static bool HasCategory(System.Reflection.MemberInfo member, string category)
            => member.CustomAttributes.Any(attribute => attribute.AttributeType == typeof(TraitAttribute)
                && Equals(attribute.ConstructorArguments[0].Value, "Category")
                && Equals(attribute.ConstructorArguments[1].Value, category));
    }

    [Fact]
    public void Fixed_checks_exclude_their_cases_from_retained_class_commands()
    {
        ReviewBaseInventory(mapped: false);
        var verify = VerifyCommandPlanner.Plan(_repo, null);
        var classes = DeterministicTestScope.DotNetProjectCommand(verify,
            "backend.Tests/OrchestratorApi.Tests.csproj", ["BoardReadMutationTests", "RequestDelegateGeneratorCoverageTests"])!;
        var selected = PerCardVerifyPlanner.WithReviewBase(_repo, verify,
            new StagedVerifyPlan([classes], new TestSelectionAudit()), ["backend/FooService.cs"]);

        var retained = Assert.Single(selected.Commands, command => command.Command.Contains(
            "FullyQualifiedName~BoardReadMutationTests", StringComparison.Ordinal));
        Assert.Contains("Category!=ReviewBase", retained.Command);
        Assert.Contains("Category!=Guard", retained.Command);
        Assert.Contains("FullyQualifiedName!~.Architecture.", retained.Command);
        Assert.Single(selected.Commands, command => command.Command.Contains("Category=ReviewBase", StringComparison.Ordinal));
    }

    [Fact]
    public void Fixed_check_exclusions_stay_with_the_invocation_that_runs_the_cases()
    {
        ReviewBaseInventory(mapped: false);
        var verify = VerifyCommandPlanner.Plan(_repo, null);
        var backend = DeterministicTestScope.DotNetProjectCommand(verify,
            "backend.Tests/OrchestratorApi.Tests.csproj", ["Example.CutoverTests"])!;
        var engine = DeterministicTestScope.DotNetProjectCommand(verify,
            "engine.Tests/Engine.Tests.csproj", ["Example.CutoverTests"])!;
        var selected = PerCardVerifyPlanner.WithReviewBase(_repo, verify,
            new StagedVerifyPlan([backend, engine], new TestSelectionAudit()), ["contracts/Changed.cs"]);

        var retainedBackend = Assert.Single(selected.Commands, command => command.Command.StartsWith(
            "dotnet test backend.Tests/", StringComparison.Ordinal) && command.TestScope != ReviewBaseChecks.Scope);
        var retainedEngine = Assert.Single(selected.Commands, command => command.Command.StartsWith(
            "dotnet test engine.Tests/", StringComparison.Ordinal) && command.TestScope != ReviewBaseChecks.Scope);
        Assert.Contains("Category!=ReviewBase", retainedBackend.Command);
        Assert.DoesNotContain("FullyQualifiedName!~Example.CutoverTests", retainedBackend.Command);
        Assert.Contains("FullyQualifiedName!~Example.CutoverTests", retainedEngine.Command);
        Assert.DoesNotContain("Category!=ReviewBase", retainedEngine.Command);
        Assert.All(new[] { retainedBackend, retainedEngine }, command => Assert.Contains("Category!=Guard", command.Command));
        Assert.Contains(TestSelectionPlanner.Describe(retainedBackend), selected.Audit.SelectedCommands);
        Assert.Contains(TestSelectionPlanner.Describe(retainedEngine), selected.Audit.SelectedCommands);
        Assert.Empty(selected.Audit.AttemptedTestCommands);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("directory")]
    [InlineData("options")]
    public void Fixed_checks_preserve_cases_without_a_matching_replacement_invocation(string difference)
    {
        ReviewBaseInventory(mapped: false);
        var verify = VerifyCommandPlanner.Plan(_repo, null);
        var retained = DeterministicTestScope.DotNetProjectCommand(verify,
            "backend.Tests/OrchestratorApi.Tests.csproj", ["Example.CutoverTests"])!;
        retained = difference switch
        {
            "project" => retained with { Command = retained.Command.Replace("backend.Tests/OrchestratorApi.Tests.csproj", "other.Tests/Other.Tests.csproj", StringComparison.Ordinal) },
            "directory" => retained with { WorkingSubdir = "other" },
            _ => retained with { Command = retained.Command + " --configuration Release" },
        };
        var selected = PerCardVerifyPlanner.WithReviewBase(_repo, verify,
            new StagedVerifyPlan([retained], new TestSelectionAudit()), ["contracts/Changed.cs"]);

        Assert.Contains(retained, selected.Commands);
        Assert.Contains(TestSelectionPlanner.Describe(retained), selected.Audit.SelectedCommands);
    }

    [Theory]
    [InlineData("Category!=ReviewFlaky", "work-package")]
    [InlineData("Category!=ReviewFlaky", "guard")]
    [InlineData("Category!=ReviewFlaky", "review-base")]
    [InlineData("(Category!=MachineBound)|(Category=Guard)", "work-package")]
    [InlineData("(Category!=MachineBound)&(FullyQualifiedName~Example.Tests)|Category=Guard", "work-package")]
    [InlineData(null, "work-package")]
    public void Fixed_checks_preserve_same_project_cases_outside_the_replacement_filter(string? filter, string scope)
    {
        ReviewBaseInventory(mapped: false);
        var verify = VerifyCommandPlanner.Plan(_repo, null);
        var retained = verify.Commands.Single(command => command.Command == BackendTest) with
        {
            Command = "dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build"
                + (filter is null ? "" : $" --filter \"{filter}\""),
            TestScope = scope,
        };
        var selected = PerCardVerifyPlanner.WithReviewBase(_repo, verify,
            new StagedVerifyPlan([retained], new TestSelectionAudit()), ["backend/Changed.cs"]);

        // These selections may include MachineBound Guard/ReviewBase cases.
        // The fixed invocation excludes those cases, so it cannot replace them.
        Assert.Contains(retained, selected.Commands);
        Assert.Contains(TestSelectionPlanner.Describe(retained), selected.Audit.SelectedCommands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_project_declared_filter_variants_do_not_overwrite_replacement_coverage(bool reverse)
    {
        ReviewBaseInventory(mapped: false);
        var verify = VerifyCommandPlanner.Plan(_repo, null);
        var routine = verify.Commands.Single(command => command.Command == BackendTest);
        var variant = routine with { Command = BackendTest.Replace("Category!=MachineBound",
            "Category!=ReviewFlaky", StringComparison.Ordinal) };
        VerifyCommand[] variants = reverse ? [variant, routine] : [routine, variant];
        verify = verify with { Commands = verify.Commands.Where(command => command != routine).Concat(variants).ToArray() };
        var retained = variant with
        {
            Command = variant.Command.Replace("Category!=ReviewFlaky",
                "\"(Category!=ReviewFlaky)&(FullyQualifiedName~BoardReadMutationTests)\"", StringComparison.Ordinal),
        };
        var selected = PerCardVerifyPlanner.WithReviewBase(_repo, verify,
            new StagedVerifyPlan([retained], new TestSelectionAudit()), ["backend/Changed.cs"]);

        var remaining = Assert.Single(selected.Commands, command => command.Command.Contains(
            "FullyQualifiedName~BoardReadMutationTests", StringComparison.Ordinal));
        // The variant covers its own guards, but only the routine covers ReviewBase.
        Assert.Contains("Category!=Guard", remaining.Command);
        Assert.DoesNotContain("Category!=ReviewBase", remaining.Command);
        Assert.Equal(2, selected.Commands.Count(command => command.TestScope == ReviewBaseChecks.Scope
            && command.Command.Contains("backend.Tests/", StringComparison.Ordinal)));
    }

    [Fact]
    public void Fixed_dotnet_checks_preserve_selected_other_ecosystem_commands()
    {
        ReviewBaseInventory(mapped: true);
        var verify = VerifyCommandPlanner.Plan(_repo, null);
        string[] changed = ["backend/FooService.cs", "frontend/src/app.ts"];
        var affected = PerCardVerifyPlanner.Plan(_repo, verify, changed, null, null, null);
        var selected = PerCardVerifyPlanner.WithReviewBase(_repo, verify,
            affected, changed);

        Assert.Contains(selected.Commands, command => command.Command == FrontendUnit);
        Assert.Contains("frontend-unit", selected.Audit.SelectedSuites);
        Assert.DoesNotContain(selected.Audit.OmittedTestCommands, command => command.Contains(FrontendUnit, StringComparison.Ordinal));
        Assert.DoesNotContain(selected.Commands, command => command.Command == BackendTest);
    }

    [Theory]
    [InlineData("Bad|Category=Guard")]
    [InlineData("Bad\"; echo unsafe")]
    public void Fixed_checks_reject_filter_operators_in_selector_names(string category)
    {
        ReviewBaseInventory(mapped: false);
        Write(ReviewBaseChecks.InventoryPath, JsonSerializer.Serialize(new[]
        {
            new { suite = "backend-routine", paths = new[] { "*" }, category },
        }));
        var verify = VerifyCommandPlanner.Plan(_repo, null);
        var selection = new StagedVerifyPlan([], new TestSelectionAudit());

        Assert.Throws<InvalidDataException>(() => PerCardVerifyPlanner.WithReviewBase(
            _repo, verify, selection, ["backend/FooService.cs"]));
    }

    private void ReviewBaseInventory(bool mapped)
    {
        var definition = File.ReadAllText(Path.Combine(_repo, ".agent-studio/project.yml"))
            .Replace($"    - {BackendTest}\n", $"    - {BackendTest}\n    - dotnet test engine.Tests/Engine.Tests.csproj --no-build --filter Category!=MachineBound\n", StringComparison.Ordinal)
            .Replace("testSuites:\n", $"testSuites:\n  - id: backend-routine\n    category: routine\n    expectedDurationSeconds: 1\n    command: {BackendTest}\n  - id: engine-routine\n    category: routine\n    expectedDurationSeconds: 1\n    command: dotnet test engine.Tests/Engine.Tests.csproj --no-build --filter Category!=MachineBound\n", StringComparison.Ordinal);
        if (mapped) definition += """

            testSelection:
              - paths: [backend/, backend.Tests/]
                suites: [backend-routine]
                build: [0, 1]
              - paths: [runner/, contracts/, task-server/, orchestrator-engine/, orchestration-decisions/]
                suites: [engine-routine]
              - paths: [frontend/]
                suites: [frontend-unit]
                lint: [0]
            """;
        Write(".agent-studio/project.yml", definition);
        Write("engine.Tests/Engine.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
        Write(ReviewBaseChecks.InventoryPath, """
            [
              { "suite": "backend-routine", "paths": ["*"], "category": "ReviewBase" },
              { "suite": "engine-routine", "paths": ["runner/", "contracts/", "task-server/", "orchestrator-engine/", "orchestration-decisions/"], "classes": ["Example.CutoverTests"] }
            ]
            """);
    }

    [Fact]
    public void FastAndBalancedKeepTheSameCommandsWhenTheTrialFindsNoAdditionalTests()
    {
        var resultSha = Commit("backend/FooService.cs");

        var fast = Build(Settings(TestingStrategies.Fast), resultSha, "backend/FooService.cs");

        var balanced = Build(Settings(TestingStrategies.Balanced), resultSha, "backend/FooService.cs");
        Assert.Equal(Json(fast with { TestSelectionDigest = null }), Json(balanced with { TestSelectionDigest = null }));
        Assert.Equal(Json(fast), Json(Build(new ProjectSettings(), resultSha, "backend/FooService.cs")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Broad_fast_review_retains_new_tests_and_the_review_base(bool pilot)
    {
        ReviewBaseInventory(mapped: true);
        for (var index = 0; index < 33; index++)
            Write($"backend.Tests/Caller{index}.cs", TestSource($"Caller{index}", "FooService"));
        Git("add", ".");
        Git("commit", "-qm", "wide reference baseline");
        Git("switch", "-qc", "agent-studio/results/task");
        Write("backend.Tests/ProductStartupFixtureTests.cs", TestSource("ProductStartupFixtureTests"));
        File.AppendAllText(Path.Combine(_repo, "backend/FooService.cs"), "// changed\n");
        Git("add", ".");
        Git("commit", "-qm", "wide delivery with a new fixture");
        var sha = Git("rev-parse", "HEAD").Trim();
        Git("switch", "-q", "develop");

        var plan = Build(Settings(TestingStrategies.Fast), sha, "backend/FooService.cs", pilot: pilot);
        var shells = plan.Commands.SelectMany(command => command.Arguments).ToArray();
        Assert.Contains(shells, command => command.Contains("Category=ReviewBase", StringComparison.Ordinal));
        var slice = Assert.Single(shells, command => command.Contains("FullyQualifiedName~ProductStartupFixtureTests", StringComparison.Ordinal));
        Assert.Contains("FullyQualifiedName~FooServiceTests", slice);
        Assert.Contains("Category!=MachineBound", slice);
        Assert.DoesNotContain(shells, command => command.Contains("FullyQualifiedName~Caller0", StringComparison.Ordinal));
        Assert.DoesNotContain(BackendTest, shells);
        Assert.Contains(plan.SuiteSelection!.Reasons, reason => reason.Contains("broad-reference omission", StringComparison.Ordinal)
            && reason.Contains("33 test-source files", StringComparison.Ordinal)
            && reason.Contains("post-merge routine suite", StringComparison.Ordinal)
            && reason.Contains("backend.Tests/Caller32.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Shadow_preserves_a_stronger_prior_class_filter_until_the_pilot_is_enabled()
    {
        for (var index = 0; index < 33; index++)
            Write($"backend.Tests/Caller{index}.cs", TestSource($"Caller{index}", "FooService"));
        for (var index = 0; index < 70; index++)
            Write($"backend.Tests/Other{index}.cs", TestSource($"Other{index}"));
        Git("add", ".");
        Git("commit", "-qm", "wide reference baseline");
        var sha = Commit("backend/FooService.cs");

        var shadow = Build(Settings(TestingStrategies.Fast), sha, "backend/FooService.cs");
        var pilot = Build(Settings(TestingStrategies.Fast), sha, "backend/FooService.cs", pilot: true);

        Assert.Contains(shadow.Commands, command => command.Arguments.Any(argument =>
            argument.Contains("FullyQualifiedName~Caller0", StringComparison.Ordinal)));
        Assert.DoesNotContain(pilot.Commands, command => command.Arguments.Any(argument =>
            argument.Contains("FullyQualifiedName~Caller0", StringComparison.Ordinal)));
        Assert.All(new[] { shadow, pilot }, plan => Assert.Contains(plan.Commands, command =>
            command.Arguments.Any(argument => argument.Contains("FullyQualifiedName~FooServiceTests", StringComparison.Ordinal))));
    }

    [Fact]
    public void Docs_only_fix_round_still_selects_tests_from_the_unintegrated_delivery()
    {
        Git("switch", "-qc", "agent-studio/results/task");
        Write("backend.Tests/NewDeliveryTests.cs", TestSource("NewDeliveryTests"));
        Git("add", ".");
        Git("commit", "-qm", "pending delivery");
        Write("docs/fix.md", "Documentation fix.\n");
        Git("add", ".");
        Git("commit", "-qm", "fix round");
        var sha = Git("rev-parse", "HEAD").Trim();
        Git("switch", "-q", "develop");

        var plan = Build(Settings(TestingStrategies.Fast), sha, "docs/fix.md");
        var rebuilt = Build(Settings(TestingStrategies.Fast), sha, null);

        Assert.Contains(plan.Commands, command => command.Arguments.Any(argument =>
            argument.Contains("FullyQualifiedName~NewDeliveryTests", StringComparison.Ordinal)));
        Assert.Equal(Json(plan), Json(rebuilt));
        Assert.Equal(TestExecutionLevels.WorkPackage, plan.TestLevel);
    }

    [Fact]
    public void Unavailable_delivery_diff_keeps_the_fast_build_fallback()
    {
        var plan = Build(Settings(TestingStrategies.Fast), new string('a', 40), null);

        Assert.Contains(plan.Commands, command => command.Arguments.Contains("echo build"));
        Assert.DoesNotContain(plan.Commands, command => command.Arguments.Contains(BackendTest));
        Assert.Equal(TestExecutionLevels.BuildOnly, plan.TestLevel);
    }

    [Fact]
    public void Renamed_source_uses_both_names_and_test_sources_from_the_pinned_tree()
    {
        ReviewBaseInventory(mapped: true);
        Write("backend.Tests/RenamedServiceTests.cs", TestSource("RenamedServiceTests"));
        Git("add", ".");
        Git("commit", "-qm", "companion tests");
        Git("switch", "-qc", "agent-studio/results/task");
        Git("mv", "backend/FooService.cs", "backend/RenamedService.cs");
        Write("backend/RenamedService.cs", "namespace AgentStudio;\npublic sealed class RenamedService { }\n");
        Git("add", ".");
        Git("commit", "-qm", "source rename");
        var sha = Git("rev-parse", "HEAD").Trim();
        Git("switch", "-q", "develop");
        Write("backend.Tests/CheckoutOnlyTests.cs", TestSource("CheckoutOnlyTests", "FooService"));

        var commands = Build(Settings(TestingStrategies.Fast), sha, "backend/RenamedService.cs")
            .Commands.SelectMany(command => command.Arguments).ToArray();

        var slice = Assert.Single(commands, command => command.Contains("FullyQualifiedName~FooServiceTests", StringComparison.Ordinal));
        Assert.Contains("FullyQualifiedName~RenamedServiceTests", slice);
        Assert.DoesNotContain(commands, command => command.Contains("CheckoutOnlyTests", StringComparison.Ordinal));
    }

    private static string TestSource(string name, string? reference = null)
        => $"public sealed class {name}\n{{\n    [Fact]\n    public void Runs() {{ "
            + (reference is null ? "" : $"_ = typeof({reference});") + " }\n}\n";

    [Fact]
    public void AffectedBackendDiffRunsTheReferencingClassesBuildAndLint()
    {
        var resultSha = Commit("backend/FooService.cs");

        var plan = Build(Settings(TestingStrategies.Fast), resultSha, "backend/FooService.cs");

        var shells = plan.Commands.Where(command => command.Arguments.Count == 2)
            .Select(command => command.Arguments[1]).ToList();
        Assert.Contains("echo build", shells);
        Assert.Contains("bash scripts/npm-frontend.sh run lint", shells);
        Assert.Contains("dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter "
            + "\"(Category!=MachineBound)&(FullyQualifiedName~FooServiceTests)\"", shells);
        Assert.Contains("dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter "
            + "\"(Category!=MachineBound)&(FullyQualifiedName~.Architecture.|Category=Guard)\"", shells);
        Assert.DoesNotContain(BackendTest, shells);
        Assert.DoesNotContain(FrontendUnit, shells);
        Assert.DoesNotContain("bash scripts/npm-frontend.sh run typecheck", shells);
        Assert.Equal(TestExecutionLevels.WorkPackage, plan.TestLevel);
        Assert.Matches("^[0-9a-f]{64}$", plan.TestSelectionDigest!);
        Assert.False(plan.BuildTestDeferredToBatch);
    }

    [Theory]
    [InlineData("backend/OrchestratorApi.csproj")]
    [InlineData("runner/AgentRunner.csproj")]
    public void PackageReferenceDeliveryKeepsReleaseBuildInReviewPlans(string project)
    {
        Git("switch", "-qc", "agent-studio/results/task");
        Write(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
            + "<PackageReference Include=\"Example\" Version=\"2.0\" />"
            + "</ItemGroup></Project>\n");
        Git("add", ".");
        Git("commit", "-qm", "package reference change");
        var sha = Git("rev-parse", "HEAD").Trim();
        Git("switch", "-q", "develop");

        var affected = Build(Settings(TestingStrategies.Fast), sha, project);
        var batch = Build(new ProjectSettings
        {
            BatchGate = new(Enabled: true, Scope: "code", ReviewTestLevel: "build-only"),
        }, sha, project);

        foreach (var plan in new[] { affected, batch })
        {
            var release = Assert.Single(plan.Commands, command => command.Arguments.Contains(
                "bash scripts/check-release-build.sh"));
            Assert.Contains(release.Aspect, new[] { "build-tests", "review-verification" });
            Assert.Equal(Contract.ReviewBaselineModes.ExitStatus, release.BaselineMode);
        }
    }

    [Fact]
    public void BackendReviewRunsDeclaredGuardsWhenClassSelectionFindsNoTest()
    {
        var verify = VerifyCommandPlanner.Plan(_repo, null);
        var selected = verify with
        {
            Commands = verify.Commands.Where(command => command.Kind != VerifyCommandKind.Test).ToArray(),
        };

        var plan = V1ReviewPlaneEndpoints.FallbackPlan(_repo, null, "develop",
            ["backend/FooService.cs"], selected);

        Assert.Contains(plan.Commands, command => command.Arguments.Contains(
            "dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter "
            + "\"(Category!=MachineBound)&(FullyQualifiedName~.Architecture.|Category=Guard)\""));
        Assert.DoesNotContain(plan.Commands, command => command.Arguments.Contains(BackendTest));
    }

    [Fact]
    public void ResultsEvidenceChangeChangesSealedAspectDigest()
    {
        var resultSha = Commit("backend/FooService.cs");
        var results = Path.Combine(_folder, "results");
        Directory.CreateDirectory(results);
        var report = Path.Combine(results, "duration-comparison.md");
        File.WriteAllText(report, "Before 120 seconds, after 80 seconds.\n");
        var first = Contract.ReviewLibraryStepPolicy.Seal(
            Build(new ProjectSettings(), resultSha, "backend/FooService.cs"), resultSha);
        File.WriteAllText(report, "Before 120 seconds, after 60 seconds.\n");
        var second = Contract.ReviewLibraryStepPolicy.Seal(
            Build(new ProjectSettings(), resultSha, "backend/FooService.cs"), resultSha);

        var firstAspect = Assert.Single(first.Commands, command => command.Aspect == "requirement-fit");
        var secondAspect = Assert.Single(second.Commands, command => command.Aspect == "requirement-fit");
        Assert.Contains("Before 120 seconds, after 80 seconds", firstAspect.Prompt);
        Assert.NotEqual(firstAspect.LibraryStep!.Digest, secondAspect.LibraryStep!.Digest);
    }

    [Fact]
    public void BackendReviewRunsGuardsWhenThePlanBuilderResolvesTheDiff()
    {
        var resultSha = Commit("backend/FooService.cs");
        var plan = Builder().Build(new TaskInfo
        {
            Id = "task", TaskKey = "task", ProjectName = "project",
            WatchPath = _repo, FolderPath = _folder, State = TaskStates.AutoReview,
        }, _repo, new ProjectSettings(), "develop", resultSha,
            changedFiles: null, immutableResultRef: ResultRef);

        Assert.Contains(plan.Commands, command => command.Arguments.Contains(
            "dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter "
            + "\"(Category!=MachineBound)&(FullyQualifiedName~.Architecture.|Category=Guard)\""));
    }

    [Theory]
    [InlineData("merge", 0, 4)]
    [InlineData("code", 4, 0)]
    [InlineData("status", 2, 2)]
    public void PassedDeliveryOwnChangeControlsContentReview(
        string round, int expectedAgentCommands, int expectedCarriedVerdicts)
    {
        var previousSha = Commit("backend/FooService.cs");
        var previousBase = Git("merge-base", "develop", previousSha).Trim();
        RecordPassedReview(previousSha, previousBase);

        Write("backend/Unrelated.cs", "namespace AgentStudio; public class Unrelated { }\n");
        Git("add", ".");
        Git("commit", "-qm", "integration change");
        Git("switch", "-q", "agent-studio/results/task");
        Git("merge", "-q", "develop");
        if (round == "code")
        {
            File.AppendAllText(Path.Combine(_repo, "backend/FooService.cs"), "// another line\n");
            Git("add", ".");
            Git("commit", "-qm", "change card code");
        }
        else if (round == "status")
        {
            Write("status.md", "Delivery evidence updated.\n");
            Git("add", ".");
            Git("commit", "-qm", "update status");
        }
        var resultSha = Git("rev-parse", "HEAD").Trim();
        Git("switch", "-q", "develop");

        var plan = Builder().Build(new TaskInfo
        {
            Id = "task", TaskKey = "task", ProjectName = "project",
            WatchPath = _repo, FolderPath = _folder, State = TaskStates.AutoReview,
        }, _repo, new ProjectSettings(), "develop", resultSha, null, ResultRef);

        var agents = plan.Commands.Where(command =>
            Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind)).ToArray();
        Assert.Equal(expectedAgentCommands, agents.Length);
        Assert.Equal(expectedCarriedVerdicts, plan.CarriedVerdicts?.Count ?? 0);
        if (round == "status")
            Assert.Equal(["requirement-fit", "tests-and-evidence"],
                agents.Select(command => command.Aspect).Order(StringComparer.Ordinal).ToArray());
        if (round == "merge")
            Assert.All(plan.CarriedVerdicts!, verdict => Assert.Equal("review-1", verdict.CarriedOverFrom));
        Assert.Contains(plan.Commands, command => command.Aspect == "build-tests");
    }

    /// <summary>
    /// AGT-3968: after a passing review, a round whose new commits are only a
    /// merge of develop plus its conflict resolution reviews the resolution.
    /// One aspect reviews the resolved files; the aspects that passed on the
    /// unchanged work are carried over; build and tests still run.
    /// </summary>
    [Fact]
    public void MergeOnlyRoundAfterAPassReviewsOnlyTheResolution()
    {
        var previousSha = Commit("backend/FooService.cs");
        var previousBase = Git("merge-base", "develop", previousSha).Trim();
        RecordPassedReview(previousSha, previousBase);

        File.AppendAllText(Path.Combine(_repo, "backend/FooService.cs"), "// develop line\n");
        Write("backend/Unrelated.cs", "namespace AgentStudio; public class Unrelated { }\n");
        Git("add", ".");
        Git("commit", "-qm", "integration change in the same place");
        Git("switch", "-q", "agent-studio/results/task");
        var merge = Process.Start(new ProcessStartInfo("git", ["merge", "-q", "develop"])
        {
            WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        merge.WaitForExit();
        Assert.NotEqual(0, merge.ExitCode);
        var resolved = File.ReadAllLines(Path.Combine(_repo, "backend/FooService.cs"))
            .Where(line => !line.StartsWith("<<<<<<<", StringComparison.Ordinal)
                && !line.StartsWith("=======", StringComparison.Ordinal)
                && !line.StartsWith(">>>>>>>", StringComparison.Ordinal))
            .Select(line => line == "// changed" ? "// changed, kept next to develop" : line);
        File.WriteAllLines(Path.Combine(_repo, "backend/FooService.cs"), resolved);
        Git("add", ".");
        Git("commit", "-qm", "merge develop and resolve");
        var resultSha = Git("rev-parse", "HEAD").Trim();
        Git("switch", "-q", "develop");

        var plan = Builder().Build(new TaskInfo
        {
            Id = "task", TaskKey = "task", ProjectName = "project",
            WatchPath = _repo, FolderPath = _folder, State = TaskStates.AutoReview,
        }, _repo, new ProjectSettings(), "develop", resultSha, null, ResultRef);

        var agent = Assert.Single(plan.Commands, command =>
            Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind));
        Assert.Equal(RemoteReviewPlanBuilder.MergeResolutionAspect, agent.Aspect);
        Assert.Contains("## Merge resolution to review", agent.Prompt);
        Assert.Contains("backend/FooService.cs", agent.Prompt);
        Assert.DoesNotContain("backend/Unrelated.cs", agent.Prompt![agent.Prompt.IndexOf("## Merge resolution", StringComparison.Ordinal)..]);
        Assert.Equal(["documentation-impact", "requirement-fit", "tests-and-evidence"],
            plan.CarriedVerdicts!.Select(verdict => verdict.Aspect).Order(StringComparer.Ordinal).ToArray());
        Assert.All(plan.CarriedVerdicts!, verdict => Assert.Equal("review-1", verdict.CarriedOverFrom));
        Assert.Contains(plan.Commands, command => command.Aspect == "build-tests");

        // The same delivery with an own commit after the merge is reviewed in full.
        Git("switch", "-q", "agent-studio/results/task");
        Write("backend/Other.cs", "namespace AgentStudio; public class Other { }\n");
        Git("add", ".");
        Git("commit", "-qm", "more card work");
        var withWork = Git("rev-parse", "HEAD").Trim();
        Git("switch", "-q", "develop");
        var full = Builder().Build(new TaskInfo
        {
            Id = "task", TaskKey = "task", ProjectName = "project",
            WatchPath = _repo, FolderPath = _folder, State = TaskStates.AutoReview,
        }, _repo, new ProjectSettings(), "develop", withWork, null, ResultRef);
        Assert.Equal(4, full.Commands.Count(command => Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind)));
        Assert.Empty(full.CarriedVerdicts ?? []);
    }

    private void RecordPassedReview(string resultSha, string mergeBase)
    {
        var report = new Contract.ReviewReportRequest(
            "executor", "instance", "lease", 1, "report-1", "Pass", null, null,
            new Contract.ReviewWorkspaceProofDto("repo", resultSha, resultSha, resultSha,
                false, false, "workspace", "namespace", MergeBaseSha: mergeBase),
            new Contract.ReviewEnvironmentDto("host", "executor", "instance", "linux", "x64", "test",
                new Dictionary<string, string>(), new Dictionary<string, string>()),
            [], [],
            new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" }
                .Select(aspect => new Contract.ReviewVerdictDto(aspect, "pass", "clean", "Passed."))
                .ToArray(),
            BriefSha256: BriefVersionStore.ReadOrCreate(_folder));
        RemoteReviewSettlementJournal.Write(_folder, new RemoteReviewSettlementEntry
        {
            AttemptId = "review-1", TaskKey = "task", IdempotencyKey = "report-1",
            ReportSha256 = RemoteReviewSettlementJournal.Hash(report), Report = report,
            ReceivedAtUtc = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
        });
    }

    [Fact]
    public void AgentStudioTestOnlyDiffRunsTheChangedClassAndBuild()
    {
        Write("backend.Tests/Delivery/FocusedTests.cs",
            "namespace AgentStudio.Tests;\npublic sealed class FocusedTests { [Fact] public void Runs() { } }\n");
        Git("add", ".");
        Git("commit", "-qm", "focused test baseline");
        var sha = Commit("backend.Tests/Delivery/FocusedTests.cs");

        var plan = Build(Settings(TestingStrategies.Fast), sha,
            "backend.Tests/Delivery/FocusedTests.cs");

        Assert.Contains(plan.Commands, command => command.Aspect == "build-tests"
            && command.Arguments.Contains("echo build"));
        Assert.Contains(plan.Commands, command => command.Arguments.Any(argument =>
            argument.Contains("FullyQualifiedName~FocusedTests", StringComparison.Ordinal)));
        Assert.False(plan.BuildTestDeferredToBatch);
    }

    [Theory]
    [InlineData("fast", false)]
    [InlineData("strict", true)]
    public void SharedTestHelperDefersFullProjectSuiteOnlyUnderFast(string strategy, bool fullBeforeMerge)
    {
        Write("backend.Tests/SharedFixtureTests.cs", "namespace AgentStudio.Tests;\npublic sealed class SharedFixtureTests { }\n");
        Git("add", ".");
        Git("commit", "-qm", "shared test fixture");
        var sha = Commit("backend.Tests/SharedFixtureTests.cs");

        var plan = Build(Settings(strategy), sha,
            "backend.Tests/SharedFixtureTests.cs");

        Assert.Equal(fullBeforeMerge, plan.Commands.Any(command => command.Arguments.Contains(BackendTest)));
        Assert.Contains(plan.Commands, command => command.Arguments.Contains("echo build"));
    }

    [Theory]
    [InlineData("tests/TokenEconomy.Tests/WebsiteCostFrontierDataTests.cs")]
    [InlineData("tests/TokenEconomy.Tests/TokenEconomy.Tests.csproj")]
    public void TokenEconomyTestDiffDefersFullProjectSuiteAndKeepsBuild(string changed)
    {
        Write("src/TokenEconomy/TokenEconomy.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write("tests/TokenEconomy.Tests/TokenEconomy.Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>"
            + "<ItemGroup><ProjectReference Include=\"../../src/TokenEconomy/TokenEconomy.csproj\" /></ItemGroup></Project>\n");
        Write("tests/TokenEconomy.Tests/WebsiteCostFrontierDataTests.cs",
            "namespace TokenEconomy.Tests;\npublic sealed class WebsiteCostFrontierDataTests { [Fact] public void Runs() { } }\n");
        Write(".agent-studio/project.yml",
            "schemaVersion: 1\nstack: [dotnet]\ntoolVersions:\ncommands:\n"
            + "  prepare: .agent-studio/prepare\n  build: []\n  test:\n"
            + "    - dotnet test tests/TokenEconomy.Tests/TokenEconomy.Tests.csproj --no-build --filter Category!=MachineBound\n"
            + "  lint: []\ntestSuites: []\ncachePaths: []\ncapabilities: [git]\nenvironment: {}\n");
        Git("add", ".");
        Git("commit", "-qm", "token economy project definition");
        var sha = Commit(changed);

        var plan = Build(Settings(TestingStrategies.Fast), sha, changed);

        Assert.DoesNotContain(plan.Commands, command => command.Arguments.Contains(
            "dotnet test tests/TokenEconomy.Tests/TokenEconomy.Tests.csproj --no-build --filter Category!=MachineBound"));
        Assert.Contains(plan.Commands, command => command.Arguments.Any(argument =>
            argument.Contains("dotnet build", StringComparison.Ordinal)));
    }

    [Fact]
    public void DocumentationOnlyDiffDoesNotSynthesizeACompiledBuild()
    {
        Write("docs/notes.md", "Notes.\n");
        Git("add", ".");
        Git("commit", "-qm", "documentation baseline");
        var sha = Commit("docs/notes.md");

        var plan = Build(Settings(TestingStrategies.Fast), sha, "docs/notes.md");

        Assert.DoesNotContain(plan.Commands, command => command.Arguments.Any(argument =>
            argument.StartsWith("dotnet build", StringComparison.Ordinal)));
    }

    [Fact]
    public void AffectedFrontendDiffKeepsTypeCheckAndDefersUnnarrowedUnitSuite()
    {
        var resultSha = Commit("frontend/src/app.ts");

        var plan = Build(Settings(TestingStrategies.Fast), resultSha, "frontend/src/app.ts");

        var typecheck = Assert.Single(plan.Commands, command =>
            command.Arguments.Contains("bash scripts/npm-frontend.sh run typecheck"));
        Assert.Equal("lint", typecheck.Aspect);
        Assert.Equal(Contract.ReviewBaselineModes.ExitStatus, typecheck.BaselineMode);
        Assert.DoesNotContain(plan.Commands, command => command.Arguments.Contains(FrontendUnit));
        Assert.DoesNotContain(plan.Commands, command =>
            command.Arguments.Any(argument => argument.StartsWith("dotnet test", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Fast_review_selects_the_pinned_template_owner_and_barrel_consumer(bool deferred, bool mapped)
    {
        Write("frontend/package.json", """{"scripts":{"test:ci":"node scripts/run-unit-tests.mjs"}}""");
        Write("frontend/src/app/child/child.component.ts", """
            @Component({templateUrl: './view.html'}) export class Child {}
            """);
        Write("frontend/src/app/child/view.html", "<p>Old</p>");
        Write("frontend/src/app/child/child.component.spec.ts", "it('renders', () => {});");
        Write("frontend/src/app/child/lazy.ts", "export { Child as PublicChild } from './child.component';");
        Write("frontend/src/app/parent/parent.ts", "import { PublicChild as Rendered } from '../child/lazy'; export class Parent {}");
        Write("frontend/src/app/parent/parent.spec.ts", "it('renders', () => {});");
        Write("frontend/src/app/app.spec.ts", "it('app', () => {});");
        Write("frontend/src/app/features/studio-shell/studio-shell.component.spec.ts", "it('shell', () => {});");
        Write("frontend/src/app/features/task-detail/task-detail.spec.ts", "it('detail', () => {});");
        if (mapped)
            File.AppendAllText(Path.Combine(_repo, ".agent-studio/project.yml"), """

                testSelection:
                  - paths: [frontend/]
                    suites: [frontend-unit]
                    build: [0]
                    lint: [0]
                """);
        Git("add", ".");
        Git("commit", "-qm", "frontend baseline");
        Git("switch", "-qc", "agent-studio/results/task");
        Git("mv", "frontend/src/app/child/child.component.ts", "frontend/src/app/child/renamed.component.ts");
        Git("mv", "frontend/src/app/child/child.component.spec.ts", "frontend/src/app/child/renamed.component.spec.ts");
        Write("frontend/src/app/child/lazy.ts", "export { Child as PublicChild } from './renamed.component';");
        Write("frontend/src/app/parent/result-only.spec.ts", "import { PublicChild } from '../child/lazy'; it('uses child', () => {});");
        Git("add", ".");
        Git("commit", "-qm", "rename child");
        var sha = Git("rev-parse", "HEAD").Trim();
        Git("switch", "-q", "develop");
        var changed = new[] { "frontend/src/app/child/child.component.ts", "frontend/src/app/child/renamed.component.ts",
            "frontend/src/app/child/child.component.spec.ts", "frontend/src/app/child/renamed.component.spec.ts",
            "frontend/src/app/child/view.html", "frontend/src/app/child/lazy.ts" };
        var settings = Settings(TestingStrategies.Fast);
        if (deferred) settings = settings with
        {
            BatchGate = new(Enabled: true, Scope: "code", ReviewTestLevel: "build-only"),
        };
        var plan = Builder().Build(new TaskInfo
        {
            Id = "task", TaskKey = "task", ProjectName = "project", WatchPath = _repo,
            FolderPath = _folder, State = TaskStates.AutoReview, Mode = TaskModes.Coding,
        }, _repo, settings, "develop", sha, changed, ResultRef);
        var angular = Assert.Single(plan.Commands, command => command.Arguments.Any(argument => argument.Contains("--include=")));
        var shell = string.Join(" ", angular.Arguments);
        foreach (var spec in new[] { "src/app/child/renamed.component.spec.ts", "src/app/parent/parent.spec.ts",
                     "src/app/parent/result-only.spec.ts", "src/app/app.spec.ts",
                     "src/app/features/studio-shell/studio-shell.component.spec.ts", "src/app/features/task-detail/task-detail.spec.ts" })
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(shell, System.Text.RegularExpressions.Regex.Escape(spec)).Count);
        Assert.Contains("npm run test:ci --", shell);
        Assert.DoesNotContain("child/child.component.spec.ts", shell);
        Assert.DoesNotContain(plan.Commands, command => command.Arguments.Contains(FrontendUnit));
        Assert.Equal(TestExecutionLevels.WorkPackage, plan.TestLevel);
    }

    [Fact]
    public void Strict_frontend_review_keeps_the_declared_unit_suite()
    {
        Write("frontend/src/app/app.spec.ts", "it('app', () => {});");
        var sha = Commit("frontend/src/app.ts");
        var plan = Build(Settings(TestingStrategies.Strict), sha, "frontend/src/app.ts");
        Assert.Contains(plan.Commands, command => command.Arguments.Contains(FrontendUnit));
        Assert.DoesNotContain(plan.Commands, command => command.Arguments.Any(argument => argument.Contains("--include=")));
    }

    [Theory]
    [InlineData("docs/notes.md", null)]
    [InlineData("frontend/README.md", null)]
    [InlineData("frontend/src/app/app.spec.ts", TaskModes.Research)]
    public void Documentation_and_report_only_deliveries_do_not_add_an_Angular_slice(string changed, string? mode)
    {
        Write("frontend/src/app/app.spec.ts", "it('app', () => {});");
        Git("add", ".");
        Git("commit", "-qm", "Angular test baseline");
        Write(changed, "Notes");
        var sha = Commit(changed);
        var plan = Build(Settings(TestingStrategies.Fast), sha, changed, mode);
        Assert.DoesNotContain(plan.Commands, command => command.Arguments.Any(argument => argument.Contains("--include=")));
        if (mode is null) Assert.DoesNotContain(plan.Commands, command => command.Arguments.Contains(FrontendUnit));
    }

    [Fact]
    public void FastChecksFollowTheTouchedPackages()
    {
        var verify = VerifyCommandPlanner.Plan(_repo, null);

        var backend = FastCheckPlanner.Plan(_repo, verify, ["backend/FooService.cs"]);
        Assert.Empty(backend.Commands);
        Assert.Contains("fast check type check: the build commands compile the touched .NET projects", backend.Reasons);
        Assert.Contains("fast check format: no .NET format check is declared", backend.Reasons);

        var frontend = FastCheckPlanner.Plan(_repo, verify, ["frontend/src/app.ts"]);
        var command = Assert.Single(frontend.Commands);
        Assert.Equal("bash scripts/npm-frontend.sh run typecheck", command.Command);
        Assert.Equal(VerifyCommandKind.Lint, command.Kind);
        Assert.Equal("", command.WorkingSubdir);
        Assert.Contains("fast check format: frontend/ declares no 'format:check' script", frontend.Reasons);

        // A package without a declared invocation runs its own scripts in place.
        Write("web/package.json",
            "{\"scripts\":{\"lint\":\"eslint .\",\"typecheck\":\"tsc\",\"format:check\":\"prettier --check .\"}}");
        Git("add", "web/package.json");
        var web = FastCheckPlanner.Plan(_repo, verify with { Commands = [] }, ["web/index.ts"]);
        Assert.Equal(["npm run lint", "npm run typecheck", "npm run format:check"],
            web.Commands.Select(item => item.Command));
        Assert.All(web.Commands, item => Assert.Equal("web", item.WorkingSubdir));
    }

    private Contract.ReviewPlanDto Build(ProjectSettings? settings, string resultSha, string? changed, string? mode = null,
        bool pilot = false)
        => Builder(pilot).Build(new TaskInfo
        {
            Id = "task", TaskKey = "task", ProjectName = "project",
            WatchPath = _repo, FolderPath = _folder, State = TaskStates.AutoReview,
            Mode = mode ?? TaskModes.Coding,
        }, _repo, settings, "develop", resultSha, changed is null ? null : [changed], ResultRef);

    private static ProjectSettings Settings(string strategy)
        => new() { Testing = new ProjectTestingSettings { Strategy = strategy } };

    // The deep-review receipt names its own attempt; the rest of the plan is
    // the byte-identity subject.
    private static string Json(Contract.ReviewPlanDto plan)
        => JsonSerializer.Serialize(plan with { DeepReviewDecision = null });

    private static RemoteReviewPlanBuilder Builder(bool pilot = false)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteReview:UseBudgetedDotNetScope"] = pilot.ToString(),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        return new RemoteReviewPlanBuilder(aspects, config, git, new FakeTimeProvider());
    }

    private void Repository()
    {
        Git("init", "-q", "-b", "develop");
        Git("config", "user.email", "per-card@example.invalid");
        Git("config", "user.name", "Per Card");
        Git("config", "core.autocrlf", "false");
        Write(".agent-studio/prepare", "#!/bin/sh\nexit 0\n");
        Write(".agent-studio/project.yml",
            "schemaVersion: 1\nstack: [custom]\ntoolVersions:\ncommands:\n"
            + "  prepare: .agent-studio/prepare\n  build:\n    - echo build\n"
            + "    - bash scripts/check-release-build.sh\n  test:\n"
            + $"    - {BackendTest}\n    - {FrontendUnit}\n"
            + "  lint:\n    - bash scripts/npm-frontend.sh run lint\n"
            + "testSuites:\n  - id: frontend-unit\n    category: routine\n    expectedDurationSeconds: 360\n"
            + $"    command: {FrontendUnit}\n"
            + "cachePaths:\ncapabilities: [linux]\nenvironment:\n");
        Write("scripts/npm-frontend.sh", "#!/bin/sh\nexit 0\n");
        Write("scripts/check-release-build.sh", "#!/bin/sh\nexit 0\n");
        Write("frontend/package.json",
            "{\"scripts\":{\"lint\":\"ng lint\",\"typecheck\":\"tsc --noEmit\",\"test:ci\":\"ng test\"}}");
        Write("frontend/src/app.ts", "export const app = 1;\n");
        Write("global.json", "{}\n");
        Write("backend/OrchestratorApi.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write("runner/AgentRunner.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write("backend/FooService.cs", "namespace AgentStudio;\npublic sealed class FooService { }\n");
        Write("backend.Tests/OrchestratorApi.Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
            + "  <ItemGroup><ProjectReference Include=\"..\\backend\\OrchestratorApi.csproj\" /></ItemGroup>\n"
            + "</Project>\n");
        Write("backend.Tests/FooServiceTests.cs",
            "namespace AgentStudio.Tests;\npublic sealed class FooServiceTests\n{\n"
            + "    [Fact]\n    public void Runs() => _ = new FooService();\n}\n");
        foreach (var name in new[] { "BarTests", "BazTests", "QuxTests" })
            Write($"backend.Tests/{name}.cs",
                $"namespace AgentStudio.Tests;\npublic sealed class {name}\n{{\n    [Fact]\n    public void Runs() {{ }}\n}}\n");
        Git("add", ".");
        Git("commit", "-qm", "base");
    }

    /// <summary>Commits a change to <paramref name="path"/> on the result branch and returns to develop.</summary>
    private string Commit(string path)
    {
        Git("switch", "-qc", "agent-studio/results/task");
        File.AppendAllText(Path.Combine(_repo, path), path.EndsWith(".json", StringComparison.Ordinal)
            ? "\n" : path.EndsWith(".csproj", StringComparison.Ordinal) ? "<!-- changed -->\n" : "// changed\n");
        Git("add", ".");
        Git("commit", "-qm", "delivery");
        var sha = Git("rev-parse", "HEAD").Trim();
        Git("switch", "-q", "develop");
        return sha;
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output;
    }
}
