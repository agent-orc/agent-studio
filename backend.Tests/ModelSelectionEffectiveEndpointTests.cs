using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Cli;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelSelectionEffectiveEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "model-effective-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Pace_write_validates_audits_and_changes_the_next_claim_plan()
    {
        const string path = "/api/model-selection/policy/pace";
        var clock = new MutableClock(new DateTime(2030, 11, 4, 12, 0, 0, DateTimeKind.Utc));
        var now = clock.GetUtcNow().UtcDateTime;
        var settings = new Dictionary<string, string?> { ["TaskRepository"] = _root,
            ["Security:Profile"] = "networked", ["AllowedHosts"] = "studio.test", ["PublicDemo:Enabled"] = "false" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        Directory.CreateDirectory(_root);
        new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance).Write([
            new QuotaSnapshot { CliType = "codex", SubscriptionKey = "account", FetchedAt = now,
                Windows = [new() { Label = "Weekly", UsedPct = 65, ResetAt = now.AddDays(3) }] },
        ]);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Test").ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(settings))
                .ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock))));
        var options = new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://studio.test"), HandleCookies = false };
        using var client = factory.CreateClient(options);
        using var runner = factory.CreateClient(options);
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        security.Bootstrap(new BootstrapRequest("owner", "correct horse battery staple!", null));
        var login = security.Login("owner", "correct horse battery staple!", "pace|test");
        client.DefaultRequestHeaders.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={login.SessionToken}");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", login.CsrfToken);
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        runner.DefaultRequestHeaders.Authorization = new("Bearer", security.EnrollRunner(security.CreateEnrollment(
            new RunnerEnrollmentRequest("pace-host", [RunnerScopes.Events], null, null)).Code).Secret);

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var claim = new ModelRouteAdmissionRequest(new SelectionRequest { Consumer = SelectionConsumers.Coding,
            Pins = new("codex", "gpt-6-sol", "high"), IsExplicitPin = true }, [new("codex", "gpt-6-sol", "high")], []);
        async Task<SelectionResult> Plan()
        {
            using var response = await runner.PostAsJsonAsync("/api/model-selection/plan", claim);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<SelectionResult>(json))!;
        }

        Assert.Equal(SelectionOutcome.Wait, (await Plan()).Outcome);
        foreach (var (target, mode, time) in new[] { ("other/Weekly", "steady", ""), ("codex/typo", "steady", ""),
                     ("codex/Weekly", "unknown", ""), ("codex/Weekly", "useBy", "tomorrow"), ("codex/Weekly", "useBy", "2030-11-05") })
        {
            var invalid = await client.PutAsJsonAsync($"{path}/{target}", new { mode, useBy = time.Length == 0 ? null : time });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var denied = await runner.PutAsJsonAsync(path + "/codex/Weekly", new { mode = "unpaced" });
        Assert.Contains(denied.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        var set = await client.PutAsJsonAsync(path + "/codex/weekly", new { mode = "useBy", useBy = now.AddHours(12) });
        set.EnsureSuccessStatusCode();
        var rows = await client.GetFromJsonAsync<QuotaPaceEndpoints.WindowStatus[]>(path);
        var row = Assert.Single(rows!, r => r.CliType == "codex" && r.WindowLabel.Equals("Weekly", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(now.AddHours(12), row.Pace.UseBy?.UtcDateTime);
        var selected = await Plan();
        Assert.Equal(SelectionOutcome.Primary, selected.Outcome);
        var receipt = factory.Services.GetRequiredService<ModelSelectionLedger>().Query(eventId: selected.LedgerId).Events.First();
        Assert.Equal("useBy", receipt.Quota?.WindowPaces?["Weekly"].Mode);
        var audit = Assert.Single(factory.Services.GetRequiredService<ControlPlaneAuditLog>()
            .ReadNewest(e => e.Stream == ControlPlaneAuditStreams.Operator && e.Operation == "quota-pace-set", 10));
        var recorded = audit.Details!.Value.Deserialize<WindowPaceSetting>();
        Assert.True(recorded!.Matches("codex", "Weekly"));
        Assert.Equal(row.Pace, recorded.Pace);
        (await client.PutAsJsonAsync(path + "/codex/Weekly", new { mode = "steady" })).EnsureSuccessStatusCode();
        Assert.Equal(SelectionOutcome.Wait, (await Plan()).Outcome);
    }

    [Fact]
    public async Task Installation_drill_uses_fake_quotas_and_leaves_no_selection_receipts()
    {
        Directory.CreateDirectory(_root);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Test").ConfigureAppConfiguration((_, settings) =>
                settings.AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })));
        var policy = factory.Services.GetRequiredService<ModelSelectionPolicyStore>();
        Assert.Equal(10, policy.Get().ShortStepReservePoints);
        var ledger = factory.Services.GetRequiredService<ModelSelectionLedger>();
        var before = ledger.Query().Events.Count;
        using var client = factory.CreateClient();
        var report = await client.GetFromJsonAsync<RoutingDrillReport>("/api/model-selection/drill",
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        Assert.True(report!.DryRun);
        Assert.True(report.Passed, string.Join("\n", report.Cases.Where(item => !item.Passed).Select(item => item.Selection.Reason)));
        Assert.NotEmpty(report.MissingEquivalence);
        if (Environment.GetEnvironmentVariable("JOB_RESULTS_DIR") is { Length: > 0 } results)
            File.WriteAllText(Path.Combine(results, "routing-drill--test-installation.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Assert.Equal(before, ledger.Query().Events.Count);
    }

    [Theory]
    [InlineData("quota-fallback", 98, false, "switched", "quota-cap")]
    [InlineData("quota-wait", 98, true, "waiting", "pin")]
    [InlineData("host-rejection", 20, false, "refused", "host-capability")]
    public async Task SelectionServiceDecisionsAppearInEventsApi(
        string scenario, double used, bool hardPin, string kind, string rule)
    {
        var root = Path.Combine(_root, scenario);
        Directory.CreateDirectory(root);
        var clock = new MutableClock(new DateTime(2030, 11, 4, 12, 0, 0, DateTimeKind.Utc));
        var now = clock.GetUtcNow().UtcDateTime;
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TaskRepository"] = root }).Build();
        new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance).Write([
            new QuotaSnapshot { CliType = "claude", FetchedAt = now,
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = used,
                    ResetAt = now.AddDays(3) }] },
            new QuotaSnapshot { CliType = "codex", FetchedAt = now,
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 10,
                    ResetAt = now.AddDays(3) }] },
        ]);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Test").ConfigureAppConfiguration((_, settings) =>
                settings.AddInMemoryCollection(new Dictionary<string, string?>
                    { ["TaskRepository"] = root, ["PublicDemo:Enabled"] = "false" }))
                .ConfigureTestServices(services => services.Replace(
                    ServiceDescriptor.Singleton<TimeProvider>(clock))));
        factory.Services.GetRequiredService<CliQuotaFallbackService>().Set(new CliModelRouteProfile
        {
            CliType = "claude", PrimaryModel = "claude-sonnet-5", FallbackCliType = "codex",
            FallbackModel = "gpt-6-sol", FallbackThinkingLevel = "high",
        });
        var request = new SelectionRequest
        {
            Consumer = SelectionConsumers.Coding, Project = "demo", TaskKey = "DEMO-1",
            IsExplicitPin = true,
            Pins = new SelectionPins("claude", "claude-sonnet-5", "high",
                hardPin ? SelectionPinMode.Hard : SelectionPinMode.Preference),
            Host = new SelectionHost("host-a"),
        };
        var decision = factory.Services.GetRequiredService<ModelSelectionService>().Select(
            new ModelSelectionCall(request, "remote-coding-claim",
                HostAccepts: scenario == "host-rejection" ? (_, _) => false : null));
        Assert.NotNull(decision.Result.LedgerId);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var events = await client.GetFromJsonAsync<JsonElement>(
            $"/api/model-selection/events?provider=claude&project=demo&consumer=coding&rule={rule}&from=claude-sonnet-5");
        var entry = Assert.Single(events.GetProperty("events").EnumerateArray());
        Assert.Equal(decision.Result.LedgerId, entry.GetProperty("id").GetString());
        Assert.Equal(kind, entry.GetProperty("kind").GetString());
        Assert.Equal("claude", entry.GetProperty("from").GetProperty("cliType").GetString());
        Assert.Equal("high", entry.GetProperty("from").GetProperty("thinkingLevel").GetString());
        if (kind == "switched")
        {
            Assert.Equal("codex", entry.GetProperty("to").GetProperty("cliType").GetString());
            Assert.Equal("gpt-6-sol", entry.GetProperty("to").GetProperty("model").GetString());
            Assert.Equal("medium", entry.GetProperty("to").GetProperty("thinkingLevel").GetString());
        }
        Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("reason").GetString()));
    }

    [Fact]
    public void LedgerWriteFailureDoesNotChangeQuotaFallbackDecision()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TaskRepository"] = _root }).Build();
        var now = new DateTime(2030, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var clock = new MutableClock(now);
        var cache = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        cache.Write([
            new QuotaSnapshot { CliType = CliTypes.Claude, FetchedAt = now,
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 98, ResetAt = now.AddDays(3) }] },
            new QuotaSnapshot { CliType = CliTypes.Codex, FetchedAt = now,
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 10, ResetAt = now.AddDays(3) }] },
        ]);
        var quota = new QuotaService(NullLogger<QuotaService>.Instance, [], config, cache);
        var caps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
        var fallback = new CliQuotaFallbackService(config, NullLogger<CliQuotaFallbackService>.Instance, new ModelEquivalenceCatalog(TokenTestServices.Pricing()));
        fallback.Set(new CliModelRouteProfile
        {
            CliType = CliTypes.Claude, PrimaryModel = ModelIds.ClaudeSonnet5,
            FallbackCliType = CliTypes.Codex, FallbackModel = ModelIds.Gpt6Sol,
            FallbackThinkingLevel = "high",
        });
        var wait = new CliQuotaWaitPolicyService(NullLogger<CliQuotaWaitPolicyService>.Instance, config);
        var projects = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var service = new ModelSelectionService(quota, caps, fallback, wait, projects,
            timeProvider: clock, ledger: new FailingSelectionLedger());

        var decision = service.Select(new ModelSelectionCall(new SelectionRequest
        {
            Consumer = SelectionConsumers.Coding,
            Pins = new SelectionPins(CliTypes.Claude, ModelIds.ClaudeSonnet5, "high"),
        }, "remote-coding-claim"));

        Assert.Equal(QuotaAdmissionOutcome.LaunchFallback, decision.Admission.Outcome);
        Assert.Equal(CliTypes.Codex, decision.Admission.CliType);
        Assert.Equal(ModelIds.Gpt6Sol, decision.Admission.Model);
        Assert.Null(decision.Result.LedgerId);
    }

    [Fact]
    public async Task CostGuardWaitUsesTheSameDurableEventsRoute()
    {
        Directory.CreateDirectory(_root);
        var clock = new MutableClock(new DateTime(2030, 11, 4, 12, 0, 0, DateTimeKind.Utc));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Test").ConfigureAppConfiguration((_, settings) =>
                settings.AddInMemoryCollection(new Dictionary<string, string?>
                    { ["TaskRepository"] = _root, ["PublicDemo:Enabled"] = "false" }))
                .ConfigureTestServices(services => services.Replace(
                    ServiceDescriptor.Singleton<TimeProvider>(clock))));
        var request = new SelectionRequest
        {
            Consumer = SelectionConsumers.Coding, Project = "demo", TaskKey = "DEMO-2",
            Pins = new SelectionPins("claude", "claude-opus-5", "high"),
        };
        var route = new SelectionRoute("claude", "claude-opus-5", "high");
        var decision = new SelectionResult
        {
            Outcome = SelectionOutcome.Wait, Route = route, Primary = route,
            Rule = SelectionRules.CostGuard, Reason = "waiting: fallback price exceeds the cost guard",
        };
        var entry = SelectionLedgerEntry.For(request, decision, "remote-coding-claim",
            ModelSelectionEventId.New(), clock.GetUtcNow().UtcDateTime)!;
        factory.Services.GetRequiredService<IModelSelectionLedger>().Record(entry);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var events = await client.GetFromJsonAsync<JsonElement>(
            "/api/model-selection/events?project=demo&rule=cost-guard&from=claude-opus-5");
        var saved = Assert.Single(events.GetProperty("events").EnumerateArray());
        Assert.Equal(entry.Id, saved.GetProperty("id").GetString());
        Assert.Equal("waiting", saved.GetProperty("kind").GetString());
        Assert.Equal("high", saved.GetProperty("from").GetProperty("thinkingLevel").GetString());
    }

    [Fact]
    public async Task SelectionEventsRouteFiltersNewestFirst()
    {
        Directory.CreateDirectory(_root);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Test")
                .ConfigureAppConfiguration((_, settings) => settings.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["TaskRepository"] = _root, ["PublicDemo:Enabled"] = "false" })));
        var ledger = factory.Services.GetRequiredService<ModelSelectionLedger>();
        var first = new ModelSelectionEvent
        {
            Id = ModelSelectionLedger.NewId(), Kind = "switched", Consumer = "remote-claim",
            Project = "demo", Rule = "quota-cap", Reason = "Weekly cap",
            From = new("claude", "opus-5", "high"), To = new("codex", "gpt-6-sol", "high"),
        };
        Assert.True(ledger.Append(first));
        Assert.True(ledger.Append(first with { Id = ModelSelectionLedger.NewId(), At = first.At.AddSeconds(1), Consumer = "one-shot" }));
        Assert.True(ledger.Append(first with { Id = ModelSelectionLedger.NewId(), At = first.At.AddSeconds(2), TaskKey = "DEMO-2" }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var filtered = await client.GetFromJsonAsync<JsonElement>(
            "/api/model-selection/events?provider=codex&project=demo&consumer=remote-claim&rule=quota-cap&from=opus-5&to=gpt-6-sol");
        Assert.Equal(2, filtered.GetProperty("events").GetArrayLength());
        Assert.Equal(first.Id, filtered.GetProperty("events")[1].GetProperty("id").GetString());
        var linked = await client.GetFromJsonAsync<JsonElement>(
            $"/api/model-selection/events?taskKey=DEMO-2&eventId={ledger.Query(taskKey: "DEMO-2").Events[0].Id}");
        Assert.Equal("DEMO-2", Assert.Single(linked.GetProperty("events").EnumerateArray())
            .GetProperty("taskKey").GetString());
        var outsidePeriod = await client.GetFromJsonAsync<JsonElement>(
            $"/api/model-selection/events?since={Uri.EscapeDataString(first.At.AddDays(1).ToString("O"))}");
        Assert.Empty(outsidePeriod.GetProperty("events").EnumerateArray());
        var all = await client.GetFromJsonAsync<JsonElement>("/api/model-selection/events");
        Assert.Equal("DEMO-2", all.GetProperty("events")[0].GetProperty("taskKey").GetString());
    }

    [Fact]
    public async Task SelectionIngestionRejectsOperatorPrincipal()
    {
        Directory.CreateDirectory(_root);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, settings) => settings.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root, ["Security:Profile"] = "networked",
                    ["AllowedHosts"] = "studio.test", ["Watcher:Enabled"] = "false",
                }));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        security.Bootstrap(new BootstrapRequest("owner", "correct horse battery staple!", null));
        var login = security.Login("owner", "correct horse battery staple!", "selection|test");
        client.DefaultRequestHeaders.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={login.SessionToken}");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", login.CsrfToken);
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        using var response = await client.PostAsJsonAsync("/api/model-selection/events", new ModelSelectionEvent
            { Kind = "waiting", Consumer = "test", Rule = "quota-cap", Reason = "test" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var runner = security.EnrollRunner(security.CreateEnrollment(
            new RunnerEnrollmentRequest("selection-host", [RunnerScopes.Events], null, null)).Code);
        using var hostClient = factory.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        hostClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", runner.Secret);
        var entry = new ModelSelectionEvent
            { Kind = "waiting", Consumer = "remote-claim", Rule = "quota-cap", Reason = "test" };
        using var accepted = await hostClient.PostAsJsonAsync("/api/model-selection/events", entry);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.True((await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("added").GetBoolean());
        using var replay = await hostClient.PostAsJsonAsync("/api/model-selection/events", entry);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.False((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("added").GetBoolean());
    }

    [Fact]
    public async Task One_put_moves_12_projects_and_3_pipeline_types_then_expiry_restores_all_review_routes()
    {
        Directory.CreateDirectory(_root);
        var clock = new MutableClock(new DateTime(2030, 11, 4, 12, 0, 0, DateTimeKind.Utc));
        var reset = clock.GetUtcNow().UtcDateTime.AddHours(1);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TaskRepository"] = _root }).Build();
        new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance).Write([
            new QuotaSnapshot
            {
                CliType = CliTypes.Claude, FetchedAt = clock.GetUtcNow().UtcDateTime,
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 20, ResetAt = reset }],
            },
        ]);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Test")
                .ConfigureAppConfiguration((_, settings) => settings.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _root,
                        ["PublicDemo:Enabled"] = "false",
                        [ReviewAspectModelPolicy.CliSetting] = CliTypes.Claude,
                        [ReviewAspectModelPolicy.ModelSetting] = ModelIds.ClaudeSonnet5,
                    }))
                .ConfigureTestServices(services => services.Replace(
                    ServiceDescriptor.Singleton<TimeProvider>(clock))));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var projectSettings = factory.Services.GetRequiredService<ProjectSettingsService>();
        var selection = factory.Services.GetRequiredService<ModelSelectionEffectiveService>();
        var projects = Enumerable.Range(1, 12).Select(index => $"review-{index}").ToArray();
        var types = new[] { PipelineTypes.Task, PipelineTypes.Bug, PipelineTypes.Feature };
        foreach (var project in projects)
        foreach (var type in types)
        {
            var setting = new PipelineStepSetting
            {
                CliType = CliTypes.Claude, Model = ModelIds.ClaudeSonnet5, ThinkingLevel = "high",
            };
            projectSettings.SetPipelineStep(project, type, "aspect-code-quality", setting);
            projectSettings.SetPipelineStep(project, type, PipelineCatalogue.CodeReviewGradeStepId, setting);
            projectSettings.SetPipelineStep(project, type, "post-deep-review", setting);
        }

        static EffectiveModelSelection[] ReviewRoutes(IReadOnlyList<EffectiveModelSelection> rows) =>
            rows.Where(row => row.StepId is "aspect-code-quality" or "post-code-review-grade" or "post-deep-review")
                .ToArray();
        foreach (var project in projects)
        foreach (var type in types)
            Assert.All(ReviewRoutes(selection.Get(project, type)), row => Assert.Equal(CliTypes.Claude, row.Effective?.CliType));

        var put = await client.PutAsJsonAsync("/api/cli/quota/fallback-preference",
            new { cliType = CliTypes.Claude, preferFallback = true });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        foreach (var project in projects)
        foreach (var type in types)
        {
            var rows = ReviewRoutes(selection.Get(project, type));
            Assert.Equal(3, rows.Length);
            Assert.All(rows, row =>
            {
                Assert.Equal(CliTypes.Claude, row.Configured.CliType);
                Assert.Equal(CliTypes.Codex, row.Effective?.CliType);
                Assert.Equal("operator-switch", row.Rule);
            });
        }
        using var response = JsonDocument.Parse(await client.GetStringAsync(
            "/api/model-selection/effective?project=review-1&pipelineType=task"));
        Assert.Contains(response.RootElement.GetProperty("routes").EnumerateArray(), row =>
            row.GetProperty("stepId").GetString() == "post-deep-review"
            && row.GetProperty("effective").GetProperty("cliType").GetString() == CliTypes.Codex);

        clock.Advance(TimeSpan.FromHours(2));
        foreach (var project in projects)
        foreach (var type in types)
            Assert.All(ReviewRoutes(selection.Get(project, type)), row => Assert.Equal(CliTypes.Claude, row.Effective?.CliType));
    }

    [Fact]
    public void Clean_project_preview_matches_the_frozen_review_step_and_admission()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "prompt.md"), "Review the delivered change.");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Test").ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })));
        var services = factory.Services;
        var preview = services.GetRequiredService<ModelSelectionEffectiveService>().Get("clean-project", PipelineTypes.Task);
        var task = new TaskInfo { Id = "AGT-1", ProjectName = "clean-project", FolderPath = _root };
        var plan = services.GetRequiredService<RemoteReviewPlanBuilder>().Build(
            task, null, new ProjectSettings(), "develop", "0123456789abcdef", ["backend/Program.cs"]);
        var command = Assert.Single(plan.Commands, item => item.StepId == "aspect-code-quality");
        var row = Assert.Single(preview, item => item.StepId == command.StepId);
        var quota = services.GetRequiredService<QuotaAdmissionService>();
        var admitted = quota.Plan(new QuotaAdmissionRequest(
            command.CliType, command.Model, command.ThinkingLevel, task.ProjectName, 0,
            "remote-review-claim", SameClassOnly: true, StepId: command.StepId));
        Assert.True(admitted.ShouldLaunch, admitted.Reason);
        Assert.Equal(new EffectiveModelRoute("codex", CodexCliRelease.Gpt62Sol, "medium"), row.Effective);
        Assert.Equal(row.Effective, new EffectiveModelRoute(admitted.CliType, admitted.Model, admitted.ThinkingLevel));
        Assert.Equal(row.Effective, new EffectiveModelRoute(command.CliType!, command.Model, command.ThinkingLevel));
        var grade = Assert.Single(preview, item => item.StepId == PipelineCatalogue.CodeReviewGradeStepId);
        Assert.Equal(row.Effective, grade.Effective);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class MutableClock(DateTime now) : TimeProvider
    {
        private DateTime _now = now;
        public override DateTimeOffset GetUtcNow() => new(_now);
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class FailingSelectionLedger : IModelSelectionLedger
    {
        public void Record(SelectionLedgerEntry entry) => throw new IOException("ledger unavailable");
    }
}
