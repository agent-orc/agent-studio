using System.Net;
using System.Diagnostics;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

public sealed class TaskListProjectFilterEndpointTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "task-list-project-filter-" + Guid.NewGuid().ToString("N"));
    private readonly string _alphaPath;
    private readonly string _betaPath;

    public TaskListProjectFilterEndpointTests(ITestOutputHelper output)
    {
        _output = output;
        _alphaPath = Path.Combine(_workspace, "alpha");
        _betaPath = Path.Combine(_workspace, "beta");
        SeedTask(_alphaPath, "alpha-task", "Alpha task");
        SeedTask(_betaPath, "beta-task", "Beta task");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task List_ByProjectId_ReturnsFilteredListInsteadOfAnalysisEnvelope()
    {
        await using var factory = BuildFactory();
        using var client = CreateClient(factory);
        var registry = factory.Services.GetRequiredService<ProjectRegistry>();
        var alpha = registry.FindByStorageLocation(_alphaPath);
        Assert.NotNull(alpha);

        using var response = await client.GetAsync(
            $"/api/tasks?project={Uri.EscapeDataString(alpha!.Id)}");

        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        var task = Assert.Single(body.RootElement.EnumerateArray());
        Assert.Equal("alpha-task", task.GetProperty("id").GetString());
    }

    [Fact]
    public async Task List_ProjectScopeComposesWithAnalysisFilters()
    {
        await using var factory = BuildFactory();
        using var client = CreateClient(factory);
        var registry = factory.Services.GetRequiredService<ProjectRegistry>();
        var alpha = registry.FindByStorageLocation(_alphaPath);
        Assert.NotNull(alpha);

        using var response = await client.GetAsync(
            $"/api/tasks?project={Uri.EscapeDataString(alpha!.Id)}&state={TaskStates.Ready}");

        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("total").GetInt32());
        var task = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("alpha-task", task.GetProperty("id").GetString());
    }

    [Fact]
    public async Task List_UnknownProject_ReturnsNotFoundInsteadOfWorkspaceTasks()
    {
        await using var factory = BuildFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/api/tasks?project=PROJ-999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Slim_list_omits_decision_options_and_detail_overview_restores_them()
    {
        var folder = Path.Combine(_alphaPath, TaskStates.Ready, "alpha-task");
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = "alpha-task", title = "Choose a plan", state = TaskStates.Ready,
            kind = "decision", creationSource = "orchestrator", released = true,
            decision = new
            {
                question = "Which plan?", status = "pending", recommendedOptionId = "a",
                recommendationReason = "Smaller change",
                options = new[] { new { id = "a", label = "Plan A", consequences = "Less work" },
                    new { id = "b", label = "Plan B", consequences = "More work" } },
            },
        }));
        await using var factory = BuildFactory();
        using var client = CreateClient(factory);
        var project = factory.Services.GetRequiredService<ProjectRegistry>()
            .FindByStorageLocation(_alphaPath)!;
        using var list = await client.GetAsync($"/api/tasks?project={Uri.EscapeDataString(project.Id)}");
        list.EnsureSuccessStatusCode();
        var listBytes = await list.Content.ReadAsByteArrayAsync();
        using var listJson = JsonDocument.Parse(listBytes);
        var card = Assert.Single(listJson.RootElement.EnumerateArray());
        Assert.True(listBytes.Length < 2048, $"List card was {listBytes.Length} bytes");
        Assert.True(card.GetProperty("released").GetBoolean());
        Assert.False(card.GetProperty("decision").TryGetProperty("options", out _));

        using var overview = await client.GetAsync(
            $"/api/tasks/alpha-task/details/overview?project={Uri.EscapeDataString(project.Id)}");
        overview.EnsureSuccessStatusCode();
        using var detailJson = JsonDocument.Parse(await overview.Content.ReadAsStringAsync());
        var data = detailJson.RootElement.GetProperty("data");
        Assert.Equal("orchestrator", data.GetProperty("creationSource").GetString());
        Assert.Equal("a", data.GetProperty("decision").GetProperty("recommendedOptionId").GetString());
        Assert.Equal(2, data.GetProperty("decision").GetProperty("options").GetArrayLength());
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Warm_board_responses_on_235_card_fixture()
    {
        for (var i = 1; i < 235; i++)
            SeedTask(_alphaPath, $"alpha-{i:D3}", $"Representative card {i:D3}");
        await using var factory = BuildFactory();
        using var client = CreateClient(factory);
        var project = factory.Services.GetRequiredService<ProjectRegistry>()
            .FindByStorageLocation(_alphaPath)!;
        var listPath = $"/api/tasks?project={Uri.EscapeDataString(project.Id)}";
        var groupedPath = "/api/tasks/grouped?includeLegacyReviewLane=false";
        foreach (var (name, path) in new[] { ("list", listPath), ("grouped", groupedPath) })
        {
            using var cold = await client.GetAsync(path);
            cold.EnsureSuccessStatusCode();
            var timer = Stopwatch.StartNew();
            using var warm = await client.GetAsync(path);
            var bytes = await warm.Content.ReadAsByteArrayAsync();
            timer.Stop();
            warm.EnsureSuccessStatusCode();
            _output.WriteLine($"{name}: {bytes.Length} bytes; warm {timer.Elapsed.TotalMilliseconds:F1} ms");
            Assert.True(bytes.Length < 500_000, $"{name} was {bytes.Length} bytes");
        }
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = "Alpha Project",
                    ["WatchPaths:0:Path"] = _alphaPath,
                    ["WatchPaths:0:RootPath"] = _alphaPath,
                    ["WatchPaths:1:Name"] = "Beta Project",
                    ["WatchPaths:1:Path"] = _betaPath,
                    ["WatchPaths:1:RootPath"] = _betaPath,
                }));
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        return client;
    }

    private static void SeedTask(string watchPath, string id, string title)
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(watchPath, state));

        var taskFolder = Path.Combine(watchPath, TaskStates.Ready, id);
        Directory.CreateDirectory(taskFolder);
        File.WriteAllText(Path.Combine(taskFolder, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            title,
            state = TaskStates.Ready,
            order = 1,
            agent = "codex",
        }));
    }
}
