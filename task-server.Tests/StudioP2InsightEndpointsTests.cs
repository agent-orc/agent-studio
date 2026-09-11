using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TaskServer.Tests;

public sealed class StudioP2InsightEndpointsTests
{
    [Fact]
    public async Task Bus_messages_can_be_ingested_listed_filtered_and_fetched_by_id()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2InsightApiFactory(temp.Path);
        using var client = Client(factory);
        var project = await CreateProjectAsync(client, "Bus Project", "BUS");

        var emptyList = await client.GetFromJsonAsync<List<BusMessageDto>>(
            $"/api/v1/studio/bus/{project}/messages");
        Assert.Empty(emptyList!);

        var firstIngest = await client.PostAsJsonAsync(
            $"/api/v1/studio/bus/{project}/messages/ingest",
            new BusMessageIngestRequest("First message", Kind: "decision", Severity: "info", Tag: "orchestrator-chat"));
        Assert.Equal(HttpStatusCode.Created, firstIngest.StatusCode);
        var first = (await firstIngest.Content.ReadFromJsonAsync<BusMessageDto>())!;
        Assert.Equal(project, first.ProjectId);
        Assert.Equal("First message", first.Text);

        var secondIngest = await client.PostAsJsonAsync(
            $"/api/v1/studio/bus/{project}/messages/ingest",
            new BusMessageIngestRequest("Second message", Kind: "log", Severity: "warn", RunId: "run-1"));
        secondIngest.EnsureSuccessStatusCode();
        var second = (await secondIngest.Content.ReadFromJsonAsync<BusMessageDto>())!;

        var list = await client.GetFromJsonAsync<List<BusMessageDto>>($"/api/v1/studio/bus/{project}/messages");
        Assert.Equal([second.Id, first.Id], list!.Select(item => item.Id));

        var filtered = await client.GetFromJsonAsync<List<BusMessageDto>>(
            $"/api/v1/studio/bus/{project}/messages?kind=decision");
        var filteredMessage = Assert.Single(filtered!);
        Assert.Equal(first.Id, filteredMessage.Id);

        var byRunId = await client.GetFromJsonAsync<List<BusMessageDto>>(
            $"/api/v1/studio/bus/{project}/messages?runId=run-1");
        Assert.Equal([second.Id], byRunId!.Select(item => item.Id));

        var fetched = await client.GetFromJsonAsync<BusMessageDto>($"/api/v1/studio/bus/{project}/messages/{first.Id}");
        Assert.Equal("First message", fetched!.Text);

        var missing = await client.GetAsync($"/api/v1/studio/bus/{project}/messages/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var wrongProject = await CreateProjectAsync(client, "Other Bus Project", "OBP");
        var crossProject = await client.GetAsync($"/api/v1/studio/bus/{wrongProject}/messages/{first.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossProject.StatusCode);

        var recent = await client.GetFromJsonAsync<List<BusMessageDto>>($"/api/v1/studio/bus/{project}/recent?limit=1");
        var recentMessage = Assert.Single(recent!);
        Assert.Equal(second.Id, recentMessage.Id);

        var summary = await client.GetFromJsonAsync<BusMessageSummaryResponse>($"/api/v1/studio/bus/{project}/summary");
        Assert.Equal(2, summary!.TotalMessages);
        Assert.Equal(1, summary.ByKind["decision"]);
        Assert.Equal(1, summary.ByKind["log"]);
        Assert.Equal(1, summary.BySeverity["info"]);
        Assert.Equal(1, summary.BySeverity["warn"]);
    }

    [Fact]
    public async Task Bus_summary_and_token_aggregate_are_zero_shaped_for_a_project_with_no_data()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2InsightApiFactory(temp.Path);
        using var client = Client(factory);
        var project = await CreateProjectAsync(client, "Empty Bus Project", "EBP");

        var summary = await client.GetFromJsonAsync<BusMessageSummaryResponse>($"/api/v1/studio/bus/{project}/summary");
        Assert.Equal(0, summary!.TotalMessages);
        Assert.Empty(summary.ByKind);

        var aggregate = await client.GetFromJsonAsync<BusTokenAggregateResponse>(
            $"/api/v1/studio/bus/{project}/token-aggregate");
        Assert.Equal(0, aggregate!.TotalTokens);
        Assert.Equal(0, aggregate.RecordCount);
    }

