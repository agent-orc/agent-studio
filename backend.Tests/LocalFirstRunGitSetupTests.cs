using System.Text.Json;
using System.Diagnostics;
using AgentStudio.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalFirstRunGitSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "first-run-git-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Token_stays_in_protected_host_file_and_verify_uses_real_probe_result()
    {
        Directory.CreateDirectory(_root);
        var repository = Path.Combine(_root, "checkout");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
        }).Build();
        var projects = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var workspaces = new WorkspaceRegistry(config, NullLogger<WorkspaceRegistry>.Instance);
        var project = projects.EnsureProjectForStorage(Path.Combine(_root, "project"), "Demo", "ws-default");
        projects.Update(project.Id, new UpdateProjectRequest
        {
            RepositoryUrl = "https://github.com/example/demo.git",
            RepositoryPath = repository,
        }, workspaces);
        var git = new GitProbe();
        var probe = new LocalFirstRunRepositoryProbe(new FakeTimeProvider(), git);
        var setup = new LocalFirstRunGitSetup(projects, probe,
            Path.Combine(_root, "secrets"), Path.Combine(_root, "gitconfig"),
            Path.Combine(_root, "ssh", "config"));
        var token = "test-" + new string('x', 36);

        var started = await setup.StartAsync(new(project.Id, "token", token), default);
        Assert.DoesNotContain(token, JsonSerializer.Serialize(started));
        Assert.Equal(token, File.ReadAllText(Path.Combine(_root, "secrets", started.Id, "token")).Trim());
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(_root, "gitconfig")));
        Assert.Contains("password=" + token, GitCredential("example/demo.git"));
        Assert.DoesNotContain(token, GitCredential("example/other.git"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(_root, "secrets", started.Id, "token")));

        var verified = await setup.VerifyAsync(started.Id, default);
        Assert.Equal("verified", verified?.State);
        Assert.True(verified!.FetchVerified);
        Assert.True(verified.PushVerified);
        Assert.DoesNotContain(token, JsonSerializer.Serialize(verified));
        Assert.Contains(git.Calls, args => args[0] == "push" && args[2].StartsWith(':'));

        var keySetup = await setup.StartAsync(new(project.Id, "deploy-key", null), default);
        Assert.StartsWith("ssh-ed25519 ", keySetup.PublicKey);
        Assert.True(File.Exists(Path.Combine(_root, "secrets", keySetup.Id, "deploy-key")));
        Assert.DoesNotContain("PRIVATE KEY", JsonSerializer.Serialize(keySetup));
        Assert.Equal("https://github.com/example/demo.git",
            GitUrl("https://github.com/example/demo.git"));
        Assert.Equal("https://github.com/example/demo-tools.git",
            GitUrl("https://github.com/example/demo-tools.git"));
        Assert.DoesNotContain("sshCommand", File.ReadAllText(Path.Combine(_root, "gitconfig")));
        Assert.False(File.Exists(Path.Combine(_root, "ssh", "config")));
        var keyVerified = await setup.VerifyAsync(keySetup.Id, default);
        Assert.Equal("verified", keyVerified?.State);
        Assert.Contains(git.Calls, args => args[0] == "ls-remote"
            && args[2] == "git@github.com:example/demo.git");
        Assert.Contains(git.Environments, env => env.TryGetValue("GIT_SSH_COMMAND", out var command)
            && command!.Contains(Path.Combine(_root, "secrets", keySetup.Id, "deploy-key")));

        var secondKey = await setup.StartAsync(new(project.Id, "deploy-key", null), default);
        Assert.Equal("https://github.com/example/demo.git",
            GitUrl("https://github.com/example/demo.git"));
        Assert.False(File.Exists(Path.Combine(_root, "ssh", "config")));
        Assert.NotEqual(keySetup.Id, secondKey.Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string GitCredential(string path)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = "git", RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        } };
        process.StartInfo.ArgumentList.Add("credential");
        process.StartInfo.ArgumentList.Add("fill");
        process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(_root, "gitconfig");
        process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GCM_INTERACTIVE"] = "Never";
        process.StartInfo.Environment["HOME"] = _root;
        Assert.True(process.Start());
        process.StandardInput.Write($"protocol=https\nhost=github.com\npath={path}\n\n");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private string GitUrl(string origin)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = "git", RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        } };
        process.StartInfo.ArgumentList.Add("ls-remote");
        process.StartInfo.ArgumentList.Add("--get-url");
        process.StartInfo.ArgumentList.Add(origin);
        process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(_root, "gitconfig");
        process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output.Trim();
    }

    private sealed class GitProbe : ILocalFirstRunGitCommand
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public List<IReadOnlyDictionary<string, string?>> Environments { get; } = [];

        public Task<(bool Success, string Output)> RunAsync(string directory,
            IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> environment,
            CancellationToken ct)
        {
            Environments.Add(environment);
            return RunAsync(directory, args, ct);
        }

        public Task<(bool Success, string Output)> RunAsync(
            string directory, IReadOnlyList<string> args, CancellationToken ct)
        {
            Calls.Add(args);
            return Task.FromResult((true,
                args[0] == "config" ? "https://github.com/example/demo.git" : ""));
        }
    }
}
