using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.Deploy;
using AgentStudio.Telemetry;
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
    public async Task Production_without_telemetry_is_not_observed_even_when_health_probe_succeeds()
    {
        var fixture = Build(extraDeclaration: Production(), withTelemetry: true);
        _target.Respond(HttpStatusCode.OK);
        await fixture.Service.RunDueAsync();

        var production = fixture.Service.Read(Project)!.Environments.Single(item => item.EnvironmentId == "production");
        Assert.Equal("not-observed", production.Status);
        Assert.Equal(4, production.Checks.Count(item => item.Kind == "telemetry" && item.Status == "not-observed"));
        Assert.Empty(fixture.Cards());
    }

    [Theory]
    [InlineData("5xx-rate")]
    [InlineData("latency-p95")]
    [InlineData("storage-usage")]
    [InlineData("restarts")]
    public async Task Telemetry_rule_opens_one_environment_incident_and_recovers_after_two_good_evaluations(string rule)
    {
        var fixture = Build(extraDeclaration: Production(), withTelemetry: true);
        _target.Respond(HttpStatusCode.OK);
        WriteSignal(fixture, rule, firing: true);
        await fixture.Service.RunDueAsync();

        var production = fixture.Service.Read(Project)!.Environments.Single(item => item.EnvironmentId == "production");
        Assert.Equal("down", production.Status);
        Assert.Equal("telemetry-" + rule, production.CurrentIncident!.CheckId);
        Assert.InRange(_clock.GetUtcNow() - production.CurrentIncident.StartedAt,
            TimeSpan.Zero, TimeSpan.FromMinutes(5));
        Assert.Contains(rule, production.CurrentIncident.FirstError);
        Assert.Contains("over", production.CurrentIncident.FirstError);
        var card = Assert.Single(fixture.Cards());
        Assert.Contains(rule, File.ReadAllText(Path.Combine(card.FolderPath, "prompt.md")));
        Assert.Single(fixture.Activity().Where(item => item.Topic == OrchestratorLogTopics.EnvironmentIncidentOpened));

        _clock.Advance(TimeSpan.FromMinutes(6));
        WriteSignal(fixture, rule, firing: false);
        await fixture.Service.RunDueAsync();
        Assert.NotNull(fixture.Service.Read(Project)!.Environments.Single(item => item.EnvironmentId == "production").CurrentIncident);
        _clock.Advance(TimeSpan.FromSeconds(15));
        WriteSignal(fixture, rule, firing: false);
        await fixture.Service.RunDueAsync();
        Assert.Null(fixture.Service.Read(Project)!.Environments.Single(item => item.EnvironmentId == "production").CurrentIncident);
        Assert.Single(fixture.Cards());
        Assert.Equal(1, fixture.Activity().Count(item => item.Topic == OrchestratorLogTopics.EnvironmentIncidentRecovered));
    }

    [Theory]
    [InlineData("5xx-rate")]
    [InlineData("latency-p95")]
    [InlineData("storage-usage")]
    [InlineData("restarts")]
    public async Task Telemetry_rule_flapping_reuses_the_incident_and_missing_samples_do_not_recover_it(string rule)
    {
        var fixture = Build(extraDeclaration: Production(), withTelemetry: true);
        _target.Respond(HttpStatusCode.OK);
        WriteSignal(fixture, rule, firing: true);
        await fixture.Service.RunDueAsync();
        _clock.Advance(TimeSpan.FromMinutes(6));
        WriteSignal(fixture, rule, firing: false);
        await fixture.Service.RunDueAsync();
        _clock.Advance(TimeSpan.FromMinutes(6));
        WriteSignal(fixture, rule, firing: true);
        await fixture.Service.RunDueAsync();
        Assert.Single(fixture.Cards());
        Assert.Single(fixture.Activity().Where(item => item.Topic == OrchestratorLogTopics.EnvironmentIncidentOpened));
        Assert.NotNull(fixture.Service.Read(Project)!.Environments.Single(item => item.EnvironmentId == "production").CurrentIncident);
        _clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.Service.RunDueAsync();
        Assert.NotNull(fixture.Service.Read(Project)!.Environments.Single(item => item.EnvironmentId == "production").CurrentIncident);
    }

    [Fact]
    public async Task Filesystem_state_series_supply_the_default_reservation_for_the_disk_drill()
    {
        var fixture = Build(extraDeclaration: Production(reservation: false), withTelemetry: true);
        _target.Respond(HttpStatusCode.OK);
        MetricSample Gauge(string state, double bytes) => new("system.filesystem.usage", "By", MetricKind.Gauge,
            MetricTemporality.Unspecified, TelemetryAttributes.From([
                KeyValuePair.Create("mountpoint", "/data"), KeyValuePair.Create("state", state)]),
            _clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000,
            _clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000, bytes, 1, null, null);
        fixture.Telemetry!.Write(new(Project, "production"),
            [Gauge("used", 2_100_000_000), Gauge("free", 7_900_000_000), Gauge("reserved", 0)],
            [], _clock.GetUtcNow());
        await fixture.Service.RunDueAsync();
        Assert.Null(fixture.Service.Read(Project)!.Environments
            .Single(item => item.EnvironmentId == "production").CurrentIncident);
        _clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Telemetry!.Write(new(Project, "production"),
            [Gauge("used", 9_800_000_000), Gauge("free", 200_000_000), Gauge("reserved", 0)],
            [], _clock.GetUtcNow());
        await fixture.Service.RunDueAsync();
        Assert.Equal("telemetry-storage-usage", fixture.Service.Read(Project)!.Environments
            .Single(item => item.EnvironmentId == "production").CurrentIncident?.CheckId);
    }

    [Fact]
    public async Task Four_second_504_histogram_opens_one_card_for_both_latency_and_error_rate()
    {
        var fixture = Build(extraDeclaration: Production(), withTelemetry: true);
        _target.Respond(HttpStatusCode.OK);
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000;
        var duration = new MetricSample("http.server.request.duration", "s", MetricKind.Histogram,
            MetricTemporality.Delta, TelemetryAttributes.From([
                KeyValuePair.Create("http.response.status_code", "504")]),
            now, now - 1_000_000_000, 400, 100, 4, 4);
        fixture.Telemetry!.Write(new(Project, "production"), [duration], [], _clock.GetUtcNow());
        await fixture.Service.RunDueAsync();

        var environment = fixture.Service.Read(Project)!.Environments.Single(item => item.EnvironmentId == "production");
        Assert.Equal("down", environment.Status);
        Assert.Equal("down", environment.Checks.Single(item => item.Id == "telemetry-5xx-rate").Status);
        Assert.Equal("down", environment.Checks.Single(item => item.Id == "telemetry-latency-p95").Status);
        Assert.Single(fixture.Cards());
        Assert.Single(fixture.Activity().Where(item => item.Topic == OrchestratorLogTopics.EnvironmentIncidentOpened));
    }

    private static string Production(bool reservation = true) => $$"""
        { "id": "production", "name": "Production", "kind": "production", "targetHostId": "prod-host",
          "address": "http://production.example", "template": "container", "destination": "8082",
          "healthUrl": "https://production.example/health",
          "telemetryAlerts": { {{(reservation ? "\"storageReservationBytes\": 10000000000" : "")}} } }
        """;

    private void WriteSignal(Fixture fixture, string rule, bool firing)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000;
        MetricSample Sample(string name, MetricKind kind, double value, double count = 1,
            string? status = null)
            => new(name, kind == MetricKind.Histogram ? "s" : "1", kind, MetricTemporality.Delta,
                status is null ? TelemetryAttributes.Empty : TelemetryAttributes.From([
                    KeyValuePair.Create("http.response.status_code", status)]),
                now, now - 1_000_000_000, value, count, null, null);
        MetricSample[] points = rule switch
        {
            "5xx-rate" => [Sample("http.server.request.duration", MetricKind.Histogram, firing ? 400 : 10, 100,
                firing ? "500" : "200")],
            "latency-p95" => [Sample("http.server.request.duration.p95", MetricKind.Gauge, firing ? 4 : 0.1)],
            "storage-usage" => [Sample("system.filesystem.usage", MetricKind.Gauge, firing ? 9_800_000_000 : 2_000_000_000)],
            _ => [Sample("process.restart.count", MetricKind.Sum, firing ? 1 : 0)],
        };
        fixture.Telemetry!.Write(new(Project, "production"), points, [], _clock.GetUtcNow());
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
    public async Task IncidentCard_IsCreatedBySystem_AndStaysOutOfOwnerActivity()
    {
        var fixture = Build();
        await Sequence(fixture, false, false, false);
        var card = Assert.Single(fixture.Cards());

        var created = Assert.Single(fixture.Timeline.ReadAll(card.FolderPath),
            item => item.Kind == TimelineEventKinds.PromptCreated);
        Assert.Equal(TimelineActors.System, created.Actor);
        Assert.Empty(OwnerActivityProjection.FromTimeline(card, fixture.Timeline.ReadAll(card.FolderPath)));

        var environment = Assert.Single(fixture.Service.Read(Project)!.Environments);
        Assert.NotNull(card.Key);
        Assert.Equal(card.Key, environment.IncidentCardKey);
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
        var incidentId = fixture.Store.Load(Project).Environments["stage"].OpenIncident!.Id;
        new DeployStore(fixture.Configuration).SaveDeployments(Project, [new DeploymentRecord
        {
            Id = "rollback-1", ReleaseId = "release-1", EnvironmentId = "stage",
            State = DeploymentStates.Succeeded, Trigger = DeploymentTriggers.Rollback,
            DeployStep = DeployStepNames.StartContainer, RequestedBy = "operator",
            CreatedAtUtc = _clock.GetUtcNow().UtcDateTime,
            FinishedAtUtc = _clock.GetUtcNow().UtcDateTime,
            IncidentId = incidentId, IncidentCardId = card.Id,
        }]);

        await Sequence(fixture, true, true);

        var environment = Assert.Single(fixture.Service.Read(Project)!.Environments);
        Assert.Equal("up", environment.Status);
        Assert.Null(environment.CurrentIncident);
        Assert.Equal((card.Id, _clock.GetUtcNow()), (environment.LastIncident!.CardId, environment.LastIncident.EndedAt!.Value));
        Assert.Equal(TaskStates.Completed, Assert.Single(fixture.Cards()).State);
        var comment = Assert.Single(fixture.Comments(card));
        Assert.Equal("recovered", comment.Details!["change"]);
        Assert.Contains("rollback by operator release release-1: succeeded", comment.Summary);
        Assert.Contains("Running release: release-1", comment.Summary);
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

        // A quick recurrence reopens the resolved card instead of creating a second one.
        await Sequence(fixture, true, true, false, false, false);
        Assert.Equal(card.Id, Assert.Single(fixture.Cards()).Id);
        Assert.Equal(TaskStates.Backlog, Assert.Single(fixture.Cards()).State);
        Assert.Equal(["recovered", "reopened"], fixture.Comments(card).Select(item => item.Details!["change"]));

        // A tick that created the card but stopped before saving its id adopts the card by slug.
        var store = fixture.Store;
        var state = store.Load(Project);
        var environmentState = state.Environments["stage"];
        environmentState.OpenIncident = environmentState.OpenIncident! with
        {
            Id = "stage/lost", StartedAt = _clock.GetUtcNow().AddMinutes(-90), CardId = null, OpenedEventRaised = false,
        };
        environmentState.IncidentCardId = null;
        store.Save(Project, state);
        var slug = $"health-stage-{_clock.GetUtcNow().AddMinutes(-90):yyyyMMddHHmmss}";
        var lost = fixture.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = slug, Title = "lost", PromptMarkdown = "lost", WatchPath = fixture.WatchPath, TargetState = TaskStates.Backlog,
        });
        await Sequence(fixture, false);
        Assert.Equal(2, fixture.Cards().Count);
        Assert.Equal(lost, store.Load(Project).Environments["stage"].OpenIncident!.CardId);

        // After the cool-down, the next incident gets its own card.
        await Sequence(fixture, true, true);
        new TaskStateMachine(fixture.Scanner, NullLogger<TaskStateMachine>.Instance)
            .MoveJob(lost!, TaskStates.Completed, fixture.WatchPath, reason: "test: incident handled");
        Assert.Equal(TaskStates.Completed, fixture.Scanner.FindJob(lost!, fixture.WatchPath)!.State);
        _clock.Advance(TimeSpan.FromMinutes(31));
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
    public void Retention_FoldsCompletedDaysIntoHourlyAggregates_AndDeletesRawDaysPastThirtyDays_Idempotently()
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

        // Both completed days fold; today stays raw until it is complete.
        Assert.Equal(2, store.ApplyRetention(Project, now));
        Assert.Equal(0, store.ApplyRetention(Project, now));

        Assert.Equal(new HealthHourlyAggregate[]
        {
            new(old.AddHours(3), 2, 1, 100, 100, 100), new(old.AddHours(4), 1, 0, 40, 40, 40),
            new(now.AddDays(-29), 1, 0, 70, 70, 70),
        }, store.Hourly(Project, "stage", "http"));
        Assert.Equal([now.AddDays(-29), now.AddMinutes(-1)],
            store.Samples(Project, "stage", "http", now.AddDays(-40)).Select(sample => sample.At));

        Assert.Equal(1, store.ApplyRetention(Project, now.AddDays(1)));
        Assert.Equal(4, store.Hourly(Project, "stage", "http").Count);
        Assert.Equal(0, store.ApplyRetention(Project, now.AddDays(401)));
        Assert.Empty(store.Hourly(Project, "stage", "http"));
        Assert.Empty(store.Samples(Project, "stage", "http", now.AddDays(-40)));
    }

    [Fact]
    public async Task ThirtyDayUptime_ComesFromHourlyAggregates_NotFromRawSamples()
    {
        var fixture = Build();
        var now = _clock.GetUtcNow();
        fixture.Store.Append(Project, "stage", "http", new(now.AddDays(-20), false, null, 502, "unexpected-status"));
        fixture.Store.Append(Project, "stage", "http", new(now.AddDays(-20).AddMinutes(1), true, 50, 200, null));
        Assert.Equal(1, fixture.Store.ApplyRetention(Project, now));
        // Without the raw day only the aggregates know about the failure 20 days ago.
        var raw = Path.Combine(_root, "workspace", "health-checks", Project, "stage", "http", "raw",
            now.AddDays(-20).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl");
        File.Delete(raw);
        _target.Respond(HttpStatusCode.OK);

        await fixture.Service.RunDueAsync();

        var environment = Assert.Single(fixture.Service.Read(Project)!.Environments);
        Assert.Equal((1.0, 1.0), (environment.Uptime24h, environment.Uptime7d));
        Assert.Equal(2d / 3, environment.Uptime30d!.Value, 6);
    }

    [Fact]
    public void DayFileCache_IsBounded()
    {
        var fixture = Build();
        var store = new HealthCheckStore(fixture.Configuration);
        var now = _clock.GetUtcNow();
        var days = HealthCheckStore.CacheCapacity + 10;
        for (var day = 0; day < days; day++)
            store.Append(Project, "stage", "http", new(now.AddDays(-day), true, 10, 200, null));

        Assert.Equal(days, store.Samples(Project, "stage", "http", now.AddDays(-days)).Count);
        Assert.Equal(HealthCheckStore.CacheCapacity, store.CachedFiles);
        Assert.Equal(days, store.Samples(Project, "stage", "http", now.AddDays(-days)).Count);
        Assert.Equal(HealthCheckStore.CacheCapacity, store.CachedFiles);
    }

    [Fact]
    public async Task NetworkProbe_Tcp_SucceedsAgainstAListener_AndReportsARefusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var closed = new ClosedLoopbackPort();
        var connects = new List<IPEndPoint>();
        // app.example resolves to a public address; the test socket reaches the local listener instead.
        var connector = new HealthCheckConnector((_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.7") }),
            async (endpoint, token) =>
            {
                connects.Add(endpoint);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, endpoint.Port), token);
                    return socket;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            });
        var probe = new NetworkHealthCheckProbe(new FakeHttpClients(_target), connector, _clock);
        var check = new HealthCheckDefinition("tcp", "tcp", null, "app.example", port, 200, null, 60, 5, 3, 2);

        Assert.True((await probe.ProbeAsync(check, CancellationToken.None)).Ok);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.7"), port), Assert.Single(connects));
        var refused = await probe.ProbeAsync(check with { Port = closed.Port }, CancellationToken.None);
        Assert.Equal((false, "connection-failed"), (refused.Ok, refused.Error));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.7"), closed.Port), connects[1]);
    }

    [Fact]
    public async Task NetworkProbe_RefusesLoopbackLinkLocalMetadataAndPrivateTargets_BeforeConnecting()
    {
        var answers = new Dictionary<string, IPAddress[]>
        {
            ["localhost"] = [IPAddress.Loopback, IPAddress.IPv6Loopback],
            ["metadata.example"] = [IPAddress.Parse("169.254.169.254")],
            ["internal.example"] = [IPAddress.Parse("10.0.0.22")],
        };
        var connects = new List<IPEndPoint>();
        var connector = new HealthCheckConnector((host, _) => Task.FromResult(answers[host]), (endpoint, _) =>
        {
            lock (connects) connects.Add(endpoint);
            return Task.FromException<Socket>(new SocketException((int)SocketError.ConnectionRefused));
        });
        using var handler = connector.CreateHandler();
        var probe = new NetworkHealthCheckProbe(new FakeHttpClients(handler), connector, _clock);
        HealthCheckDefinition Http(string url) => new("http", "http", url, null, null, 200, "ami-id", 60, 5, 3, 2);
        HealthCheckDefinition Endpoint(string kind, string host, int port) => new(kind, kind, null, host, port, 200, null, 60, 5, 3, 2);

        foreach (var check in new[]
        {
            Http("http://169.254.169.254/latest/meta-data/"), Http("http://metadata.example/computeMetadata/v1/"),
            Http("http://localhost:5030/api/health"), Http("http://[::1]:5030/"), Http("http://0.0.0.0/"),
            Http("http://internal.example/"), Endpoint("tcp", "internal.example", 22), Endpoint("tcp", "localhost", 5030),
            Endpoint("tls", "metadata.example", 443),
            Http("http://localhost:5030/") with { AllowPrivateNetwork = true },
        })
        {
            var sample = await probe.ProbeAsync(check, CancellationToken.None);
            Assert.Equal((false, HealthSample.AddressBlocked), (sample.Ok, sample.Error));
        }
        Assert.Empty(connects);

        // The environment's opt-in admits RFC 1918 addresses, never loopback or metadata.
        var optedIn = await probe.ProbeAsync(Endpoint("tcp", "internal.example", 22) with { AllowPrivateNetwork = true },
            CancellationToken.None);
        Assert.Equal("connection-failed", optedIn.Error);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.22"), 22), Assert.Single(connects));
    }

    [Fact]
    public async Task BlockedTargets_ShowAsInvalid_AndNeverOpenAnIncident()
    {
        var connector = new HealthCheckConnector(
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
            (_, _) => throw new InvalidOperationException("a refused address must never be connected to"));
        using var handler = connector.CreateHandler();
        var fixture = Build(checks: """
            [ { "id": "meta", "kind": "http", "url": "http://169.254.169.254/latest/meta-data/" },
              { "id": "local", "kind": "tcp", "host": "localhost", "port": 5030 } ]
            """, probe: new NetworkHealthCheckProbe(new FakeHttpClients(handler), connector, _clock));

        await Ticks(fixture, 4);

        var environment = Assert.Single(fixture.Service.Read(Project)!.Environments);
        Assert.Equal("invalid", environment.Status);
        Assert.All(environment.Checks, check => Assert.Equal(("invalid", HealthSample.AddressBlocked, 0),
            (check.Status, check.LastSample!.Error, check.ConsecutiveFailures)));
        Assert.Null(environment.Uptime24h);
        Assert.Null(environment.CurrentIncident);
        Assert.Empty(fixture.Cards());
        Assert.Empty(fixture.Activity());
        // F16: the environment names each refused target and its fix instead of a bare Misconfigured.
        Assert.Contains("Health check meta targets http://169.254.169.254/latest/meta-data/, which the control plane never probes",
            environment.Error);
        Assert.Contains("Health check local targets localhost:5030", environment.Error);
    }

    [Fact]
    public async Task PrivateTargets_NameTheOptIn_AndProbeOnceItIsDeclared()
    {
        var connects = new List<IPEndPoint>();
        var connector = new HealthCheckConnector(
            (_, _) => throw new InvalidOperationException("an address literal is never resolved"),
            (endpoint, _) =>
            {
                lock (connects) connects.Add(endpoint);
                return Task.FromException<Socket>(new SocketException((int)SocketError.ConnectionRefused));
            });
        var fixture = Build(checks: """[ { "id": "lan", "kind": "tcp", "host": "172.17.0.1", "port": 8080 } ]""",
            probe: new NetworkHealthCheckProbe(new FakeHttpClients(_target), connector, _clock));

        await Ticks(fixture, 1);

        var blocked = Assert.Single(fixture.Service.Read(Project)!.Environments);
        Assert.Equal("invalid", blocked.Status);
        Assert.Contains("Health check lan targets 172.17.0.1:8080, a private network address", blocked.Error);
        Assert.Contains("\"healthChecksAllowPrivateNetwork\": true in docs/deployments/environments/stage.json", blocked.Error);
        Assert.Empty(connects);

        File.WriteAllText(Path.Combine(fixture.Declarations, "stage.json"), Stage(
            """[ { "id": "lan", "kind": "tcp", "host": "172.17.0.1", "port": 8080 } ]""")
            .Replace("\"id\": \"stage\",", "\"id\": \"stage\", \"healthChecksAllowPrivateNetwork\": true,", StringComparison.Ordinal));
        await Ticks(fixture, 1);

        var probed = Assert.Single(fixture.Service.Read(Project)!.Environments);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("172.17.0.1"), 8080), Assert.Single(connects));
        Assert.NotEqual("invalid", probed.Status);
        Assert.Null(probed.Error);
    }

    [Fact]
    public async Task BrokenDeclarations_AreReportedOnTheirEnvironment_WhileEveryOtherEnvironmentKeepsRunning()
    {
        var fixture = Build(
            extraDeclaration: """
                { "id": "production", "name": "Production", "kind": "production", "targetHostId": "prod-host",
                  "address": "https://prod.example", "template": "container", "destination": "8082",
                  "healthUrl": "https://prod.example/health",
                  "healthChecks": [ { "id": "api", "kind": "http", "url": "https://prod.example/api", "intervalSeconds": 90.5 } ] }
                """,
            otherDeclaration: """
                { "id": "stage", "kind": "stage", "targetHostId": "other-host", "address": "https://other.example",
                  "template": "container", "destination": "8083", "healthUrl": "https://other.example/health" }
                """);
        _target.Respond(HttpStatusCode.OK);

        Assert.Equal(1, (await fixture.Service.RunDueAsync()).Probed);

        var environments = fixture.Service.Read(Project)!.Environments;
        Assert.Equal(["stage", "production"], environments.Select(item => item.EnvironmentId));
        Assert.Equal(("up", null), (environments[0].Status, environments[0].Error));
        Assert.Equal("invalid", environments[1].Status);
        Assert.Contains("intervalSeconds must be an integer", environments[1].Error);
        var other = Assert.Single(fixture.Service.Overview().Environments, item => item.Project == "other");
        Assert.Equal(("invalid", "stage"), (other.Status, other.EnvironmentId));
        Assert.Contains("requires name", other.Error);
        Assert.Equal(3, fixture.Service.Overview().Environments.Count);
    }

    [Fact]
    public async Task Broken_file_with_a_different_name_keeps_its_environment_incident_open()
    {
        var fixture = Build();
        await Sequence(fixture, false, false, false);
        var opened = fixture.Store.Load(Project).Environments["stage"].OpenIncident;
        Assert.NotNull(opened);

        File.Move(Path.Combine(fixture.Declarations, "stage.json"),
            Path.Combine(fixture.Declarations, "renamed.json"));
        File.WriteAllText(Path.Combine(fixture.Declarations, "renamed.json"),
            Stage(null).Replace("\"name\": \"Stage\", ", "", StringComparison.Ordinal));
        await Sequence(fixture, true);

        var environment = Assert.Single(fixture.Service.Read(Project)!.Environments);
        Assert.Equal(("stage", "invalid"), (environment.EnvironmentId, environment.Status));
        Assert.Equal(opened.Id, fixture.Store.Load(Project).Environments["stage"].OpenIncident?.Id);
        Assert.DoesNotContain(fixture.Activity(), entry => entry.Topic == OrchestratorLogTopics.EnvironmentIncidentRecovered);
    }

    [Fact]
    public async Task Recovered_card_comment_is_not_repeated_when_activity_append_retries()
    {
        var fixture = Build();
        await Sequence(fixture, false, false, false, true);
        var card = Assert.Single(fixture.Cards());
        var activityPath = Path.Combine(fixture.WatchPath, ".orchestrator", "orchestrator.jsonl");
        var savedPath = activityPath + ".saved";
        File.Move(activityPath, savedPath);
        Directory.CreateDirectory(activityPath);

        await Sequence(fixture, true);
        Assert.Single(fixture.Comments(card), comment => comment.Details!["change"] == "recovered");
        Assert.False(fixture.Store.Load(Project).Environments["stage"].LastIncident!.RecoveryReported);

        Directory.Delete(activityPath);
        File.Move(savedPath, activityPath);
        await Sequence(fixture, true);

        Assert.Single(fixture.Comments(card), comment => comment.Details!["change"] == "recovered");
        Assert.Single(fixture.Activity(), entry => entry.Topic == OrchestratorLogTopics.EnvironmentIncidentRecovered);
    }

    [Fact]
    public async Task AThrowingProbe_FailsOnlyItsCheck()
    {
        var probe = new ScriptedProbe(_clock);
        var fixture = Build(checks: """
            [ { "id": "boom", "kind": "tcp", "host": "stage.example", "port": 1 },
              { "id": "http", "kind": "http", "url": "https://stage.example/health" } ]
            """, probe: probe);

        await Ticks(fixture, 4);

        var checks = Assert.Single(fixture.Service.Read(Project)!.Environments).Checks;
        Assert.Equal(("unknown", HealthSample.ProbeFailed), (checks[0].Status, checks[0].LastSample!.Error));
        Assert.Equal(("up", 1.0), (checks[1].Status, checks[1].Uptime24h));
        Assert.Empty(fixture.Cards());
    }

    [Fact]
    public async Task ChecksOfOneEnvironment_ShareOneIncident_OneCardAndOneAlert()
    {
        var probe = new ScriptedProbe(_clock);
        var fixture = Build(checks: """
            [ { "id": "http", "kind": "http", "url": "https://stage.example/health" },
              { "id": "tls", "kind": "tls", "host": "stage.example" } ]
            """, probe: probe);
        probe.Set("http", false);
        await Ticks(fixture, 3);
        var card = Assert.Single(fixture.Cards());
        Assert.Equal("Health incident: Stage is failing", card.Title);
        Assert.Contains("## Check `http`", File.ReadAllText(Path.Combine(card.FolderPath, "prompt.md")));

        probe.Set("tls", false);
        await Ticks(fixture, 3);
        probe.Set("http", true);
        await Ticks(fixture, 2);
        Assert.Equal("down", Assert.Single(fixture.Service.Read(Project)!.Environments).Status);
        probe.Set("tls", true);
        await Ticks(fixture, 2);

        Assert.Equal(card.Id, Assert.Single(fixture.Cards()).Id);
        Assert.Equal(["check-failing", "check-recovered", "recovered"],
            fixture.Comments(card).Select(item => item.Details!["change"]));
        Assert.Equal([OrchestratorLogTopics.EnvironmentIncidentOpened, OrchestratorLogTopics.EnvironmentIncidentRecovered],
            fixture.Activity().Select(entry => entry.Topic));
        var environment = Assert.Single(fixture.Service.Read(Project)!.Environments);
        Assert.Equal(("up", card.Id), (environment.Status, environment.LastIncident!.CardId));
    }

    [Fact]
    public async Task An_environment_without_a_healthy_deploy_report_is_not_probed_and_opens_no_incident()
    {
        var fixture = Build(deployed: false);
        _target.Respond(HttpStatusCode.BadGateway);

        for (var tick = 0; tick < 5; tick++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(new HealthRunSummary(0, 0, 0, 0), await fixture.Service.RunDueAsync());
        }

        Assert.Empty(_target.Requests);
        var environment = Assert.Single(fixture.Service.Read(Project)!.Environments);
        Assert.Equal((EnvironmentHealthService.NotDeployed, null, null, null),
            (environment.Status, environment.Uptime24h, environment.Uptime7d, environment.Uptime30d));
        Assert.All(environment.Checks, check => Assert.Equal(EnvironmentHealthService.NotDeployed, check.Status));
        Assert.Null(environment.CurrentIncident);
        Assert.Empty(fixture.Cards());
        Assert.Empty(fixture.Store.Load(Project).Checks);

        // A failed deploy report does not start the probes either; the first healthy one does.
        Report(fixture.Configuration, Project, ("stage", "failed"));
        await Ticks(fixture, 1);
        Assert.Empty(_target.Requests);

        Report(fixture.Configuration, Project, ("stage", "healthy"));
        _target.Respond(HttpStatusCode.OK, "ok");
        await Ticks(fixture, 1);
        Assert.Single(_target.Requests);
        environment = Assert.Single(fixture.Service.Read(Project)!.Environments);
        Assert.Equal(("up", 1.0), (environment.Status, environment.Uptime24h));
    }

    [Fact]
    public async Task A_stopped_environment_stops_probing_and_closes_its_open_incident()
    {
        var fixture = Build();
        await Sequence(fixture, false, false, false);
        var card = Assert.Single(fixture.Cards());
        Assert.NotNull(fixture.Store.Load(Project).Environments["stage"].OpenIncident);
        var probes = _target.Requests.Count;

        Report(fixture.Configuration, Project, ("stage", "stopped"));
        await Sequence(fixture, false, false, false, false);

        Assert.Equal(probes, _target.Requests.Count);
        var environment = Assert.Single(fixture.Service.Read(Project)!.Environments);
        Assert.Equal((EnvironmentHealthService.NotDeployed, null), (environment.Status, environment.CurrentIncident));
        var comment = Assert.Single(fixture.Comments(card));
        Assert.Contains("stopped", comment.Summary);
        Assert.Single(fixture.Cards());
        var state = fixture.Store.Load(Project);
        Assert.Null(state.Environments["stage"].OpenIncident);
        Assert.Equal(EnvironmentHealthService.NotDeployed, state.Checks.Values.Single().LastIncident!.EndReason);
    }

    [Fact]
    public async Task ARemovedCheck_ClosesItsOpenIncident()
    {
        var probe = new ScriptedProbe(_clock);
        var fixture = Build(checks: """
            [ { "id": "http", "kind": "http", "url": "https://stage.example/health" },
              { "id": "db", "kind": "tcp", "host": "db.example", "port": 5432 } ]
            """, probe: probe);
        probe.Set("db", false);
        await Ticks(fixture, 3);
        var card = Assert.Single(fixture.Cards());

        fixture.Declare("""[ { "id": "http", "kind": "http", "url": "https://stage.example/health" } ]""");
        await Ticks(fixture, 1);

        var environment = Assert.Single(fixture.Service.Read(Project)!.Environments);
        Assert.Equal(("up", null), (environment.Status, environment.CurrentIncident));
        var comment = Assert.Single(fixture.Comments(card));
        Assert.Equal("recovered", comment.Details!["change"]);
        Assert.Contains("removed from the declaration", comment.Summary);
        Assert.Equal(OrchestratorLogTopics.EnvironmentIncidentRecovered, fixture.Activity()[^1].Topic);
        var state = fixture.Store.Load(Project);
        Assert.False(state.Checks.ContainsKey(HealthCheckStore.Key("stage", "db")));
        Assert.Null(state.Environments["stage"].OpenIncident);
    }

    [Fact]
    public async Task AFailingCardWrite_DoesNotStopRetention_AndIsRetriedOnTheNextTick()
    {
        var fixture = Build(checks: """[ { "id": "http", "kind": "http", "url": "https://stage.example/health", "failureThreshold": 1 } ]""");
        var now = _clock.GetUtcNow();
        fixture.Store.Append(Project, "stage", "http", new(now.AddDays(-40), true, 30, 200, null));
        var jobs = TaskStorageLayout.JobsRoot(fixture.WatchPath);
        if (Directory.Exists(jobs)) Directory.Delete(jobs, true);
        // A file where the card folders belong makes the card write throw.
        File.WriteAllText(jobs, "not a directory");

        await Sequence(fixture, false);

        Assert.Contains(fixture.Logger.Entries, entry => entry.Any(item => item.Key == "{OriginalFormat}"
            && item.Value is string format && format.StartsWith("environment-health report failed", StringComparison.Ordinal)));
        Assert.Single(fixture.Store.Hourly(Project, "stage", "http"));
        Assert.Null(fixture.Store.Load(Project).Environments["stage"].OpenIncident!.CardId);

        File.Delete(jobs);
        await Sequence(fixture, false);
        var card = Assert.Single(fixture.Cards());
        Assert.Equal(card.Id, fixture.Store.Load(Project).Environments["stage"].OpenIncident!.CardId);
    }

    [Fact]
    public async Task CardsActivityAndView_ShowTargetsWithoutUserInfoOrQuery()
    {
        var password = "pw-" + Guid.NewGuid().ToString("N")[..8];
        var token = "tk-" + Guid.NewGuid().ToString("N")[..8];
        var fixture = Build(checks: $$"""
            [ { "id": "http", "kind": "http", "url": "https://probe:{{password}}@stage.example/health?token={{token}}" } ]
            """);

        await Sequence(fixture, false, false, false);

        var prompt = File.ReadAllText(Path.Combine(Assert.Single(fixture.Cards()).FolderPath, "prompt.md"));
        var alert = Assert.Single(fixture.Activity());
        var view = Assert.Single(Assert.Single(fixture.Service.Read(Project)!.Environments).Checks);
        Assert.Contains("https://stage.example/health", prompt);
        Assert.Contains("https://stage.example/health", alert.Reasoning);
        Assert.Equal("https://stage.example/health", view.Target);
        foreach (var text in new[] { prompt, alert.Summary, alert.Reasoning ?? "" })
        {
            Assert.DoesNotContain(password, text);
            Assert.DoesNotContain(token, text);
        }
    }

    [Fact]
    public async Task ProductionWebhook_SendsOpenedAndRecoveredOnce_AndRetriesFailedDelivery()
    {
        var fixture = Build(extraDeclaration: """
            { "id": "production", "name": "Production", "kind": "production", "targetHostId": "prod-host",
              "address": "https://prod.example", "template": "container", "destination": "8082",
              "healthUrl": "https://prod.example/health",
              "healthChecks": [ { "id": "api", "kind": "http", "url": "https://user:password@prod.example/health?token=secret", "failureThreshold": 1, "recoveryThreshold": 1 } ] }
            """);
        fixture.Webhooks.Set(Project, "https://hooks.example/incident", "https://studio.example");
        fixture.WebhookTarget.Status = HttpStatusCode.BadGateway;

        await Sequence(fixture, false);
        Assert.Single(fixture.WebhookTarget.Bodies);
        Assert.False(fixture.Store.Load(Project).Environments["production"].OpenIncident!.OpenedWebhookDelivered);

        fixture.WebhookTarget.Status = HttpStatusCode.OK;
        await Sequence(fixture, false, false);
        Assert.Equal(2, fixture.WebhookTarget.Bodies.Count);
        Assert.True(fixture.Store.Load(Project).Environments["production"].OpenIncident!.OpenedWebhookDelivered);
        Assert.DoesNotContain("password", fixture.WebhookTarget.Bodies[1]);
        Assert.DoesNotContain("secret", fixture.WebhookTarget.Bodies[1]);
        Assert.Contains("\"signal\":\"api\"", fixture.WebhookTarget.Bodies[1]);

        await Sequence(fixture, true, true);
        Assert.Equal(3, fixture.WebhookTarget.Bodies.Count);
        Assert.Contains("\"event\":\"recovered\"", fixture.WebhookTarget.Bodies[2]);
        Assert.True(fixture.Store.Load(Project).Environments["production"].LastIncident!.RecoveryWebhookDelivered);
    }

    /// <summary>Appends deploy reports to the project's deploy history, as a host's report does.</summary>
    private void Report(IConfiguration configuration, string project, params (string Environment, string Status)[] reports)
    {
        var path = Path.Combine(configuration["TaskRepository"]!, "deployments", project + ".json");
        var state = File.Exists(path)
            ? JsonSerializer.Deserialize<ProjectEnvironmentService.State>(File.ReadAllText(path), Web)!
            : new ProjectEnvironmentService.State();
        var at = _clock.GetUtcNow().UtcDateTime;
        state.History.AddRange(reports.Select(report => new ProjectEnvironmentService.EnvironmentHistoryRow(
            report.Environment, at, new DeploymentHistoryItem(at, report.Status, "app:1", null, null))));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, Web));
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private async Task Ticks(Fixture fixture, int count)
    {
        for (var tick = 0; tick < count; tick++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            await fixture.Service.RunDueAsync();
        }
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

    private Fixture Build(string? checks = null, IHealthCheckProbe? probe = null, string? extraDeclaration = null,
        string? otherDeclaration = null, bool withTelemetry = false, bool deployed = true)
    {
        var repo = Path.Combine(_root, "repo");
        var watch = Path.Combine(_root, "workspace", "projects", Project);
        var otherRepo = Path.Combine(_root, "other-repo");
        var otherWatch = Path.Combine(_root, "workspace", "projects", "other");
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(watch, state));
            Directory.CreateDirectory(Path.Combine(otherWatch, state));
        }
        var declarations = Path.Combine(repo, "docs", "deployments", "environments");
        Directory.CreateDirectory(declarations);
        File.WriteAllText(Path.Combine(declarations, "stage.json"), Stage(checks));
        if (extraDeclaration is not null) File.WriteAllText(Path.Combine(declarations, "production.json"), extraDeclaration);
        if (otherDeclaration is not null)
        {
            var otherDeclarations = Path.Combine(otherRepo, "docs", "deployments", "environments");
            Directory.CreateDirectory(otherDeclarations);
            File.WriteAllText(Path.Combine(otherDeclarations, "stage.json"), otherDeclaration);
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["WatchPaths:0:Name"] = Project, ["WatchPaths:0:Path"] = watch,
            ["WatchPaths:0:RootPath"] = repo, ["WatchPaths:0:RepositoryPath"] = repo,
            ["WatchPaths:1:Name"] = "other", ["WatchPaths:1:Path"] = otherWatch,
            ["WatchPaths:1:RootPath"] = otherRepo, ["WatchPaths:1:RepositoryPath"] = otherRepo,
            ["HealthChecks:MaxConcurrency"] = "2",
        }).Build();
        if (deployed)
        {
            Report(configuration, Project, ("stage", "healthy"), ("production", "healthy"));
            Report(configuration, "other", ("stage", "healthy"));
        }
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
        var stateMachine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance,
            timeline: timeline, configuration: configuration);
        var alerts = new HealthAlerts(scanner, mutations, timeline, activity, _clock,
            NullLogger<HealthAlerts>.Instance, stateMachine, new AgentStudio.Deploy.DeployStore(configuration));
        var webhookTarget = new WebhookTarget();
        var webhooks = new IncidentWebhookService(configuration, new FakeHttpClients(webhookTarget),
            NullLogger<IncidentWebhookService>.Instance);
        var store = new HealthCheckStore(configuration);
        var logger = new CapturingLogger<EnvironmentHealthService>();
        var connector = new HealthCheckConnector();
        var telemetryStore = withTelemetry ? new TelemetryStore(new TelemetryOptions { StoreRoot = Path.Combine(_root, "telemetry") },
            NullLogger<TelemetryStore>.Instance) : null;
        var service = new EnvironmentHealthService(scanner, environments, store,
            probe ?? new NetworkHealthCheckProbe(new FakeHttpClients(_target), connector, _clock), alerts, webhooks, configuration,
            _clock, logger, telemetryStore is null ? null : new TelemetryAlertReader(telemetryStore));
        return new(service, scanner, mutations, timeline, activity, configuration, watch, store, logger, declarations,
            webhooks, webhookTarget, telemetryStore);
    }

    private static string Stage(string? checks) => $$"""
        { "id": "stage", "name": "Stage", "kind": "stage", "targetHostId": "stage-host",
          "address": "http://stage.example", "template": "container", "destination": "8081",
          "healthUrl": "https://stage.example/health"{{(checks is null ? "" : ", \"healthChecks\": " + checks)}} }
        """;

    private sealed record Fixture(EnvironmentHealthService Service, TaskScannerService Scanner,
        TaskMutationService Mutations, TimelineLog Timeline, OrchestratorLog Log, IConfiguration Configuration,
        string WatchPath, HealthCheckStore Store, CapturingLogger<EnvironmentHealthService> Logger, string Declarations,
        IncidentWebhookService Webhooks, WebhookTarget WebhookTarget, TelemetryStore? Telemetry)
    {
        public IReadOnlyList<TaskInfo> Cards() => Scanner.ScanAllJobs()
            .Where(card => card.Tags.Contains(HealthAlerts.Tag) || card.Id.StartsWith("health-", StringComparison.Ordinal)).ToArray();

        public IReadOnlyList<TimelineEvent> Comments(TaskInfo card) => Timeline.ReadAll(
                Scanner.FindJob(card.Id, WatchPath)?.FolderPath ?? card.FolderPath)
            .Where(item => item.Kind == TimelineEventKinds.EnvironmentHealthUpdated).ToArray();

        public IReadOnlyList<OrchestratorLogEntry> Activity() => Log.Read(WatchPath);

        public void Declare(string checks) => File.WriteAllText(Path.Combine(Declarations, "stage.json"), Stage(checks));
    }

    private sealed class WebhookTarget : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(Status);
        }
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

    /// <summary>Answers per check id (default success); the check <c>boom</c> throws.</summary>
    private sealed class ScriptedProbe(TimeProvider clock) : IHealthCheckProbe
    {
        private readonly Dictionary<string, bool> _results = new(StringComparer.Ordinal);

        public void Set(string checkId, bool ok)
        {
            lock (_results) _results[checkId] = ok;
        }

        public Task<HealthSample> ProbeAsync(HealthCheckDefinition check, CancellationToken cancellationToken)
        {
            if (check.Id == "boom") throw new InvalidOperationException("probe defect");
            bool ok;
            lock (_results) ok = _results.GetValueOrDefault(check.Id, true);
            return Task.FromResult(ok ? new HealthSample(clock.GetUtcNow(), true, 20, 200, null)
                : new HealthSample(clock.GetUtcNow(), false, null, 502, "unexpected-status"));
        }
    }

    private sealed class CertificateProbe(TimeProvider clock, DateTimeOffset notAfter) : IHealthCheckProbe
    {
        public Task<HealthSample> ProbeAsync(HealthCheckDefinition check, CancellationToken cancellationToken)
            => Task.FromResult(new HealthSample(clock.GetUtcNow(), true, 15, null, null, notAfter));
    }
}
