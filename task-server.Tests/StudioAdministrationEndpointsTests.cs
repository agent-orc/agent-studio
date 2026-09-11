using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// Covers the P3 "administration and long tail" v1 routes (AGT-2758): admin
/// config, the runtime prompt catalog, auto-review status, CLI quota/model
/// routing, per-project CLI/lane-sort settings, the pipeline catalogue, and
/// the task-results half of global search.
/// </summary>
public sealed class StudioAdministrationEndpointsTests
{
    [Fact]
    public async Task Orchestrator_config_snapshot_lists_the_catalog_with_defaults()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioAdminApiFactory(temp.Path);
        using var client = Client(factory);

        var snapshot = await client.GetFromJsonAsync<StudioOrchestratorConfigSnapshot>(
            "/api/v1/studio/admin/config/orchestrator");

        Assert.NotEmpty(snapshot!.Options);
        var hardCheck = Assert.Single(snapshot.Options, o => o.Key == "Supervisor:HardCheckEnabled");
        Assert.True(((System.Text.Json.JsonElement)hardCheck.CurrentValue!).GetBoolean());
        Assert.All(snapshot.Options, o => Assert.False(o.HasOverride));
    }

    [Fact]
    public async Task Prompt_catalog_and_detail_read_the_embedded_default_tree()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioAdminApiFactory(temp.Path);
        using var client = Client(factory);

        var catalog = await client.GetFromJsonAsync<StudioPromptCatalogResponse>("/api/v1/studio/admin/prompts");
        Assert.NotEmpty(catalog!.Items);
        var item = catalog.Items.First(i => i.Name == "commit-message.md");
        Assert.True(item.HasDefault);
        Assert.False(item.HasOverride);

        var detail = await client.GetFromJsonAsync<StudioPromptDetail>("/api/v1/studio/admin/prompts/commit-message.md");
        Assert.Equal("commit-message.md", detail!.Name);
        Assert.NotNull(detail.DefaultContent);
        Assert.Equal(detail.DefaultContent, detail.EffectiveContent);

        var missing = await client.GetAsync("/api/v1/studio/admin/prompts/does-not-exist.md");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Auto_review_status_reflects_tasks_currently_in_the_auto_review_lane()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioAdminApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Review WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Review Project", "REV"), "test", default);
        await store.CreateTaskAsync(
            project.ProjectId, new CreateTaskRequest("Under review", State: "4-auto-review"), "test", default);

        var status = await client.GetFromJsonAsync<StudioAutoReviewStatus>("/api/v1/studio/auto-review/status");

        Assert.Equal(1, status!.Pending);
        Assert.Contains(status.ActiveJobs, job => job.Project == "Review Project");
    }

    [Fact]
    public async Task Cli_quota_and_model_routing_routes_return_documented_defaults()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioAdminApiFactory(temp.Path);
        using var client = Client(factory);

        var policy = await client.GetFromJsonAsync<StudioModelRoutingPolicyView>("/api/v1/studio/cli/model-routing/policy");
        Assert.False(policy!.EconomyMode);
        Assert.NotEmpty(policy.Rows);

        var recommendation = await client.GetFromJsonAsync<StudioModelRoutingRecommendation>(
            "/api/v1/studio/cli/model-routing/recommendation?taskType=bug&cliType=claude");
        Assert.False(recommendation!.EconomyDowngraded);
        Assert.Equal("bug", recommendation.TaskType);

        var badCli = await client.GetAsync("/api/v1/studio/cli/model-routing/recommendation?taskType=bug&cliType=not-a-cli");
        Assert.Equal(HttpStatusCode.BadRequest, badCli.StatusCode);

        var caps = await client.GetFromJsonAsync<StudioCliQuotaCaps>("/api/v1/studio/cli/quota/caps");
        Assert.Equal(95, caps!.DefaultCapPct);

        var waitPolicy = await client.GetFromJsonAsync<StudioCliQuotaWaitPolicy>("/api/v1/studio/cli/quota/wait-policy");
        Assert.False(waitPolicy!.Enabled);

        var modelRoutes = await client.GetFromJsonAsync<StudioCliModelRoutesResponse>("/api/v1/studio/cli/quota/model-routes");
        Assert.Contains("claude", modelRoutes!.Profiles.Keys);
    }

    [Fact]
    public async Task Project_scoped_settings_resolve_defaults_and_404_for_an_unknown_project()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioAdminApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Settings WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Settings Project", "SET"), "test", default);

        var cliModes = await client.GetFromJsonAsync<StudioProjectCliModesResponse>(
            $"/api/v1/studio/projects/{project.Name}/cli-modes");
        Assert.NotEmpty(cliModes!.Resolved);
        Assert.NotEmpty(cliModes.Available);

        var contextModes = await client.GetFromJsonAsync<StudioProjectCliContextModesResponse>(
            $"/api/v1/studio/projects/{project.Name}/cli-context-modes");
        Assert.NotEmpty(contextModes!.Resolved);

        var laneSort = await client.GetFromJsonAsync<StudioLaneSortStrategiesResponse>(
            $"/api/v1/studio/projects/{project.Name}/lane-sort-strategies");
        Assert.Equal(8, laneSort!.Resolved.Count);

        var waitPolicy = await client.GetFromJsonAsync<StudioProjectCliQuotaWaitPolicy>(
            $"/api/v1/studio/projects/{project.Name}/quota-wait-policy");
        Assert.Equal("global", waitPolicy!.Source);

        var settingsMap = await client.GetFromJsonAsync<Dictionary<string, StudioProjectSettingsEntry>>(
            "/api/v1/studio/projects/settings");
        Assert.True(settingsMap!.ContainsKey("Settings Project"));
        Assert.Equal("local", settingsMap["Settings Project"].ExecutionLocation);

        var notFound = await client.GetAsync("/api/v1/studio/projects/does-not-exist/cli-modes");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task Pipeline_catalogue_returns_the_standard_step_list()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioAdminApiFactory(temp.Path);
        using var client = Client(factory);

        var catalogue = await client.GetFromJsonAsync<StudioPipelineCatalogue>("/api/v1/studio/projects/pipeline-catalogue");

        Assert.Equal("standard-task-pipeline", catalogue!.PipelineId);
        Assert.Contains(catalogue.Steps, step => step.Id == "core-agent-run");
        Assert.Equal(4, catalogue.Steps.Count(step => step.Kind == "aspect"));
    }

    [Fact]
    public async Task Search_finds_tasks_by_key_and_title()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioAdminApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Search WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Search Project", "SRC"), "test", default);
        await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Fix the flaky retry loop"), "test", default);

        var results = await client.GetFromJsonAsync<StudioSearchResponse>("/api/v1/studio/search?q=flaky&limit=10");

        Assert.Single(results!.Tasks);
        Assert.Equal("Search Project", results.Tasks[0].ProjectName);

        var tooShort = await client.GetFromJsonAsync<StudioSearchResponse>("/api/v1/studio/search?q=f");
        Assert.Empty(tooShort!.Tasks);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "studio-admin-api-test");
        return client;
    }

    private sealed class StudioAdminApiFactory(string dataDirectory) : WebApplicationFactory<Program>
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
