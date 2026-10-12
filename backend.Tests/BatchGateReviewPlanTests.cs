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
            var resultSha = Git(fixture.Repo, "rev-parse", "develop").Trim();
            var settings = new ProjectSettings { BatchGate = new(Enabled: true, Scope: "code") };

            // The result ref is unknown to this checkout, so the builder's own
            // diff is null and must not be read as "no change".
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
