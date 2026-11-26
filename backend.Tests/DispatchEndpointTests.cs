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
