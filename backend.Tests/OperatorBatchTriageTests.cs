using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Diagnostics;
using AgentStudio.Cli;
using AgentStudio.Operator;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// OP-4 (AGT-3462): <c>GET /api/operator/batch-triage</c> on the real
/// networked host. A fixture of two projects, three flights and one pending
/// push must read the same as the per-project <c>batch-gate/state</c> routes,
/// flag the push once it is older than <c>BatchGate:PendingPushAlertMinutes</c>,
/// and show a project-scoped token only its own projects.
/// </summary>
public sealed class OperatorBatchTriageTests : IDisposable
{
    [Fact]
    public async Task Capacity_reads_cached_quota_and_redacts_scoped_tokens()
    {
        var now = _clock.UtcDateTime;
        var hidden = WriteCapacityCard("beta", "BETA-1", TaskStates.Ready);
        QuotaWaitMarker.Write(hidden, new QuotaWaitRecord
        {
            CliType = "codex", StartedAt = now, ResetAt = now.AddHours(1),
            ThresholdMinutes = 10, Reason = "hidden wait",
        });
        var quotaConfig = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TaskRepository"] = _workspace }).Build();
        new QuotaCacheStore(quotaConfig, Microsoft.Extensions.Logging.Abstractions.NullLogger<QuotaCacheStore>.Instance)
            .Write([new QuotaSnapshot { CliType = "codex", SubscriptionKey = "test-subscription", FetchedAt = now,
                RawSample = "private sample", Windows = [new QuotaWindow
                    { Label = "Weekly", UsedPct = 99, ResetAt = now.AddDays(1) }] }]);
        using var factory = BuildFactory();
        Assert.Contains(factory.Services.GetRequiredService<QuotaService>().GetCached().Snapshots,
            snapshot => snapshot.CliType == "codex" && snapshot.RawSample == "private sample");
        using var client = BearerClient(factory, CreateToken(factory, StudioRoles.Operator, ["alpha"]));
        using var response = await client.GetAsync("/api/operator/capacity");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(body.RootElement.GetProperty("quota").EnumerateArray(), row =>
            row.GetProperty("cliType").GetString() == "codex"
            && row.GetProperty("snapshot").GetProperty("rawSample").ValueKind == JsonValueKind.Null);
        Assert.DoesNotContain("BETA-1", body.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capacity_names_exhausted_cli_active_fallback_and_waiting_card()
    {
        var now = _clock.UtcDateTime;
        var quotaConfig = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TaskRepository"] = _workspace }).Build();
        new QuotaCacheStore(quotaConfig, Microsoft.Extensions.Logging.Abstractions.NullLogger<QuotaCacheStore>.Instance)
            .Write([new QuotaSnapshot { CliType = "claude", SubscriptionKey = "test-subscription",
                FetchedAt = now, Windows = [new QuotaWindow
                    { Label = "Weekly", UsedPct = 99, ResetAt = now.AddDays(1) }] }]);
        using var factory = BuildFactory();
        factory.Services.GetRequiredService<AgentStudio.Tasks.TaskIndexCache>().GetSnapshot();
        var running = WriteCapacityCard("alpha", "ALPHA-1", TaskStates.Progress);
        var scanner = factory.Services.GetRequiredService<AgentStudio.Tasks.TaskScannerService>();
        QuotaFallbackMarker.Write(running, new QuotaFallbackRecord
        {
            CliType = "codex", Model = "gpt-6-sol", Reason = "weekly cap",
            ModelFallback = new ModelFallbackInfo(
                "claude-opus-5", "gpt-6-sol", "weekly cap"), ActivatedAt = now,
        });
        var waiting = WriteCapacityCard("alpha", "ALPHA-2", TaskStates.Ready);
        QuotaWaitMarker.Write(waiting, new QuotaWaitRecord
        {
            CliType = "claude", StartedAt = now, ResetAt = now.AddDays(1),
            ThresholdMinutes = 20, Reason = "hard pin",
        });
        // These cards are created after the host's first index publication.
        // Publish each completed folder as the task creation path does.
        scanner.PublishCreatedFromFolder(running, ProjectPath("alpha"), "alpha", TaskStates.Progress);
        scanner.PublishCreatedFromFolder(waiting, ProjectPath("alpha"), "alpha", TaskStates.Ready);
        var resident = scanner.GetResidentIdentityFacts();
        var runningFact = Assert.Single(resident, task => task.Key == "ALPHA-1");
        Assert.Equal(("alpha", TaskStates.Progress, "gpt-6-sol"),
            (runningFact.ProjectName, runningFact.State, runningFact.QuotaFallback?.Model));
        Assert.Contains(resident, task => task.Key == "ALPHA-2" && task.QuotaWait?.ThresholdMinutes == 20);
        var scans = scanner.RawScanCalls;
        using var client = BearerClient(factory, CreateToken(factory, StudioRoles.Operator, ["alpha"]));
        using var response = await client.GetAsync("/api/operator/capacity");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(scans, scanner.RawScanCalls);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(body.RootElement.GetProperty("quota").EnumerateArray(), row =>
            row.GetProperty("cliType").GetString() == "claude" && row.GetProperty("exhausted").GetBoolean());
        Assert.Contains(body.RootElement.GetProperty("waitingCards").EnumerateArray(),
            row => row.GetProperty("cardKey").GetString() == "ALPHA-2");
        var fallback = Assert.Single(body.RootElement.GetProperty("activeFallbacks").EnumerateArray(),
            row => row.GetProperty("cardKey").GetString() == "ALPHA-1");
        Assert.Equal("ALPHA-1", fallback.GetProperty("cardKey").GetString());
        Assert.Equal("claude-opus-5", fallback.GetProperty("sourceModel").GetString());
        Assert.Equal("gpt-6-sol", fallback.GetProperty("targetModel").GetString());
        Assert.Contains("weekly cap", fallback.GetProperty("reason").GetString());
        var wait = Assert.Single(body.RootElement.GetProperty("waitingCards").EnumerateArray());
        Assert.Equal("ALPHA-2", wait.GetProperty("cardKey").GetString());
        Assert.Equal(20, wait.GetProperty("waitPolicy").GetProperty("thresholdMinutes").GetInt32());
    }

    private string WriteCapacityCard(string project, string key, string state)
    {
        var folder = Path.Combine(ProjectPath(project), "tasks", "000", key);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = key, key, title = key, state, order = 1, agent = "codex", kind = "task",
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Fixture card.");
        return folder;
    }
    [Fact]
    public async Task Restart_preflight_names_a_pending_push_and_passes_after_clear()
    {
        using var factory = BuildFactory();
        using var client = BearerClient(factory, CreateToken(factory, StudioRoles.Operator, ["*"]));
        MarkPush(factory);

        using var blocked = await client.GetAsync("/api/operator/preflight/restart");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        using var body = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.Contains(body.RootElement.GetProperty("blockers").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "pending-push"
                && item.GetProperty("project").GetString() == "alpha");

        factory.Services.GetRequiredService<BatchIntegrationReservationService>()
            .MarkPushVerified("alpha", "alpha-push-job", ResultSha);
        using var clear = await client.GetAsync("/api/operator/preflight/restart");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
    }

    [Fact]
    public async Task Deploy_plan_orders_server_before_engine_and_hosts()
    {
        using var factory = BuildFactory();
        using var client = BearerClient(factory, CreateToken(factory, StudioRoles.Operator, ["*"]));
        using var response = await client.PostAsJsonAsync("/api/operator/deploy/plan",
            new { targetVersion = "1.1.0", windowMinutes = 30 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["drain", "task-server", "readiness", "engine", "hosts"],
            body.RootElement.GetProperty("steps").EnumerateArray()
                .Select(step => step.GetProperty("kind").GetString()));
    }

    private const string Password = "correct horse battery staple!";
    private const int AlertMinutes = 5;
    private static readonly string BaseSha = new('b', 40);
    private static readonly string ResultSha = new('c', 40);
    private static readonly string CandidateSha = new('d', 40);

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "operator-batch-triage-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _clock = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
    private string? _reservationRoot;

    [Fact]
    public async Task Triage_matches_the_per_project_state_routes_field_by_field()
    {
        using var factory = BuildFactory();
        var flights = SeedFlights(factory);
        MarkPush(factory);
        _clock = _clock.AddMinutes(AlertMinutes + 1);
        using var client = BearerClient(factory, CreateToken(factory, StudioRoles.Operator, ["*"]));

        using var triage = await GetJsonAsync(client, "/api/operator/batch-triage");
        var root = triage.RootElement;
        Assert.Equal(AlertMinutes, root.GetProperty("pendingPushAlertMinutes").GetDouble());
        var projects = root.GetProperty("projects").EnumerateArray().ToArray();
        Assert.Equal(["alpha", "beta"], projects.Select(item => item.GetProperty("project").GetString()));
        Assert.Equal(3, projects.Sum(item => item.GetProperty("flights").GetArrayLength()));
        Assert.Equal(1, projects.Sum(item => item.GetProperty("pendingPushes").GetArrayLength()));

        foreach (var project in projects)
        {
            var name = project.GetProperty("project").GetString()!;
            using var stateDocument = await GetJsonAsync(client,
                $"/api/projects/{name}/batch-gate/state?history={BatchTriageReader.FlightHistory}");
            var state = stateDocument.RootElement;
            AssertSame(state.GetProperty("plan"), project.GetProperty("plan"), $"{name} plan");

            var batches = state.GetProperty("batches").EnumerateArray()
                .ToDictionary(item => item.GetProperty("batchId").GetString()!);
            foreach (var flight in project.GetProperty("flights").EnumerateArray())
            {
                var batchId = flight.GetProperty("batchId").GetString()!;
                var batch = batches[batchId];
                AssertSame(batch, flight.GetProperty("state"), $"{name} flight {batchId}");
                Assert.Equal(batch.GetProperty("phase").GetString(), flight.GetProperty("phase").GetString());
                Assert.Equal(batch.GetProperty("parentBatchId").ToString(), flight.GetProperty("parentBatchId").ToString());
                Assert.Equal(batch.GetProperty("members").EnumerateArray().Select(item => item.GetProperty("taskKey").GetString()),
                    flight.GetProperty("memberKeys").EnumerateArray().Select(item => item.GetString()));
            }

            var members = state.GetProperty("members");
            foreach (var member in project.GetProperty("members").EnumerateArray())
            {
                var key = member.GetProperty("taskKey").GetString()!;
                var expected = members.GetProperty(key);
                AssertSame(expected, member.GetProperty("state"), $"{name} member {key}");
                Assert.Equal(expected.GetProperty("status").GetString(), member.GetProperty("status").GetString());
                Assert.Equal(expected.GetProperty("batchId").ToString(), member.GetProperty("batchId").ToString());
            }

            var waits = state.GetProperty("pushWaits").EnumerateArray().ToArray();
            var pushes = project.GetProperty("pendingPushes").EnumerateArray().ToArray();
            Assert.Equal(waits.Length, pushes.Length);
            foreach (var (wait, push) in waits.Zip(pushes))
            {
                foreach (var field in new[] { "jobId", "approvedSha", "sinceUtc", "ageMinutes", "reason" })
                    AssertSame(wait.GetProperty(field), push.GetProperty(field), $"{name} push {field}");
                Assert.Equal(wait.GetProperty("ageAlert").GetBoolean(), push.GetProperty("overdue").GetBoolean());
            }

            using var stalls = await GetJsonAsync(client, $"/api/projects/{name}/batch-gate/queue-stalls");
            AssertSame(stalls.RootElement, project.GetProperty("queueStalls"), $"{name} queue stalls");
        }

        var alpha = projects[0];
        Assert.Equal([flights.AlphaRunning], alpha.GetProperty("flights").EnumerateArray()
            .Select(item => item.GetProperty("batchId").GetString()));
        Assert.All(alpha.GetProperty("members").EnumerateArray(), member =>
            Assert.Equal("wait", member.GetProperty("nextOperation").GetProperty("operation").GetString()));
        Assert.Empty(alpha.GetProperty("isolatingMembers").EnumerateArray());

        // The red parent and its running child isolate; eject is the only member action.
        var beta = projects[1];
        Assert.Equal(new[] { flights.BetaRed, flights.BetaChild }.Order(), beta.GetProperty("flights").EnumerateArray()
            .Select(item => item.GetProperty("batchId").GetString()).Order());
        Assert.All(beta.GetProperty("flights").EnumerateArray(), flight =>
            Assert.True(flight.GetProperty("isolating").GetBoolean()));
        Assert.Equal(["beta-1", "beta-2", "beta-3"], beta.GetProperty("isolatingMembers").EnumerateArray()
            .Select(item => item.GetString()).Order());
        Assert.Equal(["beta-3"], beta.GetProperty("redMembers").EnumerateArray().Select(item => item.GetString()));
        Assert.All(beta.GetProperty("members").EnumerateArray(), member =>
            Assert.Equal("eject", member.GetProperty("nextOperation").GetProperty("operation").GetString()));
    }

    [Fact]
    public async Task A_pending_push_older_than_the_alert_minutes_is_overdue()
    {
        using var factory = BuildFactory();
        MarkPush(factory);
        using var client = BearerClient(factory, CreateToken(factory, StudioRoles.Viewer, ["*"]));

        _clock = _clock.AddMinutes(AlertMinutes - 1);
        var early = await PushAsync(client);
        Assert.False(early.GetProperty("overdue").GetBoolean());
        Assert.Equal(AlertMinutes - 1, early.GetProperty("ageMinutes").GetDouble(), 6);
        Assert.Equal(AlertMinutes, early.GetProperty("alertAfterMinutes").GetDouble());

        _clock = _clock.AddMinutes(2);
        var late = await PushAsync(client);
        Assert.True(late.GetProperty("overdue").GetBoolean());
        Assert.Equal(AlertMinutes + 1, late.GetProperty("ageMinutes").GetDouble(), 6);
    }

    [Fact]
    public async Task A_project_scoped_token_sees_only_its_projects()
    {
        using var factory = BuildFactory();
        SeedFlights(factory);
        MarkPush(factory);
        foreach (var role in new[] { StudioRoles.Viewer, StudioRoles.Operator })
        {
            using var client = BearerClient(factory, CreateToken(factory, role, ["beta"]));
            using var triage = await GetJsonAsync(client, "/api/operator/batch-triage");
            var project = Assert.Single(triage.RootElement.GetProperty("projects").EnumerateArray());
            Assert.Equal("beta", project.GetProperty("project").GetString());
            Assert.DoesNotContain("alpha", triage.RootElement.GetRawText(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_triage_route_is_read_only()
    {
        using var factory = BuildFactory();
        using var client = BearerClient(factory, CreateToken(factory, StudioRoles.Operator, ["*"]));
        using var response = await client.PostAsync("/api/operator/batch-triage", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private static void AssertSame(JsonElement expected, JsonElement actual, string what)
        => Assert.True(JsonElement.DeepEquals(expected, actual),
            $"{what} differs:\n  state:  {expected.GetRawText()}\n  triage: {actual.GetRawText()}");

    private static async Task<JsonElement> PushAsync(HttpClient client)
    {
        using var triage = await GetJsonAsync(client, "/api/operator/batch-triage");
        return triage.RootElement.GetProperty("projects").EnumerateArray()
            .Single(item => item.GetProperty("project").GetString() == "alpha")
            .GetProperty("pendingPushes").EnumerateArray().Single().Clone();
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body);
    }

    private sealed record Flights(string AlphaRunning, string BetaRed, string BetaChild);

    private Flights SeedFlights(WebApplicationFactory<Program> factory)
    {
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var alpha = Manifest("alpha", ["alpha-1", "alpha-2"]);
        store.CloseManifest(alpha);
        BatchGateStates.Reach(store, new BatchGateState(alpha.BatchId, alpha.MembershipDigest, BatchPhase.Running,
            CandidateSha, 1, _clock));

        var red = Manifest("beta", ["beta-1", "beta-2", "beta-3"]);
        store.CloseManifest(red);
        BatchGateStates.Reach(store, new BatchGateState(red.BatchId, red.MembershipDigest, BatchPhase.Red,
            CandidateSha, 1, _clock, "deterministic suite red"));
        var child = Manifest("beta", ["beta-1", "beta-2"], parent: red.BatchId);
        store.CloseManifest(child);
        BatchGateStates.Reach(store, new BatchGateState(child.BatchId, child.MembershipDigest, BatchPhase.Running,
            CandidateSha, 1, _clock));
        var solo = Manifest("beta", ["beta-3"], parent: red.BatchId);
        store.CloseManifest(solo);
        var runId = Guid.NewGuid().ToString("N");
        var evidence = Path.Combine(_workspace, runId + ".jsonl");
        File.WriteAllText(evidence, "deterministic test failure");
        BatchGateStates.Reach(store, new BatchGateState(solo.BatchId, solo.MembershipDigest,
            BatchPhase.Assembled, CandidateSha, 1, _clock));
        store.RecordRun(new BatchGateRunRecord(runId, solo.BatchId, solo.MembershipDigest,
            solo.BaseSha, CandidateSha, solo.Scope.GateProfile,
            solo.Scope.GateProfileDigest, "fixture", 1, ["echo test"], _clock, evidence));
        store.RecordVerdict(new BatchGateRunVerdict(runId, solo.BatchId, solo.MembershipDigest,
            CandidateSha, solo.Scope.GateProfileDigest, "red", "DeterministicSuiteRed",
            evidence, _clock));
        BatchGateStates.Reach(store, new BatchGateState(solo.BatchId, solo.MembershipDigest,
            BatchPhase.Red, CandidateSha, 1, _clock));
        store.RecordIsolationFinding(new BatchGateIsolationFinding("beta", red.BatchId, "beta-3", "run-beta-3",
            ResultSha, BaseSha, "proven-red", evidence, _clock, solo.BatchId, runId));
        return new Flights(alpha.BatchId, red.BatchId, child.BatchId);
    }

    private BatchGateManifest Manifest(string project, string[] keys, string? parent = null)
    {
        var scope = new BatchGateScope(project, project + "-repo", "develop", "full", "digest", "v1");
        var members = keys.Select((key, index) => new BatchGateSubject(
            key, project, scope.Repository, "develop", "full", "digest", "v1",
            "refs/heads/agent-studio/results/" + key, ResultSha, "run-" + key, 1, 1, ResultSha,
            true, true, true, true, false, null, false, _clock.AddMinutes(-10 + index), index + 1)).ToArray();
        return new BatchGateManifest(Guid.NewGuid().ToString("N"), scope, BaseSha,
            BatchGatePolicy.MembershipDigest(BaseSha, scope, members), _clock, members, [], keys,
            ParentBatchId: parent);
    }

    private static void MarkPush(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<BatchIntegrationReservationService>()
            .MarkPushPending("alpha", "alpha-push-job", ResultSha);

    private string CreateToken(WebApplicationFactory<Program> factory, string role, string[] projects)
    {
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var ownerId = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        return store.CreateApiToken(new CreateApiTokenRequest("triage " + role, role, projects,
            _clock.UtcDateTime.AddDays(30)), ownerId).Secret;
    }

    private WebApplicationFactory<Program> BuildFactory()
    {
        foreach (var project in new[] { "alpha", "beta" })
            foreach (var state in TaskStates.All)
                Directory.CreateDirectory(Path.Combine(ProjectPath(project), state));
        var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = "networked",
                ["AllowedHosts"] = "studio.test",
                ["DeliveryChain:Guarded"] = "false",
                ["ReviewDecisionOrchestrator:Enabled"] = "false",
                ["BatchGate:PendingPushAlertMinutes"] = AlertMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Secrets:KeyFile"] = Path.Combine(_workspace + "-keys", "secrets.kek"),
                ["Secrets:KeyEnvironmentVariable"] = "AGENT_STUDIO_TEST_UNSET_KEK_" + Guid.NewGuid().ToString("N"),
                ["WatchPaths:0:Name"] = "alpha",
                ["WatchPaths:0:Path"] = ProjectPath("alpha"),
                ["WatchPaths:0:RootPath"] = ProjectPath("alpha"),
                ["WatchPaths:1:Name"] = "beta",
                ["WatchPaths:1:Path"] = ProjectPath("beta"),
                ["WatchPaths:1:RootPath"] = ProjectPath("beta"),
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<BatchGateStore>();
                services.AddSingleton(new BatchGateStore(Path.Combine(_workspace, "batch-gates")));
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixtureTimeProvider(() => _clock));
                // The seeded flights stay as written: no planner tick or recovery loop runs.
                for (var index = services.Count - 1; index >= 0; index--)
                    if (services[index].ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                        && services[index].ImplementationType?.Namespace?.StartsWith("AgentStudio.", StringComparison.Ordinal) == true)
                        services.RemoveAt(index);
            });
        });
        var settings = factory.Services.GetRequiredService<ProjectSettingsService>();
        foreach (var project in new[] { "alpha", "beta" })
            settings.SetBatchGate(project, new BatchGateFormationOptions(Enabled: true, Scope: "code"));
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        _reservationRoot = BatchGateStorageRoot.ForInstance(environment.ContentRootPath, _workspace, "batch-push-reservations");
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        security.Bootstrap(new BootstrapRequest("first.owner", Password, null));
        return factory;
    }

    private string ProjectPath(string project) => Path.Combine(_workspace, "projects", project);

    private static HttpClient BearerClient(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private sealed class FixtureTimeProvider(Func<DateTimeOffset> now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now();
    }

    public void Dispose()
    {
        foreach (var folder in new[] { _workspace, _workspace + "-keys", _reservationRoot })
        {
            try
            {
                if (folder is not null && Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SilentCatch.Note(ex, "OperatorBatchTriageTests: temporary folder cleanup failed");
            }
        }
    }
}
