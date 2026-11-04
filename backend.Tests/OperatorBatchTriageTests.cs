using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentStudio.Diagnostics;
using AgentStudio.Operator;
using AgentStudio.Pipeline;
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
        store.AppendState(new BatchGateState(alpha.BatchId, alpha.MembershipDigest, BatchPhase.Running,
            CandidateSha, 1, _clock));

        var red = Manifest("beta", ["beta-1", "beta-2", "beta-3"]);
        store.CloseManifest(red);
        store.AppendState(new BatchGateState(red.BatchId, red.MembershipDigest, BatchPhase.Red,
            CandidateSha, 1, _clock, "deterministic suite red"));
        var child = Manifest("beta", ["beta-1", "beta-2"], parent: red.BatchId);
        store.CloseManifest(child);
        store.AppendState(new BatchGateState(child.BatchId, child.MembershipDigest, BatchPhase.Running,
            CandidateSha, 1, _clock));
        store.RecordIsolationFinding(new BatchGateIsolationFinding("beta", red.BatchId, "beta-3", "run-beta-3",
            ResultSha, BaseSha, "proven-red", "evidence", _clock));
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
