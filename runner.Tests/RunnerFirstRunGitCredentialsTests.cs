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
    public async Task Deploy_key_rewrite_uses_only_its_repository_alias_and_replaces_the_key()
    {
        Directory.CreateDirectory(_root);
        var config = Path.Combine(_root, "gitconfig");
        var sshConfig = Path.Combine(_root, "ssh", "config");
        Directory.CreateDirectory(Path.GetDirectoryName(sshConfig)!);
        await File.WriteAllTextAsync(sshConfig, "Host *\n  Port 22\n");
        var origin = "https://github.com/example/demo.git";
        var firstKey = Path.Combine(_root, "first-key");
        var nextKey = Path.Combine(_root, "next-key");

        await RunnerGitSetupAgent.ConfigureGitAsync(origin, null, firstKey, default,
            config, sshConfig);
        var environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_GLOBAL"] = config,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
        };
        var resolved = await ProcessRunner.RunAsync("git",
            ["ls-remote", "--get-url", origin], environment: environment);
        Assert.True(resolved.Success);
        Assert.StartsWith("git@agent-studio-first-run-", resolved.StdOut.Trim());
        Assert.EndsWith(":example/demo.git", resolved.StdOut.Trim());
        var other = await ProcessRunner.RunAsync("git",
            ["ls-remote", "--get-url", "git@github.com:example/other.git"],
            environment: environment);
        Assert.Equal("git@github.com:example/other.git", other.StdOut.Trim());
        Assert.DoesNotContain("sshCommand", await File.ReadAllTextAsync(config));

        await RunnerGitSetupAgent.ConfigureGitAsync(origin, null, nextKey, default,
            config, sshConfig);
        Assert.Equal(1, (await File.ReadAllTextAsync(config))
            .Split("insteadOf = " + origin).Length - 1);
        var ssh = await File.ReadAllTextAsync(sshConfig);
        Assert.StartsWith("# agent-studio-first-run ", ssh);
        Assert.Contains("Host *\n  Port 22\n", ssh);
        Assert.DoesNotContain(firstKey, ssh);
        Assert.Contains(nextKey.Replace('\\', '/'), ssh);
        Assert.Equal(1, ssh.Split("HostName github.com").Length - 1);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
