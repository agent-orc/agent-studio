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
    [Trait("Category", "MachineBound")]
    public void PackageTagPath_BumpsManifest_CommitsAndAtomicallyPushesVersionTag()
    {
        var repo = Path.Combine(_root, "repo");
        var remote = Path.Combine(_root, "remote.git");
        Git(_root, "init", "--bare", "-q", remote);
        Git(_root, "init", "-q", "-b", "main", repo);
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "test");
        Git(repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "package-lock.json"), "{\"name\":\"fixture\",\"version\":\"1.2.3\",\"lockfileVersion\":3,\"packages\":{\"\":{\"version\":\"1.2.3\"}}}");
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
        using var lockfile = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "package-lock.json")));
        Assert.Equal("1.3.0", lockfile.RootElement.GetProperty("version").GetString());
        Assert.Equal("1.3.0", lockfile.RootElement.GetProperty("packages").GetProperty("").GetProperty("version").GetString());
        Assert.Equal("v1.3.0", Git(repo, "describe", "--tags", "--exact-match").Trim());
        Assert.Equal(Git(repo, "rev-parse", "HEAD").Trim(), Git(remote, "rev-parse", "refs/heads/main").Trim());
        Assert.Equal(Git(repo, "rev-parse", "v1.3.0").Trim(), Git(remote, "rev-parse", "refs/tags/v1.3.0").Trim());
        Git(repo, "switch", "-q", "-c", "develop");
        Assert.Throws<InvalidOperationException>(() =>
            PublishActionService.PublishPackageTagPath(repo, PublishEcosystems.Npm, "1.3.1"));
        Assert.Empty(Git(repo, "tag", "-l", "v1.3.1"));
        Assert.Empty(Git(remote, "branch", "--list", "develop"));
        Assert.Equal("develop", Git(repo, "branch", "--show-current").Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "MachineBound")]
    public void Project_release_uses_declared_branch_and_verifies_before_script(bool failVerification)
    {
        if (OperatingSystem.IsWindows()) return;
        var repo = Path.Combine(_root, "project");
        var remote = Path.Combine(_root, "project.git");
        Git(_root, "init", "--bare", "-q", remote);
        Git(_root, "init", "-q", "-b", "main", repo);
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "test");
        Git(repo, "config", "commit.gpgsign", "false");
        Directory.CreateDirectory(Path.Combine(repo, ".agent-studio"));
        var yaml = "schemaVersion: 2\nproject:\n  id: example-project\nstack: [node]\ncommands:\n  prepare: echo prepared\n  build: "
            + (failVerification ? "false" : "test $(git branch --show-current) = main")
            + "\n  test: echo verified\nrelease:\n  command: git tag v$RELEASE_VERSION\n  branch: main\n";
        File.WriteAllText(Path.Combine(repo, ".agent-studio", "project.yml"), yaml);
        Git(repo, "add", "-A");
        Git(repo, "commit", "-qm", "seed");
        Git(repo, "remote", "add", "origin", remote);
        Git(repo, "push", "-q", "origin", "main");
        Git(repo, "switch", "-qc", "develop");
        File.WriteAllText(Path.Combine(repo, "unreleased.txt"), "integration only");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-qm", "integration only");
        var integration = Git(repo, "rev-parse", "HEAD");

        var preview = ProjectReleaseExecution.Execute(repo, "1.2.4", true, CancellationToken.None);
        Assert.Contains("Branch: main", preview);
        Assert.Empty(Git(repo, "tag", "-l", "v1.2.4"));
        Assert.DoesNotContain("studio-release-", Git(repo, "worktree", "list"));
        if (failVerification)
        {
            Assert.Throws<InvalidOperationException>(() => ProjectReleaseExecution.Execute(repo, "1.2.4", false, CancellationToken.None));
            Assert.Empty(Git(repo, "tag", "-l", "v1.2.4"));
        }
        else
        {
            ProjectReleaseExecution.Execute(repo, "1.2.4", false, CancellationToken.None);
            Assert.Equal(Git(repo, "rev-parse", "main"), Git(remote, "rev-parse", "refs/tags/v1.2.4"));
            Assert.NotEqual(integration, Git(remote, "rev-parse", "refs/heads/main"));
        }
        Assert.Equal(integration, Git(repo, "rev-parse", "HEAD"));
        Assert.Equal("develop", Git(repo, "branch", "--show-current").Trim());
        Assert.DoesNotContain("studio-release-", Git(repo, "worktree", "list"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "MachineBound")]
    public void Project_release_reports_cleanup_failure_without_masking_command_failure(bool failRelease)
    {
        if (OperatingSystem.IsWindows()) return;
        var repo = Path.Combine(_root, "cleanup-project");
        var remote = Path.Combine(_root, "cleanup-project.git");
        Git(_root, "init", "--bare", "-q", remote);
        Git(_root, "init", "-q", "-b", "main", repo);
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "test");
        Git(repo, "config", "commit.gpgsign", "false");
        Directory.CreateDirectory(Path.Combine(repo, ".agent-studio"));
        File.WriteAllText(Path.Combine(repo, ".agent-studio", "project.yml"),
            "schemaVersion: 2\nproject:\n  id: example-project\nstack: [node]\ncommands:\n  prepare: echo ready\n  build: echo built\n  test: echo verified\nrelease:\n  branch: main\n  command: git worktree lock . && "
            + (failRelease ? "echo original-release-failure >&2 && false" : "git tag v$RELEASE_VERSION") + "\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-qm", "seed");
        Git(repo, "remote", "add", "origin", remote);
        Git(repo, "switch", "-qc", "develop");
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                ProjectReleaseExecution.Execute(repo, "1.2.4", false, CancellationToken.None));
            if (failRelease)
            {
                Assert.Contains("original-release-failure", error.Message);
                var cleanup = Assert.IsType<InvalidOperationException>(error.Data["ReleaseWorktreeCleanupFailure"]);
                Assert.Contains("locked", cleanup.Message);
                Assert.Empty(Git(remote, "tag", "-l", "v1.2.4"));
            }
            else
            {
                Assert.Contains("locked", error.Message);
                Assert.Equal(Git(repo, "rev-parse", "main"), Git(remote, "rev-parse", "v1.2.4"));
            }
        }
        finally
        {
            var checkouts = Git(repo, "worktree", "list", "--porcelain").Split('\n')
                .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
                .Select(line => line["worktree ".Length..]).Where(path => path != repo).ToArray();
            foreach (var checkout in checkouts)
            {
                Git(repo, "worktree", "unlock", checkout);
                Git(repo, "worktree", "remove", "--force", checkout);
            }
        }
    }

    [Fact]
    public void Invalid_project_manifest_has_a_specific_definition_error()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".agent-studio"));
        File.WriteAllText(Path.Combine(_root, ".agent-studio", "project.yml"),
            "schemaVersion: 2\nstack: [node]\ncommands:\n  prepare: echo ready\nrelease:\n  command: echo release\n");

        var error = Assert.Throws<ProjectReleaseDefinitionException>(() => ProjectReleaseExecution.Read(_root));

        Assert.Contains("Invalid project.yml:", error.Message);
        Assert.Contains("Release command and branch must be declared together.", error.Message);
    }

    [Fact]
    public void Package_release_receipt_records_tag_and_branch_without_changing_release_gates()
    {
        var timeline = new TimelineLog(Microsoft.Extensions.Logging.Abstractions.NullLogger<TimelineLog>.Instance);
        Assert.True(timeline.Append(_root, TimelineEventKinds.PackageReleased, TimelineActors.System, "Released as v1.3.0",
            details: new() { ["tag"] = "v1.3.0", ["branch"] = "main" }));
        var receipt = Assert.Single(timeline.ReadAll(_root));
        Assert.Equal("package_released", receipt.Kind);
        Assert.Equal(TimelineActors.System, receipt.Actor);
        Assert.NotEqual(TimelineEventKinds.TaskReleased, receipt.Kind);
        Assert.Equal("v1.3.0", receipt.Details!["tag"]);
        Assert.Equal("main", receipt.Details["branch"]);
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
