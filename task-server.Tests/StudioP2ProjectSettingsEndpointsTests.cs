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

public sealed class StudioP2ProjectSettingsEndpointsTests
{
    [Fact]
    public async Task Simple_project_settings_upsert_independently_and_report_full_state()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioP2ProjectSettingsApiFactory(temp.Path);
        using var client = Client(factory);
        var project = await SeedProjectAsync(factory, "Settings Project", "SET");
        var basePath = $"/api/v1/studio/projects/{project.ProjectId}";

        var autoCommit = await client.PutAsJsonAsync($"{basePath}/auto-commit", new SetAutoCommitRequest(true));
        autoCommit.EnsureSuccessStatusCode();
        var afterAutoCommit = (await autoCommit.Content.ReadFromJsonAsync<StudioProjectSettingsDto>())!;
        Assert.True(afterAutoCommit.AutoCommit);
        Assert.Equal("manual", afterAutoCommit.AutoPushStrategy);

        var maxParallelism = await client.PutAsJsonAsync($"{basePath}/max-parallelism", new SetMaxParallelismRequest(4));
        maxParallelism.EnsureSuccessStatusCode();
        var afterMaxParallelism = (await maxParallelism.Content.ReadFromJsonAsync<StudioProjectSettingsDto>())!;
        // A later setter must not clobber a field an earlier setter wrote.
        Assert.True(afterMaxParallelism.AutoCommit);
        Assert.Equal(4, afterMaxParallelism.MaxParallelism);

        var invalidParallelism = await client.PutAsJsonAsync($"{basePath}/max-parallelism", new SetMaxParallelismRequest(0));
        Assert.Equal(HttpStatusCode.BadRequest, invalidParallelism.StatusCode);

        var orchestratorModel = await client.PutAsJsonAsync(
            $"{basePath}/orchestrator-model", new SetOrchestratorModelRequest("gpt-5"));
        orchestratorModel.EnsureSuccessStatusCode();
        Assert.Equal("gpt-5", (await orchestratorModel.Content.ReadFromJsonAsync<StudioProjectSettingsDto>())!.OrchestratorModel);

        var quotaWaitPolicy = await client.PutAsJsonAsync(
            $"{basePath}/quota-wait-policy", new SetQuotaWaitPolicyRequest("""{"mode":"wait"}"""));
        quotaWaitPolicy.EnsureSuccessStatusCode();
        Assert.Equal(
            """{"mode":"wait"}""",
            (await quotaWaitPolicy.Content.ReadFromJsonAsync<StudioProjectSettingsDto>())!.QuotaWaitPolicyJson);

        var crashRecovery = await client.PutAsJsonAsync($"{basePath}/crash-recovery", new SetCrashRecoveryRequest(true));
        crashRecovery.EnsureSuccessStatusCode();
        Assert.True((await crashRecovery.Content.ReadFromJsonAsync<StudioProjectSettingsDto>())!.CrashRecoveryEnabled);

        var laneSortStrategy = await client.PutAsJsonAsync(
            $"{basePath}/lane-sort-strategy", new SetLaneSortStrategyRequest("priority"));
        laneSortStrategy.EnsureSuccessStatusCode();
        Assert.Equal("priority", (await laneSortStrategy.Content.ReadFromJsonAsync<StudioProjectSettingsDto>())!.LaneSortStrategy);

        var cliMode = await client.PutAsJsonAsync($"{basePath}/cli-mode", new SetCliModeRequest("headless"));
        cliMode.EnsureSuccessStatusCode();
        Assert.Equal("headless", (await cliMode.Content.ReadFromJsonAsync<StudioProjectSettingsDto>())!.CliMode);

        var cliContextMode = await client.PutAsJsonAsync(
            $"{basePath}/cli-context-mode", new SetCliContextModeRequest("minimal"));
        cliContextMode.EnsureSuccessStatusCode();
        Assert.Equal("minimal", (await cliContextMode.Content.ReadFromJsonAsync<StudioProjectSettingsDto>())!.CliContextMode);

        var autoPushStrategy = await client.PutAsJsonAsync(
            $"{basePath}/auto-push-strategy", new SetAutoPushStrategyRequest("always"));
        autoPushStrategy.EnsureSuccessStatusCode();
        var finalState = (await autoPushStrategy.Content.ReadFromJsonAsync<StudioProjectSettingsDto>())!;
        Assert.Equal("always", finalState.AutoPushStrategy);
        // Every earlier setter's value is still present on the final read.
        Assert.True(finalState.AutoCommit);
        Assert.Equal(4, finalState.MaxParallelism);
        Assert.Equal("gpt-5", finalState.OrchestratorModel);
        Assert.True(finalState.CrashRecoveryEnabled);
        Assert.Equal("priority", finalState.LaneSortStrategy);
        Assert.Equal("headless", finalState.CliMode);
        Assert.Equal("minimal", finalState.CliContextMode);

