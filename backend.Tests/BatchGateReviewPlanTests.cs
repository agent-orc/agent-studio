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
                + "  prepare: .agent-studio/prepare\n  build:\n  test: [echo test]\n"
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
            ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
            {
                TaskKey = "task", RunAttemptId = "run", AttemptChainId = "chain",
                Project = "project", Repository = "repo", ResultSha = resultSha,
                ImmutableResultRef = resultRef, CompletedAtUtc = Now,
            });
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
                new ProjectSettings { BatchGate = new(Enabled: true) }, "develop");

            Assert.True(plan.BuildTestDeferredToBatch);
            Assert.Contains(plan.Commands, command =>
                AgentStudio.TaskServer.Contracts.ReviewCommandKinds.IsAgent(command.ExecutionKind));
            Assert.DoesNotContain(plan.Commands, command => command.Aspect == "build-tests");

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
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
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
