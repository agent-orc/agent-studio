using System.Diagnostics;
using AgentStudio.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3567: <see cref="GitRefFiles"/> answers single-ref reads from the
/// repository files. Every answer it gives must be the one Git gives, and every
/// case it cannot settle must be <see cref="GitRefFileAnswer.Unknown"/> so the
/// caller asks Git.
/// </summary>
public sealed class GitRefFilesTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "agent-studio-git-ref-files-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;

    public GitRefFilesTests()
    {
        _repo = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(_repo);
        Git(_repo, "init", "-q", "-b", "main");
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "test");
        Git(_repo, "commit", "-q", "--allow-empty", "-m", "seed");
    }

    [Theory]
    [InlineData("refs/heads/main")]
    [InlineData("refs/heads/task/20")]
    [InlineData("refs/agent-studio/integration/develop")]
    [InlineData("refs/remotes/origin/develop")]
    [InlineData("refs/heads/missing")]
    [InlineData("refs/heads/task")]
    [InlineData("main")]
    [InlineData("task/20")]
    [InlineData("origin/develop")]
    [InlineData("agent-studio/integration/develop")]
    [InlineData("develop")]
    public void Loose_and_packed_refs_answer_what_rev_parse_answers(string name)
    {
        var head = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        Git(_repo, "update-ref", "refs/heads/task/20", head);
        Git(_repo, "update-ref", "refs/agent-studio/integration/develop", head);
        Git(_repo, "update-ref", "refs/remotes/origin/develop", head);

        AssertSameAsGit(_repo, name);
        Git(_repo, "pack-refs", "--all");
        Assert.False(File.Exists(Path.Combine(_repo, ".git", "refs", "heads", "main")));
        AssertSameAsGit(_repo, name);
    }

    [Fact]
    public void Loose_ref_wins_over_its_stale_packed_copy()
    {
        var seed = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        Git(_repo, "update-ref", "refs/agent-studio/integration/develop", seed);
        Git(_repo, "pack-refs", "--all");
        Git(_repo, "commit", "-q", "--allow-empty", "-m", "next");
        var next = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        Git(_repo, "update-ref", "refs/agent-studio/integration/develop", next);

        Assert.Equal(GitRefFileAnswer.Found,
            GitRefFiles.TryResolve(_repo, "refs/agent-studio/integration/develop", out var objectId));
        Assert.Equal(next, objectId);
        AssertSameAsGit(_repo, "refs/agent-studio/integration/develop");
    }

    [Fact]
    public void Git_spellings_of_a_full_name_resolve_in_gits_order()
    {
        var head = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        // rev-parse also tries refs/heads/<name> for a name that is missing as written.
        Git(_repo, "update-ref", "refs/heads/refs/heads/odd", head);

        AssertSameAsGit(_repo, "refs/heads/odd");
    }

    [Fact]
    public void Ambiguous_short_name_resolves_to_the_ref_git_picks()
    {
        var seed = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        Git(_repo, "commit", "-q", "--allow-empty", "-m", "next");
        Git(_repo, "branch", "dup");
        Git(_repo, "tag", "dup", seed);

        AssertSameAsGit(_repo, "dup");
        Assert.Equal(GitRefFileAnswer.Found, GitRefFiles.TryResolve(_repo, "dup", out var objectId));
        Assert.Equal(seed, objectId);
    }

    [Fact]
    public void Short_name_that_exists_in_the_git_directory_is_left_to_git()
    {
        var head = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        File.WriteAllText(Path.Combine(_repo, ".git", "develop"), head + "\n");

        Assert.Equal(GitRefFileAnswer.Unknown, GitRefFiles.TryResolve(_repo, "develop", out _));
    }

    [Fact]
    public void A_ref_pointing_at_a_missing_object_is_still_answered_like_git()
    {
        var ghost = new string('1', 40);
        File.WriteAllText(Path.Combine(_repo, ".git", "refs", "heads", "ghost"), ghost + "\n");

        Assert.Equal(GitRefFileAnswer.Found, GitRefFiles.TryResolve(_repo, "refs/heads/ghost", out var objectId));
        Assert.Equal(ghost, objectId);
        AssertSameAsGit(_repo, "refs/heads/ghost");
    }

    [Theory]
    [InlineData("refs/bisect/bad")]
    [InlineData("refs/worktree/note")]
    [InlineData("HEAD")]
    [InlineData("MERGE_HEAD")]
    [InlineData("deadbeef")]
    [InlineData("main@{u}")]
    [InlineData("refs/heads/main^{tree}")]
    [InlineData("refs/heads/a..b")]
    [InlineData("refs/heads/.hidden")]
    [InlineData("refs/heads/x.lock")]
    public void Names_outside_the_plain_case_are_left_to_git(string name)
        => Assert.Equal(GitRefFileAnswer.Unknown, GitRefFiles.TryResolve(_repo, name, out _));

    [Fact]
    public void Symbolic_ref_is_left_to_git()
    {
        Git(_repo, "symbolic-ref", "refs/heads/alias", "refs/heads/main");

        Assert.Equal(GitRefFileAnswer.Unknown, GitRefFiles.TryResolve(_repo, "refs/heads/alias", out _));
    }

    [Fact]
    public void Subdirectory_bare_repository_and_reftable_are_left_to_git()
    {
        var subdirectory = Path.Combine(_repo, "src");
        Directory.CreateDirectory(subdirectory);
        var bare = Path.Combine(_tempDir, "origin.git");
        Git(_tempDir, "init", "-q", "--bare", bare);

        Assert.Equal(GitRefFileAnswer.Unknown, GitRefFiles.TryResolve(subdirectory, "refs/heads/main", out _));
        Assert.Equal(GitRefFileAnswer.Unknown, GitRefFiles.TryResolve(bare, "refs/heads/main", out _));
        Assert.Null(GitRefFiles.IsLinkedWorktree(subdirectory));
        Assert.Equal(GitRefFileAnswer.Unknown, GitRefFiles.TryResolveHead(subdirectory, out _));

        Directory.CreateDirectory(Path.Combine(_repo, ".git", "reftable"));
        Assert.Equal(GitRefFileAnswer.Unknown, GitRefFiles.TryResolve(_repo, "refs/heads/main", out _));
    }

    [Fact]
    public void Linked_worktree_reads_the_shared_refs_and_its_own_head()
    {
        var worktree = Path.Combine(_tempDir, "integration");
        Git(_repo, "worktree", "add", "-q", "--detach", worktree, "main");
        Git(_repo, "commit", "-q", "--allow-empty", "-m", "main moves on");

        Assert.False(GitRefFiles.IsLinkedWorktree(_repo));
        Assert.True(GitRefFiles.IsLinkedWorktree(worktree));
        Assert.Equal(IsLinkedByGit(_repo), GitRefFiles.IsLinkedWorktree(_repo));
        Assert.Equal(IsLinkedByGit(worktree), GitRefFiles.IsLinkedWorktree(worktree));
        AssertSameAsGit(worktree, "refs/heads/main");
        AssertHeadSameAsGit(worktree);
        AssertHeadSameAsGit(_repo);
    }

    [Fact]
    public void Abbreviated_head_and_common_directory_match_git()
    {
        var worktree = Path.Combine(_tempDir, "integration");
        Git(_repo, "worktree", "add", "-q", "--detach", worktree, "main");

        AssertAbbreviatedHeadSameAsGit(_repo);
        AssertAbbreviatedHeadSameAsGit(worktree);
        Assert.Equal("main", Abbreviated(_repo));
        Assert.Equal("HEAD", Abbreviated(worktree));
        foreach (var root in new[] { _repo, worktree })
        {
            var common = Git(root, "rev-parse", "--git-common-dir").Out.Trim();
            Assert.Equal(Path.GetFullPath(common, root), GitRefFiles.CommonDirectory(root));
        }

        Git(_repo, "checkout", "-q", "-b", "task/20");
        AssertAbbreviatedHeadSameAsGit(_repo);
        Assert.Equal("task/20", Abbreviated(_repo));
    }

    [Fact]
    public void Abbreviated_head_that_git_would_lengthen_is_left_to_git()
    {
        Git(_repo, "tag", "main");

        Assert.Equal("heads/main", Git(_repo, "rev-parse", "--abbrev-ref", "HEAD").Out.Trim());
        Assert.Equal(GitRefFileAnswer.Unknown, GitRefFiles.TryReadAbbreviatedHead(_repo, out _));
    }

    [Fact]
    public void Merge_head_and_git_directory_match_git()
    {
        AssertPseudoRefSameAsGit(_repo, "MERGE_HEAD");
        Git(_repo, "checkout", "-q", "-b", "side");
        File.WriteAllText(Path.Combine(_repo, "file.txt"), "side\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "side");
        Git(_repo, "checkout", "-q", "main");
        File.WriteAllText(Path.Combine(_repo, "file.txt"), "main\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "main");
        Assert.NotEqual(0, Git(_repo, "merge", "--no-edit", "side").Code);

        AssertPseudoRefSameAsGit(_repo, "MERGE_HEAD");
        Assert.Equal(GitRefFileAnswer.Found, GitRefFiles.TryResolvePseudoRef(_repo, "MERGE_HEAD", out _));
        Assert.Equal(GitRefFileAnswer.Unknown, GitRefFiles.TryResolvePseudoRef(_repo, "merge_head", out _));

        var worktree = Path.Combine(_tempDir, "integration");
        Git(_repo, "worktree", "add", "-q", "--detach", worktree, "side");
        foreach (var root in new[] { _repo, worktree })
        {
            var gitDir = Git(root, "rev-parse", "--git-dir").Out.Trim();
            Assert.Equal(Path.GetFullPath(gitDir, root), GitRefFiles.GitDirectory(root));
        }
    }

    [Fact]
    public void Remote_defined_in_the_repository_config_is_found_like_git_remote()
    {
        Assert.Null(GitRefFiles.DefinesRemote(_repo, "origin"));
        Git(_repo, "remote", "add", "origin", Path.Combine(_tempDir, "origin.git"));
        Git(_repo, "config", "remote.Upper.url", Path.Combine(_tempDir, "upper.git"));

        var listed = Git(_repo, "remote").Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("origin", listed);
        Assert.True(GitRefFiles.DefinesRemote(_repo, "origin"));
        Assert.Contains("Upper", listed);
        Assert.True(GitRefFiles.DefinesRemote(_repo, "Upper"));
        Assert.DoesNotContain("upper", listed);
        Assert.Null(GitRefFiles.DefinesRemote(_repo, "upper"));
        Assert.Null(GitRefFiles.DefinesRemote(_repo, "fork"));
    }

    [SkippableFact]
    public void Work_tree_root_matches_show_toplevel_or_is_left_to_git()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows path spelling is always left to Git.");
        var worktree = Path.Combine(_tempDir, "integration");
        Git(_repo, "worktree", "add", "-q", "--detach", worktree, "main");
        foreach (var root in new[] { _repo, worktree })
            Assert.Equal(Git(root, "rev-parse", "--show-toplevel").Out.Trim(), GitRefFiles.WorkTreeRoot(root));

        var subdirectory = Path.Combine(_repo, "src");
        Directory.CreateDirectory(subdirectory);
        Assert.Null(GitRefFiles.WorkTreeRoot(subdirectory));

        var link = Path.Combine(_tempDir, "linked-repo");
        Directory.CreateSymbolicLink(link, _repo);
        Assert.NotEqual(link, Git(link, "rev-parse", "--show-toplevel").Out.Trim());
        Assert.Null(GitRefFiles.WorkTreeRoot(link));

        Git(_repo, "config", "core.worktree", subdirectory);
        Assert.Null(GitRefFiles.WorkTreeRoot(_repo));
    }

    [Fact]
    public void Head_on_an_unborn_branch_is_absent_like_git()
    {
        var fresh = Path.Combine(_tempDir, "fresh");
        Directory.CreateDirectory(fresh);
        Git(fresh, "init", "-q", "-b", "main");

        Assert.Equal(GitRefFileAnswer.Absent, GitRefFiles.TryResolveHead(fresh, out var objectId));
        Assert.Null(objectId);
        Assert.NotEqual(0, Git(fresh, "rev-parse", "HEAD").Code);
        Assert.Equal(GitRefFileAnswer.Absent, GitRefFiles.TryReadAbbreviatedHead(fresh, out _));
        Assert.NotEqual(0, Git(fresh, "rev-parse", "--abbrev-ref", "HEAD").Code);
    }

    [Fact]
    public void GitService_reads_match_git_with_and_without_the_files()
    {
        var config = new ConfigurationBuilder().Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var head = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        Git(_repo, "update-ref", GitService.IntegrationLaneRef("develop"), head);

        Assert.Equal(head, git.GetBranchTip(_repo, GitService.IntegrationLaneRef("develop")));
        Assert.Null(git.GetBranchTip(_repo, GitService.IntegrationLaneRef("main")));
        Assert.Equal(head, git.GetBranchTip(_repo, "main"));
        Assert.True(git.BranchExists(_repo, "main"));
        Assert.False(git.BranchExists(_repo, "develop"));
        Assert.False(git.RemoteBranchExists(_repo, "develop"));
        Assert.Equal(head, git.ReadHeadShaAt(_repo));
        Assert.Equal("main", git.ReadBranchAt(_repo));

        // A subdirectory is answered by Git, with the same results.
        var subdirectory = Path.Combine(_repo, "src");
        Directory.CreateDirectory(subdirectory);
        Assert.Equal(head, git.GetBranchTip(subdirectory, GitService.IntegrationLaneRef("develop")));
        Assert.Equal(head, git.ReadHeadShaAt(subdirectory));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_tempDir)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception ex) { SilentCatch.Note(ex, "GitRefFilesTests cleanup"); }
    }

    private static void AssertSameAsGit(string repo, string name)
    {
        var git = Git(repo, "rev-parse", "--verify", "--quiet", name);
        var answer = GitRefFiles.TryResolve(repo, name, out var objectId);
        if (git.Code == 0)
        {
            Assert.Equal(GitRefFileAnswer.Found, answer);
            Assert.Equal(git.Out.Trim(), objectId);
        }
        else
        {
            Assert.Equal(GitRefFileAnswer.Absent, answer);
            Assert.Null(objectId);
        }
    }

    private static void AssertPseudoRefSameAsGit(string repo, string name)
    {
        var git = Git(repo, "rev-parse", "--verify", "--quiet", name);
        var answer = GitRefFiles.TryResolvePseudoRef(repo, name, out var objectId);
        Assert.Equal(git.Code == 0 ? GitRefFileAnswer.Found : GitRefFileAnswer.Absent, answer);
        Assert.Equal(git.Code == 0 ? git.Out.Trim() : null, objectId);
    }

    private static string? Abbreviated(string repo)
    {
        Assert.Equal(GitRefFileAnswer.Found, GitRefFiles.TryReadAbbreviatedHead(repo, out var name));
        return name;
    }

    private static void AssertAbbreviatedHeadSameAsGit(string repo)
    {
        var git = Git(repo, "rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal(0, git.Code);
        Assert.Equal(git.Out.Trim(), Abbreviated(repo));
    }

    private static void AssertHeadSameAsGit(string repo)
    {
        var git = Git(repo, "rev-parse", "HEAD");
        Assert.Equal(0, git.Code);
        Assert.Equal(GitRefFileAnswer.Found, GitRefFiles.TryResolveHead(repo, out var objectId));
        Assert.Equal(git.Out.Trim(), objectId);
    }

    private static bool IsLinkedByGit(string repo)
    {
        var lines = Git(repo, "rev-parse", "--git-dir", "--git-common-dir").Out
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return !string.Equals(Path.GetFullPath(lines[0], repo), Path.GetFullPath(lines[1], repo),
            StringComparison.Ordinal);
    }

    private static (string Out, string Err, int Code) Git(string cwd, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (stdout, stderr.GetAwaiter().GetResult(), process.ExitCode);
    }
}