    [Fact]
    public async Task Token_usage_can_be_ingested_and_read_back_through_every_projection()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2InsightApiFactory(temp.Path);
        using var client = Client(factory);
        var project = await CreateProjectAsync(client, "Token Project", "TKN");
        var now = DateTime.UtcNow;

        var firstIngest = await client.PostAsJsonAsync(
            $"/api/v1/studio/token-usage/{project}/ingest",
            new TokenUsageIngestRequest(now, 1000, 500, 100, 50, "task-a", "run-a", "claude-sonnet-5"));
        Assert.Equal(HttpStatusCode.Created, firstIngest.StatusCode);

        var secondIngest = await client.PostAsJsonAsync(
            $"/api/v1/studio/token-usage/{project}/ingest",
            new TokenUsageIngestRequest(now, 4000, 2000, 0, 0, "task-b", "run-b", "claude-opus-4-8"));
        secondIngest.EnsureSuccessStatusCode();

        var summary = await client.GetFromJsonAsync<TokenUsageSummaryResponse>(
            $"/api/v1/studio/projects/{project}/token-usage/summary");
        Assert.Equal(5000, summary!.InputTokens);
        Assert.Equal(2500, summary.OutputTokens);
        Assert.Equal(2, summary.RecordCount);
        Assert.Equal(5000 + 2500 + 100 + 50, summary.TotalTokens);

        var expensive = await client.GetFromJsonAsync<ExpensiveTaskTokenUsageResponse>(
            $"/api/v1/studio/projects/{project}/token-usage/expensive");
        Assert.Equal("task-b", expensive!.Tasks[0].TaskId);
        Assert.Equal("task-a", expensive.Tasks[1].TaskId);

        var forTask = await client.GetFromJsonAsync<List<TokenUsageDto>>(
            $"/api/v1/studio/projects/{project}/token-usage/job/task-a");
        var taskUsage = Assert.Single(forTask!);
        Assert.Equal("run-a", taskUsage.RunId);
        Assert.Equal("claude-sonnet-5", taskUsage.Model);

        var heatmap = await client.GetFromJsonAsync<TokenUsageHeatmapResponse>(
            $"/api/v1/studio/projects/{project}/token-usage/heatmap?days=7");
        Assert.Equal(7, heatmap!.Points.Count);
        Assert.Equal(now.ToString("yyyy-MM-dd"), heatmap.Points[^1].Date);
        Assert.Equal(5000, heatmap.Points[^1].InputTokens);

        var pipelineCost = await client.GetFromJsonAsync<TokenUsagePipelineCostResponse>(
            $"/api/v1/studio/projects/{project}/token-usage/pipeline-cost?days=7");
        Assert.Equal(7, pipelineCost!.Points.Count);
        Assert.Equal(2500, pipelineCost.Points[^1].OutputTokens);

