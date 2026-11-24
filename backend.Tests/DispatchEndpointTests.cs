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
