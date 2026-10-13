using System.Diagnostics;
using AgentStudio.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class BatchGateGitReplayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "batch-git-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PublishRequiresExactRemotePreTipAndVerifiesTestedSha()
    {
        Directory.CreateDirectory(_root);
        Git("init", "-q");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        File.WriteAllText(Path.Combine(_root, "file.txt"), "base\n");
        Git("add", "file.txt");
        Git("commit", "-qm", "base");
        Git("branch", "-M", "develop");
        var baseSha = Git("rev-parse", "HEAD");
        var remote = Path.Combine(_root, "remote.git");
        Git("init", "--bare", "-q", remote);
        Git("remote", "add", "origin", remote);
        Git("push", "-q", "origin", "develop");
        File.WriteAllText(Path.Combine(_root, "file.txt"), "candidate\n");
        Git("commit", "-qam", "candidate");
        var tested = Git("rev-parse", "HEAD");
        var service = Service();
        var published = service.PublishBatchCandidate(
            _root, "develop", baseSha, tested, CancellationToken.None);
        Assert.True(published.Success, published.Error);
        Assert.Contains(tested, Git("ls-remote", "origin", "refs/heads/develop"));
        File.WriteAllText(Path.Combine(_root, "file.txt"), "later\n");
        Git("commit", "-qam", "later");
        var later = Git("rev-parse", "HEAD");
        var stale = service.PublishBatchCandidate(
            _root, "develop", baseSha, later, CancellationToken.None);
        Assert.Equal("stale-base", stale.Status);
        Assert.Contains(tested, Git("ls-remote", "origin", "refs/heads/develop"));
    }

    [Fact]
    public void DirectDescendantGetsIdentityMappingAndConflictLeavesCandidateRefUnchanged()
    {
        Directory.CreateDirectory(_root);
        Git("init", "-q");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        File.WriteAllText(Path.Combine(_root, "file.txt"), "base\n");
        Git("add", "file.txt");
        Git("commit", "-qm", "base");
        var baseSha = Git("rev-parse", "HEAD");
        Git("checkout", "-qb", "first");
        File.WriteAllText(Path.Combine(_root, "file.txt"), "first\n");
        Git("commit", "-qam", "first");
        var firstSha = Git("rev-parse", "HEAD");
        var firstRef = "refs/heads/agent-studio/results/first/" + firstSha;
        Git("update-ref", firstRef, firstSha);
        Git("checkout", "-q", "--detach", baseSha);
        Git("checkout", "-qb", "second");
        File.WriteAllText(Path.Combine(_root, "file.txt"), "second\n");
        Git("commit", "-qam", "second");
        var secondSha = Git("rev-parse", "HEAD");
        var secondRef = "refs/heads/agent-studio/results/second/" + secondSha;
        Git("update-ref", secondRef, secondSha);
        var service = Service();
        var first = service.ReplayBatchMember(_root, firstRef, firstSha, baseSha);
        Assert.True(first.Success, first.Error);
        Assert.Equal(firstSha, first.TipSha);
        Assert.Contains(first.Replacements, x => x.OriginalSha == firstSha && x.RebasedSha == firstSha);
        var candidateRef = "refs/agent-studio/batch-candidates/" + new string('a', 32)
            + "/" + new string('b', 64) + "/1";
        Assert.True(service.UpdateBatchCandidateRef(_root, candidateRef, baseSha, null).Success);
        Assert.True(service.UpdateBatchCandidateRef(_root, candidateRef, firstSha, baseSha).Success);
        var conflict = service.ReplayBatchMember(_root, secondRef, secondSha, firstSha);
        Assert.False(conflict.Success);
        Assert.Contains("file.txt", conflict.ConflictedFiles);
        Assert.Equal(firstSha, Git("rev-parse", candidateRef));
        var remote = Path.Combine(_root, "remote.git");
        Git("init", "--bare", "-q", remote);
        Git("remote", "add", "origin", remote);
        var pushed = service.PushBatchCandidateRef(
            _root, candidateRef, firstSha, CancellationToken.None);
        Assert.True(pushed.Success, pushed.Error);
        Assert.Contains(firstSha, Git("ls-remote", "origin", candidateRef));
    }

    [Fact]
    public void FixRoundWithAnIntegrationMergeIsMergedIntoTheCandidateWithExactParents()
    {
        Directory.CreateDirectory(_root);
        Git("init", "-q");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        File.WriteAllText(Path.Combine(_root, "file.txt"), "base\n");
        Git("add", "file.txt");
        Git("commit", "-qm", "base");
        var oldBase = Git("rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(_root, "develop.txt"), "develop\n");
        Git("add", "develop.txt");
        Git("commit", "-qm", "develop moved");
        var develop = Git("rev-parse", "HEAD");
        // The linear member was cut on the current integration tip.
        File.WriteAllText(Path.Combine(_root, "linear.txt"), "linear\n");
        Git("add", "linear.txt");
        Git("commit", "-qm", "linear");
        var linearSha = Git("rev-parse", "HEAD");
        var linearRef = "refs/heads/agent-studio/results/linear/" + linearSha;
        Git("update-ref", linearRef, linearSha);
        // The fix round was cut on the old base and merged the integration tip.
        Git("checkout", "-q", "--detach", oldBase);
        File.WriteAllText(Path.Combine(_root, "fix.txt"), "first\n");
        Git("add", "fix.txt");
        Git("commit", "-qm", "fix round");
        Git("merge", "-q", "--no-ff", "--no-edit", develop);
        File.WriteAllText(Path.Combine(_root, "fix.txt"), "second\n");
        Git("commit", "-qam", "fix round follow-up");
        var fixSha = Git("rev-parse", "HEAD");
        var fixRef = "refs/heads/agent-studio/results/fix/" + fixSha;
        Git("update-ref", fixRef, fixSha);
        var service = Service();

        var linear = service.ReplayBatchMember(_root, linearRef, linearSha, develop);
        Assert.True(linear.Success, linear.Error);
        Assert.Equal(BatchReplayRoute.Identity, linear.Route);
        var merged = service.ReplayBatchMember(_root, fixRef, fixSha, linearSha);
        Assert.True(merged.Success, merged.Error);
        Assert.Equal(BatchReplayRoute.Merge, merged.Route);
        var replacement = Assert.Single(merged.Replacements);
        Assert.Equal(fixSha, replacement.OriginalSha);
        Assert.Equal(merged.TipSha, replacement.RebasedSha);
        Assert.Equal($"{merged.TipSha} {linearSha} {fixSha}",
            Git("rev-list", "--parents", "-n", "1", merged.TipSha!));
        Assert.Equal("second", Git("show", $"{merged.TipSha}:fix.txt"));
        Assert.Equal("linear", Git("show", $"{merged.TipSha}:linear.txt"));
        // A fix round already on the tip still merges rather than fast-forwarding
        // a non-linear range under an identity mapping.
        var direct = service.ReplayBatchMember(_root, fixRef, fixSha, develop);
        Assert.True(direct.Success, direct.Error);
        Assert.Equal(BatchReplayRoute.Merge, direct.Route);
        Assert.Equal($"{direct.TipSha} {develop} {fixSha}",
            Git("rev-list", "--parents", "-n", "1", direct.TipSha!));
        Assert.Empty(Git("worktree", "list", "--porcelain").Split('\n')
            .Where(line => line.Contains("agent-studio-batch-merge-", StringComparison.Ordinal)));
    }

    [Fact]
    public void ContainedOrMismatchedResultIsUnattributableAndMergeConflictIsAConflict()
    {
        Directory.CreateDirectory(_root);
        Git("init", "-q");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        File.WriteAllText(Path.Combine(_root, "file.txt"), "base\n");
        Git("add", "file.txt");
        Git("commit", "-qm", "base");
        var baseSha = Git("rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(_root, "file.txt"), "develop\n");
        Git("commit", "-qam", "develop");
        var develop = Git("rev-parse", "HEAD");
        Git("checkout", "-q", "--detach", baseSha);
        File.WriteAllText(Path.Combine(_root, "file.txt"), "member\n");
        Git("commit", "-qam", "member");
        File.WriteAllText(Path.Combine(_root, "other.txt"), "other\n");
        Git("add", "other.txt");
        Git("commit", "-qm", "side");
        var side = Git("rev-parse", "HEAD");
        Git("checkout", "-q", "--detach", baseSha);
        Git("merge", "-q", "--no-ff", "--no-edit", side);
        var memberSha = Git("rev-parse", "HEAD");
        var memberRef = "refs/heads/agent-studio/results/member/" + memberSha;
        Git("update-ref", memberRef, memberSha);
        var service = Service();

        var contained = service.ReplayBatchMember(_root, memberRef, memberSha, memberSha);
        Assert.False(contained.Success);
        Assert.Equal(BatchReplayFailure.Unattributable, contained.Failure);
        var mismatch = service.ReplayBatchMember(_root, memberRef, develop, develop);
        Assert.Equal(BatchReplayFailure.Unattributable, mismatch.Failure);
        var conflict = service.ReplayBatchMember(_root, memberRef, memberSha, develop);
        Assert.Equal(BatchReplayFailure.Conflict, conflict.Failure);
        Assert.Equal(BatchReplayRoute.Merge, conflict.Route);
        Assert.Contains("file.txt", conflict.ConflictedFiles);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private GitService Service()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>()).Build();
        var summary = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(
            config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private string Git(params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }
}
