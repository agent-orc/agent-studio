using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using AgentStudio.Deploy;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3891 (F1): a green integration of a card becomes a release with an
/// automatic Staging run, without a runner.release credential and without
/// promotion-train evidence written from a shell.
/// </summary>
public sealed class IntegrationReleaseServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "integration-release-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
    private string _repo = "";
    private string _watch = "";
    private IConfiguration? _configuration;

    [Fact]
    public async Task Gated_integration_tip_becomes_a_release_with_an_automatic_staging_run()
    {
        var (service, environments, store, registry) = Build();
        var tip = Head();
        Card("DEMO-1", tip);

        var status = await service.CreateAsync("demo", IntegrationReleaseService.AutomaticActor, automatic: true, default);

        Assert.Equal(IntegrationReleaseStates.Released, status!.State);
        var release = Assert.Single(store.ReadReleases("demo"));
        Assert.Equal(tip, release.CommitSha);
        Assert.Equal(release.Id, status.ReleaseId);
        Assert.Contains("DEMO-1", release.CardKeys);
        var artifact = Assert.Single(release.ArtifactDigests);
        Assert.StartsWith("release-package@sha256:", artifact);
        var run = Assert.Single(store.ReadDeployments("demo"));
        Assert.Equal("stage", run.EnvironmentId);
        Assert.Equal(release.Id, run.ReleaseId);
        var job = Assert.Single(environments.Read("demo")!.Jobs);
        Assert.Equal(("stage", artifact, "queued"), (job.EnvironmentId, job.Artifact, job.Status));
        var provenance = registry.VerifiedForArtifact("demo", tip, artifact);
        Assert.NotNull(provenance);
        Assert.Equal(ProjectEnvironmentService.IntegrationReleaseBuilder, provenance!.BuilderIdentity);
        Assert.Equal("integration/DEMO-1", provenance.GateRunId);
        Assert.Equal(["README.md", "docs/", "docs/deployments/", "docs/deployments/environments/",
                "docs/deployments/environments/production.json", "docs/deployments/environments/stage.json"],
            Entries(registry.VerifiedBlob(provenance)!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Tip_without_a_green_gate_waits_and_names_the_commit()
    {
        var (service, _, store, _) = Build();
        Card("DEMO-1", Head());
        var tip = Commit("ungated.txt");

        var status = await service.CreateAsync("demo", IntegrationReleaseService.AutomaticActor, automatic: true, default);

        Assert.Equal(IntegrationReleaseStates.WaitingForGate, status!.State);
        Assert.Contains(tip[..8], status.Message);
        Assert.False(status.CanCreate);
        Assert.NotNull(status.NextStep);
        Assert.Empty(store.ReadReleases("demo"));
    }

    [Fact]
    public async Task Status_offers_the_release_once_the_tip_is_gated_and_reports_it_after_creation()
    {
        var (service, _, store, _) = Build();
        var tip = Head();
        Card("DEMO-1", tip);

        var ready = await service.StatusAsync("demo", default);
        Assert.Equal((IntegrationReleaseStates.Ready, tip, true), (ready!.State, ready.Commit, ready.CanCreate));
        Assert.Empty(store.ReadReleases("demo"));

        await service.CreateAsync("demo", "owner", automatic: false, default);
        var again = await service.CreateAsync("demo", IntegrationReleaseService.AutomaticActor, automatic: true, default);

        Assert.Equal(IntegrationReleaseStates.Released, again!.State);
        Assert.Single(store.ReadReleases("demo"));
        Assert.Equal(IntegrationReleaseStates.Released, (await service.StatusAsync("demo", default))!.State);
    }

    [Fact]
    public async Task Promotion_train_repository_is_left_to_the_train()
    {
        var (service, _, store, _) = Build();
        Directory.CreateDirectory(Path.Combine(_repo, "scripts", "release"));
        var tip = Commit(IntegrationReleaseService.PromotionTrainScript);
        Card("DEMO-1", tip);

        var status = await service.CreateAsync("demo", IntegrationReleaseService.AutomaticActor, automatic: true, default);

        Assert.Equal(IntegrationReleaseStates.PromotionTrain, status!.State);
        Assert.Contains(IntegrationReleaseService.PromotionTrainScript, status.NextStep);
        Assert.Empty(store.ReadReleases("demo"));
    }

    [Fact]
    public async Task Container_environment_is_blocked_with_the_reason_and_a_next_step()
    {
        var (service, _, store, _) = Build();
        WriteDeclaration("stage", "Stage", "stage", "container", "8081");
        var tip = Commit("container.txt");
        Card("DEMO-1", tip);

        var status = await service.CreateAsync("demo", IntegrationReleaseService.AutomaticActor, automatic: true, default);

        Assert.Equal(IntegrationReleaseStates.Blocked, status!.State);
        Assert.Contains("container image", status.Message);
        Assert.Contains("promotion train", status.NextStep);
        Assert.Empty(store.ReadReleases("demo"));
    }

    [Fact]
    public async Task Project_without_staging_and_production_says_what_to_declare()
    {
        var (service, _, _, _) = Build();
        Git("rm", "-q", "-r", "docs");
        Git("commit", "-q", "-m", "no environments");

        var status = await service.StatusAsync("demo", default);

        Assert.Equal(IntegrationReleaseStates.Unconfigured, status!.State);
        Assert.Contains("docs/deployments/environments", status.NextStep);
    }

    [Fact]
    public async Task Symbolic_link_in_the_tree_blocks_the_package_and_names_the_path()
    {
        var (service, _, store, _) = Build();
        File.CreateSymbolicLink(Path.Combine(_repo, "link"), "README.md");
        Git("add", "link");
        Git("commit", "-q", "-m", "link");
        Card("DEMO-1", Head());

        var status = await service.CreateAsync("demo", "owner", automatic: false, default);

        Assert.Equal(IntegrationReleaseStates.Blocked, status!.State);
        Assert.Contains("link", status.Message);
        Assert.True(status.CanCreate);
        Assert.Empty(store.ReadReleases("demo"));
        Assert.Equal(status.Message, (await service.StatusAsync("demo", default))!.Message);
    }

    [Fact]
    public async Task Green_post_merge_run_on_the_tip_counts_as_the_gate()
    {
        var (_, _, store, registry) = Build();
        var tip = Head();
        var projects = new ProjectRegistry(_configuration!, NullLogger<ProjectRegistry>.Instance);
        var id = projects.EnsureProjectForStorage(_watch, "demo", DefaultWorkspace.Id, _clock).Id;
        var postMerge = new PostMergeRunStore(Path.Combine(_root, "post-merge"));
        var started = _clock.GetUtcNow();
        PostMergeRunRecord Run(string status) => new("pm-1", "demo", "window", tip, null, null, null, 1,
            ["true"], status == "running" ? [] : [new PostMergeCommandVerdict("true", 0, false)], status,
            started, status == "running" ? null : started.AddMinutes(1));
        Assert.NotNull(postMerge.Record(id, Run("running")));
        Assert.NotNull(postMerge.Record(id, Run("green")));
        var service = WithPostMerge(projects, postMerge);

        var status = await service.CreateAsync("demo", IntegrationReleaseService.AutomaticActor, automatic: true, default);

        Assert.Equal(IntegrationReleaseStates.Released, status!.State);
        var release = Assert.Single(store.ReadReleases("demo"));
        Assert.Equal("post-merge/pm-1",
            registry.VerifiedForArtifact("demo", tip, release.ArtifactDigests[0])!.GateRunId);
    }

    private (ProjectEnvironmentService Environments, DeployService Deploy, DeployStore Store, GitService Git,
        TaskScannerService Scanner, ProjectSettingsService Settings) _parts;

    private IntegrationReleaseService WithPostMerge(ProjectRegistry projects, PostMergeRunStore postMerge)
        => new(_parts.Environments, _parts.Deploy, _parts.Store, _parts.Git, _parts.Scanner, _parts.Settings,
            _clock, NullLogger<IntegrationReleaseService>.Instance, projects, postMerge);

    private (IntegrationReleaseService Service, ProjectEnvironmentService Environments, DeployStore Store,
        ReleaseArtifactRegistry Registry) Build()
    {
        _repo = Path.Combine(_root, "repo");
        _watch = Path.Combine(_root, "workspace", "projects", "demo");
        Directory.CreateDirectory(_watch);
        Directory.CreateDirectory(_repo);
        Git("init", "-q", "-b", "develop");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test User");
        Git("config", "core.symlinks", "true");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "demo site\n");
        WriteDeclaration("stage", "Stage", "stage", "static", "/srv/www/stage");
        WriteDeclaration("production", "Production", "production", "static", "/srv/www/production");
        Git("add", ".");
        Git("commit", "-q", "-m", "initial");
        // No Deploy:ReleaseEvidenceRoot: an integration release needs no promotion-train evidence.
        var values = new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["WatchPaths:0:Name"] = "demo", ["WatchPaths:0:Path"] = _watch,
            ["WatchPaths:0:RootPath"] = _repo, ["WatchPaths:0:RepositoryPath"] = _repo,
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        _configuration = config;
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var registry = new ReleaseArtifactRegistry(config);
        var store = new DeployStore(config);
        var environments = new ProjectEnvironmentService(config, scanner, git, settings, _clock, registry, store);
        var deploy = new DeployService(environments, store, git, scanner, _clock, NullLogger<DeployService>.Instance, null);
        _parts = (environments, deploy, store, git, scanner, settings);
        return (new IntegrationReleaseService(environments, deploy, store, git, scanner, settings, _clock,
            NullLogger<IntegrationReleaseService>.Instance), environments, store, registry);
    }

    private void WriteDeclaration(string id, string name, string kind, string template, string destination)
    {
        var directory = Path.Combine(_repo, "docs", "deployments", "environments");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), JsonSerializer.Serialize(new
        {
            id, name, kind, targetHostId = id + "-host", address = $"https://{id}.example", template, destination,
            healthUrl = $"https://{id}.example/health", approval = "person", secretRefs = Array.Empty<string>(),
        }));
    }

    private void Card(string key, string sha)
    {
        var folder = Path.Combine(_watch, "6-completed", key.ToLowerInvariant());
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = key.ToLowerInvariant(), key, title = "Card " + key, state = "6-completed", agent = "claude",
            createdAt = _clock.GetUtcNow(),
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "fixture");
        IntegrationVerificationStore.Write(folder, new IntegrationVerificationRecord
        {
            State = IntegrationVerificationStates.Verified, Sha = sha, IntegrationBranch = "develop",
            RecordedAtUtc = _clock.GetUtcNow(),
        });
    }

    private static IEnumerable<string> Entries(string package)
    {
        using var stream = File.OpenRead(package);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var names = new List<string>();
        while (reader.GetNextEntry() is { } entry)
        {
            Assert.True(entry.EntryType is TarEntryType.Directory or TarEntryType.RegularFile, entry.Name);
            names.Add(entry.Name);
        }
        return names;
    }

    private string Head() => Git("rev-parse", "HEAD").Trim();

    private string Commit(string path)
    {
        File.WriteAllText(Path.Combine(_repo, path), path);
        Git("add", ".");
        Git("commit", "-q", "-m", path);
        return Head();
    }

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _repo,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        var when = _clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["GIT_AUTHOR_DATE"] = when;
        start.Environment["GIT_COMMITTER_DATE"] = when;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
