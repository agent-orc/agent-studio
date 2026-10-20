using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerFirstRunGitCredentialsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "runner-first-run-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Review_finds_only_the_exact_host_owned_repository_credential()
    {
        var folder = Path.Combine(_root, "first-run-git", "setup-a");
        Directory.CreateDirectory(folder);
        var origin = "https://github.com/example/demo.git";
        var token = "test-" + new string('x', 36);
        File.WriteAllText(Path.Combine(folder, "origin"), origin);
        var credential = Path.Combine(folder, "token");
        File.WriteAllText(credential, token + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(credential, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var found = RunnerFirstRunGitCredentials.Token(_root, origin);
        Assert.Equal(token, found?.Password);
        Assert.Null(RunnerFirstRunGitCredentials.Token(_root,
            "https://github.com/example/other.git"));
    }

    [Fact]
    public async Task Deploy_key_setup_does_not_write_global_git_or_read_only_ssh_config()
    {
        Directory.CreateDirectory(_root);
        var config = Path.Combine(_root, "gitconfig");
        var sshConfig = Path.Combine(_root, "ssh", "config");
        Directory.CreateDirectory(Path.GetDirectoryName(sshConfig)!);
        await File.WriteAllTextAsync(sshConfig, "Host *\n  Port 22\n");
        var origin = "https://github.com/example/demo.git";
        var firstKey = Path.Combine(_root, "first-key");
        await File.WriteAllTextAsync(config, "[user]\n\tname = Existing\n");

        var sshDirectory = Path.GetDirectoryName(sshConfig)!;
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(sshDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await RunnerGitSetupAgent.ConfigureGitAsync(origin, null, firstKey, default,
                config, sshConfig);
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(sshDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
        }
        Assert.Equal("[user]\n\tname = Existing\n", await File.ReadAllTextAsync(config));
        Assert.Equal("Host *\n  Port 22\n", await File.ReadAllTextAsync(sshConfig));
        Assert.Equal("git@github.com:example/demo.git",
            PlatformRepositoryGitProof.GitRepositoryUrl(origin, firstKey));
        Assert.Equal("https://github.com/example/demo-tools.git",
            PlatformRepositoryGitProof.GitRepositoryUrl("https://github.com/example/demo-tools.git", null));
    }

    [Fact]
    public async Task Token_helper_answers_only_the_selected_repository()
    {
        var folder = Path.Combine(_root, "first-run-git", "setup-token");
        Directory.CreateDirectory(folder);
        var origin = "https://github.com/example/demo.git";
        var token = "test-" + new string('x', 36);
        var tokenFile = Path.Combine(folder, "token");
        var config = Path.Combine(_root, "gitconfig");
        await File.WriteAllTextAsync(tokenFile, token + "\n");
        await RunnerGitSetupAgent.ConfigureGitAsync(origin, tokenFile, null, default,
            config);
        Assert.DoesNotContain(token, await File.ReadAllTextAsync(config));
        var environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_GLOBAL"] = config,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "0",
        };
        var selected = await ProcessRunner.RunAsync("git", ["credential", "fill"],
            stdin: "protocol=https\nhost=github.com\npath=example/demo.git\n\n",
            environment: environment);
        Assert.True(selected.Success);
        Assert.Contains("password=" + token, selected.StdOut);
        var sibling = await ProcessRunner.RunAsync("git", ["credential", "fill"],
            stdin: "protocol=https\nhost=github.com\npath=example/demo-tools.git\n\n",
            environment: environment);
        Assert.DoesNotContain(token, sibling.StdOut);
    }

    [Fact]
    public void Project_git_uses_the_key_only_for_its_registered_remote()
    {
        var folder = Path.Combine(_root, "first-run-git", "setup-key");
        Directory.CreateDirectory(folder);
        var origin = "https://github.com/example/demo.git";
        File.WriteAllText(Path.Combine(folder, "origin"), origin);
        File.WriteAllText(Path.Combine(folder, "deploy-key"), "test-key");

        var (clone, environment) = RunnerFirstRunGitCredentials.ForProjectGit(_root, origin,
            ["clone", origin, "/tmp/checkout"]);
        Assert.Equal("git@github.com:example/demo.git", clone[1]);
        Assert.Contains("deploy-key", environment!["GIT_SSH_COMMAND"]);
        var (fetch, _) = RunnerFirstRunGitCredentials.ForProjectGit(_root, origin,
            ["fetch", "origin", "--prune"]);
        Assert.Equal(["fetch", "--prune", "git@github.com:example/demo.git",
            "+refs/heads/*:refs/remotes/origin/*"], fetch);
        var (push, _) = RunnerFirstRunGitCredentials.ForProjectGit(_root, origin,
            ["push", "origin", "HEAD:refs/heads/probe"]);
        Assert.Equal("git@github.com:example/demo.git", push[1]);
        var (sibling, siblingEnvironment) = RunnerFirstRunGitCredentials.ForProjectGit(_root, origin,
            ["ls-remote", "https://github.com/example/demo-tools.git"]);
        Assert.Equal("https://github.com/example/demo-tools.git", sibling[1]);
        Assert.Null(siblingEnvironment);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
