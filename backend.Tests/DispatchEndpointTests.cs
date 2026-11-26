using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Dispatch;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: index dates are stored evidence; refresh dates are explicit observations passed to the collector, never compared with the wall clock.

public sealed class DispatchEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dispatch-api-" + Guid.NewGuid().ToString("N"));
    private string Watch => Path.Combine(_root, "cards");

    private WebApplicationFactory<Program> Factory(TimeProvider? clock = null)
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
            builder.ConfigureTestServices(services =>
            {
                TestHostFactory.RemoveBackgroundHostedServices(services);
                if (clock is not null) { services.RemoveAll<TimeProvider>(); services.AddSingleton(clock); }
            });
        });
    }

    [Fact]
    public async Task Same_card_guard_persists_a_single_importance_70_fallback_and_recovers_its_feed()
    {
        var at = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(at.AddHours(2));
        await using var factory = Factory(clock);
        using var client = factory.CreateClient();
        var services = factory.Services;
        var scanner = services.GetRequiredService<TaskScannerService>();
        var mutations = services.GetRequiredService<TaskMutationService>();
        var cards = new List<TaskInfo> { scanner.FindJob("sample", Watch)! };
        for (var i = 1; i < 20; i++)
        {
            var id = mutations.CreateJob(new() { Title = "Guard evidence " + i, WatchPath = Watch, Agent = "codex", TargetState = TaskStates.Ready });
            cards.Add(scanner.FindJob(id!, Watch)!);
        }
        var project = cards[0].ProjectName;
        services.GetRequiredService<ProjectSettingsService>().SetDispatchMode(project, "live");
        var store = services.GetRequiredService<DispatchStore>();
        var timeline = services.GetRequiredService<AgentStudio.Tasks.TimelineLog>();
        var baseline = AgentStudio.OrchestrationDecisions.DispatchPlanPolicy.Plan(new(project, at,
            cards.Select((c, i) => new AgentStudio.OrchestrationDecisions.DispatchReadyCard(c.Key ?? c.TaskKey, 999, at,
                [], true, new(["file-" + i + ".cs"], ["file-" + i], AgentStudio.OrchestrationDecisions.DispatchForecastConfidence.High), [])).ToArray(),
            [], 1, AgentStudio.OrchestrationDecisions.DispatchLedgerSummary.Empty, [], Persona: "queue-keeper"),
            AgentStudio.OrchestrationDecisions.DispatchStrategies.Starter(AgentStudio.OrchestrationDecisions.DispatchStrategies.LaneEntry));
        store.AppendShadow(new("shadow", "baseline", "digest", at, "local", baseline));
        store.AppendShadow(new("live", "area", "digest", at, "remote-claim", baseline with { Persona = "area-warden" }, false));
        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i]; var key = card.Key ?? card.TaskKey;
            var forecast = new TouchForecastV1(["file-" + i + ".cs"], ["file-" + i + ".cs"], ["file-" + i], [], "high", "index")
                { ReadyEntryAt = at.UtcDateTime };
            store.Write(project, card.TaskKey, forecast with { Deliveries = [new("result-" + i, "base", at.AddMinutes(30),
                forecast.ForecastFiles, forecast.Modules, new(1, 1), new(1, 1)) { Forecast = forecast }] });
            timeline.Append(card.FolderPath, new TimelineEvent { Ts = at.AddHours(1).AddMinutes(i).UtcDateTime,
                Kind = TimelineEventKinds.IntegrationSucceeded });
            store.AppendRunDecision(project, new("area", key, at.AddMinutes(i + 1), "remote-claim", true, "admit", null));
            store.BindRun(project, key, "area", "attempt-" + i);
            if (i >= 18)
                store.AppendConflict(new("conflict-" + i, project, key, "merge", "base", "tip", ["shared.cs"], [cards[0].Key ?? cards[0].TaskKey], at.AddHours(1))
                { DispatchDecisionIds = new Dictionary<string, string?> { [key] = "area", [cards[0].Key ?? cards[0].TaskKey] = "area" } });
        }
        Assert.NotNull(store.AcquireLease(project, "area-warden", 0, clock.GetUtcNow(), TimeSpan.FromHours(1), true, true, true));
        var scheduler = services.GetRequiredService<DispatchLeaseScheduler>();
        var lease = scheduler.Refresh(project, "guard-wave")!;
        Assert.Equal("queue-keeper", lease.Persona);
        Assert.Equal("guard-fallback", lease.Reason);
        Assert.NotNull(lease.Rollout!.Guard);
        var registered = services.GetRequiredService<AgentStudio.Registry.ProjectRegistry>().FindByIdOrDisplayName(project)!;
        var items = services.GetRequiredService<AgentStudio.Personas.PersonaItemStore>();
        Assert.Equal(70, Assert.Single(items.List(registered.WorkspaceId)).Importance);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(lease.Rollout.Guard.Id, scheduler.Refresh(project, "guard-wave")!.Rollout!.Guard!.Id);
        Assert.Single(items.List(registered.WorkspaceId));
        // A restart sees the persisted guard and the same feed item rather than making a new intervention.
        Assert.Equal(lease.Rollout.Guard.Id, new DispatchStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TaskRepository"] = _root }).Build()).ReadLease(project)!.Rollout!.Guard!.Id);
    }

    [Fact]
    public async Task Lease_scheduler_is_registered_and_both_pickup_paths_use_the_platform_baseline()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var services = factory.Services;
        var index = services.GetRequiredService<TaskIndexCache>();
        index.GetSnapshot();
        var card = services.GetRequiredService<TaskScannerService>().FindJob("sample", Watch)!;
        services.GetRequiredService<ProjectSettingsService>().SetDispatchMode(card.ProjectName, "live");
        var scheduler = services.GetRequiredService<DispatchLeaseScheduler>();
        var lease = scheduler.Refresh(card.ProjectName, "wave-one")!;
        Assert.Equal("queue-keeper", lease.Persona);
        Assert.Equal("wave:wave-one", lease.Window);
        Assert.NotNull(lease.Rollout);
        var dispatcher = services.GetRequiredService<DispatcherShadowService>();
        foreach (var path in new[] { "local", "remote-claim" })
        {
            var plan = dispatcher.RecordPick(card with { WaveId = "wave-one" }, [], [], path);
            Assert.NotNull(plan);
            Assert.Equal("queue-keeper", plan.Plan.Persona);
            Assert.Equal("wave:wave-one", plan.Window);
            // This card is not a completed source wave; the wave gate remains hard.
            Assert.Equal("source-wave-releasing", dispatcher.Pickup(card with { WaveId = "wave-one" }, plan).Reason);
        }
        var store = services.GetRequiredService<DispatchStore>();
        Assert.True(store.VerifyDispatcherLog(card.ProjectName));
        Assert.Contains(store.ReadShadow(card.ProjectName), p => p.Shadow && p.Plan.Persona == "queue-keeper");
        Assert.NotNull(scheduler.Refresh(card.ProjectName, action: "stop", expectedFence: store.ReadLease(card.ProjectName)!.Fence, actor: "test-owner"));
        Assert.True(store.ReadLease(card.ProjectName)!.Rollout!.Stopped);
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("stop")]
    [InlineData("undo-guard")]
    public async Task Lease_controls_refuse_non_human_credentials(string action)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var response = await client.PutAsJsonAsync("/api/projects/Forecast%20Project/dispatch/review/lease", new
            { action, fence = 0, persona = "queue-keeper" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Batch_creation_assigns_a_wave_and_retains_cause_paths_in_backlog()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var response = await client.PostAsJsonAsync("/api/tasks/batch", new
        {
            cards = Enumerable.Range(0, 6).Select(i => new
            {
                title = "Source finding " + i, watchPath = Watch, creationSource = "playthrough",
                citedPaths = new[] { "backend/Features/Tasks/A.cs" }, targetState = TaskStates.Ready,
                cliType = "codex", model = "gpt-6.2", modelExplicit = true, thinkingLevelExplicit = true,
            }).ToArray(),
        });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var created = json.GetProperty("created").EnumerateArray().ToArray();
        Assert.Equal(6, created.Length);
        var wave = created[0].GetProperty("waveId").GetString();
        Assert.StartsWith("wave-", wave);
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        foreach (var item in created)
        {
            Assert.Equal(wave, item.GetProperty("waveId").GetString());
            var card = scanner.FindJob(item.GetProperty("card").GetProperty("id").GetString()!, Watch)!;
            Assert.Equal(TaskStates.Backlog, card.State);
            Assert.Equal(wave, card.WaveId);
            Assert.Equal(new[] { "backend/Features/Tasks/A.cs" }, card.CitedPaths);
        }
        var invalid = await client.PostAsJsonAsync("/api/tasks", new
            { title = "Invalid citation", project = "Forecast Project", citedPaths = new[] { "../secret" } });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Theory]
    [InlineData("live", false, 2)]
    [InlineData("live", true, 1)]
    [InlineData("shadow", false, 0)]
    public async Task Wave_planning_records_every_member_before_release_and_stops_on_lease_loss(string mode, bool loseLease, int moves)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var services = factory.Services;
        var scanner = services.GetRequiredService<TaskScannerService>();
        var settings = services.GetRequiredService<ProjectSettingsService>();
        settings.SetDispatchMode("Forecast Project", mode);
        var mutations = services.GetRequiredService<TaskMutationService>();
        var ids = Enumerable.Range(0, 2).Select(i => mutations.CreateJob(new()
        {
            Title = "Wave member " + i, WatchPath = Watch, CreationSource = "playthrough",
            WaveId = "complete-wave", Agent = "codex", TargetState = TaskStates.Ready,
        })).ToArray();
        Assert.All(ids, id => Assert.NotNull(id));
        var cards = ids.Select(id => scanner.FindJob(id!, Watch)!).ToArray();
        Assert.All(cards, c => Assert.Equal(TaskStates.Backlog, c.State));
        var index = services.GetRequiredService<TaskIndexCache>();
        index.GetSnapshot();
        var projects = services.GetRequiredService<AgentStudio.Registry.ProjectRegistry>();
        var project = projects.FindByIdOrDisplayName("Forecast Project")!;
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Security:Profile"] = "networked", ["TaskRepository"] = Path.Combine(_root, "wave-security"), ["Secrets:KeyFile"] = Path.Combine(_root, "wave.kek") }).Build();
        var security = new AgentStudio.Security.AccessSecurityStore(config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentStudio.Security.AccessSecurityStore>.Instance, clock);
        var bootstrap = security.Bootstrap(new("wave.owner", "correct horse battery staple!", null));
        var owner = security.AuthenticateSession(bootstrap.SessionToken)!;
        var secret = security.BeginTotpEnrollment(owner);
        security.ConfirmTotpEnrollment(owner, AgentStudio.Security.SecondFactorCodes.CurrentTotp(secret, clock.GetUtcNow().UtcDateTime));
        clock.Advance(TimeSpan.FromSeconds(30));
        security.VerifyStepUp(owner, AgentStudio.Security.SecondFactorCodes.CurrentTotp(secret, clock.GetUtcNow().UtcDateTime));
        var activations = new AgentStudio.Personas.PersonaActivationStore(null, new(), security, _ => project.WorkspaceId, clock);
        var activation = activations.Activate(project.WorkspaceId, "area-warden", new(), owner);
        clock.SetUtcNow(new DateTimeOffset(activation.ShadowUntil, TimeSpan.Zero).AddMinutes(1));
        var store = services.GetRequiredService<DispatchStore>();
        var lease = store.AcquireLease(project.DisplayName, "area-warden", 0, clock.GetUtcNow(), TimeSpan.FromHours(1), true, true, true)!;
        foreach (var card in cards)
            store.Write(card.ProjectName, card.TaskKey, new(["a.cs"], ["a.cs"], ["area"], [], "high", "v1"));
        var dispatchers = new DispatcherShadowService(store, activations, projects, index, settings, clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DispatcherShadowService>.Instance);
        var api = new DispatchRecutTests.MemoryApi(cards.Select(c => new DispatchRecutOriginal(c, "Finding", [c.Key!])));
        api.AfterMove = () =>
        {
            var release = store.ReadWaveRelease(project.DisplayName, "complete-wave")!;
            Assert.False(release.Complete);
            Assert.All(cards, c => Assert.Contains(c.Key!, release.Cards));
            var justReleased = api.Cards.Values.First(c => c.Info.State == TaskStates.Ready).Info;
            Assert.Equal("source-wave-releasing", dispatchers.Pickup(justReleased, store.ReadLive(project.DisplayName)!.Record).Reason);
            if (loseLease) store.ReleaseLease(project.DisplayName, lease.Persona, lease.Fence, clock.GetUtcNow());
        };
        var service = new DispatchWaveService(scanner, settings, store, dispatchers, null!, projects, clock);
        var result = await service.PlanAsync(project.Id, "complete-wave", api, default);
        Assert.Equal(moves, result.Released.Count);
        if (mode == "live") Assert.Equal(!loseLease, store.ReadWaveRelease(project.DisplayName, "complete-wave")!.Complete);
        Assert.Equal(mode == "shadow" ? "shadow" : loseLease ? "interrupted" : "planned", result.State);
        Assert.All(cards, c => Assert.Equal(999, c.Order));
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

    [Fact]
    public async Task Review_reads_integrated_real_file_cohort_and_validates_window_and_revision()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        factory.Services.GetRequiredService<TaskIndexCache>().GetSnapshot();
        var card = factory.Services.GetRequiredService<TaskScannerService>().FindJob("sample", Watch)!;
        var at = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var log = factory.Services.GetRequiredService<AgentStudio.Tasks.TimelineLog>();
        log.Append(card.FolderPath, new TimelineEvent { Ts = at.AddHours(-4).UtcDateTime,
            Kind = TimelineEventKinds.LaneChanged,
            Details = new() { ["to"] = TaskStates.Ready } });
        log.Append(card.FolderPath, new TimelineEvent { Ts = at.UtcDateTime,
            Kind = TimelineEventKinds.IntegrationSucceeded });
        factory.Services.GetRequiredService<DispatchStore>().Write(card.ProjectName, card.TaskKey,
            new(["a.cs"], ["a.cs"], ["a.cs"], [], "high", "index")
            {
                ReadyEntryAt = at.AddHours(-4).UtcDateTime,
                Deliveries = [new("result", "base", at.AddHours(-1), ["a.cs", "b.cs"], ["a.cs", "b.cs"], new(1, .5), new(1, .5))],
            });
        const string route = "/api/projects/Forecast%20Project/dispatch/review";
        var result = (await client.GetFromJsonAsync<DispatchReviewProjection>(route + "?window=2026-11-20/2026-11-21&weights=w1"))!;
        Assert.Equal(5, result.Personas.Count);
        Assert.Contains(result.Personas, r => r.Raw.Persona == "queue-keeper");
        Assert.All(result.Personas, r =>
        {
            Assert.Equal(1, r.Raw.IntegratedCards);
            Assert.Equal("too few cards", r.Status);
            Assert.Null(r.Value);
            Assert.Equal(2.0 / 3, r.Raw.FileF1);
            Assert.Null(r.Raw.Criteria["tokens"]);
            Assert.True(r.Raw.TokensEstimated);
        });
        Assert.All(result.Competition, c => Assert.False(c.Evaluation.ChampionEligible));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route + "?weights=w999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route + "?window=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/projects/missing/dispatch/review")).StatusCode);
        var outside = (await client.GetFromJsonAsync<DispatchReviewProjection>(route + "?window=2026-11-21/2026-11-22"))!;
        Assert.All(outside.Personas, r => Assert.Equal(0, r.Raw.IntegratedCards));
    }

    [Fact]
    public async Task Review_weights_require_human_owner_and_preserve_revision_history()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        await using var factory = Factory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:Profile"] = "networked", ["AllowedHosts"] = "studio.test",
                ["Secrets:KeyFile"] = Path.Combine(_root + "-keys", "test.kek"),
                ["Secrets:KeyEnvironmentVariable"] = "AGT3997_UNSET_" + Guid.NewGuid().ToString("N"),
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });
        var security = factory.Services.GetRequiredService<AgentStudio.Security.AccessSecurityStore>();
        var bootstrap = security.Bootstrap(new("review.owner", "correct horse battery staple!", null));
        var owner = security.AuthenticateSession(bootstrap.SessionToken)!;
        var secret = security.BeginTotpEnrollment(owner);
        security.ConfirmTotpEnrollment(owner, AgentStudio.Security.SecondFactorCodes.CurrentTotp(secret, clock.GetUtcNow().UtcDateTime));
        var workspace = factory.Services.GetRequiredService<AgentStudio.Registry.WorkspaceRegistry>().EnsureDefaultWorkspace(clock).Id;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        client.DefaultRequestHeaders.Add("Origin", "https://studio.test");
        client.DefaultRequestHeaders.Add("Cookie", AgentStudio.Security.AccessSecurityStore.SessionCookieName + "=" + bootstrap.SessionToken);
        client.DefaultRequestHeaders.Add("X-CSRF-Token", bootstrap.CsrfToken);
        var route = $"/api/workspaces/{workspace}/dispatch/review/weights";
        var request = new DispatchWeightsRequest(DispatchWeightRevision.Default.Weights, .9, "Calibrate gate");
        var saved = await client.PutAsJsonAsync(route, request);
        saved.EnsureSuccessStatusCode();
        var revision = (await saved.Content.ReadFromJsonAsync<DispatchWeightRevision>())!;
        Assert.Equal("w2", revision.Id);
        Assert.Equal(owner.User.Id, revision.Author);
        Assert.Equal(.9, revision.Gate);
        Assert.Equal(2, factory.Services.GetRequiredService<AgentStudio.Registry.WorkspaceSettingsService>().Get(workspace).DispatchReviewWeights.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(route, request with { Gate = .2 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/workspaces/missing/dispatch/review/weights", request)).StatusCode);
        const string leaseRoute = "/api/projects/Forecast%20Project/dispatch/review/lease";
        var projectSettings = factory.Services.GetRequiredService<ProjectSettingsService>();
        projectSettings.SetDispatchMode("Forecast Project", "live");
        var lease = factory.Services.GetRequiredService<DispatchLeaseScheduler>().Refresh("Forecast Project")!;
        var pinned = await client.PutAsJsonAsync(leaseRoute, new DispatchLeaseControlRequest("pin", lease.Fence, "queue-keeper"));
        pinned.EnsureSuccessStatusCode();
        var pinnedLease = (await pinned.Content.ReadFromJsonAsync<DispatchLeaseV1>())!;
        Assert.Equal(owner.User.Id, pinnedLease.Actor);
        Assert.Equal("queue-keeper", pinnedLease.Rollout!.Pinned);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(leaseRoute, new DispatchLeaseControlRequest("stop", lease.Fence))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(leaseRoute, new DispatchLeaseControlRequest("bypass", pinnedLease.Fence))).StatusCode);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        var refused = await client.PutAsJsonAsync(route, request);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task Review_observed_admission_includes_coding_and_review_tokens_per_accepted_card()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        factory.Services.GetRequiredService<TaskIndexCache>().GetSnapshot();
        var card = factory.Services.GetRequiredService<TaskScannerService>().FindJob("sample", Watch)!;
        var key = card.Key ?? card.TaskKey;
        var at = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var store = factory.Services.GetRequiredService<DispatchStore>();
        var plan = AgentStudio.OrchestrationDecisions.DispatchPlanPolicy.Plan(new(card.ProjectName, at,
            [new(key, 999, at.AddHours(-4), [], true, AgentStudio.OrchestrationDecisions.DispatchForecast.Unknown, [])], [], 1,
            AgentStudio.OrchestrationDecisions.DispatchLedgerSummary.Empty, []),
            AgentStudio.OrchestrationDecisions.DispatchStrategies.Starter(AgentStudio.OrchestrationDecisions.DispatchStrategies.LaneEntry));
        store.AppendShadow(new("evaluation", "admission", "facts", at.AddHours(-3), "local", plan, false));
        store.AppendRunDecision(card.ProjectName, new("admission", key, at.AddHours(-3), "local", true, "admit", null, "attempt", true));
        store.Write(card.ProjectName, card.TaskKey, new(["a.cs"], ["a.cs"], ["a.cs"], [], "high", "index")
        {
            ReadyEntryAt = at.AddHours(-4).UtcDateTime,
            Deliveries = [new("result", "base", at.AddHours(-1), ["a.cs"], ["a.cs"], new(1, 1), new(1, 1))],
        });
        var log = factory.Services.GetRequiredService<AgentStudio.Tasks.TimelineLog>();
        log.Append(card.FolderPath, new TimelineEvent { Ts = at.UtcDateTime, Kind = TimelineEventKinds.IntegrationSucceeded });
        log.Append(card.FolderPath, new TimelineEvent { Ts = at.AddHours(1).UtcDateTime, Kind = TimelineEventKinds.LaneChanged,
            Details = new() { ["to"] = TaskStates.Completed } });
        var bridge = factory.Services.GetRequiredService<AgentMessageBusBridge>();
        await bridge.EmitTokenUsageAsync(card.ProjectName, card.Id, "coder", "coding",
            new OrchestratorTokenUsage { Model = "test-model", InputTokens = 100, OutputTokens = 10 }, createdAt: at.AddHours(-2).UtcDateTime);
        await bridge.EmitTokenUsageAsync(card.ProjectName, card.Id, "aspect:review", "review",
            new OrchestratorTokenUsage { Model = "test-model", InputTokens = 200, OutputTokens = 20 }, createdAt: at.AddHours(-1).UtcDateTime);
        var result = (await client.GetFromJsonAsync<DispatchReviewProjection>(
            "/api/projects/Forecast%20Project/dispatch/review?window=2026-11-20/2026-11-21"))!;
        var observed = Assert.Single(result.ObservedPersonas);
        Assert.Equal("queue-keeper", observed.Persona);
        Assert.Equal(1, observed.AcceptedCards);
        Assert.Equal(330, observed.Criteria["tokens"]);
        Assert.Equal(0, observed.Criteria["conflicts"]);
        Assert.Equal(4, observed.Criteria["lead"]);
        Assert.False(observed.TokensEstimated);
        Assert.Null(observed.Criteria["rework"]);
        Assert.Null(observed.Criteria["parallelism"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (Directory.Exists(_root + "-keys")) Directory.Delete(_root + "-keys", true);
    }
}
