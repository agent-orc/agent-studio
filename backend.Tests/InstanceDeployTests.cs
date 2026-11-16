using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Deploy;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Runtime;
using AgentStudio.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3879: the running Studio instance as the Production environment of its
/// source project. Record API (permission, idempotent steps, stalled run),
/// range-to-cards mapping, the not-live-yet list, card badges and the flow
/// counter.
/// </summary>
public sealed class InstanceDeployTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "instance-deploy-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.Zero));
    private string _repo = "";
    private string _watch = "";
    private IConfiguration? _config;
    private TaskScannerService? _scanner;
    private GitService? _git;
    private ProjectSettingsService? _settings;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- Range to cards ------------------------------------------------------

    [Fact]
    public void Range_maps_cards_to_the_first_parent_commit_that_lands_them_in_order()
    {
        // develop: base <- m1 (merges f1) <- c2 "DEMO-2: ..." <- m3 (merges f3a, f3b)
        var range = new[]
        {
            new RangeCommit("m3", ["c2", "f3b"], "Batch gate: merge results"),
            new RangeCommit("f3b", ["f3a"], "second commit"),
            new RangeCommit("f3a", ["base"], "first commit"),
            new RangeCommit("c2", ["m1"], "DEMO-2: direct fix"),
            new RangeCommit("m1", ["base", "f1"], "Batch gate: merge results"),
            new RangeCommit("f1", ["base"], "work"),
        };
        var cards = new[]
        {
            new IntegratedCardRecord("DEMO-3", "Third", ["f3a"], "develop"),
            new IntegratedCardRecord("DEMO-1", "First", ["f1"], "develop"),
            new IntegratedCardRecord("DEMO-2", "Second", ["elsewhere"], "develop"),
            new IntegratedCardRecord("DEMO-9", "Not in range", ["other"], "develop"),
            new IntegratedCardRecord("DEMO-4", "Already live", ["f3b"], "develop"),
        };

        var landed = InstanceRangeCards.Map(range, "m3", cards, new HashSet<string>(["DEMO-4"]));

        Assert.Equal(["DEMO-1", "DEMO-2", "DEMO-3"], landed.Select(item => item.Key));
        Assert.Equal(["m1", "c2", "m3"], landed.Select(item => item.LandedSha));
        Assert.Equal("First", landed[0].Title);
    }

    [Fact]
    public void Range_lands_a_card_once_at_its_first_landing()
    {
        var range = new[]
        {
            new RangeCommit("c3", ["c2"], "DEMO-1: follow-up"),
            new RangeCommit("c2", ["c1"], "unrelated"),
            new RangeCommit("c1", ["c0"], "DEMO-1: first part"),
        };
        var landed = InstanceRangeCards.Map(range, "c3",
            [new IntegratedCardRecord("DEMO-1", "One", ["c3"], "develop")]);
        Assert.Equal("c1", Assert.Single(landed).LandedSha);
        Assert.Empty(InstanceRangeCards.Map([], "c3", [new IntegratedCardRecord("DEMO-1", "One", ["c3"], "develop")]));
    }

    // ---- Record API ----------------------------------------------------------

    [Fact]
    public void Steps_are_idempotent_and_a_running_report_never_reopens_a_finished_step()
    {
        var (instance, _) = Build();
        var commit = Head();
        Assert.Equal(DeployOutcomeKind.Ok, instance.Start(Start("run-1", commit), "scripts").Kind);
        Assert.Equal(DeployOutcomeKind.Ok, instance.Start(Start("run-1", commit), "scripts").Kind);
        Assert.Equal(DeployOutcomeKind.Conflict, instance.Start(Start("run-1", commit, "0.9.6"), "scripts").Kind);

        var first = instance.ReportStep("run-1", "build:server", new InstanceDeployStepRequest("succeeded"));
        _clock.Advance(TimeSpan.FromMinutes(2));
        var again = instance.ReportStep("run-1", "build:server", new InstanceDeployStepRequest("succeeded"));
        var stale = instance.ReportStep("run-1", "build:server", new InstanceDeployStepRequest("running"));

        var step = Assert.Single(again.Value!.Steps);
        Assert.Equal(first.Value!.Steps[0], step);
        Assert.Equal(first.Value.LastReportAtUtc, again.Value.LastReportAtUtc);
        Assert.Equal("succeeded", Assert.Single(stale.Value!.Steps).State);
        Assert.Equal(InstanceDeployStepKinds.Build, step.Kind);

        Assert.Equal(DeployOutcomeKind.Invalid,
            instance.ReportStep("run-1", "Build Server", new InstanceDeployStepRequest("succeeded")).Kind);
        Assert.Equal(DeployOutcomeKind.Invalid,
            instance.ReportStep("run-1", "build:server", new InstanceDeployStepRequest("done")).Kind);
        Assert.Equal(DeployOutcomeKind.NotFound,
            instance.ReportStep("run-2", "build:server", new InstanceDeployStepRequest("succeeded")).Kind);
        Assert.Equal(DeployOutcomeKind.Invalid, instance.Start(Start("run-3", "abc"), "scripts").Kind);

        Assert.Equal(DeployOutcomeKind.Ok, instance.Finish("run-1", new InstanceDeployFinishRequest("succeeded")).Kind);
        Assert.Equal(DeployOutcomeKind.Ok, instance.Finish("run-1", new InstanceDeployFinishRequest("succeeded")).Kind);
        Assert.Equal(DeployOutcomeKind.Conflict, instance.Finish("run-1", new InstanceDeployFinishRequest("failed")).Kind);
    }

    [Fact]
    public void A_failed_step_fails_the_run_and_silence_for_30_minutes_stalls_it_naming_the_last_step()
    {
        var (instance, _) = Build();
        var commit = Head();
        instance.Start(Start("run-fail", commit), "scripts");
        instance.ReportStep("run-fail", "build:server", new InstanceDeployStepRequest("succeeded"));
        var failed = instance.ReportStep("run-fail", "host:runner-1",
            new InstanceDeployStepRequest("failed", Message: "install exited 1"));
        Assert.Equal(InstanceDeployStates.Failed, failed.Value!.State);
        Assert.Equal("host:runner-1", failed.Value.FailedStep);

        _clock.Advance(TimeSpan.FromMinutes(1));
        instance.Start(Start("run-stall", commit), "scripts");
        instance.ReportStep("run-stall", "build:server", new InstanceDeployStepRequest("succeeded"));
        _clock.Advance(TimeSpan.FromMinutes(1));
        instance.ReportStep("run-stall", "switch:server", new InstanceDeployStepRequest("running"));
        _clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(InstanceDeployStates.Running, instance.View("demo")!.LatestRun!.State);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var stalled = instance.View("demo")!.LatestRun!;
        Assert.Equal(InstanceDeployStates.Stalled, stalled.State);
        Assert.Equal("switch:server", stalled.LastStep);

        // The generic deploy contract reads a stalled run as failed at its last step.
        var deploy = Deploy(instance);
        var run = deploy.DeploymentById("demo", "run-stall")!;
        Assert.Equal(DeploymentStates.Failed, run.State);
        Assert.Equal("switch:server", run.FailedStep);
        Assert.Contains(deploy.Log("demo", "run-fail", null)!.Lines, line => line.Message.Contains("install exited 1"));
    }

    [Fact]
    public async Task Report_routes_accept_only_a_deploy_reporter_token()
    {
        var (instance, config) = Build();
        var security = new AccessSecurityStore(config, NullLogger<AccessSecurityStore>.Instance, _clock);
        var (_, secret) = security.CreateDeployReporterToken("deploy-scripts", null, "owner");
        var (revoked, revokedSecret) = security.CreateDeployReporterToken("old", null, "owner");
        security.RevokeDeployReporterToken(revoked.Id);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(instance);
        builder.Services.AddSingleton(security);
        await using var app = builder.Build();
        app.MapInstanceDeployEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        var body = new { runId = "run-http", targetCommit = Head(), version = "0.9.5-test",
            components = new Dictionary<string, string> { ["server"] = "0.9.5-test" } };

        async Task<HttpStatusCode> Post(string? bearer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, InstanceDeployEndpoints.RunsPath)
                { Content = JsonContent.Create(body) };
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await Post(null));
        Assert.Equal(HttpStatusCode.Unauthorized, await Post("ssn." + Guid.NewGuid().ToString("N") + "." + new string('x', 32)));
        Assert.Equal(HttpStatusCode.Unauthorized, await Post(revokedSecret));
        Assert.Equal(HttpStatusCode.OK, await Post(secret));

        using var step = new HttpRequestMessage(HttpMethod.Put, InstanceDeployEndpoints.RunsPath + "/run-http/steps/build:server")
            { Content = JsonContent.Create(new { state = "succeeded" }) };
        step.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(step)).StatusCode);
        using var view = await client.GetAsync("/api/projects/demo/deploy/instance");
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        using var other = await client.GetAsync("/api/projects/other/deploy/instance");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);

        Assert.True(InstanceDeployEndpoints.IsReportRoute("POST", "/api/instance/deploy/runs"));
        Assert.True(InstanceDeployEndpoints.IsReportRoute("PUT", "/api/instance/deploy/runs/r/steps/build:server"));
        Assert.True(InstanceDeployEndpoints.IsReportRoute("POST", "/api/instance/deploy/runs/r/finish"));
        Assert.False(InstanceDeployEndpoints.IsReportRoute("GET", "/api/instance/deploy/runs"));
        Assert.False(InstanceDeployEndpoints.IsReportRoute("POST", "/api/instance/deploy/reporter-tokens"));
        Assert.False(InstanceDeployEndpoints.IsReportRoute("DELETE", "/api/instance/deploy/runs/r/finish"));
    }

    // ---- Live release, not live yet, badges, flow ---------------------------

    [Fact]
    public void Server_switch_takes_the_release_live_with_the_cards_of_its_first_parent_range()
    {
        var (instance, _) = Build();
        var live = Head();
        instance.Start(Start("run-a", live, "0.9.4"), "scripts");
        instance.ReportStep("run-a", "switch:server", new InstanceDeployStepRequest("succeeded"));
        instance.Finish("run-a", new InstanceDeployFinishRequest("succeeded"));

        var (one, two) = (Merge("feature-one", "DEMO-1"), Commit("DEMO-2: direct fix on develop"));
        Card("DEMO-1", one.Side);
        Card("DEMO-2", "0000000000000000000000000000000000000002");
        var target = Head();
        var three = Merge("feature-three", "DEMO-3");
        Card("DEMO-3", three.Side);
        Card("DEMO-0", live);

        var facts = instance.Refresh()!;
        Assert.Equal(["DEMO-1", "DEMO-2", "DEMO-3"], facts.NotLiveYet.Select(item => item.Key));
        Assert.Equal([one.Merge, two, three.Merge], facts.NotLiveYet.Select(item => item.LandedCommit));
        Assert.True(facts.LiveCardReleases.ContainsKey("DEMO-0"));

        _clock.Advance(TimeSpan.FromMinutes(35));
        instance.Start(Start("run-b", target, "0.9.5"), "scripts");
        instance.ReportStep("run-b", "build:server", new InstanceDeployStepRequest("succeeded"));
        instance.ReportStep("run-b", "switch:server", new InstanceDeployStepRequest("succeeded"));
        var liveAt = _clock.GetUtcNow().UtcDateTime;
        instance.ReportStep("run-b", "host:runner-1", new InstanceDeployStepRequest("failed"));
        instance.Refresh();

        var view = instance.View("demo")!;
        Assert.Equal("0.9.5", view.Live!.Version);
        Assert.Equal(liveAt, view.Live.LiveSinceUtc);
        Assert.Equal(["DEMO-1", "DEMO-2"], view.Live.Cards.Select(item => item.Key));
        Assert.Equal(["DEMO-3"], view.NotLiveYet.Select(item => item.Key));
        Assert.Equal(InstanceDeployStates.Failed, view.LatestRun!.State);
        Assert.Equal("host:runner-1", view.LatestRun.FailedStep);

        var deploy = Deploy(instance);
        var production = Assert.Single(deploy.Status("demo")!, item => item.Environment.Id == InstanceDeployService.EnvironmentId);
        Assert.Equal(EnvironmentKinds.Production, production.Environment.Kind);
        Assert.Equal("0.9.5", production.CurrentRelease!.Version);
        Assert.Equal(["DEMO-3"], production.CardsSinceLastDeploy);
        Assert.Contains(deploy.Environments("demo")!, item => item.Id == InstanceDeployService.EnvironmentId);
        Assert.Equal(2, deploy.Releases("demo")!.Count);

        var badges = deploy.Cards("demo")!.ToDictionary(item => item.CardKey,
            item => item.Environments.Single(badge => badge.EnvironmentId == InstanceDeployService.EnvironmentId));
        Assert.Equal(DeploymentStates.Succeeded, badges["DEMO-1"].State);
        Assert.Equal(liveAt, badges["DEMO-1"].AtUtc);
        Assert.Equal("0.9.5", badges["DEMO-1"].ReleaseVersion);
        Assert.Equal(DeploymentStates.Succeeded, badges["DEMO-0"].State);
        Assert.Equal("0.9.4", badges["DEMO-0"].ReleaseVersion);
        Assert.Equal(CardDeploymentProjection.NotDeployed, badges["DEMO-3"].State);

        var columns = deploy.DeliveryColumns("demo")!.Columns;
        Assert.Equal(["DEMO-3"], columns.Single(item => item.Id == DeliveryColumnProjection.IntegratedColumnId)
            .Landed.Select(item => item.Key));
        Assert.Equal(["DEMO-3"], columns.Single(item => item.EnvironmentId == InstanceDeployService.EnvironmentId)
            .Waiting.Select(item => item.Key));
        Assert.Single(deploy.Matrix().Projects, row => row.ProjectId == "demo");

        Assert.Equal(2, instance.CardsWentLive(liveAt.Date, liveAt.AddMinutes(1)));
        Assert.Equal(0, instance.CardsWentLive(liveAt.AddMinutes(1), liveAt.AddHours(1)));
    }

    [Fact]
    public void First_start_records_the_running_version_as_live_without_backfilling_cards()
    {
        InitRepository();
        var head = Head();
        var identity = new BuildIdentity(1, "Agent Studio", "v0.9.5-" + head[..9], "0.9.5-" + head[..9], head[..9], false,
            _clock.GetUtcNow().AddMinutes(-30), "sha256:" + new string('a', 64),
            new ReleaseArtifactIdentity("CodingAgentRunner", "0.9.5", "v0.9.5", head, "sha256:" + new string('b', 64)),
            new ReleaseArtifactIdentity("coding-agent-chat", "0.9.5", "v0.9.5", head, "sha256:" + new string('c', 64)));
        var (instance, _) = Build(identity, configureSource: false);
        Card("DEMO-0", head);

        Assert.True(instance.RecordRunningVersionIfEmpty());
        Assert.False(instance.RecordRunningVersionIfEmpty());
        Assert.Equal("demo", instance.SourceProject);
        var view = instance.View("demo")!;
        Assert.Equal(head, view.Live!.CommitSha);
        Assert.True(view.Live.FromVersionRoute);
        Assert.Empty(view.Live.Cards);
        Assert.Equal(InstanceDeployStates.Succeeded, view.LatestRun!.State);

        instance.Refresh();
        var badge = Deploy(instance).Cards("demo")!.Single(item => item.CardKey == "DEMO-0").Environments
            .Single(item => item.EnvironmentId == InstanceDeployService.EnvironmentId);
        Assert.Equal(DeploymentStates.Succeeded, badge.State);

        // A later report for the same commit joins the recorded release instead of adding one.
        instance.Start(Start("run-same", head), "scripts");
        instance.Finish("run-same", new InstanceDeployFinishRequest("succeeded"));
        Assert.Single(Deploy(instance).Releases("demo")!);
    }

    [Fact]
    public void Read_paths_start_no_git()
    {
        var (instance, _) = Build();
        instance.Start(Start("run-a", Head()), "scripts");
        instance.Finish("run-a", new InstanceDeployFinishRequest("succeeded"));
        instance.Refresh();
        var deploy = Deploy(instance);
        using var scope = GitReadRequestGuard.BeginRead("GET", "/api/projects/{projectName}/deploy/instance");
        Assert.NotNull(instance.View("demo"));
        Assert.Equal(0, scope.Spawns);
        Assert.Null(instance.View("other"));
        Assert.NotNull(deploy);
    }

    // ---- Fixture -------------------------------------------------------------

    private static InstanceDeployStartRequest Start(string runId, string commit, string version = "0.9.5")
        => new(runId, commit, version, new Dictionary<string, string> { ["server"] = version, ["engine"] = version });

    private (InstanceDeployService Instance, IConfiguration Config) Build(BuildIdentity? identity = null,
        bool configureSource = true)
    {
        InitRepository();
        var values = new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["WatchPaths:0:Name"] = "demo", ["WatchPaths:0:Path"] = _watch,
            ["WatchPaths:0:RootPath"] = _repo, ["WatchPaths:0:RepositoryPath"] = _repo,
        };
        if (configureSource) values["InstanceDeploy:SourceProject"] = "demo";
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        _scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        _git = new GitService(NullLogger<GitService>.Instance, _scanner, config);
        _settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        _settings.SetIntegrationBranch("demo", "develop");
        _config = config;
        var instance = new InstanceDeployService(new InstanceDeployStore(config), _git, _scanner,
            new ReleaseCardAttributionSource(_scanner), config, _clock, NullLogger<InstanceDeployService>.Instance,
            _settings, identity);
        return (instance, config);
    }

    private void InitRepository()
    {
        if (_repo.Length > 0) return;
        _repo = Path.Combine(_root, "repo");
        _watch = Path.Combine(_root, "workspace", "projects", "demo");
        Directory.CreateDirectory(_watch);
        Directory.CreateDirectory(_repo);
        Git("init", "-q", "-b", "develop");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "user.name", "Test");
        Git("config", "commit.gpgsign", "false");
        Commit("initial");
    }

    private DeployService Deploy(InstanceDeployService instance)
        => new(new ProjectEnvironmentService(_config!, _scanner!, _git!, _settings!, _clock), new DeployStore(_config!),
            _git!, _scanner!, _clock, NullLogger<DeployService>.Instance, null, instance: instance);

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

    /// <summary>A side branch with one commit, merged into develop with a batch gate style merge commit.</summary>
    private (string Side, string Merge) Merge(string branch, string name)
    {
        Git("checkout", "-q", "-b", branch);
        var side = Commit(name + " work");
        Git("checkout", "-q", "develop");
        Git("merge", "-q", "--no-ff", "-m", "Batch gate: merge " + branch, branch);
        return (side, Head());
    }

    private string Head() => Git("rev-parse", "HEAD").Trim();

    private string Commit(string message)
    {
        File.WriteAllText(Path.Combine(_repo, Guid.NewGuid().ToString("N") + ".txt"), message);
        Git("add", ".");
        Git("commit", "-q", "-m", message);
        return Head();
    }

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _repo,
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
