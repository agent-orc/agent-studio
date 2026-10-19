using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class EnvironmentHealthServiceTests : IDisposable
{
    private const string Project = "demo";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "env-health-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeHttpTarget _target;

    public EnvironmentHealthServiceTests() => _target = new FakeHttpTarget(_clock);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public async Task Success_RecordsSampleUptimeAndLatency_WithoutIncident()
    {
        var fixture = Build();
        _target.Respond(HttpStatusCode.OK, "ok", latency: TimeSpan.FromMilliseconds(120));

        var run = await fixture.Service.RunDueAsync();

        Assert.Equal(new HealthRunSummary(1, 0, 0, 0), run);
        Assert.Equal("https://stage.example/health", Assert.Single(_target.Requests));
        var environment = Assert.Single(fixture.Service.Read(Project)!.Environments);
        Assert.Equal(("up", 1.0, 1.0, 1.0), (environment.Status, environment.Uptime24h, environment.Uptime7d, environment.Uptime30d));
        Assert.Equal(new HealthLatency(120, 120, 120), environment.Latency24h);
        Assert.Null(environment.CurrentIncident);
        Assert.Equal(EnvironmentHealthService.ProbeOrigin, fixture.Service.Overview().ProbeOrigin);
        Assert.Empty(fixture.Cards());
    }

    [Fact]
    public async Task ChecksRunOnlyWhenTheirIntervalIsDue()
    {
        var fixture = Build();
        _target.Respond(HttpStatusCode.OK);

        await fixture.Service.RunDueAsync();
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, (await fixture.Service.RunDueAsync()).Probed);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, (await fixture.Service.RunDueAsync()).Probed);
    }

    [Fact]
    public async Task Timeout_IsMeasuredOnTheInjectedClock()
    {
        var fixture = Build();
        _target.Hang();

        var run = fixture.Service.RunDueAsync();
        await _target.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _clock.Advance(TimeSpan.FromSeconds(10));
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var check = Assert.Single(Assert.Single(fixture.Service.Read(Project)!.Environments).Checks);
        Assert.Equal(("degraded", false, "timeout"), (check.Status, check.LastSample!.Ok, check.LastSample.Error));
    }

    [Fact]
    public async Task WrongStatusAndMissingBodyMarker_AreFailures()
    {
        var fixture = Build(checks: """
            [ { "id": "api", "kind": "http", "url": "https://stage.example/api", "bodyContains": "ready" } ]
            """);
        _target.Respond(HttpStatusCode.ServiceUnavailable);
        await fixture.Service.RunDueAsync();
        var check = Assert.Single(Assert.Single(fixture.Service.Read(Project)!.Environments).Checks);
        Assert.Equal((503, "unexpected-status"), (check.LastSample!.Status, check.LastSample.Error));

        _clock.Advance(TimeSpan.FromMinutes(1));
        _target.Respond(HttpStatusCode.OK, "starting");
        await fixture.Service.RunDueAsync();
        check = Assert.Single(Assert.Single(fixture.Service.Read(Project)!.Environments).Checks);
        Assert.Equal((200, "body-mismatch", 2), (check.LastSample!.Status, check.LastSample.Error, check.ConsecutiveFailures));

        _clock.Advance(TimeSpan.FromMinutes(1));
        _target.Respond(HttpStatusCode.OK, "status: ready");
        await fixture.Service.RunDueAsync();
        check = Assert.Single(Assert.Single(fixture.Service.Read(Project)!.Environments).Checks);
        Assert.True(check.LastSample!.Ok);
        Assert.Equal(1d / 3, check.Uptime24h!.Value, 6);
    }

    [Fact]
    public async Task Flapping_BelowThreshold_OpensNoIncident_AndAnOpenIncidentStaysOne()
    {
        var fixture = Build();
        await Sequence(fixture, false, false, true, false, false, true, false, false, true);
        Assert.Null(Assert.Single(fixture.Service.Read(Project)!.Environments).CurrentIncident);
        Assert.Empty(fixture.Cards());
        Assert.Empty(fixture.Activity());

        await Sequence(fixture, false, false, false, false, false, true, false);

        var environment = Assert.Single(fixture.Service.Read(Project)!.Environments);
        Assert.Equal("down", environment.Status);
        Assert.Equal(6, environment.CurrentIncident!.FailedProbes);
        Assert.Equal(_clock.GetUtcNow() - TimeSpan.FromMinutes(6), environment.CurrentIncident.StartedAt);
        Assert.Single(fixture.Cards());
        Assert.Equal([OrchestratorLogTopics.EnvironmentIncidentOpened], fixture.Activity().Select(entry => entry.Topic));
    }

    [Fact]
    public async Task Recovery_ClosesTheIncident_CommentsOnTheCard_AndRaisesAnEvent()
    {
        var fixture = Build();
        await Sequence(fixture, false, false, false);
        var card = Assert.Single(fixture.Cards());
        Assert.Contains("unexpected-status", File.ReadAllText(Path.Combine(card.FolderPath, "prompt.md")));
        Assert.Equal(OrchestratorLogKinds.Alert, Assert.Single(fixture.Activity()).Kind);
        Assert.Equal(card.Id, fixture.Activity()[0].JobId);

        await Sequence(fixture, true, true);

        var environment = Assert.Single(fixture.Service.Read(Project)!.Environments);
        Assert.Equal("up", environment.Status);
        Assert.Null(environment.CurrentIncident);
        Assert.Equal((card.Id, _clock.GetUtcNow()), (environment.LastIncident!.CardId, environment.LastIncident.EndedAt!.Value));
        var comment = Assert.Single(fixture.Comments(card));
        Assert.Equal("recovered", comment.Details!["change"]);
        Assert.Equal([OrchestratorLogTopics.EnvironmentIncidentOpened, OrchestratorLogTopics.EnvironmentIncidentRecovered],
            fixture.Activity().Select(entry => entry.Topic));
        Assert.Equal(0.4, environment.Uptime24h!.Value, 6);
    }

    [Fact]
    public async Task CardCreation_IsIdempotent_AcrossTicksReopeningAndALostCardId()
    {
        var fixture = Build();
        await Sequence(fixture, false, false, false, false, false);
        var card = Assert.Single(fixture.Cards());

        // A reopened incident updates the still-open card instead of creating a second one.
        await Sequence(fixture, true, true, false, false, false);
        Assert.Equal(card.Id, Assert.Single(fixture.Cards()).Id);
        Assert.Equal(["recovered", "reopened"], fixture.Comments(card).Select(item => item.Details!["change"]));

        // A tick that created the card but stopped before saving its id adopts the card by slug.
        var store = new HealthCheckStore(fixture.Configuration);
        var state = store.Load(Project);
        var checkState = state.Checks[HealthCheckStore.Key("stage", "http")];
        checkState.OpenIncident = checkState.OpenIncident! with
        {
            Id = "stage/http/lost", StartedAt = _clock.GetUtcNow().AddMinutes(-90), CardId = null, OpenedEventRaised = false,
        };
        checkState.IncidentCardId = null;
        store.Save(Project, state);
        var slug = $"health-stage-http-{_clock.GetUtcNow().AddMinutes(-90):yyyyMMddHHmm}";
        var lost = fixture.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = slug, Title = "lost", PromptMarkdown = "lost", WatchPath = fixture.WatchPath, TargetState = TaskStates.Backlog,
        });
        await Sequence(fixture, false);
        Assert.Equal(2, fixture.Cards().Count);
        Assert.Equal(lost, store.Load(Project).Checks[HealthCheckStore.Key("stage", "http")].OpenIncident!.CardId);

        // A finished card is not reused: the next incident gets its own card.
        await Sequence(fixture, true, true);
        new TaskStateMachine(fixture.Scanner, NullLogger<TaskStateMachine>.Instance)
            .MoveJob(lost!, TaskStates.Completed, fixture.WatchPath, reason: "test: incident handled");
        Assert.Equal(TaskStates.Completed, fixture.Scanner.FindJob(lost!, fixture.WatchPath)!.State);
        await Sequence(fixture, false, false, false, false);
        Assert.Equal(3, fixture.Cards().Count);
        Assert.Equal(4, fixture.Activity().Count(entry => entry.Topic == OrchestratorLogTopics.EnvironmentIncidentOpened));
    }

    [Fact]
    public async Task CertificateExpiry_WarnsAt14AndAt3Days_OnOneCard()
    {
        var notAfter = _clock.GetUtcNow().AddDays(15).AddHours(12);
        var fixture = Build(checks: """[ { "id": "cert", "kind": "tls", "host": "stage.example", "port": 443 } ]""",
            probe: new CertificateProbe(_clock, notAfter));

        await fixture.Service.RunDueAsync();
        Assert.Empty(fixture.Activity());
        _clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, (await fixture.Service.RunDueAsync()).CertificateWarnings);
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, (await fixture.Service.RunDueAsync()).CertificateWarnings);
        _clock.Advance(TimeSpan.FromDays(10));
        Assert.Equal(1, (await fixture.Service.RunDueAsync()).CertificateWarnings);
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, (await fixture.Service.RunDueAsync()).CertificateWarnings);

        var card = Assert.Single(fixture.Cards());
        Assert.StartsWith("Renew TLS certificate", card.Title);
        Assert.Equal("certificate-warning", Assert.Single(fixture.Comments(card)).Details!["change"]);
        Assert.Equal(2, fixture.Activity().Count(entry => entry.Topic == OrchestratorLogTopics.EnvironmentCertificateExpiring));
        Assert.Equal(notAfter, Assert.Single(Assert.Single(fixture.Service.Read(Project)!.Environments).Checks).CertificateNotAfter);
    }

    [Fact]
    public void Retention_FoldsRawDaysPastThirtyDaysIntoHourlyAggregates_Idempotently()
    {
        var fixture = Build();
        var store = new HealthCheckStore(fixture.Configuration);
        var now = _clock.GetUtcNow();
        var old = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-32);
        store.Append(Project, "stage", "http", new(old.AddHours(3).AddMinutes(1), true, 100, 200, null));
        store.Append(Project, "stage", "http", new(old.AddHours(3).AddMinutes(2), false, null, 503, "unexpected-status"));
        store.Append(Project, "stage", "http", new(old.AddHours(4), true, 40, 200, null));
        store.Append(Project, "stage", "http", new(now.AddDays(-29), true, 70, 200, null));
        store.Append(Project, "stage", "http", new(now.AddMinutes(-1), true, 80, 200, null));

        Assert.Equal(1, store.ApplyRetention(Project, now));
        Assert.Equal(0, store.ApplyRetention(Project, now));

        Assert.Equal(new HealthHourlyAggregate[]
        {
            new(old.AddHours(3), 2, 1, 100, 100, 100), new(old.AddHours(4), 1, 0, 40, 40, 40),
        }, store.Hourly(Project, "stage", "http"));
        Assert.Equal([now.AddDays(-29), now.AddMinutes(-1)],
            store.Samples(Project, "stage", "http", now.AddDays(-40)).Select(sample => sample.At));

        Assert.Equal(1, store.ApplyRetention(Project, now.AddDays(29)));
        Assert.Equal(3, store.Hourly(Project, "stage", "http").Count);
        Assert.Equal(1, store.ApplyRetention(Project, now.AddDays(401)));
        Assert.Empty(store.Hourly(Project, "stage", "http"));
    }

    [Fact]
    public async Task NetworkProbe_Tcp_SucceedsAgainstAListener_AndReportsARefusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var probe = new NetworkHealthCheckProbe(new FakeHttpClients(_target), _clock);
        var check = new HealthCheckDefinition("tcp", "tcp", null, "127.0.0.1", port, 200, null, 60, 5, 3, 2);

        Assert.True((await probe.ProbeAsync(check, CancellationToken.None)).Ok);
        listener.Stop();
        var refused = await probe.ProbeAsync(check, CancellationToken.None);
        Assert.Equal((false, "connection-failed"), (refused.Ok, refused.Error));
    }

    private async Task Sequence(Fixture fixture, params bool[] results)
    {
        foreach (var ok in results)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            _target.Respond(ok ? HttpStatusCode.OK : HttpStatusCode.BadGateway);
            await fixture.Service.RunDueAsync();
        }
    }

    private Fixture Build(string? checks = null, IHealthCheckProbe? probe = null)
    {
        var repo = Path.Combine(_root, "repo");
        var watch = Path.Combine(_root, "workspace", "projects", Project);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(watch, state));
        var declarations = Path.Combine(repo, "docs", "deployments", "environments");
        Directory.CreateDirectory(declarations);
        var stage = $$"""
            { "id": "stage", "name": "Stage", "kind": "stage", "targetHostId": "stage-host",
              "address": "http://stage.example", "template": "container", "destination": "8081",
              "healthUrl": "https://stage.example/health"{{(checks is null ? "" : ", \"healthChecks\": " + checks)}} }
            """;
        File.WriteAllText(Path.Combine(declarations, "stage.json"), stage);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["WatchPaths:0:Name"] = Project, ["WatchPaths:0:Path"] = watch,
            ["WatchPaths:0:RootPath"] = repo, ["WatchPaths:0:RepositoryPath"] = repo,
            ["HealthChecks:MaxConcurrency"] = "2",
        }).Build();
        var scanner = new TaskScannerService(configuration, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);
        var environments = new ProjectEnvironmentService(configuration, scanner, git, settings, _clock);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var mutations = new TaskMutationService(scanner,
            new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance, timeline: timeline);
        var activity = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var alerts = new HealthAlerts(scanner, mutations, timeline, activity, _clock, NullLogger<HealthAlerts>.Instance);
        var service = new EnvironmentHealthService(scanner, environments, new HealthCheckStore(configuration),
            probe ?? new NetworkHealthCheckProbe(new FakeHttpClients(_target), _clock), alerts, configuration, _clock,
            NullLogger<EnvironmentHealthService>.Instance);
        return new(service, scanner, mutations, timeline, activity, configuration, watch);
    }

    private sealed record Fixture(EnvironmentHealthService Service, TaskScannerService Scanner,
        TaskMutationService Mutations, TimelineLog Timeline, OrchestratorLog Log, IConfiguration Configuration,
        string WatchPath)
    {
        public IReadOnlyList<TaskInfo> Cards() => Scanner.ScanAllJobs()
            .Where(card => card.Tags.Contains(HealthAlerts.Tag) || card.Id.StartsWith("health-", StringComparison.Ordinal)).ToArray();

        public IReadOnlyList<TimelineEvent> Comments(TaskInfo card) => Timeline.ReadAll(card.FolderPath)
            .Where(item => item.Kind == TimelineEventKinds.EnvironmentHealthUpdated).ToArray();

        public IReadOnlyList<OrchestratorLogEntry> Activity() => Log.Read(WatchPath);
    }

    private sealed class FakeHttpTarget(FakeTimeProvider clock) : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "";
        private TimeSpan _latency;
        private bool _hang;
        public List<string> Requests { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Respond(HttpStatusCode status, string body = "", TimeSpan latency = default)
            => (_status, _body, _latency, _hang) = (status, body, latency, false);

        public void Hang() => _hang = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.ToString());
            Entered.TrySetResult();
            if (_hang) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (_latency > TimeSpan.Zero) clock.Advance(_latency);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
        }
    }

    private sealed class FakeHttpClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class CertificateProbe(TimeProvider clock, DateTimeOffset notAfter) : IHealthCheckProbe
    {
        public Task<HealthSample> ProbeAsync(HealthCheckDefinition check, CancellationToken cancellationToken)
            => Task.FromResult(new HealthSample(clock.GetUtcNow(), true, 15, null, null, notAfter));
    }
}
