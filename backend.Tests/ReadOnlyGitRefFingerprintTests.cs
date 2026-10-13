using System.Diagnostics;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ReadOnlyGitRefFingerprintTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "git-ref-fingerprint-" + Guid.NewGuid().ToString("N"));

    public ReadOnlyGitRefFingerprintTests()
    {
        Directory.CreateDirectory(_root);
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "test");
        File.WriteAllText(Path.Combine(_root, "seed.txt"), "seed");
        Git("add", "-A");
        Git("commit", "-q", "-m", "seed");
        Git("branch", "develop");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Capture_IsStableForWorkingTreeAndUnrelatedTaskBranchChanges()
    {
        var before = ReadOnlyGitRefFingerprint.Capture(_root, ["develop", "main"]);

        File.WriteAllText(Path.Combine(_root, "uncommitted.txt"), "working tree only");
        Git("branch", "task/unrelated");
        var after = ReadOnlyGitRefFingerprint.Capture(_root, ["develop", "main"]);

        Assert.Equal(before, after);
    }

    [Fact]
    public void Capture_ChangesWhenTrackedBranchOrTagMoves()
    {
        var before = ReadOnlyGitRefFingerprint.Capture(
            _root, ["develop", "main"], includeTags: true);

        Git("checkout", "-q", "develop");
        File.WriteAllText(Path.Combine(_root, "develop.txt"), "change");
        Git("add", "-A");
        Git("commit", "-q", "-m", "develop change");
        var afterBranch = ReadOnlyGitRefFingerprint.Capture(
            _root, ["develop", "main"], includeTags: true);
        Assert.NotEqual(before, afterBranch);

        Git("tag", "v1.0.0");
        var afterTag = ReadOnlyGitRefFingerprint.Capture(
            _root, ["develop", "main"], includeTags: true);
        Assert.NotEqual(afterBranch, afterTag);
    }

    [Fact]
    public void Capture_OriginHeadFallbackAlsoTracksCorrespondingLocalBranch()
    {
        Git("branch", "trunk", "main");
        Git("update-ref", "refs/remotes/origin/trunk", "main");
        Git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/trunk");
        var before = ReadOnlyGitRefFingerprint.Capture(_root, ["develop", "main"]);

        Git("checkout", "-q", "trunk");
        File.WriteAllText(Path.Combine(_root, "trunk.txt"), "change");
        Git("add", "-A");
        Git("commit", "-q", "-m", "trunk change");

        var after = ReadOnlyGitRefFingerprint.Capture(_root, ["develop", "main"]);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void CaptureDetailed_ReftableUsesShortFallbackAndTablesListStamp()
    {
        var reftable = Path.Combine(_root, ".git", "reftable");
        Directory.CreateDirectory(reftable);
        var tablesList = Path.Combine(reftable, "tables.list");
        File.WriteAllText(tablesList, "0x000000000001-0x000000000002.ref\n");

        var before = ReadOnlyGitRefFingerprint.CaptureDetailed(_root, ["develop", "main"]);
        Assert.True(before.RequiresShortFallback);

        File.AppendAllText(tablesList, "0x000000000003-0x000000000004.ref\n");
        var after = ReadOnlyGitRefFingerprint.CaptureDetailed(_root, ["develop", "main"]);
        Assert.NotEqual(before.Value, after.Value);
    }

    [Fact]
    public void Capture_HeadFallbackTracksCorrespondingOriginMirror()
    {
        Git("branch", "trunk", "main");
        Git("checkout", "-q", "develop");
        File.WriteAllText(Path.Combine(_root, "develop-only.txt"), "change");
        Git("add", "-A");
        Git("commit", "-q", "-m", "develop-only change");
        Git("checkout", "-q", "trunk");
        Git("update-ref", "refs/remotes/origin/trunk", "main");
        var before = ReadOnlyGitRefFingerprint.Capture(_root, ["develop", "main"]);

        Git("update-ref", "refs/remotes/origin/trunk", "develop");

        var after = ReadOnlyGitRefFingerprint.Capture(_root, ["develop", "main"]);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Capture_NestedLooseTagChangeUsesDirectoryMetadata()
    {
        Git("tag", "v1/one");
        PinTagTreeTimes();
        var before = ReadOnlyGitRefFingerprint.Capture(
            _root, ["develop", "main"], includeTags: true);

        // Both tag writes land inside one directory-mtime tick, which is what
        // a coarse kernel tick or an overlayfs copy-up produces on a gate host.
        Git("tag", "v1/two");
        PinTagTreeTimes();

        var after = ReadOnlyGitRefFingerprint.Capture(
            _root, ["develop", "main"], includeTags: true);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Capture_NestedLooseTagRemovalWithinOneTickChangesFingerprint()
    {
        Git("tag", "v1/one");
        Git("tag", "v1/two");
        PinTagTreeTimes();
        var before = ReadOnlyGitRefFingerprint.Capture(
            _root, ["develop", "main"], includeTags: true);

        // git tag -d also rewrites packed-refs, which would hide the loose tree
        // signal. Remove only the loose file, as a loose-only prune does.
        File.Delete(Path.Combine(_root, ".git", "refs", "tags", "v1", "two"));
        PinTagTreeTimes();

        var after = ReadOnlyGitRefFingerprint.Capture(
            _root, ["develop", "main"], includeTags: true);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Capture_NestedLooseTagRewriteChangesFingerprintThroughFileMetadata()
    {
        Git("tag", "v1/one");
        PinTagTreeTimes();
        var before = ReadOnlyGitRefFingerprint.Capture(
            _root, ["develop", "main"], includeTags: true);

        // Retarget the tag without moving any tracked branch. The directory
        // listing, directory times and file size stay identical; only the loose
        // ref file's own write time moves when git replaces it.
        Git("tag", "-f", "v1/one", "main^{tree}");
        PinTagTreeTimes();
        File.SetLastWriteTimeUtc(
            Path.Combine(_root, ".git", "refs", "tags", "v1", "one"),
            PinnedTagTime.AddSeconds(1));

        var after = ReadOnlyGitRefFingerprint.Capture(
            _root, ["develop", "main"], includeTags: true);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void CaptureDetailed_OversizedLooseTagTreeUsesShortFallback()
    {
        var tags = Path.Combine(_root, ".git", "refs", "tags", "bulk");
        Directory.CreateDirectory(tags);
        for (var i = 0; i <= 4096; i++)
            File.WriteAllText(Path.Combine(tags, $"t{i:D4}"), string.Empty);

        var withTags = ReadOnlyGitRefFingerprint.CaptureDetailed(
            _root, ["develop", "main"], includeTags: true);
        var withoutTags = ReadOnlyGitRefFingerprint.CaptureDetailed(
            _root, ["develop", "main"]);

        Assert.True(withTags.RequiresShortFallback);
        Assert.False(withoutTags.RequiresShortFallback);
    }

    // clock-independent: a fixed literal replaces every refs/tags directory and
    // loose tag file time so the fingerprint cannot depend on the host's mtime tick.
    private static readonly DateTime PinnedTagTime =
        new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private void PinTagTreeTimes()
    {
        var tags = Path.Combine(_root, ".git", "refs", "tags");
        foreach (var file in Directory.EnumerateFiles(tags, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, PinnedTagTime);
        foreach (var directory in Directory.EnumerateDirectories(tags, "*", SearchOption.AllDirectories))
            Directory.SetLastWriteTimeUtc(directory, PinnedTagTime);
        Directory.SetLastWriteTimeUtc(tags, PinnedTagTime);
    }

    [Fact]
    public void ResolveCommonDirectory_LinkedWorktreeUsesSameAdmissionIdentity()
    {
        var linkedRoot = Path.Combine(
            Path.GetTempPath(),
            "git-ref-fingerprint-linked-" + Guid.NewGuid().ToString("N"));
        try
        {
            Git("worktree", "add", "-q", linkedRoot, "develop");

            var mainCommon = ReadOnlyGitRefFingerprint.ResolveCommonDirectory(_root);
            var linkedCommon = ReadOnlyGitRefFingerprint.ResolveCommonDirectory(linkedRoot);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            Assert.NotNull(mainCommon);
            Assert.True(string.Equals(mainCommon, linkedCommon, comparison));
            Assert.True(string.Equals(
                BuildTestGateRunner.ResolveAdmissionKey(_root),
                BuildTestGateRunner.ResolveAdmissionKey(linkedRoot),
                comparison));
        }
        finally
        {
            if (Directory.Exists(linkedRoot))
            {
                try { Git("worktree", "remove", "--force", linkedRoot); }
                catch { /* best effort */ }
                try { Directory.Delete(linkedRoot, recursive: true); }
                catch { /* best effort */ }
            }
        }
    }

    private void Git(params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
    }
}
