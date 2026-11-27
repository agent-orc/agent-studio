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
    [InlineData(false, "parked", false, true)]
    [InlineData(false, "parked", true, true)]
    public void TwoCardsCollidingProduceOneDurableRecordBeforeWorktreeCleanup(bool additions, string resolution, bool direct, bool ledgerUnavailable = false)
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
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Dispatch:StoragePath"] = Path.Combine(_root, ".git", "dispatch-ledger") }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var service = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var ledgerPath = Path.Combine(_root, ".git", "conflict-ledger");
        if (ledgerUnavailable) File.WriteAllText(ledgerPath, "not a directory");
        var ledger = new IntegrationConflictLedger(ledgerPath);
        var passed = DateTimeOffset.UnixEpoch;
        var reviewReads = 0;
        using (IntegrationConflictCapture.Begin("Project", "AGT-2", "develop", delivery, baseSha,
            ledger: ledger, reviewTimeProvider: () => { reviewReads++; return passed; }))
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
                using var pinned = IntegrationConflictCapture.Pin(delivery, tip);
                IntegrationConflictCapture.Resolve(result.Success ? "mechanical" : "parked");
            }
        }
        Assert.Equal(1, reviewReads);
        if (!ledgerUnavailable)
        {
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
        }
        Assert.Equal("", Git("status", "--porcelain", "--untracked-files=no"));
        Assert.Single(Git("worktree", "list", "--porcelain").Split('\n'), line => line.StartsWith("worktree "));
    }

    [Fact]
    public void NetChangeConflictCarriesTargetTipAndAttributionBeforeCleanup()
    {
        Directory.CreateDirectory(_root);
        Git("init", "-q", "-b", "develop");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        File.WriteAllText(Path.Combine(_root, "shared.cs"), "base\n");
        Git("add", ".");
        Git("commit", "-qm", "base");
        var baseSha = Git("rev-parse", "HEAD");
        Git("checkout", "-qb", "task/agt-2");
        File.WriteAllText(Path.Combine(_root, "shared.cs"), "delivery\n");
        Git("commit", "-qam", "AGT-2: delivery");
        Git("checkout", "-q", "develop");
        File.WriteAllText(Path.Combine(_root, "other.cs"), "other\n");
        Git("add", ".");
        Git("commit", "-qm", "AGT-3: unrelated");
        Git("checkout", "-q", "task/agt-2");
        Git("merge", "--no-ff", "-qm", "merge develop", "develop");
        var delivery = Git("rev-parse", "HEAD");
        Git("checkout", "-q", "develop");
        File.WriteAllText(Path.Combine(_root, "shared.cs"), "target\n");
        Git("commit", "-qam", "AGT-1: target change");
        var tip = Git("rev-parse", "HEAD");
        var config = new ConfigurationBuilder().Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var service = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var ledger = new IntegrationConflictLedger(Path.Combine(_root, ".git", "conflicts"));
        using var capture = IntegrationConflictCapture.Begin("Project", "AGT-2", "develop", delivery, baseSha, ledger: ledger);

        var result = service.MergeBranchIntoIntegration(_root, "task/agt-2", "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.AgentRoundRequired, result.Outcome);
        Assert.NotNull(result.ConflictReport);
        Assert.Equal(tip, result.ConflictReport.IntegrationTipSha);
        Assert.Equal(delivery, result.ConflictReport.DeliverySha);
        Assert.Equal("shared.cs", Assert.Single(result.ConflictReport.ConflictedFiles));
        var file = Assert.Single(Assert.Single(ledger.Read("Project")).Files);
        Assert.Equal("AGT-1", Assert.Single(file.CardKeys));
        Assert.Equal(tip, Assert.Single(file.Commits).Sha);
        var context = service.ConflictChanges(_root, delivery, baseSha, tip, ["shared.cs"], "AGT-2");
        Assert.Equal(["AGT-1"], context.Cards);
        Assert.Contains(tip, context.Changes);
        Assert.Contains("+target", context.Changes);
        Assert.Contains("-base", context.Changes);
        Assert.DoesNotContain("AGT-3", context.Changes);
        var continuationContext = service.ConflictChanges(_root, delivery, delivery, tip, ["shared.cs"], "AGT-2");
        Assert.Contains("-base", continuationContext.Changes);
        Assert.DoesNotContain("-delivery", continuationContext.Changes);
        Assert.False(service.TargetTouchedPaths(_root, tip, tip, ["shared.cs"]));
        File.WriteAllText(Path.Combine(_root, "other.cs"), "unrelated target change\n");
        Git("commit", "-qam", "AGT-4: unrelated target change");
        var unrelatedTip = Git("rev-parse", "HEAD");
        Assert.False(service.TargetTouchedPaths(_root, tip, unrelatedTip, ["shared.cs"]));
        File.WriteAllText(Path.Combine(_root, "shared.cs"), "changed target\n");
        Git("commit", "-qam", "AGT-5: relevant target change");
        Assert.True(service.TargetTouchedPaths(_root, tip, Git("rev-parse", "HEAD"), ["shared.cs"]));
        Assert.Equal("", Git("status", "--porcelain", "--untracked-files=no"));
    }

    [Fact]
    public void CleanReplayDoesNotReadReviewHistory()
    {
        Directory.CreateDirectory(_root);
        Git("init", "-q");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        File.WriteAllText(Path.Combine(_root, "base.cs"), "base\n");
        Git("add", ".");
        Git("commit", "-qm", "base");
        var baseSha = Git("rev-parse", "HEAD");
        Git("checkout", "-qb", "develop");
        File.WriteAllText(Path.Combine(_root, "first.cs"), "first\n");
        Git("add", ".");
        Git("commit", "-qm", "AGT-1: first card");
        var tip = Git("rev-parse", "HEAD");
        Git("checkout", "-qb", "task/agt-2", baseSha);
        File.WriteAllText(Path.Combine(_root, "second.cs"), "second\n");
        Git("add", ".");
        Git("commit", "-qm", "AGT-2: second card");
        var delivery = Git("rev-parse", "HEAD");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Dispatch:StoragePath"] = Path.Combine(_root, ".git", "dispatch-ledger") }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var service = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var ledger = new IntegrationConflictLedger(Path.Combine(_root, ".git", "conflicts"));
        var reads = 0;
        using var capture = IntegrationConflictCapture.Begin("Project", "AGT-2", "develop", delivery,
            ledger: ledger, reviewTimeProvider: () => { reads++; return DateTimeOffset.UnixEpoch; });
        var result = service.ReplayBatchMember(_root, "task/agt-2", delivery, tip);
        Assert.True(result.Success, result.Error);
        Assert.Equal(0, reads);
        Assert.Empty(ledger.Read("Project"));
    }

    [Fact]
    public void ListingUnmergedFilesDoesNotCaptureEvidence()
    {
        Directory.CreateDirectory(_root);
        Git("init", "-q");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        File.WriteAllText(Path.Combine(_root, "shared.cs"), "base\n");
        Git("add", ".");
        Git("commit", "-qm", "base");
        var tip = Git("rev-parse", "HEAD");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Dispatch:StoragePath"] = Path.Combine(_root, ".git", "dispatch-ledger") }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var service = new GitService(NullLogger<GitService>.Instance, scanner, config);
        using var scope = IntegrationConflictCapture.Begin("Project", "AGT-2", "develop", tip,
            baseSha: "missing-base", ledger: new IntegrationConflictLedger(Path.Combine(_root, "ledger")));
        using var pinned = IntegrationConflictCapture.Pin(tip, tip);
        // A synthetic unmerged index leaves the working file available for hunk reads.
        var blob = Git("rev-parse", "HEAD:shared.cs");
        Git("update-index", "--force-remove", "shared.cs");
        using (var process = new Process { StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root, RedirectStandardInput = true, UseShellExecute = false,
        } })
        {
            process.StartInfo.ArgumentList.Add("update-index");
            process.StartInfo.ArgumentList.Add("--index-info");
            process.Start();
            process.StandardInput.Write($"100644 {blob} 1\tshared.cs\n100644 {blob} 2\tshared.cs\n100644 {blob} 3\tshared.cs\n");
            process.StandardInput.Close();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        Assert.Equal("shared.cs", Assert.Single(service.ListUnmergedFiles(_root)));
        Assert.False(Directory.Exists(Path.Combine(_root, "ledger")));
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
