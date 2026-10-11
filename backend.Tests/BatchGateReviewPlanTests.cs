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
        var root = Path.Combine(Path.GetTempPath(), "batch-plan-test-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        var folder = Path.Combine(root, "task");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(folder);
        try
        {
            Git(repo, "init", "-q", "-b", "develop");
            Git(repo, "config", "user.email", "batch-test@example.invalid");
            Git(repo, "config", "user.name", "Batch Test");
            Directory.CreateDirectory(Path.Combine(repo, ".agent-studio"));
            File.WriteAllText(Path.Combine(repo, ".agent-studio", "prepare"), "#!/bin/sh\nexit 0\n");
            File.WriteAllText(Path.Combine(repo, ".agent-studio", "project.yml"),
                "schemaVersion: 1\nstack: [custom]\ntoolVersions:\ncommands:\n"
                + "  prepare: .agent-studio/prepare\n  build: [echo build]\n  test: [echo test]\n"
                + "  lint:\ntestSuites:\ncachePaths:\ncapabilities: [linux]\nenvironment:\n");
            Git(repo, "add", ".");
            Git(repo, "commit", "-qm", "base");
            Git(repo, "switch", "-qc", "agent-studio/results/task");
            Directory.CreateDirectory(Path.Combine(repo, "docs"));
            File.WriteAllText(Path.Combine(repo, "docs", "pilot.md"), "Pilot notes.\n");
            Git(repo, "add", ".");
            Git(repo, "commit", "-qm", "docs");
            var resultSha = Git(repo, "rev-parse", "HEAD").Trim();
            Git(repo, "switch", "-q", "develop");

            var resultRef = "refs/heads/agent-studio/results/task";
            File.WriteAllText(Path.Combine(folder, "prompt.md"), "Document the pilot.\n");
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
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
            DeleteDirectory(root);
        }
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
