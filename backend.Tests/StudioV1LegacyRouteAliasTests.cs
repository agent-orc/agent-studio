using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Host;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using System.Net.Http.Headers;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2983 moved the Studio's core-attach calls onto their versioned Task
/// Server routes. The local monolith keeps serving exactly those method and
/// path shapes from its legacy handlers and leaves every other <c>/api/v1</c>
/// route to its v1 owner. With a standalone Task Server the legacy handlers are
/// closed, so the plane proxy forwards the versioned shapes and only the hub
/// alias stays local.
/// </summary>
public sealed class StudioV1LegacyRouteAliasTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "studio-v1-alias-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("GET", "/api/v1/studio/auth/status", "/api/auth/status", null)]
    [InlineData("POST", "/api/v1/studio/auth/bootstrap", "/api/auth/bootstrap", null)]
    [InlineData("POST", "/api/v1/studio/auth/login", "/api/auth/login", null)]
    [InlineData("POST", "/api/v1/studio/auth/logout", "/api/auth/logout", null)]
    [InlineData("POST", "/api/v1/studio/auth/change-password", "/api/auth/change-password", null)]
    [InlineData("GET", "/api/v1/studio/board", "/api/tasks/grouped", null)]
    [InlineData("GET", "/api/v1/workspaces", "/api/workspaces", null)]
    [InlineData("POST", "/api/v1/workspaces", "/api/workspaces", null)]
    [InlineData("GET", "/api/v1/projects", "/api/projects", null)]
    [InlineData("POST", "/api/v1/projects", "/api/projects", null)]
    [InlineData("GET", "/api/v1/projects/Agent Studio/tasks/AGT-1", "/api/tasks/AGT-1", "Agent Studio")]
    [InlineData("GET", "/api/v1/projects/-/tasks/AGT-1", "/api/tasks/AGT-1", null)]
    [InlineData("DELETE", "/api/v1/projects/-/tasks/AGT-1", "/api/tasks/AGT-1", null)]
    [InlineData("POST", "/api/v1/projects/-/tasks/AGT-1/continue", "/api/tasks/AGT-1/continue", null)]
    [InlineData("POST", "/api/v1/projects/-/tasks/AGT-1/move", "/api/tasks/AGT-1/move", null)]
    [InlineData("POST", "/api/v1/projects/-/tasks/AGT-1/move-to-top", "/api/tasks/AGT-1/move-to-top", null)]
    [InlineData("POST", "/api/v1/projects/-/tasks/AGT-1/start", "/api/tasks/AGT-1/start", null)]
    [InlineData("PUT", "/api/v1/projects/-/tasks/AGT-1/state", "/api/tasks/AGT-1/state", null)]
    [InlineData("POST", "/api/v1/projects/-/tasks/AGT-1/stop", "/api/tasks/AGT-1/stop", null)]
    [InlineData("GET", "/api/v1/studio/orchestrator/context/global", "/api/orchestrator/context/global", null)]
    [InlineData("GET", "/api/v1/studio/orchestrator/context/project:PROJ", "/api/orchestrator/context/project:PROJ", null)]
    [InlineData("GET", "/api/v1/studio/orchestrator/context/task:PROJ/AGT-1", "/api/orchestrator/context/task:PROJ/AGT-1", null)]
    [InlineData("POST", "/api/v1/studio/orchestrator/context/global/refresh", "/api/orchestrator/context/global/refresh", null)]
    [InlineData("POST", "/api/v1/studio/orchestrator/context/project:PROJ/refresh", "/api/orchestrator/context/project:PROJ/refresh", null)]
    [InlineData("POST", "/api/v1/studio/orchestrator/context/task:PROJ/AGT-1/refresh", "/api/orchestrator/context/task:PROJ/AGT-1/refresh", null)]
    [InlineData("GET", "/api/v1/studio/orchestrator/sessions", "/api/orchestrator/sessions", null)]
    [InlineData("POST", "/api/v1/studio/orchestrator/sessions/workbench:PROJ/wb-1/turns", "/api/orchestrator/sessions/workbench:PROJ/wb-1/turns", null)]
    [InlineData("POST", "/api/v1/studio/orchestrator/sessions/archive", "/api/orchestrator/sessions/archive", null)]
    [InlineData("GET", "/api/v1/studio/runner/status", "/api/runner/status", null)]
    [InlineData("GET", "/api/v1/studio/runner/PROJ/orchestrator-chat", "/api/runner/PROJ/orchestrator-chat", null)]
    [InlineData("POST", "/api/v1/studio/runner/PROJ/orchestrator-chat", "/api/runner/PROJ/orchestrator-chat", null)]
    [InlineData("GET", "/api/v1/studio/runner/PROJ/orchestrator-chat/attachments/a.png", "/api/runner/PROJ/orchestrator-chat/attachments/a.png", null)]
    [InlineData("GET", "/hubs/v1/studio", "/hubs/jobs", null)]
    [InlineData("POST", "/hubs/v1/studio/negotiate", "/hubs/jobs/negotiate", null)]
    public void Core_attach_shapes_resolve_to_their_legacy_handler(string method, string path, string legacy, string? project)
    {
        var target = StudioV1LegacyRouteAlias.Resolve(method, path);

        Assert.NotNull(target);
        Assert.Equal(legacy, target.Value.Path);
        Assert.Equal(project, target.Value.Project);
    }

    [Theory]
    [InlineData("GET", "/api/v1/management/status")]
    [InlineData("GET", "/api/v1/protocol")]
    [InlineData("PUT", "/api/v1/projects/PROJ/tasks/AGT-1")]
    [InlineData("GET", "/api/v1/projects/PROJ/tasks")]
    [InlineData("POST", "/api/v1/projects/PROJ/tasks")]
    [InlineData("GET", "/api/v1/projects/PROJ/tasks/AGT-1/history")]
    [InlineData("GET", "/api/v1/projects/PROJ/tasks/AGT-1/move")]
    [InlineData("PUT", "/api/v1/projects/PROJ/tasks/AGT-1/move")]
    [InlineData("POST", "/api/v1/projects/PROJ/tasks/AGT-1/state")]
    [InlineData("DELETE", "/api/v1/workspaces")]
    [InlineData("POST", "/api/v1/studio/board")]
    [InlineData("GET", "/api/v1/studio/auth/login")]
    [InlineData("POST", "/api/v1/studio/auth/status")]
    [InlineData("POST", "/api/v1/studio/orchestrator/context/global")]
    [InlineData("GET", "/api/v1/studio/orchestrator/context")]
    [InlineData("GET", "/api/v1/studio/orchestrator/context/global/refresh")]
    [InlineData("GET", "/api/v1/studio/orchestrator/context/task:PROJ/AGT-1/refresh")]
    [InlineData("GET", "/api/v1/studio/orchestrator/context/a/b/c/d")]
    [InlineData("GET", "/api/v1/studio/orchestrator/context/workbench:PROJ/wb-1")]
    [InlineData("POST", "/api/v1/studio/orchestrator/context/global/x/refresh")]
    [InlineData("POST", "/api/v1/studio/orchestrator/sessions/project:PROJ/turns")]
    [InlineData("GET", "/api/v1/studio/orchestrator/sessions/archive")]
    [InlineData("POST", "/api/v1/studio/orchestrator/sessions/unarchive")]
    [InlineData("PUT", "/api/v1/studio/runner/PROJ/mode")]
    [InlineData("POST", "/api/v1/studio/runner/PROJ/orchestrator-chat/attachments")]
    [InlineData("GET", "/api/v1/studio/search")]
    [InlineData("GET", "/api/tasks/grouped")]
    [InlineData("GET", "/hubs/jobs")]
    [InlineData("GET", "/hubs/events")]
    public void Every_other_route_is_left_to_its_owner(string method, string path)
        => Assert.Null(StudioV1LegacyRouteAlias.Resolve(method, path));

    [Theory]
    [InlineData("GET", "/api/v1/studio/auth/status")]
    [InlineData("GET", "/api/v1/studio/board")]
    [InlineData("GET", "/api/v1/projects")]
    [InlineData("GET", "/api/v1/projects/-/tasks/AGT-1")]
    [InlineData("POST", "/api/v1/projects/-/tasks/AGT-1/start")]
    [InlineData("GET", "/api/v1/studio/orchestrator/context/task:PROJ/AGT-1")]
    [InlineData("GET", "/api/v1/studio/runner/status")]
    public void Standalone_task_server_keeps_versioned_api_shapes_for_the_plane_proxy(string method, string path)
    {
        Assert.NotNull(StudioV1LegacyRouteAlias.Resolve(method, path, standaloneTaskServer: false));
        Assert.Null(StudioV1LegacyRouteAlias.Resolve(method, path, standaloneTaskServer: true));
    }

    [Theory]
    [InlineData("GET", "/hubs/v1/studio", "/hubs/jobs")]
    [InlineData("POST", "/hubs/v1/studio/negotiate", "/hubs/jobs/negotiate")]
    public void Standalone_task_server_still_aliases_the_local_hub(string method, string path, string legacy)
        => Assert.Equal(legacy, StudioV1LegacyRouteAlias.Resolve(method, path, standaloneTaskServer: true)?.Path);

    [Fact]
    public async Task Local_profile_serves_the_versioned_core_attach_paths_from_the_legacy_handlers()
    {
        using var factory = BuildFactory(taskServerBaseUrl: null);
        using var browser = CreateClient(factory);

        await AssertSameBodyAsync(browser, "/api/v1/studio/auth/status", "/api/auth/status");
        var bootstrap = await browser.PostAsJsonAsync("/api/v1/studio/auth/bootstrap", new
        {
            username = "local.owner",
            password = "correct horse battery staple!",
            displayName = "Local Owner",
        });
        bootstrap.EnsureSuccessStatusCode();
        var login = await browser.PostAsJsonAsync(
            "/api/v1/studio/auth/login", new { username = "local.owner", password = "correct horse battery staple!" });
        login.EnsureSuccessStatusCode();
        Assert.True((await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authenticated").GetBoolean());

        foreach (var path in new[]
                 {
                     "/api/v1/studio/board",
                     "/api/v1/workspaces",
                     "/api/v1/projects",
                     "/api/v1/studio/runner/status",
                     "/api/v1/studio/orchestrator/sessions",
                 })
        {
            var response = await browser.GetAsync(path);
            Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}.");
        }

        var negotiate = await browser.PostAsync("/hubs/v1/studio/negotiate?negotiateVersion=1", null);
        negotiate.EnsureSuccessStatusCode();
        Assert.True((await negotiate.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("connectionToken", out _));
    }

    [Fact]
    public async Task Proxy_profile_forwards_core_attach_v1_routes_to_the_standalone_task_server()
    {
        var upstream = new RecordingUpstream();
        using var factory = BuildFactory(taskServerBaseUrl: "http://task-server.invalid", upstream);
        using var browser = CreateClient(factory);

        foreach (var path in new[]
                 {
                     "/api/v1/studio/board",
                     "/api/v1/studio/auth/status",
                     "/api/v1/projects/-/tasks/AGT-1",
                     "/api/v1/studio/runner/status",
                     "/api/v1/protocol",
                 })
        {
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(path)).StatusCode);
            Assert.Contains(path, upstream.Paths);
        }

        // The legacy handlers stay closed behind a standalone Task Server.
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/api/tasks/grouped")).StatusCode);
    }

    [Fact]
    public async Task Proxy_profile_records_card_free_decisions_on_the_engine_visible_receipt_plane()
    {
        var upstream = new RecordingUpstream();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero));
        using var factory = BuildFactory("http://task-server.invalid", upstream, clock);
        var workspace = factory.Services.GetRequiredService<WorkspaceRegistry>().Create("Capacity", clock: clock);
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var engine = security.EnrollRunner(security.CreateEnrollment(new RunnerEnrollmentRequest(
            "engine-one", [RunnerScopes.Orchestration], null, null)).Code);
        using var browser = CreateClient(factory);
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", engine.Secret);
        Assert.Equal(HttpStatusCode.OK,
            (await browser.GetAsync($"/api/workspaces/{workspace.Id}/decision-rules")).StatusCode);
        var occurrence = "capacity:host-1:sample";
        var response = await browser.PostAsJsonAsync("/api/v1/orchestration/host-decisions",
            new HostDecisionOccurrenceRequestV1(occurrence, workspace.Id, "host-1",
                "capacity-check", "capacity-check", clock.GetUtcNow().UtcDateTime, [], [], [], "scheduled"));
        response.EnsureSuccessStatusCode();
        var frozen = await response.Content.ReadFromJsonAsync<DecisionShadowInputV1>();
        Assert.NotNull(frozen);
        Assert.Equal("host", frozen.Grounds.Scope.Kind);
        Assert.Equal([occurrence], await browser.GetFromJsonAsync<string[]>(
            "/api/v1/orchestration/decision-grounds/pending?limit=10"));
        var read = await browser.GetFromJsonAsync<DecisionShadowInputV1>(
            $"/api/v1/orchestration/runs/{occurrence}/grounds-input");
        Assert.Equal(occurrence, read?.Grounds.OccurrenceId);
        var grounds = DecisionGroundsPolicy.Freeze(frozen.Grounds);
        var jobResponse = await browser.PostAsJsonAsync("/api/v1/orchestration/decision-jobs",
            new OrchestratorDecisionJobRequest(occurrence, "", "", 1, grounds.ContentDigest,
                DecisionReceiptPolicy.RenderPrompt(grounds), "codex", grounds.ModelRoute.Model,
                grounds.ModelRoute.ThinkingLevel, 8_000, 1_000));
        jobResponse.EnsureSuccessStatusCode();
        var job = await jobResponse.Content.ReadFromJsonAsync<OrchestratorDecisionJobDto>();
        Assert.NotNull(job);
        Assert.Equal(job.JobId, (await browser.GetFromJsonAsync<OrchestratorDecisionJobDto>(
            $"/api/v1/orchestration/decision-jobs/{job.JobId}"))?.JobId);
        var claimResponse = await browser.PostAsJsonAsync("/api/runner/orchestrator-decisions/claim",
            new OrchestratorDecisionJobClaimRequest("runner-1", "runner-1"));
        claimResponse.EnsureSuccessStatusCode();
        var claim = (await claimResponse.Content.ReadFromJsonAsync<OrchestratorDecisionJobClaimResponse>())?.Work;
        Assert.NotNull(claim);
        var completionResponse = await browser.PostAsJsonAsync("/api/runner/orchestrator-decisions/complete",
            new OrchestratorDecisionJobCompletion(claim.JobId, claim.ClaimToken, "runner-1", true,
                """{"chosenActions":[],"confidence":0.8,"reasoning":"Capacity is stable.","citedFacts":[],"assessment":"Stable","report":"No action needed."}""",
                grounds.ModelRoute.Model, [new("call-1", grounds.ModelRoute.Model, 100, 0, 0, 20, false)],
                clock.GetUtcNow().UtcDateTime, clock.GetUtcNow().UtcDateTime.AddSeconds(2), null, false));
        completionResponse.EnsureSuccessStatusCode();
        job = await browser.GetFromJsonAsync<OrchestratorDecisionJobDto>(
            $"/api/v1/orchestration/decision-jobs/{job.JobId}");
        Assert.Equal(DecisionJobStatuses.Completed, job?.Status);
        var receipt = DecisionReceiptPolicy.Build(new(grounds,
            new(DecisionActionKinds.Observe, "host-1", "applied:deterministic"),
            "Host occurrence", job, 1, false, null, DecisionReceiptPolicy.ActiveMode));
        (await browser.PutAsJsonAsync($"/api/v1/orchestration/runs/{occurrence}/decision-receipt", receipt))
            .EnsureSuccessStatusCode();
        Assert.Empty(await browser.GetFromJsonAsync<string[]>(
            "/api/v1/orchestration/decision-grounds/pending?limit=10"));
        var historyResponse = await browser.GetAsync("/api/orchestration/decision-receipts?hostId=host-1");
        var historyJson = await historyResponse.Content.ReadAsStringAsync();
        Assert.True(historyResponse.IsSuccessStatusCode,
            $"Host history returned {(int)historyResponse.StatusCode}: {historyJson}");
        using var history = JsonDocument.Parse(historyJson);
        Assert.Equal(occurrence, history.RootElement[0].GetProperty("occurrenceId").GetString());
        Assert.Null(history.RootElement[0].GetProperty("taskId").GetString());
        Assert.Equal(120, history.RootElement[0].GetProperty("totalTokens").GetInt32());
        Assert.DoesNotContain(upstream.Paths, path => path.Contains("decision-grounds", StringComparison.Ordinal)
            || path.Contains("decision-jobs", StringComparison.Ordinal)
            || path.Contains("decision-receipt", StringComparison.Ordinal)
            || path.Contains("host-decisions", StringComparison.Ordinal)
            || path.Contains("orchestrator-decisions", StringComparison.Ordinal));
    }

    private static async Task AssertSameBodyAsync(HttpClient client, string versioned, string legacy)
    {
        var fromVersioned = await client.GetStringAsync(versioned);
        var fromLegacy = await client.GetStringAsync(legacy);
        Assert.Equal(fromLegacy, fromVersioned);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        return client;
    }

    private WebApplicationFactory<Program> BuildFactory(string? taskServerBaseUrl, RecordingUpstream? upstream = null,
        TimeProvider? clock = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            // Program registers the proxy client before these configuration
            // overrides are visible, so the test sets its origin directly.
            builder.ConfigureTestServices(services =>
            {
                if (clock is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(clock);
                }
                if (upstream is not null)
                    services.AddHttpClient(TaskServerPlaneProxy.ClientName)
                        .ConfigureHttpClient(client => client.BaseAddress = new Uri(taskServerBaseUrl!.TrimEnd('/') + "/"))
                        .ConfigurePrimaryHttpMessageHandler(() => upstream);
            });
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = "local",
                ["TaskServer:BaseUrl"] = taskServerBaseUrl ?? string.Empty,
            }));
        });

    /// <summary>Stands in for the standalone Task Server behind the transitional proxy.</summary>
    private sealed class RecordingUpstream : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { proxied = true }) });
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { }
    }
}
