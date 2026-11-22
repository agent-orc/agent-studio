using System.Diagnostics;
using AgentStudio.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class IntegrationConflictCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "conflict-capture-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, "parked", false)]
    [InlineData(false, "parked", true)]
    [InlineData(true, "union", false)]
    public void TwoCardsCollidingProduceOneDurableRecordBeforeWorktreeCleanup(bool additions, string resolution, bool direct)
    {
        Directory.CreateDirectory(_root);
        Git("init", "-q");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        File.WriteAllText(Path.Combine(_root, "shared file.cs"), "start\nbase\nend\n");
        Git("add", ".");
        Git("commit", "-qm", "base");
        var baseSha = Git("rev-parse", "HEAD");
        Git("checkout", "-qb", "develop");
        File.WriteAllText(Path.Combine(_root, "shared file.cs"), additions ? "start\nbase\nfirst\nend\n" : "start\nfirst\nend\n");
        Git("commit", "-qam", "feat(AGT-1): first card DP-7\n\nRelated: AGT-9999");
        var tip = Git("rev-parse", "HEAD");
        Git("checkout", "-qb", "task/agt-2", baseSha);
        File.WriteAllText(Path.Combine(_root, "shared file.cs"), additions ? "start\nbase\nsecond\nend\n" : "start\nsecond\nend\n");
        Git("commit", "-qam", "feat(AGT-2): second card");
        var delivery = Git("rev-parse", "HEAD");
        var config = new ConfigurationBuilder().Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var service = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var ledger = new IntegrationConflictLedger(Path.Combine(_root, "ledger"));
        var passed = DateTimeOffset.UnixEpoch;
        using (IntegrationConflictCapture.Begin("Project", "AGT-2", "develop", delivery, baseSha, passed, ledger))
        {
            if (direct)
            {
                var result = service.MergeBranchIntoIntegration(_root, "task/agt-2", "develop");
                Assert.Equal(MergeIntoIntegrationOutcome.AgentRoundRequired, result.Outcome);
            }
            else
            {
                var result = service.ReplayBatchMember(_root, "task/agt-2", delivery, tip);
                Assert.Equal(additions, result.Success);
                IntegrationConflictCapture.Resolve(result.Success ? "mechanical" : "parked");
            }
        }
        var row = Assert.Single(ledger.Read("Project"));
        Assert.Equal("AGT-2", row.CardKey);
        Assert.Equal("develop", row.IntegrationTarget);
        Assert.Equal(baseSha, row.DeliveryBaseSha);
        Assert.Equal(1, row.CommitsSinceBase);
        Assert.Equal(passed, row.ReviewPassedAtUtc);
        Assert.True(row.ReviewWaitSeconds > 0);
        var file = Assert.Single(row.Files);
        Assert.Equal("shared file.cs", file.Path);
        Assert.Equal(1, file.HunkCount);
        Assert.Equal("AGT-1", Assert.Single(file.CardKeys));
        Assert.Equal(resolution, row.Resolution);
        Assert.Equal("", Git("status", "--porcelain", "--untracked-files=no"));
        Assert.Single(Git("worktree", "list", "--porcelain").Split('\n'), line => line.StartsWith("worktree "));
    }

    private string Git(params string[] args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