        var missingProject = await client.PutAsJsonAsync(
            "/api/v1/studio/projects/does-not-exist/auto-commit", new SetAutoCommitRequest(true));
        Assert.Equal(HttpStatusCode.NotFound, missingProject.StatusCode);
    }

    [Fact]
    public async Task Project_update_renames_and_bumps_version_via_either_route_param_call_site()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioP2ProjectSettingsApiFactory(temp.Path);
        using var client = Client(factory);
        var project = await SeedProjectAsync(factory, "Original Name", "REN");

        var rename = await client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}", new UpdateStudioProjectRequest("Renamed Project"));
        rename.EnsureSuccessStatusCode();
        var renamed = (await rename.Content.ReadFromJsonAsync<ProjectDto>())!;
        Assert.Equal("Renamed Project", renamed.Name);
        Assert.Equal(project.Version + 1, renamed.Version);

        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var persisted = await store.RequireProjectAsync(project.ProjectId, default);
        Assert.Equal("Renamed Project", persisted.Name);
        Assert.Equal(project.Version + 1, persisted.Version);
    }

    [Fact]
    public async Task Project_delete_is_blocked_while_tasks_exist_and_succeeds_once_empty()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioP2ProjectSettingsApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var project = await SeedProjectAsync(factory, "Deletable Project", "DEL");
        await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Blocking task"), "test", default);

        var blocked = await client.DeleteAsync($"/api/v1/studio/projects/{project.ProjectId}");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("project-has-tasks", (await blocked.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var emptyProject = await SeedProjectAsync(factory, "Empty Project", "EMP");
        var deleted = await client.DeleteAsync($"/api/v1/studio/projects/{emptyProject.ProjectId}");
        deleted.EnsureSuccessStatusCode();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => store.RequireProjectAsync(emptyProject.ProjectId, default));
    }

    [Fact]
    public async Task Ownership_mapping_upsert_creates_then_updates_the_same_mapping_id()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioP2ProjectSettingsApiFactory(temp.Path);
        using var client = Client(factory);
        var project = await SeedProjectAsync(factory, "Ownership Project", "OWN");
        var mappingPath = $"/api/v1/studio/projects/{project.ProjectId}/ownership-mappings/map-1";

        var created = await client.PutAsJsonAsync(mappingPath, new UpsertProjectOwnershipMappingRequest("src/**", "team-a"));
        created.EnsureSuccessStatusCode();
        var createdMapping = (await created.Content.ReadFromJsonAsync<ProjectOwnershipMappingDto>())!;
        Assert.Equal("map-1", createdMapping.MappingId);
        Assert.Equal("src/**", createdMapping.Pattern);
        Assert.Equal("team-a", createdMapping.Owner);

        var updated = await client.PutAsJsonAsync(mappingPath, new UpsertProjectOwnershipMappingRequest("docs/**", "team-b"));
        updated.EnsureSuccessStatusCode();
        var updatedMapping = (await updated.Content.ReadFromJsonAsync<ProjectOwnershipMappingDto>())!;
        Assert.Equal("map-1", updatedMapping.MappingId);
        Assert.Equal("docs/**", updatedMapping.Pattern);
        Assert.Equal("team-b", updatedMapping.Owner);
    }

    [Fact]
    public async Task Project_urls_support_create_update_and_delete_scoped_to_their_project()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioP2ProjectSettingsApiFactory(temp.Path);
        using var client = Client(factory);
        var project = await SeedProjectAsync(factory, "URLs Project", "URL");
        var otherProject = await SeedProjectAsync(factory, "Other Project", "OTH");

        var create = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/urls",
            new CreateProjectUrlRequest("https://example.com", "Example"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var createdUrl = (await create.Content.ReadFromJsonAsync<ProjectUrlDto>())!;
        Assert.Equal("https://example.com", createdUrl.Url);
        Assert.Equal("Example", createdUrl.Label);

        var update = await client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/urls/{createdUrl.UrlId}",
            new UpdateProjectUrlRequest("https://example.com/updated", null));
        update.EnsureSuccessStatusCode();
        var updatedUrl = (await update.Content.ReadFromJsonAsync<ProjectUrlDto>())!;
        Assert.Equal("https://example.com/updated", updatedUrl.Url);
        Assert.Equal("Example", updatedUrl.Label);

        var updateWrongProject = await client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{otherProject.ProjectId}/urls/{createdUrl.UrlId}",
            new UpdateProjectUrlRequest("https://wrong.example.com", null));
        Assert.Equal(HttpStatusCode.NotFound, updateWrongProject.StatusCode);

        var deleteWrongProject = await client.DeleteAsync(
            $"/api/v1/studio/projects/{otherProject.ProjectId}/urls/{createdUrl.UrlId}");
        Assert.Equal(HttpStatusCode.NotFound, deleteWrongProject.StatusCode);

        var delete = await client.DeleteAsync($"/api/v1/studio/projects/{project.ProjectId}/urls/{createdUrl.UrlId}");
        delete.EnsureSuccessStatusCode();

        var deleteAgain = await client.DeleteAsync($"/api/v1/studio/projects/{project.ProjectId}/urls/{createdUrl.UrlId}");
        Assert.Equal(HttpStatusCode.NotFound, deleteAgain.StatusCode);
    }

    private static async Task<ProjectDto> SeedProjectAsync(
        WebApplicationFactory<Program> factory, string name, string prefix)
    {
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest($"{name} WS"), "test", default);
        return await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, name, prefix), "test", default);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "studio-p2-project-settings-test");
        return client;
    }

    private sealed class StudioP2ProjectSettingsApiFactory(string dataDirectory) : WebApplicationFactory<Program>
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
