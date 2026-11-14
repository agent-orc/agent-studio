using System.Diagnostics;
using AgentStudio.Registry;
using Xunit;

namespace AgentStudio.Tests;

public sealed class NetworkedRepositoryCheckoutTests
{
    [Fact]
    public async Task UrlRegistrationCreatesCheckoutOutsideManagedProjectStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), "networked-checkout-" + Guid.NewGuid().ToString("N"));
        var storage = Path.Combine(root, "projects", "PROJ-1", "tasks");
        var seed = Path.Combine(root, "seed");
        var origin = Path.Combine(root, "origin.git");
        Directory.CreateDirectory(storage);
        Directory.CreateDirectory(seed);
        try
        {
            Git(root, "init", "--bare", origin);
            Git(seed, "init", "-b", "main");
            Git(seed, "config", "user.name", "Fixture");
            Git(seed, "config", "user.email", "fixture@example.invalid");
            File.WriteAllText(Path.Combine(seed, "README.md"), "fixture\n");
            Git(seed, "add", "README.md");
            Git(seed, "commit", "-m", "initial");
            Git(seed, "remote", "add", "origin", origin);
            Git(seed, "push", "origin", "main");
            Git(origin, "symbolic-ref", "HEAD", "refs/heads/main");

            var checkout = await NetworkedRepositoryCheckout.CloneAsync(origin, storage, "PROJ-1", CancellationToken.None);

            Assert.Equal(Path.Combine(root, "repository-checkouts", "PROJ-1"), checkout);
            Assert.Equal("fixture", File.ReadAllText(Path.Combine(checkout, "README.md")).Trim());
            Assert.Equal(origin, Git(checkout, "remote", "get-url", "origin").Trim());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FailedCloneLeavesNoCheckout()
    {
        var root = Path.Combine(Path.GetTempPath(), "networked-checkout-" + Guid.NewGuid().ToString("N"));
        var storage = Path.Combine(root, "projects", "PROJ-2", "tasks");
        Directory.CreateDirectory(storage);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => NetworkedRepositoryCheckout.CloneAsync(
                Path.Combine(root, "missing.git"), storage, "PROJ-2", CancellationToken.None));
            Assert.False(Directory.Exists(Path.Combine(root, "repository-checkouts", "PROJ-2")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Git(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
