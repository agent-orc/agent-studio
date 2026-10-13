extern alias Runner;

using System.Diagnostics;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Contract = AgentStudio.TaskServer.Contracts;
using RGitWorkspace = Runner::AgentRunner.GitWorkspace;
using ROptions = Runner::AgentRunner.RunnerOptions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3056 (AGT-W65 D17 A), proven on real Git across the runner and the
/// server: a review fix round prepares its worktree on the reviewed fenced
/// result, keeps the integration branch as rebase target, and a delivery whose
/// rebase onto a moved integration branch conflicts ends in the existing
/// integration dead-end exit (AGT-2909) with a typed reason instead of being
/// restarted from the integration branch.
/// </summary>
public sealed class ReviewFixRoundRebaseConflictTests : IDisposable
{
    private const string TaskKey = "AGT-3056";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fix-round-rebase-" + Guid.NewGuid().ToString("N"));
    private readonly string _origin;

    public ReviewFixRoundRebaseConflictTests()
    {
        Directory.CreateDirectory(_root);
        _origin = Path.Combine(_root, "origin.git");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Fix_round_starts_on_the_reviewed_sha_and_a_conflicting_rebase_takes_the_dead_end_exit()
    {
        Git(_root, "init", "-q", "--bare", "--initial-branch=develop", _origin);
        var publisher = Path.Combine(_root, "publisher");
        Git(_root, "clone", "-q", _origin, publisher);
        Git(publisher, "checkout", "-q", "-b", "develop");
        Commit(publisher, "shared.txt", "base\n", "seed");
        Git(publisher, "push", "-q", "origin", "develop");

        // Attempt n: the delivery the review graded ProductFailure, on its fenced ref.
        Git(publisher, "checkout", "-q", "-b", "reviewed");
        var reviewedSha = Commit(publisher, "shared.txt", "reviewed delivery\n", "feat: reviewed delivery");
        var reviewedRef = Contract.FencedGitRefs.ImmutableResult("run_reviewed", 1, reviewedSha);
        Git(publisher, "push", "-q", "origin", $"HEAD:{reviewedRef}");
        Git(publisher, "checkout", "-q", "develop");

        // Attempt n+1 claims the fix round with the reviewed result as base.
        var runner = new RGitWorkspace(
            new ROptions
            {
                ServerUrl = "http://localhost",
                RunnerId = "runner-test",
                RunnerName = "runner-test",
                Hostname = "test-host",
                BackendName = "test",
                GitRemote = _origin,
                WorkDir = Path.Combine(_root, "runner"),
                StateDir = Path.Combine(_root, "runner", ".runner-state"),
                BaseBranch = "develop",
                ClaudeCliBin = "test",
            },
            TaskKey,
            _ => { },
            sourceRunAttemptId: "run_fix",
            fencingToken: 2,
            continuationBaseRef: reviewedRef,
            continuationBaseSha: reviewedSha,
            continuationBaseReason: Contract.ContinuationBaseReasons.ReviewFixRound);
        await runner.PrepareAsync(CancellationToken.None);

        Assert.Equal(reviewedSha, Git(runner.RepoPath, "rev-parse", "HEAD"));
        Assert.Equal("refs/heads/develop", runner.IntegrationBranchRef);

        // The agent fixes the finding; meanwhile develop changes the same line.
        File.WriteAllText(Path.Combine(runner.RepoPath, "shared.txt"), "reviewed delivery, finding fixed\n");
        Commit(publisher, "shared.txt", "develop moved on\n", "chore: develop edits shared");
        Git(publisher, "push", "-q", "origin", "develop");
        var developTip = Git(publisher, "rev-parse", "HEAD");
        var delivered = await runner.TeardownAsync("Done", "run_fix", CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(delivered.ImmutableResultRef));
        var deliverySha = delivered.ResultSha!;
        Assert.Equal(reviewedSha, Git(_origin, "rev-parse", $"{deliverySha}^"));

        // The server integrates the fix round's delivery onto the moved develop.
        var server = Path.Combine(_root, "server");
        Git(_root, "clone", "-q", _origin, server);
        Git(server, "config", "user.email", "server@example.invalid");
        Git(server, "config", "user.name", "Server");
        var result = BuildGitService(server).MergeRemoteDeliveryIntoIntegration(
            server,
            delivered.ImmutableResultRef!["refs/heads/".Length..],
            deliverySha,
            "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.AgentRoundRequired, result.Outcome);
        Assert.Equal(AcceptedIntegrationFailureCodes.DeliveryAttributionAmbiguous, AcceptedIntegrationFailureCodes.For(result));
        var report = Assert.IsType<IntegrationConflictReport>(result.ConflictReport);
        Assert.Equal(["shared.txt"], report.ConflictedFiles);
        Assert.Equal(developTip, report.IntegrationTipSha);
        Assert.Equal(RemoteIntegrationContinuationAction.StartAgentRound,
            RemoteIntegrationContinuationPolicy.Decide(result.Outcome, automaticAgentRoundsUsed: 0));
        var prompt = IntegrationContinuationPrompt.Build(
            TaskKey, delivered.ImmutableResultRef, deliverySha, "develop", "merge-into-develop",
            AcceptedIntegrationFailureCodes.For(result)!, report);
        Assert.Contains("resolve the integration dead end", prompt, StringComparison.Ordinal);
        Assert.Contains("Continue on the existing delivery branch", prompt, StringComparison.Ordinal);
        // develop was never moved by the refused integration.
        Assert.Equal(developTip, Git(_origin, "rev-parse", "refs/heads/develop"));
    }

    private static GitService BuildGitService(string repo)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Fixture",
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
            ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private static string Commit(string repo, string path, string content, string message)
    {
        File.WriteAllText(Path.Combine(repo, path), content);
        Git(repo, "add", "--all");
        Git(repo, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-q", "-m", message);
        return Git(repo, "rev-parse", "HEAD");
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Result.Trim();
    }
}
