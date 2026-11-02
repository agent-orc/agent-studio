using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2008: the git-pane's rendered md/html preview reads a file's full text
/// through <see cref="GitService.GetFileContentResult"/>, and the diff of an
/// untracked file falls back to its body through <see cref="GitService.GetDiffResult"/>.
/// These tests stand up a real repo + registered job and assert:
///  - the working-tree copy is read live;
///  - a NUL-containing file is reported as binary with no text;
///  - path traversal and a missing file are refused;
///  - only a regular file inside the checkout is served: a committed link,
///    Git metadata and an oversize file are refused by both readers.
/// </summary>
public class GitServiceFileContentTests : IDisposable
{
    private readonly CheckoutFileFixture _checkout = new("git-file-content");

    public void Dispose() => _checkout.Dispose();

    [Fact]
    public void WorkingTree_ReadsLiveFileContent()
    {
        var (jobId, watchPath) = SetupJob();
        _checkout.Write("README.md", "# committed\n");
        _checkout.Commit();
        // Working tree now diverges from HEAD; the preview must show the live copy.
        _checkout.Write("README.md", "# working tree\n");

        var git = BuildGitService(watchPath);
        var result = git.GetFileContentResult(jobId, watchPath, "README.md", preferRunLocation: true);

        Assert.True(result.Success);
        Assert.False(result.IsBinary);
        Assert.Equal("# working tree\n", result.Content);
    }

    [Fact]
    public void BinaryBlob_IsFlagged_WithoutText()
    {
        var (jobId, watchPath) = SetupJob();
        File.WriteAllBytes(_checkout.Full("logo.html"), [0x3C, 0x00, 0x68, 0x74, 0x6D, 0x6C]);

        var git = BuildGitService(watchPath);
        var result = git.GetFileContentResult(jobId, watchPath, "logo.html", preferRunLocation: true);

        Assert.True(result.Success);
        Assert.True(result.IsBinary);
        Assert.Equal("", result.Content);
    }

    [Fact]
    public void MissingWorkingTreeFile_Fails()
    {
        var (jobId, watchPath) = SetupJob();
        _checkout.Write("README.md", "seed");
        _checkout.Commit();

        var git = BuildGitService(watchPath);
        var result = git.GetFileContentResult(jobId, watchPath, "does/not/exist.md", preferRunLocation: true);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void TraversalPath_IsRejected()
    {
        var (jobId, watchPath) = SetupJob();
        // Plant a secret one level above the repo root.
        File.WriteAllText(Path.Combine(_checkout.Parent, "secret.md"), "top secret");

        var git = BuildGitService(watchPath);
        var result = git.GetFileContentResult(jobId, watchPath, "../secret.md", preferRunLocation: true);

        Assert.False(result.Success);
        Assert.DoesNotContain("top secret", result.Content);
    }

    [Fact]
    public void CommittedLinkToFileOutsideCheckout_IsRefused()
    {
        var (jobId, watchPath) = SetupJob();
        var outside = _checkout.OutsideFile("secret.md", "outside content");
        if (!_checkout.TryCommitLink("docs/linked.md", outside)) return;

        AssertRefusedByBothReaders(jobId, watchPath, "docs/linked.md", "outside content");
    }

    [Fact]
    public void CommittedLinkToProcessEnvironment_IsRefused()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (jobId, watchPath) = SetupJob();
        Assert.True(_checkout.TryCommitLink("environ.md", "/proc/self/environ"));

        var git = BuildGitService(watchPath);
        var file = git.GetFileContentResult(jobId, watchPath, "environ.md", preferRunLocation: true);
        var diff = git.GetDiffResult(jobId, watchPath, "environ.md", preferRunLocation: true);

        Assert.False(file.Success);
        Assert.Equal("", file.Content);
        Assert.True(diff.Success);
        Assert.DoesNotContain("PATH=", diff.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public void GitConfig_IsRefused()
    {
        var (jobId, watchPath) = SetupJob();
        _checkout.Write("README.md", "seed");
        _checkout.Commit();

        AssertRefusedByBothReaders(jobId, watchPath, ".git/config", "[core]");
        if (_checkout.TryCommitLink("docs/config.md", _checkout.Full(".git/config")))
            AssertRefusedByBothReaders(jobId, watchPath, "docs/config.md", "[core]");
    }

    [Fact]
    public void OversizeFile_IsRefused()
    {
        var (jobId, watchPath) = SetupJob();
        _checkout.Commit();
        _checkout.WriteOversize("docs/big.md", GitService.MaxWorkingTreeFileReadBytes);

        var git = BuildGitService(watchPath);
        var result = git.GetFileContentResult(jobId, watchPath, "docs/big.md", preferRunLocation: true);

        Assert.False(result.Success);
        Assert.Equal("File is too large to preview.", result.Error);
        var diff = git.GetDiffResult(jobId, watchPath, "docs/big.md", preferRunLocation: true);
        Assert.True(diff.Success);
        Assert.Equal("", diff.Diff);
    }

    [Fact]
    public void UntrackedRegularFile_DiffStillShowsItsBody()
    {
        var (jobId, watchPath) = SetupJob();
        _checkout.Write("README.md", "seed");
        _checkout.Commit();
        _checkout.Write("docs/new.md", "# new page\n");

        var git = BuildGitService(watchPath);
        var diff = git.GetDiffResult(jobId, watchPath, "docs/new.md", preferRunLocation: true);

        Assert.True(diff.Success);
        Assert.Equal("# new page\n", diff.Diff);
    }

    private void AssertRefusedByBothReaders(string jobId, string watchPath, string path, string secret)
    {
        var git = BuildGitService(watchPath);
        var file = git.GetFileContentResult(jobId, watchPath, path, preferRunLocation: true);
        Assert.False(file.Success);
        Assert.Equal("Invalid file path.", file.Error);
        Assert.DoesNotContain(secret, file.Content, StringComparison.Ordinal);

        var diff = git.GetDiffResult(jobId, watchPath, path, preferRunLocation: true);
        Assert.DoesNotContain(secret, diff.Diff, StringComparison.Ordinal);
    }

    private (string jobId, string watchPath) SetupJob()
    {
        var watchPath = Path.Combine(_checkout.Root, ".orchestrator", "jobs");
        File.WriteAllText(Path.Combine(_checkout.Root, ".gitignore"), ".orchestrator/\n");

        var jobId = "demo-task";
        var jobFolder = Path.Combine(watchPath, "3-progress", jobId);
        Directory.CreateDirectory(Path.Combine(jobFolder, "logs"));
        var jobJson = new
        {
            id = jobId,
            title = "Demo task",
            state = "3-progress",
            order = 1,
            agent = "claude",
            createdAt = DateTime.UtcNow.ToString("o")
        };
        File.WriteAllText(Path.Combine(jobFolder, "task.json"),
            JsonSerializer.Serialize(jobJson, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(jobFolder, "prompt.md"), "Do the thing.");
        return (jobId, watchPath);
    }

    private GitService BuildGitService(string watchPath)
    {
        var dict = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:RootPath"] = _checkout.Root,
            ["WatchPaths:0:RepositoryPath"] = _checkout.Root,
            ["WatchPaths:0:Path"] = watchPath
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }
}
