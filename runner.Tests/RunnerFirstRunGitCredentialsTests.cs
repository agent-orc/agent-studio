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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
