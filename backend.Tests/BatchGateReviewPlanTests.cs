using System.Diagnostics;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateReviewPlanTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20_000);

    [Fact]
    public void Deep_review_size_uses_the_merge_base_diff_line_count()
    {
        var fixture = CreateFixture();
        try
        {
            Git(fixture.Repo, "switch", "-qc", "agent-studio/results/deep-size");
            File.WriteAllText(Path.Combine(fixture.Repo, "change.txt"), "one\ntwo\nthree\n");
            Git(fixture.Repo, "add", ".");
            Git(fixture.Repo, "commit", "-qm", "delivery");
            var resultSha = Git(fixture.Repo, "rev-parse", "HEAD").Trim();
            Git(fixture.Repo, "switch", "-q", "develop");
            Git(fixture.Repo, "update-ref", "refs/remotes/origin/agent-studio/results/deep-size", resultSha);
            Git(fixture.Repo, "update-ref", "-d", "refs/heads/agent-studio/results/deep-size");
            Assert.Null(fixture.Git.ChangedLinesAgainstMergeBase(
                fixture.Repo, "develop", "refs/heads/agent-studio/results/deep-size"));
            Assert.Equal(3, fixture.Git.ChangedLinesAgainstMergeBase(fixture.Repo, "develop", resultSha));
            var plan = fixture.Builder.Build(fixture.Task, fixture.Repo, new ProjectSettings(),
                "develop", resultSha, null, "refs/heads/agent-studio/results/deep-size");
            Assert.NotNull(plan.DeepReviewDecision);
            Assert.False(plan.DeepReviewDecision.Selected);
            Assert.DoesNotContain("diff-size-unavailable", plan.DeepReviewDecision.Grounds);
        }
        finally { DeleteDirectory(fixture.Root); }
    }

    [Fact]
    public void Corrupt_round_budget_does_not_stop_review_plan_creation()
    {
        var fixture = CreateFixture();
        try
        {
            File.WriteAllText(Path.Combine(fixture.Folder, ReviewRoundBudgetStore.FileName), "{broken");
            var plan = fixture.Builder.Build(fixture.Task, fixture.Repo, new ProjectSettings(),
                "develop", new string('a', 40), ["change.txt"], "refs/heads/agent-studio/results/task");
            Assert.NotNull(plan.DeepReviewDecision);
            Assert.False(plan.DeepReviewDecision.Selected);
        }
        finally { DeleteDirectory(fixture.Root); }
    }

    [Fact]
    public void DocumentationOnlyResultDefersBuildTestsButKeepsModelReview()
    {
        var fixture = CreateFixture();
        var (repo, folder, builder, task) = (fixture.Repo, fixture.Folder, fixture.Builder, fixture.Task);
        try
        {
            Git(repo, "switch", "-qc", "agent-studio/results/task");
            Directory.CreateDirectory(Path.Combine(repo, "docs"));
            File.WriteAllText(Path.Combine(repo, "docs", "pilot.md"), "Pilot notes.\n");
            Git(repo, "add", ".");
            Git(repo, "commit", "-qm", "docs");
            var resultSha = Git(repo, "rev-parse", "HEAD").Trim();
            Git(repo, "switch", "-q", "develop");

            var resultRef = "refs/heads/agent-studio/results/task";
            var plan = builder.Build(task, repo,
                new ProjectSettings { BatchGate = new(Enabled: true) }, "develop",
                resultSha, ["docs/pilot.md"], resultRef);

            Assert.True(plan.BuildTestDeferredToBatch);
            Assert.Equal("build-only", plan.TestLevel);
            Assert.NotNull(plan.TestSelectionDigest);
            Assert.Contains(plan.Commands, command => command.Arguments.Contains("echo build"));
            Assert.Contains(plan.Commands, command =>
                AgentStudio.TaskServer.Contracts.ReviewCommandKinds.IsAgent(command.ExecutionKind));
            Assert.DoesNotContain(plan.Commands, command => command.Arguments.Contains("echo test"));

            Git(repo, "switch", "-qc", "agent-studio/results/code-task");
            Directory.CreateDirectory(Path.Combine(repo, "backend"));
            File.WriteAllText(Path.Combine(repo, "backend", "Pilot.cs"), "class Pilot {}\n");
            Git(repo, "add", ".");
            Git(repo, "commit", "-qm", "code");
            var codeSha = Git(repo, "rev-parse", "HEAD").Trim();
            ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
            {
                TaskKey = "task", RunAttemptId = "run-code", AttemptChainId = "chain-code",
                Project = "project", Repository = "repo", ResultSha = codeSha,
                ImmutableResultRef = "refs/heads/agent-studio/results/code-task",
                CompletedAtUtc = Now,
            });
            var codePlan = builder.Build(task, repo,
                new ProjectSettings { BatchGate = new(Enabled: true) }, "develop");
            Assert.False(codePlan.BuildTestDeferredToBatch);
            Assert.Contains(codePlan.Commands, command => command.Aspect == "build-tests");

            // The real completion path builds before writing ReviewSubjectStore.
            // An explicit result envelope must therefore be sufficient for a
            // code-scoped plan as well.
            ReviewSubjectStore.InvalidateForNewAttempt(folder);
            var codeBatch = builder.Build(task, repo,
                new ProjectSettings { BatchGate = new(Enabled: true,
                    Scope: "code", ReviewTestLevel: "build-only") }, "develop",
                codeSha, ["backend/Pilot.cs"],
                "refs/heads/agent-studio/results/code-task");
            Assert.True(codeBatch.BuildTestDeferredToBatch);
            Assert.Equal("build-only", codeBatch.TestLevel);
            var changedInventory = builder.Build(task, repo,
                new ProjectSettings { BatchGate = new(Enabled: true,
                    Scope: "code") }, "develop", codeSha,
                [".agent-studio/project.yml"],
                "refs/heads/agent-studio/results/code-task");
            Assert.False(changedInventory.BuildTestDeferredToBatch);
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    /// <summary>
    /// AGT-3085: a deferred plan for a backend-only delivery runs the test
    /// classes that name the changed types, read from the exact result commit
    /// through one <c>git cat-file --batch</c>. The added test file exists only
    /// at the result SHA, and its non-ASCII comment precedes the changed
    /// production file in the batch output, so a decoded-string parser would
    /// read the wrong bytes for it.
    /// </summary>
    [Fact]
    public void BackendDeliveryDefersWithAClassFilteredTestCommandReadFromTheResultCommit()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-plan-test-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        var folder = Path.Combine(root, "task");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(folder);
        try
        {
            var resultRef = BackendRepository(repo, folder);
            File.WriteAllText(Path.Combine(repo, "backend", "FooService.cs"),
                "// Größenprüfung für den Dienst, 日本語のコメント\n"
                + "namespace AgentStudio;\npublic sealed class FooService { }\n"
                + "public sealed record FooReceipt(int Id);\n");
            // Its own folder: a changed test file also selects its directory
            // siblings, and this fixture's project root is small.
            Directory.CreateDirectory(Path.Combine(repo, "backend.Tests", "Delivery"));
            File.WriteAllText(Path.Combine(repo, "backend.Tests", "Delivery", "NewFooTests.cs"),
                "// Prüfung: überträgt Größen, ½ und 日本\n"
                + "namespace AgentStudio.Tests;\npublic sealed class NewFooTests\n{\n"
                + "    [Fact]\n    public void Runs() => _ = new FooService();\n}\n");
            Git(repo, "add", ".");
            Git(repo, "commit", "-qm", "backend change");
            var resultSha = Git(repo, "rev-parse", "HEAD").Trim();
            Git(repo, "switch", "-q", "develop");
            Assert.False(File.Exists(Path.Combine(repo, "backend.Tests", "Delivery", "NewFooTests.cs")));

            var plan = PlanBuilder().Build(AutoReviewTask(repo, folder), repo,
                new ProjectSettings { BatchGate = new(Enabled: true, Scope: "code") }, "develop",
                resultSha, ["backend.Tests/Delivery/NewFooTests.cs", "backend/FooService.cs"], resultRef);

            Assert.True(plan.BuildTestDeferredToBatch);
            Assert.Equal("work-package", plan.TestLevel);
            var test = Assert.Single(plan.Commands, command =>
                command.Arguments.Any(argument => argument.Contains("dotnet test", StringComparison.Ordinal)));
            Assert.Equal(
                "dotnet test \"backend.Tests/OrchestratorApi.Tests.csproj\" --filter "
                + "\"(Category!=MachineBound)&(FullyQualifiedName~FooServiceTests"
                + "|FullyQualifiedName~NewFooTests|FullyQualifiedName~ReceiptTests)\"",
                test.Arguments[1]);

            var limited = AgentStudio.TaskServer.Contracts.ReviewPlanResourcePolicy.Apply(plan);
            var shell = Assert.Single(limited.Commands, command => command.StepId == test.StepId).Arguments[1];
            var filter = System.Text.RegularExpressions.Regex.Match(shell, "--filter (\"[^\"]*\")(?:\\s|$)");
            Assert.True(filter.Success, shell);
            Assert.Equal(
                "\"(Category!=MachineBound)&(FullyQualifiedName~FooServiceTests"
                + "|FullyQualifiedName~NewFooTests|FullyQualifiedName~ReceiptTests)\"",
                filter.Groups[1].Value);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// AGT-3085: when no test class names the only changed type and no other
    /// test command remains, the deferred plan would verify nothing, so the
    /// review keeps the ordinary per-card gate.
    /// </summary>
    [Fact]
    public void UnreferencedProductionChangeWithNoOtherTestCommandKeepsDeferralOff()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-plan-test-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        var folder = Path.Combine(root, "task");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(folder);
        try
        {
            var resultRef = BackendRepository(repo, folder);
            File.WriteAllText(Path.Combine(repo, "backend", "RunnerReleaseIdentity.cs"),
                "namespace AgentStudio;\ninternal static class RunnerReleaseIdentity { }\n");
            Git(repo, "add", ".");
            Git(repo, "commit", "-qm", "unreferenced change");
            var resultSha = Git(repo, "rev-parse", "HEAD").Trim();
            Git(repo, "switch", "-q", "develop");

            var plan = PlanBuilder().Build(AutoReviewTask(repo, folder), repo,
                new ProjectSettings { BatchGate = new(Enabled: true, Scope: "code") }, "develop",
                resultSha, ["backend/RunnerReleaseIdentity.cs"], resultRef);

            Assert.False(plan.BuildTestDeferredToBatch);
            Assert.Null(plan.TestLevel);
            Assert.Contains(plan.Commands, command => command.Aspect == "build-tests");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// AGT-3108: OrchestratorApi.Tests references task-server only so that
    /// ConnectorNegativeMatrixTests can start its built output. A deferred plan
    /// for a task-server-only result runs that class instead of the whole test
    /// project.
    /// </summary>
    [Fact]
    public void TaskServerOnlyDeliveryDefersWithAClassFilteredOrchestratorApiTestsCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-plan-test-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        var folder = Path.Combine(root, "task");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(folder);
        try
        {
            var resultRef = BackendRepository(repo, folder, withTaskServer: true);
            File.WriteAllText(Path.Combine(repo, "task-server", "LeaseStore.cs"),
                "namespace AgentStudio.TaskServer;\npublic sealed class LeaseStore { public int Version => 2; }\n");
            Git(repo, "add", ".");
            Git(repo, "commit", "-qm", "task-server change");
            var resultSha = Git(repo, "rev-parse", "HEAD").Trim();
            Git(repo, "switch", "-q", "develop");

            var plan = PlanBuilder().Build(AutoReviewTask(repo, folder), repo,
                new ProjectSettings { BatchGate = new(Enabled: true, Scope: "code") }, "develop",
                resultSha, ["task-server/LeaseStore.cs"], resultRef);

            Assert.True(plan.BuildTestDeferredToBatch);
            Assert.Equal("work-package", plan.TestLevel);
            var test = Assert.Single(plan.Commands, command =>
                command.Arguments.Any(argument => argument.Contains("dotnet test", StringComparison.Ordinal)));
            Assert.Equal(
                "dotnet test \"backend.Tests/OrchestratorApi.Tests.csproj\" --filter "
                + "\"(Category!=MachineBound)&(FullyQualifiedName~ConnectorNegativeMatrixTests)\"",
                test.Arguments[1]);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// A develop checkout with one backend project, its test project, and a
    /// result branch checked out for the delivery's commit. With
    /// <paramref name="withTaskServer"/>, the test project also references a
    /// task-server project at process level, as the real one does.
    /// </summary>
    private static string BackendRepository(string repo, string folder, bool withTaskServer = false)
    {
        Git(repo, "init", "-q", "-b", "develop");
        Git(repo, "config", "user.email", "batch-test@example.invalid");
        Git(repo, "config", "user.name", "Batch Test");
        Git(repo, "config", "core.autocrlf", "false");
        Directory.CreateDirectory(Path.Combine(repo, ".agent-studio"));
        File.WriteAllText(Path.Combine(repo, ".agent-studio", "prepare"), "#!/bin/sh\nexit 0\n");
        File.WriteAllText(Path.Combine(repo, ".agent-studio", "project.yml"),
            "schemaVersion: 1\nstack: [custom]\ntoolVersions:\ncommands:\n"
            + "  prepare: .agent-studio/prepare\n  build: [echo build]\n  test: [echo test]\n"
            + "  lint:\ntestSuites:\ncachePaths:\ncapabilities: [linux]\nenvironment:\n");
        Directory.CreateDirectory(Path.Combine(repo, "backend"));
        Directory.CreateDirectory(Path.Combine(repo, "backend.Tests"));
        File.WriteAllText(Path.Combine(repo, "backend", "OrchestratorApi.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        File.WriteAllText(Path.Combine(repo, "backend", "FooService.cs"),
            "namespace AgentStudio;\npublic sealed class FooService { }\n");
        var processReference = withTaskServer
            ? "<ProjectReference Include=\"..\\task-server\\TaskServer.csproj\" ReferenceOutputAssembly=\"false\" />"
            : "";
        File.WriteAllText(Path.Combine(repo, "backend.Tests", "OrchestratorApi.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
            + "  <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>\n"
            + "  <ItemGroup><ProjectReference Include=\"..\\backend\\OrchestratorApi.csproj\" />"
            + processReference + "</ItemGroup>\n"
            + "</Project>\n");
        if (withTaskServer)
        {
            Directory.CreateDirectory(Path.Combine(repo, "task-server"));
            File.WriteAllText(Path.Combine(repo, "task-server", "TaskServer.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n"
                + "  <PropertyGroup><AssemblyName>task-server</AssemblyName></PropertyGroup>\n"
                + "</Project>\n");
            File.WriteAllText(Path.Combine(repo, "task-server", "LeaseStore.cs"),
                "namespace AgentStudio.TaskServer;\npublic sealed class LeaseStore { }\n");
            File.WriteAllText(Path.Combine(repo, "backend.Tests", "ConnectorNegativeMatrixTests.cs"),
                "namespace AgentStudio.Tests;\n[Trait(\"ProcessReference\", \"TaskServer\")]\n"
                + "public sealed class ConnectorNegativeMatrixTests\n{\n    [Fact]\n"
                + "    public void Runs() => BuiltProcessLauncher.StartBuilt(\".\", \"task-server\", \"task-server.dll\");\n}\n");
        }
        File.WriteAllText(Path.Combine(repo, "backend.Tests", "FooServiceTests.cs"),
            "namespace AgentStudio.Tests;\npublic sealed class FooServiceTests\n{\n"
            + "    [Fact]\n    public void Runs() => _ = new FooService();\n}\n");
        // Names a type that only the delivery's version of FooService.cs declares.
        File.WriteAllText(Path.Combine(repo, "backend.Tests", "ReceiptTests.cs"),
            "namespace AgentStudio.Tests;\npublic sealed class ReceiptTests\n{\n"
            + "    [Fact]\n    public void Runs() => _ = new FooReceipt(1);\n}\n");
        foreach (var name in new[] { "BarTests", "BazTests", "QuxTests", "QuuxTests" })
        {
            File.WriteAllText(Path.Combine(repo, "backend.Tests", $"{name}.cs"),
                $"namespace AgentStudio.Tests;\npublic sealed class {name}\n{{\n    [Fact]\n    public void Runs() {{ }}\n}}\n");
        }
        Git(repo, "add", ".");
        Git(repo, "commit", "-qm", "base");
        Git(repo, "switch", "-qc", "agent-studio/results/backend-task");
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Change the backend.\n");
        return "refs/heads/agent-studio/results/backend-task";
    }

    private static RemoteReviewPlanBuilder PlanBuilder()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        return new RemoteReviewPlanBuilder(aspects, config, git);
    }

    private static TaskInfo AutoReviewTask(string repo, string folder) => new()
    {
        Id = "task", TaskKey = "task", ProjectName = "project",
        WatchPath = repo, FolderPath = folder, State = TaskStates.AutoReview,
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResultEqualToDevelopGetsNoChangePlan(bool batchGate)
    {
        var fixture = CreateFixture(LintCommand);
        try
        {
            Git(fixture.Repo, "branch", "agent-studio/results/task");
            var resultSha = Git(fixture.Repo, "rev-parse", "develop").Trim();
            var settings = new ProjectSettings
            {
                BatchGate = new(Enabled: batchGate, Scope: "code"),
            };

            var plan = fixture.Builder.Build(fixture.Task, fixture.Repo, settings, "develop",
                resultSha, immutableResultRef: ResultRef);

            AssertNoChangePlan(plan, OrdinaryPlan(fixture, settings));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [Fact]
    public void ResultAlreadyContainedInDevelopGetsNoChangePlan()
    {
        var fixture = CreateFixture(LintCommand);
        try
        {
            Git(fixture.Repo, "branch", "agent-studio/results/task");
            var resultSha = Git(fixture.Repo, "rev-parse", "develop").Trim();
            File.WriteAllText(Path.Combine(fixture.Repo, "later.md"), "Later develop work.\n");
            Git(fixture.Repo, "add", ".");
            Git(fixture.Repo, "commit", "-qm", "later");
            var settings = new ProjectSettings { BatchGate = new(Enabled: true, Scope: "code") };

            var plan = fixture.Builder.Build(fixture.Task, fixture.Repo, settings, "develop",
                resultSha, immutableResultRef: ResultRef);

            AssertNoChangePlan(plan, OrdinaryPlan(fixture, settings));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [Fact]
    public void NoChangePlanWithoutAgentAspectKeepsTheCompletionCommand()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReviewDecisionOrchestrator:AspectRunners:0"] = "not-a-catalogue-aspect",
        }).Build();
        var fixture = CreateFixture(LintCommand, config);
        try
        {
            var resultSha = Git(fixture.Repo, "rev-parse", "develop").Trim();

            var plan = fixture.Builder.Build(fixture.Task, fixture.Repo, new ProjectSettings(),
                "develop", resultSha, [], ResultRef);

            var command = Assert.Single(plan.Commands);
            Assert.Equal("verify-subject", command.StepId);
            Assert.Equal("completion", command.Aspect);
            Assert.Equal(new[] { "completion" }, plan.RequiredAspects);
            Assert.Empty(plan.Preparation ?? []);
            Assert.Contains(plan.SkippedAspects!, skip => skip.Aspect == "build-tests");
            Assert.Contains(plan.SkippedAspects!, skip => skip.Aspect == "lint");
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [Fact]
    public void UncomputableDiffKeepsTheFullPlan()
    {
        var fixture = CreateFixture(LintCommand);
        try
        {
            var resultSha = new string('f', 40);
            var settings = new ProjectSettings { BatchGate = new(Enabled: true, Scope: "code") };

            // Neither the immutable result SHA nor its ref is present in this
            // checkout, so the diff is unknown rather than a no-change result.
            var plan = fixture.Builder.Build(fixture.Task, fixture.Repo, settings, "develop",
                resultSha, null, "refs/heads/agent-studio/results/missing");

            var ordinary = OrdinaryPlan(fixture, settings);
            Assert.Equal(ToolSteps(ordinary), ToolSteps(plan));
            Assert.Equal(PreparationSteps(ordinary), PreparationSteps(plan));
            Assert.Contains(plan.Commands, command => command.Aspect == "build-tests");
            Assert.Contains(plan.Commands, command => command.Aspect == "lint");
            Assert.DoesNotContain(plan.SkippedAspects ?? [], skip => skip.Aspect is "build-tests" or "lint");
            Assert.False(plan.BuildTestDeferredToBatch);
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [Theory]
    [InlineData("docs/pilot.md", false)]
    [InlineData("docs/pilot.md", true)]
    [InlineData("backend/Pilot.cs", false)]
    [InlineData("backend/Pilot.cs", true)]
    public void OneChangedFileKeepsTheOrdinaryPlan(string path, bool batchGate)
    {
        var fixture = CreateFixture(LintCommand);
        try
        {
            Git(fixture.Repo, "switch", "-qc", "agent-studio/results/task");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fixture.Repo, path))!);
            File.WriteAllText(Path.Combine(fixture.Repo, path), "Changed.\n");
            Git(fixture.Repo, "add", ".");
            Git(fixture.Repo, "commit", "-qm", "change");
            var resultSha = Git(fixture.Repo, "rev-parse", "HEAD").Trim();
            Git(fixture.Repo, "switch", "-q", "develop");
            var settings = new ProjectSettings
            {
                BatchGate = new(Enabled: batchGate, Scope: "code", ReviewTestLevel: "build-only"),
            };

            var plan = fixture.Builder.Build(fixture.Task, fixture.Repo, settings, "develop",
                resultSha, [path], ResultRef);

            Assert.Equal(batchGate, plan.BuildTestDeferredToBatch);
            Assert.NotEmpty(plan.Preparation ?? []);
            Assert.DoesNotContain(plan.SkippedAspects ?? [], skip =>
                skip.Aspect is "build-tests" or "review-verification");
            Assert.Contains(plan.Commands, command => command.Arguments.Contains("echo build"));
            if (batchGate)
            {
                Assert.Equal("build-only", plan.TestLevel);
                Assert.Contains(plan.Commands, command => command.Aspect == "review-verification");
                Assert.DoesNotContain(plan.Commands, command => command.Arguments.Contains("echo test"));
            }
            else
            {
                Assert.Null(plan.TestLevel);
                var ordinary = OrdinaryPlan(fixture, settings);
                Assert.Equal(ToolSteps(ordinary), ToolSteps(plan));
                Assert.Equal(PreparationSteps(ordinary), PreparationSteps(plan));
                Assert.Contains(plan.Commands, command => command.Arguments.Contains("echo test"));
            }
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [Theory]
    [InlineData(0, ValidSha, ResultRef, true)]
    [InlineData(-1, ValidSha, ResultRef, false)]
    [InlineData(1, ValidSha, ResultRef, false)]
    [InlineData(0, "abc123", ResultRef, false)]
    [InlineData(0, null, ResultRef, false)]
    [InlineData(0, ValidSha, null, false)]
    [InlineData(0, ValidSha, " ", false)]
    public void NoChangeResultNeedsAnEmptyComputedDiffAValidShaAndAResultRef(
        int changedCount, string? resultSha, string? resultRef, bool expected)
    {
        IReadOnlyList<string>? changed = changedCount < 0
            ? null
            : Enumerable.Range(0, changedCount).Select(index => $"file-{index}.cs").ToArray();

        Assert.Equal(expected, RemoteReviewPlanBuilder.IsNoChangeResult(changed, resultSha, resultRef));
    }

    [Fact]
    public void ReplanDiffsTheResultShaSoARemoteOnlyResultRefStaysNoChange()
    {
        var fixture = CreateFixture(LintCommand);
        try
        {
            var resultSha = Git(fixture.Repo, "rev-parse", "develop").Trim();
            Git(fixture.Repo, "update-ref", "refs/remotes/origin/agent-studio/results/task", resultSha);
            var subject = new ReviewSubjectDto(
                "subject", "repo", resultSha, "run", "requirements", "policy", [],
                Now.UtcDateTime, ResultRef: ResultRef);

            // The old replan diffed the local result ref, which this checkout
            // does not hold, and so rebuilt the full plan.
            Assert.Null(RemoteReviewPlanBuilder.DeliveryChangedFiles(
                fixture.Git, fixture.Repo, "develop", subject.ResultRef));
            var changed = AgentStudio.Projects.ProjectSettingsEndpoints.ReplanChangedFiles(
                fixture.Git, fixture.Repo, "develop", subject);
            Assert.NotNull(changed);
            Assert.Empty(changed);
            // A legacy subject without a result ref keeps its diff-independent plan.
            Assert.Null(AgentStudio.Projects.ProjectSettingsEndpoints.ReplanChangedFiles(
                fixture.Git, fixture.Repo, "develop", subject with { ResultRef = null }));

            var settings = new ProjectSettings { BatchGate = new(Enabled: true, Scope: "code") };
            var plan = fixture.Builder.Build(fixture.Task, fixture.Repo, settings, "develop",
                subject.ExpectedResultSha, changed, subject.ResultRef);
            AssertNoChangePlan(plan, OrdinaryPlan(fixture, settings));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ContinuationRoundWithoutCommitOnAnUnintegratedResultKeepsTheFullPlan(bool batchGate)
    {
        var fixture = CreateFixture(LintCommand);
        try
        {
            // The previous round's result changes code and is not on develop.
            // The continuation round picked it up as its base and made no
            // commit, so base == result == that unintegrated commit.
            Git(fixture.Repo, "switch", "-qc", "agent-studio/results/task");
            Directory.CreateDirectory(Path.Combine(fixture.Repo, "backend"));
            File.WriteAllText(Path.Combine(fixture.Repo, "backend", "Pilot.cs"), "class Pilot {}\n");
            Git(fixture.Repo, "add", ".");
            Git(fixture.Repo, "commit", "-qm", "previous round");
            var resultSha = Git(fixture.Repo, "rev-parse", "HEAD").Trim();
            Git(fixture.Repo, "switch", "-q", "develop");
            var pickupBaseSha = resultSha;

            // The pickup-base diff alone is empty and would read as no change.
            Assert.Empty(RemoteReviewPlanBuilder.DeliveryChangedFiles(
                fixture.Git, fixture.Repo, pickupBaseSha, resultSha)!);
            var changed = RemoteReviewPlanBuilder.CompletionChangedFiles(
                fixture.Git, fixture.Repo, pickupBaseSha, "develop", resultSha);
            Assert.Equal(new[] { "backend/Pilot.cs" }, changed);

            var settings = new ProjectSettings
            {
                BatchGate = new(Enabled: batchGate, Scope: "code", ReviewTestLevel: "build-only"),
            };
            var plan = fixture.Builder.Build(fixture.Task, fixture.Repo, settings, "develop",
                resultSha, changed, ResultRef);

            // A batch-deferred code plan runs its build as review-verification.
            Assert.Contains(plan.Commands, command =>
                command.Aspect == (batchGate ? "review-verification" : "build-tests"));
            Assert.Contains(plan.Commands, command => command.Aspect == "lint");
            Assert.NotEmpty(plan.Preparation ?? []);
            Assert.DoesNotContain(plan.SkippedAspects ?? [], skip =>
                skip.Aspect is "build-tests" or "lint" or "review-verification");
            Assert.Equal(batchGate, plan.BuildTestDeferredToBatch);
            if (!batchGate)
            {
                var ordinary = OrdinaryPlan(fixture, settings);
                Assert.Equal(ToolSteps(ordinary), ToolSteps(plan));
                Assert.Equal(PreparationSteps(ordinary), PreparationSteps(plan));
            }

            // A replan or review retry diffs the same SHA against develop and
            // must freeze the same plan as the completion path (AGT-2827).
            var replan = fixture.Builder.Build(fixture.Task, fixture.Repo, settings, "develop",
                resultSha,
                RemoteReviewPlanBuilder.DeliveryChangedFiles(fixture.Git, fixture.Repo, "develop", resultSha),
                ResultRef);
            Assert.Equal(ToolSteps(replan), ToolSteps(plan));
            Assert.Equal(PreparationSteps(replan), PreparationSteps(plan));
            Assert.Equal(replan.BuildTestDeferredToBatch, plan.BuildTestDeferredToBatch);
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [Fact]
    public void CompletionChangedFilesRemeasuresOnlyAnEmptyPickupDiff()
    {
        var fixture = CreateFixture(LintCommand);
        try
        {
            // A result equal to develop stays no change through the completion path.
            var developSha = Git(fixture.Repo, "rev-parse", "develop").Trim();
            Assert.Empty(RemoteReviewPlanBuilder.CompletionChangedFiles(
                fixture.Git, fixture.Repo, developSha, "develop", developSha)!);
            Assert.Empty(RemoteReviewPlanBuilder.CompletionChangedFiles(
                fixture.Git, fixture.Repo, null, "develop", developSha)!);

            // A round that commits on top of an unintegrated previous result
            // keeps today's pickup-base diff.
            Git(fixture.Repo, "switch", "-qc", "agent-studio/results/task");
            Directory.CreateDirectory(Path.Combine(fixture.Repo, "backend"));
            File.WriteAllText(Path.Combine(fixture.Repo, "backend", "Pilot.cs"), "class Pilot {}\n");
            Git(fixture.Repo, "add", ".");
            Git(fixture.Repo, "commit", "-qm", "previous round");
            var previousSha = Git(fixture.Repo, "rev-parse", "HEAD").Trim();
            Directory.CreateDirectory(Path.Combine(fixture.Repo, "docs"));
            File.WriteAllText(Path.Combine(fixture.Repo, "docs", "pilot.md"), "Pilot notes.\n");
            Git(fixture.Repo, "add", ".");
            Git(fixture.Repo, "commit", "-qm", "this round");
            var resultSha = Git(fixture.Repo, "rev-parse", "HEAD").Trim();
            Assert.Equal(new[] { "docs/pilot.md" }, RemoteReviewPlanBuilder.CompletionChangedFiles(
                fixture.Git, fixture.Repo, previousSha, "develop", resultSha));

            // An integration ref this checkout cannot resolve keeps the diff
            // uncomputable, so the plan stays full.
            Assert.Null(RemoteReviewPlanBuilder.CompletionChangedFiles(
                fixture.Git, fixture.Repo, previousSha, "refs/heads/missing", previousSha));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    private const string ResultRef = "refs/heads/agent-studio/results/task";
    private const string ValidSha = "e21984dc539cdac298866477913e333f27a7154b";
    // An npm lint command plans an `npm ci` preparation step for the tracked
    // tools/package.json, so the ordinary plan carries a preparation to drop.
    private const string LintCommand = "  lint: [npm --prefix tools run lint]\n";

    private static readonly string[] ToolAspects = ["build-tests", "lint", "review-verification"];

    private static void AssertNoChangePlan(
        AgentStudio.TaskServer.Contracts.ReviewPlanDto plan,
        AgentStudio.TaskServer.Contracts.ReviewPlanDto ordinary)
    {
        // The fixture's ordinary plan carries both tool aspects and preparation,
        // so the assertions below prove they were dropped, not never planned.
        Assert.Contains(ordinary.Commands, command => command.Aspect == "build-tests");
        Assert.Contains(ordinary.Commands, command => command.Aspect == "lint");
        Assert.NotEmpty(ordinary.Preparation ?? []);

        Assert.DoesNotContain(plan.Commands, command =>
            ToolAspects.Contains(command.Aspect) || command.StepId.StartsWith("compose-render-"));
        Assert.Empty(plan.Preparation ?? []);
        Assert.DoesNotContain(plan.RequiredAspects, aspect => ToolAspects.Contains(aspect));
        foreach (var aspect in new[] { "build-tests", "lint" })
        {
            var skip = Assert.Single(plan.SkippedAspects!, item => item.Aspect == aspect);
            Assert.Contains("merge base with develop", skip.Reason);
        }
        Assert.Contains(plan.Commands, command =>
            command.Aspect == "requirement-fit"
            && AgentStudio.TaskServer.Contracts.ReviewCommandKinds.IsAgent(command.ExecutionKind));
        Assert.False(plan.BuildTestDeferredToBatch);
        Assert.Null(plan.TestLevel);
        Assert.Null(plan.TestSelectionDigest);
        Assert.Equal(ordinary.IntegrationRef, plan.IntegrationRef);
        Assert.Equal(ordinary.BuildProfileFingerprint, plan.BuildProfileFingerprint);
        Assert.Equal(ordinary.LibraryVersion, plan.LibraryVersion);
        Assert.Equal(ordinary.PreserveGlobs, plan.PreserveGlobs);
    }

    // The plan the builder froze before AGT-3079 when it could not tell the
    // result's diff: no git service, no changed-file list.
    private static AgentStudio.TaskServer.Contracts.ReviewPlanDto OrdinaryPlan(
        PlanFixture fixture,
        ProjectSettings settings)
        => new RemoteReviewPlanBuilder(fixture.Aspects, fixture.Config)
            .Build(fixture.Task, fixture.Repo, settings, "develop");

    private static string[] ToolSteps(AgentStudio.TaskServer.Contracts.ReviewPlanDto plan)
        => plan.Commands
            .Where(command => !AgentStudio.TaskServer.Contracts.ReviewCommandKinds.IsAgent(command.ExecutionKind))
            .Select(command => $"{command.StepId}:{command.Aspect}:{string.Join(' ', command.Arguments)}")
            .ToArray();

    private static string[] PreparationSteps(AgentStudio.TaskServer.Contracts.ReviewPlanDto plan)
        => (plan.Preparation ?? [])
            .Select(command => $"{command.StepId}:{string.Join(' ', command.Arguments)}")
            .ToArray();

    private sealed record PlanFixture(
        string Root,
        string Repo,
        string Folder,
        IConfiguration Config,
        GitService Git,
        AspectRunnerService Aspects,
        RemoteReviewPlanBuilder Builder,
        TaskInfo Task);

    private static PlanFixture CreateFixture(string lint = "  lint:\n", IConfiguration? config = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-plan-test-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        var folder = Path.Combine(root, "task");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(folder);
        Git(repo, "init", "-q", "-b", "develop");
        Git(repo, "config", "user.email", "batch-test@example.invalid");
        Git(repo, "config", "user.name", "Batch Test");
        Directory.CreateDirectory(Path.Combine(repo, ".agent-studio"));
        File.WriteAllText(Path.Combine(repo, ".agent-studio", "prepare"), "#!/bin/sh\nexit 0\n");
        File.WriteAllText(Path.Combine(repo, ".agent-studio", "project.yml"),
            "schemaVersion: 1\nstack: [custom]\ntoolVersions:\ncommands:\n"
            + "  prepare: .agent-studio/prepare\n  build: [echo build]\n  test: [echo test]\n"
            + lint + "testSuites:\ncachePaths:\ncapabilities: [linux]\nenvironment:\n");
        if (lint.Contains("npm --prefix tools"))
        {
            Directory.CreateDirectory(Path.Combine(repo, "tools"));
            File.WriteAllText(Path.Combine(repo, "tools", "package.json"), "{ \"private\": true }\n");
        }
        Git(repo, "add", ".");
        Git(repo, "commit", "-qm", "base");

        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Document the pilot.\n");
        config ??= new ConfigurationBuilder().AddInMemoryCollection().Build();
        var summary = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config,
            NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var prompts = new RuntimePromptService(config,
            NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts,
            NullLogger<AspectRunnerService>.Instance);
        var builder = new RemoteReviewPlanBuilder(aspects, config, git);
        var task = new TaskInfo
        {
            Id = "task", TaskKey = "task", ProjectName = "project",
            WatchPath = repo, FolderPath = folder, State = TaskStates.AutoReview,
        };
        return new PlanFixture(root, repo, folder, config, git, aspects, builder, task);
    }

    // Git for Windows writes object files read-only, and Directory.Delete
    // refuses read-only files there; clear the attribute first so cleanup
    // cannot fail the test on the Windows merge gate.
    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }
        try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Git(string repo, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
        return output;
    }
}
