using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Publishing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PublishActionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pub-actions-" + Guid.NewGuid().ToString("N"));

    public PublishActionServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            Directory.Delete(_root, true);
        }
        catch { }
    }

    [Theory]
    [InlineData(new[] { "bug" }, "1.2.4")]
    [InlineData(new[] { "chore", "bug" }, "1.2.4")]
    [InlineData(new[] { "bug", "feature" }, "1.3.0")]
    public void SemVerSuggestion_UsesMinorForAnyFeature_OtherwisePatch(string[] taskTypes, string expected)
    {
        Assert.Equal(expected, PublishActionService.SuggestNextVersion("1.2.3", taskTypes));
    }

    [Fact]
    public void AutomationLadder_ClampsPackagesAndAllowsWebsiteAuto()
    {
        Assert.Equal("suggest", PublishAutomationModes.Normalize("package:npm", "auto"));
        Assert.Equal("suggest", PublishActionService.ResolveLadderAction("package:npm", "auto", true));
        Assert.Equal("auto", PublishActionService.ResolveLadderAction("website", "auto", true));
        Assert.Equal("none", PublishActionService.ResolveLadderAction("website", "auto", false));
    }

    [Fact]
    public void Older_inactive_deployment_cannot_release_a_new_dispatch_reservation()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        var reservedAt = clock.GetUtcNow();
        Assert.False(PublishActionService.DeploymentCanReleaseReservation(reservedAt,
            reservedAt.AddSeconds(-1)));
        Assert.False(PublishActionService.DeploymentCanReleaseReservation(reservedAt, null));
        Assert.True(PublishActionService.DeploymentCanReleaseReservation(reservedAt,
            reservedAt.AddSeconds(1)));
    }

    [Fact]
    public void PackageTagPath_BumpsManifest_CommitsAndAtomicallyPushesVersionTag()
    {
        var repo = Path.Combine(_root, "repo");
        var remote = Path.Combine(_root, "remote.git");
        Git(_root, "init", "--bare", "-q", remote);
        Git(_root, "init", "-q", "-b", "main", repo);
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "test");
        Git(repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "package.json"), "{\"name\":\"fixture\",\"version\":\"1.2.3\"}\n");
        Directory.CreateDirectory(Path.Combine(repo, ".github", "workflows"));
        File.WriteAllText(Path.Combine(repo, ".github", "workflows", "release.yml"),
            "on:\n  push:\n    tags: ['v*']\njobs:\n  publish:\n    steps:\n      - run: npm publish\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "seed");
        Git(repo, "tag", "v1.2.3");
        Git(repo, "remote", "add", "origin", remote);
        Git(repo, "push", "-q", "origin", "main", "refs/tags/v1.2.3");

        File.WriteAllText(Path.Combine(repo, "index.js"), "export const feature = true;\n");
        Git(repo, "add", "index.js");
        Git(repo, "commit", "-q", "-m", "feat: pending work");

        PublishActionService.PublishPackageTagPath(repo, PublishEcosystems.Npm, "1.3.0");

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "package.json")));
        Assert.Equal("1.3.0", manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal("v1.3.0", Git(repo, "describe", "--tags", "--exact-match").Trim());
        Assert.Equal(Git(repo, "rev-parse", "HEAD").Trim(), Git(remote, "rev-parse", "refs/heads/main").Trim());
        Assert.Equal(Git(repo, "rev-parse", "v1.3.0").Trim(), Git(remote, "rev-parse", "refs/tags/v1.3.0").Trim());
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(20_000);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {error}");
        return output;
    }
}
