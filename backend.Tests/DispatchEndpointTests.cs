using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Dispatch;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: index dates are stored evidence; refresh dates are explicit observations passed to the collector, never compared with the wall clock.

public sealed class DispatchEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dispatch-api-" + Guid.NewGuid().ToString("N"));
    private string Watch => Path.Combine(_root, "cards");

    private WebApplicationFactory<Program> Factory()
    {
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(Watch, state));
        var folder = Path.Combine(Watch, TaskStates.Ready, "sample");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
            { id = "sample", title = "Sample", state = TaskStates.Ready, order = 1, agent = "codex" }));
        return new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = "Forecast Project",
                ["WatchPaths:0:Path"] = Watch,
                ["WatchPaths:0:RootPath"] = Watch,
            }));
            builder.ConfigureTestServices(TestHostFactory.RemoveBackgroundHostedServices);
        });
    }

    [Theory]
    [InlineData("local")]
    [InlineData("remote-claim")]
    public async Task Live_pickup_uses_stored_forecasts_for_candidate_and_delivery_exposure(string path)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var index = factory.Services.GetRequiredService<TaskIndexCache>();
        index.GetSnapshot();
        var card = factory.Services.GetRequiredService<TaskScannerService>().FindJob("sample", Watch)!;
        var projects = factory.Services.GetRequiredService<AgentStudio.Registry.ProjectRegistry>();
        var project = projects.FindByIdOrDisplayName(card.ProjectName)!;
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Security:Profile"] = "networked", ["TaskRepository"] = Path.Combine(_root, "isolated-security"), ["Secrets:KeyFile"] = Path.Combine(_root, "test.kek") }).Build();
        var security = new AgentStudio.Security.AccessSecurityStore(config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentStudio.Security.AccessSecurityStore>.Instance, clock);
        var bootstrap = security.Bootstrap(new("dispatch.owner", "correct horse battery staple!", null));
        var owner = security.AuthenticateSession(bootstrap.SessionToken)!;
        var secret = security.BeginTotpEnrollment(owner);
        security.ConfirmTotpEnrollment(owner, AgentStudio.Security.SecondFactorCodes.CurrentTotp(secret, clock.GetUtcNow().UtcDateTime));
        clock.Advance(TimeSpan.FromSeconds(30));
        security.VerifyStepUp(owner, AgentStudio.Security.SecondFactorCodes.CurrentTotp(secret, clock.GetUtcNow().UtcDateTime));
        var activations = new AgentStudio.Personas.PersonaActivationStore(null, new(), security, _ => project.WorkspaceId, clock);
        var activation = activations.Activate(project.WorkspaceId, "area-warden", new(), owner);
        clock.SetUtcNow(new DateTimeOffset(activation.ShadowUntil, TimeSpan.Zero).AddMinutes(1));
        var settings = factory.Services.GetRequiredService<ProjectSettingsService>();
        settings.SetDispatchMode(card.ProjectName, "live");
        var store = factory.Services.GetRequiredService<DispatchStore>();
        Assert.NotNull(store.AcquireLease(card.ProjectName, "area-warden", 0, clock.GetUtcNow(), TimeSpan.FromHours(1), true, true, true));
        const string file = "backend/Features/Tasks/Shared.cs";
        store.Write(card.ProjectName, card.TaskKey, new([file], [file, "backend/Host/Program.cs"], ["backend/Features/Tasks"], [], "high", "v1"));
        store.Write(card.ProjectName, "C", new([file], [file], ["backend/Features/Tasks"], [], "high", "v1"));
        var dispatchers = new DispatcherShadowService(store, activations, projects, index, settings, clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DispatcherShadowService>.Instance);
        var guard = new AgentStudio.Runner.OverlapDispatchGuard(clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentStudio.Runner.OverlapDispatchGuard>.Instance,
            new AgentStudio.Runner.OverlapPredictionLedger(Path.Combine(_root, "predictions")), dispatchers);
        Assert.Equal(new[] { file }, guard.Predict(card).PredictedScope);
        var exposed = new AgentStudio.Runner.InFlightTask("C", new(false, ["unrelated-prompt.cs"]), TaskStates.AutoReview) { CardKey = "C" };
        var held = guard.Decide(card, [], [exposed], 1, "off", path, "develop");
        Assert.False(held.Admitted);
        Assert.Contains("C", held.Reason);
        Assert.NotNull(held.DispatchDecisionId);
        var liveEvaluation = store.ReadLive(card.ProjectName)!.Record;
        var shadowEvaluation = store.ReadShadow(card.ProjectName).Last(r => r.Shadow);
        Assert.Equal(shadowEvaluation.EvaluationId, liveEvaluation.EvaluationId);
        Assert.Equal(shadowEvaluation.FactsDigest, liveEvaluation.FactsDigest);
        Assert.Equal(held.DispatchDecisionId, Assert.Single(store.ReadRunDecisions(card.ProjectName)).DecisionId);
        var admitted = guard.Decide(card, [], [], 1, "off", path, "develop");
        Assert.True(admitted.Admitted);
        guard.Claimed(card, "attempt");
        Assert.Equal(admitted.DispatchDecisionId, store.AdmittingDecision(card.ProjectName, card.Key ?? card.TaskKey));
        Assert.True(guard.ValidateLive(card));
        var live = store.ReadLive(card.ProjectName)!;
        Assert.True(store.WriteLive(live with { Record = live.Record with { Plan = live.Record.Plan with {
            ExclusiveMarks = [new(card.Key ?? card.TaskKey, "over-module-limit", "four modules")] } } }, clock.GetUtcNow()));
        Assert.True(guard.Predict(card).Exclusive);
        var lease = store.ReadLease(card.ProjectName)!;
        store.ReleaseLease(card.ProjectName, lease.Persona, lease.Fence, clock.GetUtcNow());
        Assert.False(guard.ValidateLive(card));
        settings.SetDispatchMode(card.ProjectName, "shadow");
        Assert.True(guard.Decide(card, [], [exposed], 1, "off", path, "develop").Admitted);
        settings.SetDispatchMode(card.ProjectName, "off");
        var records = store.ReadShadow(card.ProjectName).Count;
        Assert.True(guard.Decide(card, [], [exposed], 1, "off", path, "develop").Admitted);
        Assert.Equal(records, store.ReadShadow(card.ProjectName).Count);
    }

    [Fact]
    public async Task Dispatch_mode_defaults_to_shadow_and_persists_only_known_modes()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        const string route = "/api/projects/Forecast%20Project/dispatch/mode";
        Assert.Equal("shadow", (await client.GetFromJsonAsync<JsonElement>(route)).GetProperty("mode").GetString());
        foreach (var mode in new[] { "off", "live", "shadow" })
        {
            (await client.PutAsJsonAsync(route, new DispatchModeRequest(mode))).EnsureSuccessStatusCode();
            Assert.Equal(mode, factory.Services.GetRequiredService<ProjectSettingsService>().Get("Forecast Project").DispatchMode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(route, new DispatchModeRequest("automatic"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/projects/missing/dispatch/mode")).StatusCode);
    }

    [Fact]
    public async Task ForecastReadAndCardDetailExposeTheSameStoredForecast()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        // Hydrate the isolated index explicitly, with unrelated workers disabled.
        factory.Services.GetRequiredService<TaskIndexCache>().GetSnapshot();
        var card = factory.Services.GetRequiredService<TaskScannerService>().FindJob("sample", Watch)!;
        var forecast = TouchForecast.Compute("a.cs", TouchForecast.BuildIndex("tip", new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero), ["a.cs"], []), []);
        forecast = forecast with
        {
            Deliveries = [new TouchDelivery("result", "base", forecast.ComputedAt, ["a.cs", "b.cs"], ["a.cs", "b.cs"],
                new(1, 0.5), new(1, 0.5)) { Forecast = forecast }],
        };
        factory.Services.GetRequiredService<DispatchStore>().Write(card.ProjectName, card.TaskKey, forecast);
        using var response = await client.GetAsync("/api/projects/Forecast%20Project/dispatch/forecasts?cards=sample");
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<Dictionary<string, TouchForecastV1?>>())!;
        Assert.Equal(forecast.IndexRevision, Assert.Single(body).Value!.IndexRevision);
        var delivery = Assert.Single(Assert.Single(body).Value!.Deliveries);
        Assert.Equal(new[] { "a.cs", "b.cs" }, delivery.RealFiles);
        Assert.Equal(new ForecastAccuracy(1, 0.5), delivery.FileAccuracy);
        Assert.Equal(new ForecastAccuracy(1, 0.5), delivery.ModuleAccuracy);
        Assert.Equal(forecast.IndexRevision, delivery.Forecast!.IndexRevision);
        using var detail = JsonDocument.Parse(await client.GetStringAsync("/api/tasks/sample?watchPath=" + Uri.EscapeDataString(Watch)));
        Assert.Equal(forecast.IndexRevision, detail.RootElement.GetProperty("touchForecast").GetProperty("indexRevision").GetString());
        var project = factory.Services.GetRequiredService<ProjectRegistry>().FindByStorageLocation(Watch)!;
        using var overview = JsonDocument.Parse(await client.GetStringAsync("/api/tasks/sample/details/overview?project=" + Uri.EscapeDataString(project.Id)));
        Assert.Equal(forecast.IndexRevision, overview.RootElement.GetProperty("data").GetProperty("touchForecast").GetProperty("indexRevision").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/projects/missing/dispatch/forecasts")).StatusCode);
    }

    [Fact]
    public async Task ConflictReadIsProjectScopedAndUsesAHalfOpenRange()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var store = factory.Services.GetRequiredService<DispatchStore>();
        var at = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        store.AppendConflict(new("one", "Forecast Project", "AGT-1", "integration-conflict", "base", "tip",
            ["a.cs"], ["AGT-2"], at));
        store.AppendConflict(new("other", "Other", "AGT-3", "semantic", "base", "tip", [], [], at));
        var conflictFolder = Path.Combine(_root, ".dispatch",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("FORECAST PROJECT"))), "conflicts");
        File.WriteAllText(Path.Combine(conflictFolder, "damaged.json"), "invalid json");
        File.WriteAllText(Path.Combine(conflictFolder, "null.json"), "null");
        const string route = "/api/projects/Forecast%20Project/dispatch/conflicts";
        var rows = await client.GetFromJsonAsync<DispatchConflictRecordV1[]>(route + "?from=2026-11-20T12:00:00Z&to=2026-11-21T00:00:00Z");
        Assert.Equal("AGT-1", Assert.Single(rows!).Card);
        Assert.Empty((await client.GetFromJsonAsync<DispatchConflictRecordV1[]>(route + "?to=2026-11-20T12:00:00Z"))!);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route + "?from=2026-11-21&to=2026-11-20")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route + "?from=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/projects/missing/dispatch/conflicts")).StatusCode);
    }

    [Fact]
    public async Task Dispatcher_read_uses_project_access_and_keeps_shadows_separate_from_conflicts()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        factory.Services.GetRequiredService<TaskIndexCache>().GetSnapshot();
        var service = factory.Services.GetRequiredService<DispatcherShadowService>();
        service.Record(new AgentStudio.OrchestrationDecisions.DispatchFacts("Forecast Project",
            new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero), [], [], 1,
            AgentStudio.OrchestrationDecisions.DispatchLedgerSummary.Empty, []),
            factory.Services.GetRequiredService<AgentStudio.Personas.PersonaCatalogue>().Entries
                .Select(e => e.Descriptor).Where(d => d.Kind == "dispatcher"), "remote-claim");
        using var body = JsonDocument.Parse(await client.GetStringAsync("/api/projects/Forecast%20Project/dispatch/personas"));
        Assert.Equal(5, body.RootElement.GetProperty("shadow").GetArrayLength());
        Assert.Equal(5, body.RootElement.GetProperty("log").GetArrayLength());
        Assert.True(body.RootElement.GetProperty("chainIntact").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("lease").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/projects/missing/dispatch/personas")).StatusCode);
    }

    [Theory]
    [InlineData("DeterministicSuiteRed", true, 1)]
    [InlineData("DeterministicSuiteRed", true, 1, "write")]
    [InlineData("DeterministicSuiteRed", true, 1, "forecast")]
    [InlineData("InfrastructureRed", true, 0)]
    [InlineData("DeterministicSuiteRed", false, 0)]
    public async Task IsolationWritesOnlyDeterministicOverlappingPairs(string classification, bool overlaps, int expected, string? failure = null)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var at = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var scope = new AgentStudio.Pipeline.BatchGateScope("Forecast Project", "repo", "develop", "full", "digest", "v1");
        AgentStudio.Pipeline.BatchGateSubject Subject(string key, int order) => new(
            key, scope.Project, "repo", "develop", "full", "digest", "v1", "refs/heads/result", sha, "attempt", 1, 1,
            sha, true, true, true, true, false, null, true, at, order);
        var members = new[] { Subject("AGT-1", 1), Subject("AGT-2", 2), Subject("AGT-3", 3) };
        var parent = new AgentStudio.Pipeline.BatchGateManifest("dispatch-parent", scope, sha,
            AgentStudio.Pipeline.BatchGatePolicy.MembershipDigest(sha, scope, members), at, members, [], members.Select(member => member.TaskKey).ToArray());
        var child = parent with { BatchId = "dispatch-pair", MembershipDigest = AgentStudio.Pipeline.BatchGatePolicy.MembershipDigest(sha, scope, members.Take(2).ToArray()),
            Members = members.Take(2).ToArray(), ParentBatchId = parent.BatchId, EligibleKeys = ["AGT-1", "AGT-2"], Diagnostic = true };
        var batch = factory.Services.GetRequiredService<AgentStudio.Pipeline.BatchGateStore>();
        batch.CloseManifest(parent);
        batch.CloseManifest(child);
        BatchGateStates.Reach(batch, new(child.BatchId, child.MembershipDigest,
            AgentStudio.Pipeline.BatchPhase.Assembled, sha, 1, at));
        var evidence = Path.Combine(_root, "pair-evidence.jsonl");
        File.WriteAllText(evidence, "{}\n");
        batch.RecordRun(new("pair-run", child.BatchId, child.MembershipDigest, sha, sha, "full", "digest", "runner", 1, ["dotnet test"], at, evidence));
        batch.RecordVerdict(new("pair-run", child.BatchId, child.MembershipDigest, sha, "digest", "fail", classification, evidence, at));
        BatchGateStates.Reach(batch, new(child.BatchId, child.MembershipDigest,
            AgentStudio.Pipeline.BatchPhase.Red, sha, 1, at));
        var dispatch = factory.Services.GetRequiredService<DispatchStore>();
        dispatch.Write(scope.Project, "AGT-1", new([], ["shared.cs"], [], [], "high", "revision"));
        dispatch.Write(scope.Project, "AGT-2", new([], [overlaps ? "shared.cs" : "other.cs"], [], [], "high", "revision"));
        var pilot = factory.Services.GetRequiredService<AgentStudio.Pipeline.BatchGatePilotService>();
        var conflictFolder = Path.Combine(_root, ".dispatch",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(scope.Project.ToUpperInvariant()))), "conflicts");
        var forecastFolder = Path.Combine(Path.GetDirectoryName(conflictFolder)!, "cards");
        var forecastPath = Directory.GetFiles(forecastFolder).First();
        var forecastJson = File.ReadAllText(forecastPath);
        if (failure == "write") File.WriteAllText(conflictFolder, "unavailable");
        if (failure == "forecast") File.WriteAllText(forecastPath, "invalid json");
        if (failure is not null)
        {
            // Evidence failure must leave isolation derivation usable and retryable.
            pilot.DeriveIsolationFindings(parent, [child], skipSettled: true);
            Assert.Empty(dispatch.ReadConflicts(scope.Project));
            if (failure == "write") File.Delete(conflictFolder);
            if (failure == "forecast") File.WriteAllText(forecastPath, forecastJson);
        }
        pilot.DeriveIsolationFindings(parent, [child], skipSettled: true);
        pilot.DeriveIsolationFindings(parent, [child], skipSettled: true);
        var rows = dispatch.ReadConflicts(scope.Project);
        Assert.Equal(expected, rows.Count);
        if (expected > 0)
        {
            Assert.Equal("semantic", rows[0].Kind);
            Assert.Equal("AGT-1", rows[0].Card);
            Assert.Equal(new[] { "AGT-2" }, rows[0].PartnerCards);
        }
    }

    [Fact]
    public async Task RoutineRefreshSelectsOnlyChangedCardsAndRecoversArchiveOncePerDay()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var archived = Path.Combine(Watch, TaskStates.Archive, "archived");
        Directory.CreateDirectory(archived);
        File.WriteAllText(Path.Combine(archived, "task.json"), JsonSerializer.Serialize(new
            { id = "archived", title = "Archived", state = TaskStates.Archive, order = 1, agent = "codex" }));
        factory.Services.GetRequiredService<TaskIndexCache>().Invalidate();
        var service = factory.Services.GetRequiredService<DispatchForecastService>();
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(2, service.CollectRefreshCards(now, out var startup).Count);
        Assert.True(startup);
        Assert.Empty(service.CollectRefreshCards(now.AddMinutes(1), out var idle));
        Assert.False(idle);

        // A prompt update must not erase an already queued Ready-entry refresh.
        service.RequestCardRefresh(Watch, "sample", enteredReady: true);
        service.RequestCardRefresh(Watch, "sample");
        var ready = Assert.Single(service.CollectRefreshCards(now.AddMinutes(2), out var changed));
        Assert.False(changed);
        Assert.Equal("sample", ready.Card.Id);
        Assert.True(ready.EnteredReady);
        service.RequestCardRefresh(Watch, "archived");
        var delivery = Assert.Single(service.CollectRefreshCards(now.AddMinutes(3), out _));
        Assert.Equal("archived", delivery.Card.Id);
        Assert.False(delivery.EnteredReady);
        Assert.Empty(service.CollectRefreshCards(now.AddMinutes(4), out _));

        // The delivery subject can land after the lane event. Raw watcher changes
        // also cover prompt writes that do not publish a task mutation event.
        service.PathChanged(Path.Combine(ready.Card.FolderPath, "prompt.md"));
        Assert.Equal("sample", Assert.Single(service.CollectRefreshCards(now.AddMinutes(4), out _)).Card.Id);
        service.PathChanged(AgentStudio.Pipeline.ReviewSubjectStore.PathFor(archived));
        Assert.Equal("archived", Assert.Single(service.CollectRefreshCards(now.AddMinutes(4), out _)).Card.Id);
        service.PathChanged(Path.Combine(archived, "logs", "cli-output.log"));
        Assert.Empty(service.CollectRefreshCards(now.AddMinutes(4), out _));

        service.Refresh(); // Hub settings and bulk changes request reconciliation.
        Assert.Equal(2, service.CollectRefreshCards(now.AddMinutes(5), out var bulk).Count);
        Assert.True(bulk);
        Assert.Equal(2, service.CollectRefreshCards(now.AddDays(1), out var nightly).Count);
        Assert.True(nightly);
        Assert.Empty(service.CollectRefreshCards(now.AddDays(1).AddMinutes(1), out _));
    }

    [Fact]
    public async Task PathRefreshUsesTheCurrentFolderAndKeepsDuplicateIdsProjectScoped()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var cache = factory.Services.GetRequiredService<TaskIndexCache>();
        var service = factory.Services.GetRequiredService<DispatchForecastService>();
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var original = Assert.Single(service.CollectRefreshCards(now, out _)).Card;
        var otherWatch = Path.Combine(_root, "other-cards");
        var other = original with
        {
            WatchPath = otherWatch, ProjectName = "Other", TaskKey = TaskIdentity.CreateKey(otherWatch, original.Id),
            FolderPath = Path.Combine(otherWatch, TaskStates.Ready, original.Id),
        };
        cache.PublishCore(other);
        service.PathChanged(Path.Combine(other.FolderPath, "prompt.md"));
        Assert.Equal(other.TaskKey, Assert.Single(service.CollectRefreshCards(now, out _)).Card.TaskKey);

        var moved = original with { State = TaskStates.Archive, FolderPath = Path.Combine(Watch, TaskStates.Archive, original.Id) };
        cache.PublishCore(moved, original.FolderPath);
        service.PathChanged(Path.Combine(original.FolderPath, "prompt.md"));
        service.PathChanged(Path.Combine(Watch, TaskStates.Ready, "absent", "prompt.md"));
        service.PathChanged(Path.Combine(Path.GetPathRoot(Watch)!, AgentStudio.Pipeline.ReviewSubjectStore.FileName));
        Assert.Empty(service.CollectRefreshCards(now, out _));
        service.PathChanged(AgentStudio.Pipeline.ReviewSubjectStore.PathFor(moved.FolderPath));
        Assert.Equal(moved.FolderPath, Assert.Single(service.CollectRefreshCards(now, out _)).Card.FolderPath);
    }

    [Fact]
    public async Task HubSettingSupportsDefaultsOverrideEmptyAndResetAndRejectsTraversal()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        const string route = "/api/projects/Forecast%20Project/dispatch/hub-files";
        Assert.Equal(TouchForecast.DefaultHubFiles, await client.GetFromJsonAsync<string[]>(route));
        var response = await client.PutAsJsonAsync(route, new DispatchHubFilesRequest(["src/shared.cs"]));
        response.EnsureSuccessStatusCode();
        Assert.Equal(new[] { "src/shared.cs" }, await client.GetFromJsonAsync<string[]>(route));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(route, new DispatchHubFilesRequest(["../bad.cs"]))).StatusCode);
        (await client.PutAsJsonAsync(route, new DispatchHubFilesRequest([]))).EnsureSuccessStatusCode();
        Assert.Empty((await client.GetFromJsonAsync<string[]>(route))!);
        (await client.PutAsJsonAsync(route, new DispatchHubFilesRequest(null))).EnsureSuccessStatusCode();
        Assert.Equal(TouchForecast.DefaultHubFiles, await client.GetFromJsonAsync<string[]>(route));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
