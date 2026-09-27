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
    public void PublishRequiresRecordedPreTipAndVerifiesExactRemoteCandidate()
    {
        Directory.CreateDirectory(_root);
        Git("init", "-q");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        File.WriteAllText(Path.Combine(_root, "doc.md"), "base\n");
        Git("add", "doc.md");
        Git("commit", "-qm", "base");
        var before = Git("rev-parse", "HEAD");
        Git("branch", "develop", before);
        var remote = Path.Combine(_root, "remote.git");
        Git("init", "--bare", "-q", remote);
        Git("remote", "add", "origin", remote);
        Git("push", "-q", "origin", "develop");
        File.WriteAllText(Path.Combine(_root, "doc.md"), "candidate\n");
        Git("commit", "-qam", "candidate");
        var candidate = Git("rev-parse", "HEAD");
        var service = Service();
        var stale = service.PublishTestedBatchCandidate(_root, "develop",
            new string('b', 40), candidate, CancellationToken.None);
        Assert.False(stale.Success);
        Assert.Equal(before, service.GetRemoteIntegrationTip(_root, "develop", CancellationToken.None));
        var published = service.PublishTestedBatchCandidate(_root, "develop",
            before, candidate, CancellationToken.None);
        Assert.True(published.Success, published.Error);
        Assert.Equal(candidate, service.GetRemoteIntegrationTip(_root, "develop", CancellationToken.None));
        Assert.False(service.PublishTestedBatchCandidate(_root, "develop",
            before, candidate, CancellationToken.None).Success);
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
