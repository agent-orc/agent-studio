using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// Contract for the two core-attach routes the Studio switched onto in
/// AGT-2983 without a pre-existing Studio-shaped test: the single-task detail
/// read (<c>GET /api/v1/projects/{projectId}/tasks/{taskId}</c>) and the
/// workbench orchestrator turn. Runs under <c>AUTH=bearer</c> so the Studio
/// principal, an anonymous caller, and a principal without <c>tasks:read</c>
/// are all distinguishable.
/// </summary>
public sealed class StudioTaskDetailContractTests
{
    private const string StudioToken = "studio-detail-token-00000000000000000000000001";
    private const string EngineToken = "engine-detail-token-00000000000000000000000001";

    [Fact]
    public async Task Task_detail_returns_the_board_task_dto_for_every_studio_project_address()
    {
        using var temp = new TempDirectory();
        await using var factory = new BearerFactory(temp.Path);
        using var studio = Client(factory, StudioToken);
        var (project, task) = await SeedAsync(studio, "Detail Project", "DET");
        var otherWorkspace = await PostAsync<CreateWorkspaceRequest, WorkspaceDto>(
            studio, "/api/v1/workspaces", new CreateWorkspaceRequest("Other detail workspace"));
        var otherProject = await PostAsync<CreateProjectRequest, ProjectDto>(
            studio, "/api/v1/projects", new CreateProjectRequest(otherWorkspace.WorkspaceId, "Other Project", "OTH"));

        var board = (await studio.GetFromJsonAsync<StudioBoardResponse>("/api/v1/studio/board"))!;
        var boardTask = Assert.Single(board.Backlog, item => item.TaskId == task.TaskId);

        // The Studio addresses a project by id, by its name (the identity the
        // Angular services carry), or with the unscoped "-" token when only
        // the task id is known; the task itself by id or by task key.
        foreach (var path in new[]
                 {
                     $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}",
                     $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskKey}",
                     $"/api/v1/projects/{Uri.EscapeDataString(project.Name)}/tasks/{task.TaskId}",
                     $"/api/v1/projects/{Uri.EscapeDataString(project.Name.ToLowerInvariant())}/tasks/{task.TaskKey.ToLowerInvariant()}",
                     $"/api/v1/projects/{TaskServerStore.UnscopedProjectToken}/tasks/{task.TaskId}",
                 })
        {
            var response = await studio.GetAsync(path);
            Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}.");
            var detail = (await response.Content.ReadFromJsonAsync<TaskDto>())!;
            Assert.Equal(boardTask, detail);
        }

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await studio.GetAsync($"/api/v1/projects/{otherProject.ProjectId}/tasks/{task.TaskId}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await studio.GetAsync($"/api/v1/projects/no-such-project/tasks/{task.TaskId}")).StatusCode);
    }

    [Fact]
    public async Task Task_detail_is_denied_without_a_principal_or_without_tasks_read()
    {
        using var temp = new TempDirectory();
        await using var factory = new BearerFactory(temp.Path);
        using var studio = Client(factory, StudioToken);
        var (project, task) = await SeedAsync(studio, "Scoped Project", "SCP");
        var path = $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}";

        using var anonymous = Client(factory, credential: null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);

        var issued = await studio.PostAsJsonAsync(
            "/api/v1/management/principals",
            new CreatePrincipalRequest(
                "runner-without-read", TaskServerPrincipalKinds.Runner, [TaskServerScopes.RunsWrite], "runner-without-read"));
        issued.EnsureSuccessStatusCode();
        var credential = (await issued.Content.ReadFromJsonAsync<IssuedPrincipalCredential>())!.Credential;
        using var writeOnlyRunner = Client(factory, credential);
        Assert.Equal(HttpStatusCode.Forbidden, (await writeOnlyRunner.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Workbench_turn_appends_a_user_turn_to_the_workbench_context()
    {
        using var temp = new TempDirectory();
        await using var factory = new BearerFactory(temp.Path);
        using var studio = Client(factory, StudioToken);
        var (project, _) = await SeedAsync(studio, "Workbench Project", "WBP");

        var response = await studio.PostAsJsonAsync(
            $"/api/v1/studio/orchestrator/sessions/workbench:{Uri.EscapeDataString(project.Name)}/decision-42/turns",
            new StudioOrchestratorTurnRequest("Discuss option B", "codex", "gpt-5", "medium"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<StudioOrchestratorTurnResponse>())!;
        Assert.Equal($"workbench:{project.Name}/decision-42", accepted.ContextKey);
        Assert.Equal("user", accepted.Turn.Role);
        Assert.Equal("Discuss option B", accepted.Turn.Body);

        var transcript = await studio.GetFromJsonAsync<OrchestratorContextTranscriptResponse>(
            $"/api/v1/orchestrator-contexts/projects/{project.ProjectId}/workbenches/decision-42/turns");
        Assert.Contains(transcript!.Turns, turn => turn.TurnId == accepted.Turn.TurnId);

        var empty = await studio.PostAsJsonAsync(
            $"/api/v1/studio/orchestrator/sessions/workbench:{project.ProjectId}/decision-42/turns",
            new StudioOrchestratorTurnRequest(" "));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    private static async Task<(ProjectDto Project, TaskDto Task)> SeedAsync(HttpClient client, string name, string prefix)
    {
        var workspace = await PostAsync<CreateWorkspaceRequest, WorkspaceDto>(
            client, "/api/v1/workspaces", new CreateWorkspaceRequest($"{name} workspace"));
        var project = await PostAsync<CreateProjectRequest, ProjectDto>(
            client, "/api/v1/projects", new CreateProjectRequest(workspace.WorkspaceId, name, prefix));
        var task = await PostAsync<CreateTaskRequest, TaskDto>(
            client, $"/api/v1/projects/{project.ProjectId}/tasks", new CreateTaskRequest($"{name} task"));
        return (project, task);
    }

    private static async Task<TResponse> PostAsync<TRequest, TResponse>(HttpClient client, string path, TRequest request)
    {
        using var response = await client.PostAsJsonAsync(path, request);
        var detail = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {detail}");
        return (await response.Content.ReadFromJsonAsync<TResponse>())!;
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, string? credential)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "studio-detail-contract");
        if (credential is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return client;
    }

    private sealed class BearerFactory(string dataDirectory) : WebApplicationFactory<Program>
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
                    ["AUTH"] = "bearer",
                    ["STUDIO_AUTH_TOKEN"] = StudioToken,
                    ["ENGINE_AUTH_TOKEN"] = EngineToken,
                }));
        }
    }
}
