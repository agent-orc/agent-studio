using System.Diagnostics;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The once-per-process starting repository (AGT-3419): a copy behaves like a
/// freshly built repository, two copies never share state, and nothing a test
/// does to its copy can reach the template.
/// </summary>
public sealed class GitRepositoryTemplateTests : IDisposable
{
    private readonly TempWorkspace _workspace = new("git-template-tests");
    private int _builds;

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Template_is_built_once_and_each_copy_is_a_working_repository()
    {
        var key = Key();
        var first = _workspace.Combine("first");
        var second = _workspace.Combine("second");

        GitRepositoryTemplate.CopyTo(key, first, SeedRepository);
        GitRepositoryTemplate.CopyTo(key, second, SeedRepository);

        Assert.Equal(1, _builds);
        foreach (var repo in new[] { first, second })
        {
            Assert.Equal("main", Git(repo, "branch", "--show-current"));
            Assert.Equal("template@example.invalid", Git(repo, "config", "user.email"));
            Assert.Equal("seed", Git(repo, "log", "-1", "--format=%s"));
            Assert.Equal("", Git(repo, "status", "--porcelain"));
        }
        Assert.Equal(Git(first, "rev-parse", "HEAD"), Git(second, "rev-parse", "HEAD"));
    }

    [Fact]
    public void Writes_into_one_copy_reach_neither_the_other_copy_nor_the_template()
    {
        var key = Key();
        var first = _workspace.Combine("first");
        var second = _workspace.Combine("second");
        GitRepositoryTemplate.CopyTo(key, first, SeedRepository);
        GitRepositoryTemplate.CopyTo(key, second, SeedRepository);

        File.WriteAllText(Path.Combine(first, "README.md"), "changed\n");
        Git(first, "commit", "-qam", "change");
        Git(first, "config", "user.name", "Changed Name");

        var third = _workspace.Combine("third");
        GitRepositoryTemplate.CopyTo(key, third, SeedRepository);
        foreach (var repo in new[] { second, third })
        {
            Assert.Equal("seed\n", File.ReadAllText(Path.Combine(repo, "README.md")));
            Assert.Equal("seed", Git(repo, "log", "-1", "--format=%s"));
            Assert.Equal("Template Test", Git(repo, "config", "user.name"));
        }
    }

    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public void Template_itself_refuses_writes_from_git_and_from_files()
    {
        PlatformGate.LinuxOnly("read-only folders only stop writes on Unix");
        Skip.If(Environment.IsPrivilegedProcess, "root ignores file permissions");
        string? template = null;
        GitRepositoryTemplate.CopyTo(Key(), _workspace.Combine("copy"), root =>
        {
            template = root;
            SeedRepository(root);
        });

        Assert.NotNull(template);
        Assert.ThrowsAny<UnauthorizedAccessException>(
            () => File.WriteAllText(Path.Combine(template!, "README.md"), "changed\n"));
        Assert.ThrowsAny<UnauthorizedAccessException>(
            () => File.WriteAllText(Path.Combine(template!, "new.txt"), "new\n"));
        Assert.NotEqual(0, GitExitCode(template!, "commit", "--allow-empty", "-qm", "into the template"));
        Assert.True(TestTempRoot.TryDelete(template!));
    }

    [Fact]
    public void Copy_keeps_the_mode_of_each_file()
    {
        GitRepositoryTemplate.CopyTo(Key(), _workspace.Combine("copy"), root =>
        {
            SeedRepository(root);
            var script = Path.Combine(root, "run.sh");
            File.WriteAllText(script, "#!/bin/sh\nexit 0\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(script, File.GetUnixFileMode(script) | UnixFileMode.UserExecute);
        });

        var copy = _workspace.Combine("copy");
        File.AppendAllText(Path.Combine(copy, "README.md"), "writable\n");
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(copy, "run.sh")).HasFlag(UnixFileMode.UserExecute));
            Assert.True(File.GetUnixFileMode(Path.Combine(copy, "README.md")).HasFlag(UnixFileMode.UserWrite));
        }
    }

    [Fact]
    public void Placed_repository_and_origin_point_at_each_other_and_not_at_the_template()
    {
        var key = Key();
        var repo = _workspace.Combine("work", "project");
        var origin = _workspace.Combine("remotes", "project.git");

        GitRepositoryTemplate.CopyTo(key, SeedRepositoryWithOrigin, ("repo", repo), ("origin.git", origin));

        Assert.Equal(origin, Git(repo, "remote", "get-url", "origin"));
        // FETCH_HEAD names the remote without its `.git` suffix.
        Assert.Contains($" of {origin[..^".git".Length]}",
            File.ReadAllText(Path.Combine(repo, ".git", "FETCH_HEAD")));
        File.WriteAllText(Path.Combine(repo, "next.txt"), "next\n");
        Git(repo, "add", "next.txt");
        Git(repo, "commit", "-qm", "next");
        Git(repo, "push", "-q", "origin", "main");
        Assert.Equal(Git(repo, "rev-parse", "HEAD"), Git(origin, "rev-parse", "refs/heads/main"));

        var otherRepo = _workspace.Combine("other", "project");
        var otherOrigin = _workspace.Combine("other", "project.git");
        GitRepositoryTemplate.CopyTo(key, SeedRepositoryWithOrigin, ("repo", otherRepo), ("origin.git", otherOrigin));
        Assert.Equal("seed", Git(otherOrigin, "log", "-1", "--format=%s", "refs/heads/main"));
    }

    [Fact]
    public void Placement_that_leaves_a_path_into_the_template_is_refused()
    {
        var key = Key();

        var refused = Assert.Throws<InvalidOperationException>(() => GitRepositoryTemplate.CopyTo(
            key, SeedRepositoryWithOrigin, ("repo", _workspace.Combine("repo-only"))));

        Assert.Contains("still points into the template", refused.Message);
    }

    private string Key() => $"{nameof(GitRepositoryTemplateTests)}:{Guid.NewGuid():N}";

    private void SeedRepository(string root)
    {
        _builds++;
        Git(root, "init", "-q", "-b", "main");
        Git(root, "config", "user.email", "template@example.invalid");
        Git(root, "config", "user.name", "Template Test");
        File.WriteAllText(Path.Combine(root, "README.md"), "seed\n");
        Git(root, "add", "README.md");
        Git(root, "commit", "-qm", "seed");
    }

    private void SeedRepositoryWithOrigin(string root)
    {
        var repo = Path.Combine(root, "repo");
        var origin = Path.Combine(root, "origin.git");
        Directory.CreateDirectory(repo);
        SeedRepository(repo);
        Git(root, "init", "-q", "--bare", "-b", "main", origin);
        Git(repo, "remote", "add", "origin", origin);
        Git(repo, "push", "-q", "-u", "origin", "main");
        Git(repo, "fetch", "-q", "origin");
    }

    private static string Git(string cwd, params string[] args)
    {
        var (code, output, error) = RunGit(cwd, args);
        Assert.True(code == 0, $"git {string.Join(' ', args)}: {error}");
        return output;
    }

    private static int GitExitCode(string cwd, params string[] args) => RunGit(cwd, args).Code;

    private static (int Code, string Output, string Error) RunGit(string cwd, string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Result.Trim(), error);
    }
}