        var aggregate = await client.GetFromJsonAsync<BusTokenAggregateResponse>(
            $"/api/v1/studio/bus/{project}/token-aggregate");
        Assert.Equal(2, aggregate!.RecordCount);
        Assert.Equal(5000, aggregate.InputTokens);
    }

    [Fact]
    public async Task Token_usage_summary_is_zero_shaped_for_a_fresh_project()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2InsightApiFactory(temp.Path);
        using var client = Client(factory);
        var project = await CreateProjectAsync(client, "Fresh Token Project", "FTP");

        var summary = await client.GetFromJsonAsync<TokenUsageSummaryResponse>(
            $"/api/v1/studio/projects/{project}/token-usage/summary");
        Assert.Equal(0, summary!.TotalTokens);
        Assert.Equal(0, summary.RecordCount);

        var expensive = await client.GetFromJsonAsync<ExpensiveTaskTokenUsageResponse>(
            $"/api/v1/studio/projects/{project}/token-usage/expensive");
        Assert.Empty(expensive!.Tasks);

        var heatmap = await client.GetFromJsonAsync<TokenUsageHeatmapResponse>(
            $"/api/v1/studio/projects/{project}/token-usage/heatmap");
        Assert.Equal(30, heatmap!.Points.Count);
        Assert.All(heatmap.Points, point => Assert.Equal(0, point.TotalTokens));
    }

    [Fact]
    public async Task Runtime_events_can_be_ingested_and_listed_and_accept_the_refresh_query_param()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2InsightApiFactory(temp.Path);
        using var client = Client(factory);
        var project = await CreateProjectAsync(client, "Runtime Project", "RTP");

        var emptyEvents = await client.GetFromJsonAsync<RuntimeEventListResponse>(
            $"/api/v1/studio/runtime/{project}/events");
        Assert.Empty(emptyEvents!.Events);

        var ingest = await client.PostAsJsonAsync(
            $"/api/v1/studio/runtime/{project}/events/ingest",
            new RuntimeEventIngestRequest("deployment.started", """{"version":"1.2.3"}"""));
        Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);
        var ingested = (await ingest.Content.ReadFromJsonAsync<RuntimeEventDto>())!;
        Assert.Equal("deployment.started", ingested.Kind);

        var events = await client.GetFromJsonAsync<RuntimeEventListResponse>(
            $"/api/v1/studio/runtime/{project}/events?refresh=true");
        var runtimeEvent = Assert.Single(events!.Events);
        Assert.Equal(ingested.Id, runtimeEvent.Id);
        Assert.Contains("1.2.3", runtimeEvent.PayloadJson);
    }

    [Fact]
    public async Task Token_pricing_calculate_prices_a_known_model_and_rejects_an_unknown_one()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2InsightApiFactory(temp.Path);
        using var client = Client(factory);

        var priced = await client.PostAsJsonAsync(
            "/api/v1/studio/token-pricing/calculate",
            new CalculateTokenPricingRequest("claude-sonnet-5", 1_000_000, 1_000_000, 1_000_000, 1_000_000));
        priced.EnsureSuccessStatusCode();
        var breakdown = (await priced.Content.ReadFromJsonAsync<TokenPricingBreakdownDto>())!;
        Assert.Equal(3.00m, breakdown.InputCost);
        Assert.Equal(15.00m, breakdown.OutputCost);
        Assert.Equal(0.30m, breakdown.CacheReadCost);
        Assert.Equal(3.75m, breakdown.CacheCreationCost);
        Assert.Equal(22.05m, breakdown.TotalCost);
        Assert.Equal("USD", breakdown.Currency);

        var unknown = await client.PostAsJsonAsync(
            "/api/v1/studio/token-pricing/calculate",
            new CalculateTokenPricingRequest("not-a-real-model", 100, 100));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    private static async Task<string> CreateProjectAsync(HttpClient client, string name, string prefix)
    {
        var workspace = await client.PostAsJsonAsync("/api/v1/workspaces", new CreateWorkspaceRequest($"{name} WS"));
        workspace.EnsureSuccessStatusCode();
        var workspaceDto = (await workspace.Content.ReadFromJsonAsync<WorkspaceDto>())!;
        var project = await client.PostAsJsonAsync(
            "/api/v1/projects", new CreateProjectRequest(workspaceDto.WorkspaceId, name, prefix));
        project.EnsureSuccessStatusCode();
        var projectDto = (await project.Content.ReadFromJsonAsync<ProjectDto>())!;
        return projectDto.ProjectId;
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "studio-p2-insight-test");
        return client;
    }

    private sealed class P2InsightApiFactory(string dataDirectory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["TaskServer:RetentionSchedulerEnabled"] = "false",
                }));
        }
    }
}
