using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Deploy;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3949: the workspace deployment dashboard data. <c>/api/deploy/matrix</c>
/// and <c>/api/deploy/cards</c> carry the running Studio instance as the
/// Production environment of its source project, every watched project has a
/// matrix row whether or not it declares environments, and both routes keep
/// their per-project access filter.
/// </summary>
public sealed class WorkspaceDeployMatrixTests : IDisposable
{
    private const string Studio = "studio";
    private const string Plain = "plain";
    private const string Shop = "shop";
    private const string ProjectsHeader = "X-Test-Projects";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "deploy-matrix-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 17, 3, 0, 0, TimeSpan.Zero));
    private readonly IConfiguration _config;
    private readonly InstanceDeployService _instance;
    private readonly DeployService _deploy;
    private readonly ProjectRegistry _registry;

    public WorkspaceDeployMatrixTests()
    {
        var values = new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["InstanceDeploy:SourceProject"] = Studio,
        };
        foreach (var (name, index) in new[] { Studio, Plain, Shop }.Select((name, index) => (name, index)))
        {
            Directory.CreateDirectory(Watch(name));
            Directory.CreateDirectory(Repo(name));
            Git(name, "init", "-q", "-b", "develop");
            Git(name, "config", "user.email", "test@example.invalid");
            Git(name, "config", "user.name", "Test");
            Git(name, "config", "commit.gpgsign", "false");
            if (name == Shop) DeclareStageAndProduction();
            Commit(name, "initial");
            values[$"WatchPaths:{index}:Name"] = name;
            values[$"WatchPaths:{index}:Path"] = Watch(name);
            values[$"WatchPaths:{index}:RootPath"] = Repo(name);
            values[$"WatchPaths:{index}:RepositoryPath"] = Repo(name);
        }
        _config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        var scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, _config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, _config);
        settings.SetIntegrationBranch(Studio, "develop");
        _instance = new InstanceDeployService(new InstanceDeployStore(_config), git, scanner,
            new ReleaseCardAttributionSource(scanner), _config, _clock, NullLogger<InstanceDeployService>.Instance,
            settings);
        _registry = new ProjectRegistry(_config, NullLogger<ProjectRegistry>.Instance);
        _deploy = new DeployService(new ProjectEnvironmentService(_config, scanner, git, settings, _clock),
            new DeployStore(_config), git, scanner, _clock, NullLogger<DeployService>.Instance, null,
            instance: _instance, registry: _registry);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- Instance production -------------------------------------------------

    [Fact]
    public void Matrix_shows_the_instance_as_production_of_its_source_project()
    {
        var commit = Head(Studio);
        var liveAt = GoLive("run-a", commit, "0.9.4");

        var row = Assert.Single(_deploy.Matrix().Projects, item => item.ProjectId == Studio);

        Assert.False(row.EnvironmentsDeclared);
        Assert.Null(row.Error);
        // Production only: the Studio has no staging environment.
        var production = Assert.Single(row.Statuses);
        Assert.Equal(InstanceDeployService.EnvironmentId, production.Environment.Id);
        Assert.Equal(EnvironmentKinds.Production, production.Environment.Kind);
        Assert.Equal("0.9.4", production.CurrentRelease!.Version);
        Assert.Equal(commit, production.CurrentRelease.CommitSha);
        Assert.Equal(EnvironmentHealth.Healthy, production.Health);
        Assert.False(production.ReleaseUncertain);
        Assert.Equal(liveAt, production.Instance!.LiveSinceUtc);
        var run = production.Instance.LastRun!;
        Assert.Equal(("run-a", InstanceDeployStates.Succeeded, "0.9.4", commit),
            (run.Id, run.State, run.Version, run.CommitSha));
        Assert.Equal(liveAt, run.AtUtc);
    }

    [Fact]
    public void Matrix_names_a_running_a_stalled_and_a_failed_last_run()
    {
        GoLive("run-a", Head(Studio), "0.9.4");
        _clock.Advance(TimeSpan.FromHours(2));
        var next = Commit(Studio, "next");

        _instance.Start(Start("run-b", next, "0.9.5"), "scripts");
        _instance.ReportStep("run-b", "build:server", new InstanceDeployStepRequest("succeeded"));
        var running = Production();
        Assert.Equal(InstanceDeployStates.Running, running.Instance!.LastRun!.State);
        Assert.Equal("0.9.4", running.CurrentRelease!.Version);
        Assert.Equal(EnvironmentHealth.Healthy, running.Health);

        // Silence before the server switch: the run stalls, the old release keeps running.
        _clock.Advance(TimeSpan.FromMinutes(InstanceDeployService.StalledAfterMinutes));
        var stalled = Production();
        Assert.Equal(InstanceDeployStates.Stalled, stalled.Instance!.LastRun!.State);
        Assert.Equal("build:server", stalled.Instance.LastRun.Step);
        Assert.Equal(DeploymentStates.Failed, stalled.CurrentDeployment!.State);
        Assert.Equal(EnvironmentHealth.Unhealthy, stalled.Health);
        Assert.Equal("0.9.4", stalled.CurrentRelease!.Version);
        Assert.False(stalled.ReleaseUncertain);

        // Silence during the server switch: which release runs is open.
        _instance.ReportStep("run-b", "switch:server", new InstanceDeployStepRequest("running"));
        _clock.Advance(TimeSpan.FromMinutes(InstanceDeployService.StalledAfterMinutes));
        var switching = Production();
        Assert.Equal(InstanceDeployStates.Stalled, switching.Instance!.LastRun!.State);
        Assert.Equal("switch:server", switching.Instance.LastRun.Step);
        Assert.True(switching.ReleaseUncertain);

        // A host failure after the switch: the run failed, its release is live.
        _instance.ReportStep("run-b", "switch:server", new InstanceDeployStepRequest("succeeded"));
        var liveAt = _clock.GetUtcNow().UtcDateTime;
        _instance.ReportStep("run-b", "host:runner-1", new InstanceDeployStepRequest("failed"));
        var failed = Production();
        Assert.Equal(InstanceDeployStates.Failed, failed.Instance!.LastRun!.State);
        Assert.Equal("host:runner-1", failed.Instance.LastRun.Step);
        Assert.Equal("0.9.5", failed.CurrentRelease!.Version);
        Assert.Equal(next, failed.CurrentRelease.CommitSha);
        Assert.Equal(liveAt, failed.Instance.LiveSinceUtc);
        Assert.Equal(EnvironmentHealth.Unhealthy, failed.Health);
        Assert.False(failed.ReleaseUncertain);
    }

    [Fact]
    public void Matrix_shows_the_source_project_before_any_deploy_was_reported()
    {
        var row = Assert.Single(_deploy.Matrix().Projects, item => item.ProjectId == Studio);

        var production = Assert.Single(row.Statuses);
        Assert.Null(production.CurrentRelease);
        Assert.Equal(EnvironmentHealth.Unknown, production.Health);
        Assert.Null(production.Instance!.LiveSinceUtc);
        Assert.Null(production.Instance.LastRun);
    }

    [Fact]
    public void Card_matrix_carries_the_instance_production_badge()
    {
        var commit = Head(Studio);
        var liveAt = GoLive("run-a", commit, "0.9.4");
        Card(Studio, "STUDIO-1", commit);
        Card(Plain, "PLAIN-1", Head(Plain));
        _instance.Refresh();

        var cards = _deploy.CardMatrix();

        var card = Assert.Single(cards, item => item.ProjectId == Studio);
        Assert.Equal("STUDIO-1", card.CardKey);
        var badge = Assert.Single(card.Environments);
        Assert.Equal((InstanceDeployService.EnvironmentId, EnvironmentKinds.Production, DeploymentStates.Succeeded, "0.9.4"),
            (badge.EnvironmentId, badge.Kind, badge.State, badge.ReleaseVersion));
        Assert.Equal(liveAt, badge.AtUtc);
        // A project without environments has nothing a card could be badged with.
        Assert.DoesNotContain(cards, item => item.ProjectId == Plain);
    }

    [Fact]
    public void Delivery_lanes_of_the_source_project_follow_what_the_instance_runs()
    {
        var first = Head(Studio);
        var second = Commit(Studio, "STUDIO-2: next");
        // STUDIO-1 was integrated before the deploy records began, STUDIO-2 after the live commit.
        Card(Studio, "STUDIO-1", first);
        Card(Studio, "STUDIO-2", second);
        var liveAt = GoLive("run-a", first, "0.9.4");
        string[] Keys(string? environmentId) => _deploy.DeliveryLanes(Studio)!.Lanes
            .Single(lane => lane.EnvironmentId == environmentId).Cards.Select(card => card.Key).ToArray();

        // Until the background refresh read git, the generic placement stands.
        Assert.Equal(["STUDIO-1", "STUDIO-2"], Keys(null));
        Assert.Empty(Keys(InstanceDeployService.EnvironmentId));

        _instance.Refresh();
        Assert.Equal(["STUDIO-2"], Keys(null));
        var live = Assert.Single(_deploy.DeliveryLanes(Studio)!.Lanes
            .Single(lane => lane.EnvironmentId == InstanceDeployService.EnvironmentId).Cards);
        Assert.Equal(("STUDIO-1", "Card STUDIO-1", "0.9.4", (DateTime?)liveAt),
            (live.Key, live.Title, live.ReleaseVersion, live.DeployedAtUtc));

        // The next deploy takes the waiting card live; the newest live release leads the lane.
        _clock.Advance(TimeSpan.FromHours(1));
        GoLive("run-b", second, "0.9.5");
        _instance.Refresh();
        Assert.Empty(Keys(null));
        Assert.Equal(["STUDIO-2", "STUDIO-1"], Keys(InstanceDeployService.EnvironmentId));
        Assert.Equal(["0.9.5", "0.9.4"], _deploy.DeliveryLanes(Studio)!.Lanes
            .Single(lane => lane.EnvironmentId == InstanceDeployService.EnvironmentId).Cards.Select(card => card.ReleaseVersion));
    }

    // ---- Projects without declarations --------------------------------------

    [Fact]
    public void Matrix_lists_every_watched_project_and_marks_the_ones_without_declarations()
    {
        var matrix = _deploy.Matrix();

        Assert.Equal([EnvironmentKinds.Staging, EnvironmentKinds.Production], matrix.EnvironmentKinds);
        Assert.Equal([Plain, Shop, Studio], matrix.Projects.Select(item => item.ProjectId));
        var plain = matrix.Projects[0];
        Assert.False(plain.EnvironmentsDeclared);
        Assert.Empty(plain.Statuses);
        Assert.Empty(plain.TenantStatuses);
        Assert.Null(plain.Error);
        var shop = matrix.Projects[1];
        Assert.True(shop.EnvironmentsDeclared);
        Assert.Equal([EnvironmentKinds.Staging, EnvironmentKinds.Production],
            shop.Statuses.Select(item => item.Environment.Kind));
        Assert.All(shop.Statuses, item => Assert.Null(item.Instance));
        // Listing an undeclared project must not start persisting deploy state for it.
        Assert.False(File.Exists(Path.Combine(_root, "workspace", "deployments", Plain + ".json")));
    }

    [Fact]
    public void Declared_production_wins_over_the_source_installation_cell()
    {
        GoLive("run", Head(Studio), "1.0");
        var path = Path.Combine("docs", "deployments", "environments", "production.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Repo(Studio), path))!);
        File.Copy(Path.Combine(Repo(Shop), path), Path.Combine(Repo(Studio), path));
        var row = Assert.Single(_deploy.Matrix().Projects, item => item.ProjectId == Studio);
        Assert.Null(Assert.Single(row.Statuses).Instance);
        Assert.True(row.EnvironmentsDeclared);
    }

    [Fact]
    public void Matrix_includes_each_registered_url_unless_its_name_is_declared()
    {
        var site = _registry.EnsureProjectForStorage(Watch(Shop), Shop, "WS-001");
        _registry.AddUrl(site.Id, "PRODUCTION", "https://production.example");
        _registry.AddUrl(site.Id, "Public site", "https://site.example");
        _registry.AddUrl(site.Id, "Documentation", "https://docs.example");
        var row = Assert.Single(_deploy.Matrix().Projects, item => item.ProjectId == Shop);
        Assert.Equal(["Public site", "Documentation"], row.Urls.Select(url => url.Name));
        Assert.All(row.Urls, url => Assert.Equal(site.Id, url.ProjectId));
    }

    [Fact]
    public void Matrix_reports_a_broken_declaration_as_declared_with_its_error()
    {
        File.WriteAllText(Path.Combine(Repo(Shop), "docs", "deployments", "environments", "production.json"), "[]");

        var shop = Assert.Single(_deploy.Matrix().Projects, item => item.ProjectId == Shop);

        Assert.True(shop.EnvironmentsDeclared);
        Assert.Empty(shop.Statuses);
        Assert.Contains("production.json", shop.Error);
    }

    // ---- Routes and access filter -------------------------------------------

    [Fact]
    public async Task Routes_serve_the_instance_and_undeclared_rows_and_keep_the_project_access_filter()
    {
        var commit = Head(Studio);
        GoLive("run-a", commit, "0.9.4");
        Card(Studio, "STUDIO-1", commit);
        _instance.Refresh();
        await using var app = await Host();
        using var client = app.GetTestClient();

        async Task<JsonDocument> Get(string path, string? projects)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (projects is not null) request.Headers.Add(ProjectsHeader, projects);
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        static string[] Projects(JsonElement rows)
            => rows.EnumerateArray().Select(row => row.GetProperty("projectId").GetString()!).ToArray();

        // No signed-in person (local profile): every watched project.
        using (var all = await Get("/api/deploy/matrix", null))
        {
            var rows = all.RootElement.GetProperty("projects");
            Assert.Equal([Plain, Shop, Studio], Projects(rows));
            Assert.False(rows[0].GetProperty("environmentsDeclared").GetBoolean());
            Assert.Equal(0, rows[0].GetProperty("statuses").GetArrayLength());
            Assert.True(rows[1].GetProperty("environmentsDeclared").GetBoolean());
            var production = rows[2].GetProperty("statuses")[0];
            Assert.Equal("production", production.GetProperty("environment").GetProperty("kind").GetString());
            Assert.Equal("0.9.4", production.GetProperty("currentRelease").GetProperty("version").GetString());
            Assert.Equal(commit, production.GetProperty("currentRelease").GetProperty("commitSha").GetString());
            Assert.Equal("healthy", production.GetProperty("health").GetString());
            var instance = production.GetProperty("instance");
            Assert.Equal(JsonValueKind.String, instance.GetProperty("liveSinceUtc").ValueKind);
            Assert.Equal("succeeded", instance.GetProperty("lastRun").GetProperty("state").GetString());
            // Declared environments carry no instance facts.
            Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("statuses")[0].GetProperty("instance").ValueKind);
        }
        using (var cards = await Get("/api/deploy/cards", null))
            Assert.Equal([Studio], Projects(cards.RootElement));

        // A person sees the rows and cards of the projects they may read, declared or not.
        using (var scoped = await Get("/api/deploy/matrix", Plain))
            Assert.Equal([Plain], Projects(scoped.RootElement.GetProperty("projects")));
        using (var scoped = await Get("/api/deploy/cards", Plain))
            Assert.Empty(scoped.RootElement.EnumerateArray());
        using (var scoped = await Get("/api/deploy/matrix", Studio + "," + Shop))
            Assert.Equal([Shop, Studio], Projects(scoped.RootElement.GetProperty("projects")));
        using (var scoped = await Get("/api/deploy/cards", Studio))
            Assert.Equal([Studio], Projects(scoped.RootElement));
        using (var none = await Get("/api/deploy/matrix", "elsewhere"))
            Assert.Equal(0, none.RootElement.GetProperty("projects").GetArrayLength());
        using (var none = await Get("/api/deploy/cards", "elsewhere"))
            Assert.Empty(none.RootElement.EnumerateArray());

        // The tenant filter still drops rows without a matching tenant status.
        using (var tenant = await Get("/api/deploy/matrix?tenant=acme", null))
            Assert.Equal(0, tenant.RootElement.GetProperty("projects").GetArrayLength());
    }

    // ---- Fixture -------------------------------------------------------------

    private EnvironmentStatus Production()
        => Assert.Single(Assert.Single(_deploy.Matrix().Projects, item => item.ProjectId == Studio).Statuses);

    /// <summary>A run whose server switch and end both succeeded; returns when it went live.</summary>
    private DateTime GoLive(string runId, string commit, string version)
    {
        _instance.Start(Start(runId, commit, version), "scripts");
        _instance.ReportStep(runId, "switch:server", new InstanceDeployStepRequest("succeeded"));
        var liveAt = _clock.GetUtcNow().UtcDateTime;
        _instance.Finish(runId, new InstanceDeployFinishRequest("succeeded"));
        return liveAt;
    }

    private static InstanceDeployStartRequest Start(string runId, string commit, string version)
        => new(runId, commit, version, new Dictionary<string, string> { ["server"] = version });

    /// <summary>
    /// The deploy routes behind a stand-in for the access middleware: the test
    /// header names the projects of a signed-in Operator; without it the
    /// request has no person, as in the local profile.
    /// </summary>
    private async Task<WebApplication> Host()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Test", ContentRootPath = Directory.CreateDirectory(Path.Combine(_root, "host")).FullName,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_deploy);
        builder.Services.AddSingleton(new ProjectRegistry(_config, NullLogger<ProjectRegistry>.Instance, clock: _clock));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue(ProjectsHeader, out var projects))
            {
                var now = _clock.GetUtcNow().UtcDateTime;
                var user = new StudioUser
                {
                    Id = "usr_scoped", Username = "scoped", DisplayName = "Scoped", Role = StudioRoles.Operator,
                    PasswordHash = "unused", Projects = [.. projects.ToString().Split(',')],
                    CreatedAt = now, PasswordChangedAt = now,
                };
                context.Items[AccessSecurityMiddleware.HumanPrincipalItem] = new HumanPrincipal(user, new StudioSession
                {
                    Id = "sess", UserId = user.Id, TokenHash = "h", CsrfHash = "h", CreatedAt = now, LastSeenAt = now,
                    ExpiresAt = now.AddHours(1), AbsoluteExpiresAt = now.AddHours(8),
                });
            }
            await next(context);
        });
        app.MapDeployEndpoints();
        await app.StartAsync();
        return app;
    }

    private string Watch(string project) => Path.Combine(_root, "workspace", "projects", project);

    private string Repo(string project) => Path.Combine(_root, "repos", project);

    private void DeclareStageAndProduction()
    {
        var declarations = Directory.CreateDirectory(Path.Combine(Repo(Shop), "docs", "deployments", "environments")).FullName;
        foreach (var (id, name, kind, port) in new[] { ("stage", "Stage", "stage", "8081"), ("production", "Production", "production", "8082") })
            File.WriteAllText(Path.Combine(declarations, id + ".json"), JsonSerializer.Serialize(new
            {
                id, name, kind, targetHostId = id + "-host", address = $"https://{id}.example", template = "container",
                destination = port, healthUrl = $"https://{id}.example/health", approval = "person",
                secretRefs = Array.Empty<string>(),
            }));
    }

    private void Card(string project, string key, string sha)
    {
        var folder = Path.Combine(Watch(project), "6-completed", key.ToLowerInvariant());
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

    private string Head(string project) => Git(project, "rev-parse", "HEAD").Trim();

    private string Commit(string project, string message)
    {
        File.WriteAllText(Path.Combine(Repo(project), Guid.NewGuid().ToString("N") + ".txt"), message);
        Git(project, "add", ".");
        Git(project, "commit", "-q", "-m", message);
        return Head(project);
    }

    private string Git(string project, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Repo(project),
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)}: {error}");
        return output;
    }
}
