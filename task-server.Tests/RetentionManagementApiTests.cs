using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

public sealed class RetentionManagementApiTests
{
    [Fact]
    public async Task Policy_plan_and_history_endpoints_enforce_version_and_validate_input()
    {
        using var temp = new TempDirectory();
        await using var factory = new RetentionApiFactory(temp.Path);
        using var client = Client(factory);

        var initial = await client.GetFromJsonAsync<RetentionPolicyDto>("/api/v1/management/retention/policy");
        Assert.NotNull(initial);
        Assert.Equal(0, initial.Version);
        Assert.Equal(new FullBackupRetentionDto(), initial.FullBackups);

        var updated = await client.PutAsJsonAsync(
            "/api/v1/management/retention/policy",
            new UpdateRetentionPolicyRequest(initial.Rules, initial.Version, new FullBackupRetentionDto(6, 4, 12)));
        updated.EnsureSuccessStatusCode();
        Assert.Equal(1, (await updated.Content.ReadFromJsonAsync<RetentionPolicyDto>())!.Version);

        var stale = await client.PutAsJsonAsync(
            "/api/v1/management/retention/policy",
            new UpdateRetentionPolicyRequest(initial.Rules, initial.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("resource-version-mismatch", (await stale.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var invalid = await client.PutAsJsonAsync(
            "/api/v1/management/retention/policy",
            new UpdateRetentionPolicyRequest(initial.Rules.Take(1).ToList(), 1));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid-request", (await invalid.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var plan = await client.PostAsJsonAsync("/api/v1/management/retention/plan", new RunRetentionRequest());
        plan.EnsureSuccessStatusCode();
        Assert.Equal(0, (await plan.Content.ReadFromJsonAsync<RetentionPlanDto>())!.ActionCount);
        var runs = await client.GetFromJsonAsync<List<RetentionRunSummaryDto>>("/api/v1/management/retention/runs");
        Assert.Contains(runs!, run => run.Mode == "plan" && run.Trigger == "manual");
        var schedule = await client.GetFromJsonAsync<RetentionScheduleDto>("/api/v1/management/retention/schedule");
        Assert.False(schedule!.Enabled);
        Assert.InRange(schedule.ServerLocalHour, 0, 23);
        Assert.Null(schedule.NextRunAt);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/v1/management/retention/runs/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/v1/management/retention/archive/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/v1/management/retention/policy/projects/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/v1/management/retention/archive/missing", new RetentionArchiveTaskRequest(2))).StatusCode);
    }

    [Fact]
    public async Task Archived_artifact_content_returns_409_with_manifest_reference()
    {
        using var temp = new TempDirectory();
        await using var factory = new RetentionApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var seed = await SeedClaimedTaskAsync(store);
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("api log\n", 25)));
        await store.IngestArtifactAsync(
            seed.RunId,
            new ArtifactIngestRequest(
                "art-api-archive",
                "logs/cli-output.log",
                "text/plain",
                Convert.ToBase64String(bytes),
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                "api-archive-ingest",
                seed.Fence),
            "api-test",
            default);
        var leasedArchive = await client.PostAsJsonAsync(
            $"/api/v1/management/retention/archive/{seed.Task.TaskKey}",
            new RetentionArchiveTaskRequest(1));
        Assert.Equal(HttpStatusCode.Conflict, leasedArchive.StatusCode);
        Assert.Equal("task-lease-active", (await leasedArchive.Content.ReadFromJsonAsync<ApiError>())!.Code);
        var terminal = await store.UpdateTaskAsync(
            seed.ProjectId,
            seed.Task.TaskId,
            new UpdateTaskRequest(null, null, "7-archive", seed.Task.Version),
            "api-test",
            default);
        await store.ReleaseLeaseAsync(
            seed.RunId,
            new LeaseReleaseRequest("runner-api", seed.InstanceId, seed.LeaseId, seed.Fence, "completed"),
            "api-test",
            default);

        var archive = await client.PostAsJsonAsync(
            $"/api/v1/management/retention/archive/{terminal!.TaskKey}",
            new RetentionArchiveTaskRequest(1));
        archive.EnsureSuccessStatusCode();

        var content = await client.GetAsync($"/api/v1/runs/{seed.RunId}/artifacts/art-api-archive/content");
        Assert.Equal(HttpStatusCode.Conflict, content.StatusCode);
        using var error = JsonDocument.Parse(await content.Content.ReadAsStringAsync());
        Assert.Equal("artifact-archived", error.RootElement.GetProperty("code").GetString());
        Assert.Equal(terminal.TaskId, error.RootElement.GetProperty("detail").GetProperty("taskId").GetString());
        Assert.Equal(
            $"/api/v1/management/retention/archive/{terminal.TaskId}",
            error.RootElement.GetProperty("detail").GetProperty("manifestUrl").GetString());

        var manifest = await client.GetFromJsonAsync<RetentionArchiveManifestDto>(
            $"/api/v1/management/retention/archive/{terminal.TaskId}");
        Assert.Equal("cold", manifest!.State);
        Assert.Equal(1, Assert.Single(manifest.Stages).Stage);
    }

    [Fact]
    public async Task Manual_archive_refuses_a_task_in_a_policy_protected_lane()
    {
        using var temp = new TempDirectory();
        await using var factory = new RetentionApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Protected lane"), "api-test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Protected lane", "PRL"), "api-test", default);
        var task = await store.CreateTaskAsync(
            project.ProjectId, new CreateTaskRequest("Do not archive", State: "2-ready"), "api-test", default);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/management/retention/archive/{task.TaskId}",
            new RetentionArchiveTaskRequest(2));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("retention-lane-protected", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "retention-api-test");
        return client;
    }

    private static async Task<(TaskDto Task, string ProjectId, string RunId, string LeaseId, string InstanceId, long Fence)>
        SeedClaimedTaskAsync(TaskServerStore store)
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Retention API"), "api-test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Retention API", "RAP"), "api-test", default);
        var task = await store.CreateTaskAsync(
            project.ProjectId, new CreateTaskRequest("Archive over HTTP", State: "2-ready"), "api-test", default);
        const string instance = "runner-api:1";
        await store.RegisterRunnerAsync(
            "runner-api",
            new RegisterRunnerRequest("runner-api", "host-api", instance, "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]),
            "api-test",
            default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-api", instance), "api-test", default);
        return (claim.Task!, project.ProjectId, claim.Run!.RunId, claim.Lease!.LeaseId, instance, claim.Lease.Fence);
    }

    private sealed class RetentionApiFactory(string dataDirectory) : WebApplicationFactory<Program>
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
