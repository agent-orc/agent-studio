using System.Diagnostics;
using AgentStudio.Git;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GitIntegrationRefRaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "integration-ref-race-" + Guid.NewGuid().ToString("N"));
    private const string Race = "error: cannot lock ref 'refs/remotes/origin/develop': is at 2222 but expected 1111";

    [Fact]
    public void BranchSync_RefRaceOnce_RetriesAndSucceeds()
    {
        var (repo, slot, _) = Setup();
        var calls = 0;
        var git = CreateGit((start, timeout, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                return new GitProcessResult(1, "", Race, GitProcessFailureKind.None);
            return GitNetworkProcessRunner.Run(start, null, timeout, ct);
        });

        var result = git.SynchronizeIntegrationBranch(slot, "develop");

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, calls);
        Assert.Equal(Git(repo, "rev-parse develop"), Git(slot, "rev-parse refs/remotes/origin/develop"));
    }

    [Fact]
    public void BranchSync_RefRaceExhausted_HasDistinctCardSignature()
    {
        var (_, slot, _) = Setup();
        var calls = 0;
        var git = CreateGit((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return new GitProcessResult(1, "", Race, GitProcessFailureKind.None);
        });

        var result = git.SynchronizeIntegrationBranch(slot, "develop");
        var classification = RunFailureClassifier.Classify(new RunFailureEvidence { Text = result.Error });

        Assert.False(result.Success);
        Assert.Equal(3, calls);
        Assert.Equal(RunFailureClass.Infrastructure, classification.Class);
        Assert.Equal(RunFailureSignatures.GitRefLockRace, classification.Signature);
    }

    [Fact]
    public async Task BranchSyncAndPush_TwoSlots_SerializeRemoteRefWrites()
    {
        var (_, firstSlot, secondSlot) = Setup();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var pushStarted = new ManualResetEventSlim();
        var calls = 0;
        var git = CreateGit((start, timeout, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(ct);
            }
            return GitNetworkProcessRunner.Run(start, null, timeout, ct);
        });

        var first = Task.Run(() => git.SynchronizeIntegrationBranch(firstSlot, "develop"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var second = Task.Run(() =>
        {
            pushStarted.Set();
            return git.PushIntegrationBranchAsync(secondSlot, "develop");
        });
        try
        {
            Assert.True(pushStarted.Wait(TimeSpan.FromSeconds(10)));
            await Task.Delay(150);
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.False(second.IsCompleted);
        }
        finally { release.Set(); }
        Assert.True((await first).Success);
        Assert.True((await second).Success);
        Assert.Equal(1, calls);
    }

    private (string Repo, string First, string Second) Setup()
    {
        Directory.CreateDirectory(_root);
        var repo = Path.Combine(_root, "repo");
        var remote = Path.Combine(_root, "remote.git");
        Directory.CreateDirectory(repo);
        Git(repo, "init -q -b develop");
        Git(repo, "config user.email test@example.com");
        Git(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed");
        Git(repo, "add -A");
        Git(repo, "commit -q -m seed");
        Git(_root, $"init -q --bare \"{remote}\"");
        Git(repo, $"remote add origin \"{remote}\"");
        Git(repo, "push -q origin develop");
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Git(repo, $"worktree add -q --detach \"{first}\"");
        Git(repo, $"worktree add -q --detach \"{second}\"");
        return (repo, first, second);
    }

    private static GitService CreateGit(Func<ProcessStartInfo, TimeSpan, CancellationToken, GitProcessResult> fetch)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config)
        {
            IntegrationFetchForTesting = fetch,
        };
    }

    private static string Git(string cwd, string args)
    {
        using var process = Process.Start(new ProcessStartInfo("git", args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
