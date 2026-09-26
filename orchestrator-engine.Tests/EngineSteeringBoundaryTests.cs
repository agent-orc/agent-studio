extern alias TaskServerAssembly;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.OrchestratorEngine;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrchestratorEngine.Tests;

public sealed class EngineSteeringBoundaryTests
{
    [Fact]
    public async Task Engine_steers_only_through_authenticated_task_server_urls_without_a_daemon()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"engine-steering-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var factory = new SteeringFactory(directory);
            using var setup = factory.CreateClient();
            var store = factory.Services.GetRequiredService<TaskServerStore>();
            var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Engine"), "setup", default);
            var project = await store.CreateProjectAsync(
                new CreateProjectRequest(workspace.WorkspaceId, "Engine", "ENG"), "setup", default);
            var task = await store.CreateTaskAsync(project.ProjectId,
                new CreateTaskRequest("Steer", State: "2-ready"), "setup", default);

            using var transport = factory.CreateClient();
            transport.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", SteeringFactory.EngineToken);
            transport.DefaultRequestHeaders.Add("X-Actor-Id", "forged-client-actor");
            using var engine = new EngineTaskServerClient(transport);
            var receipt = await engine.ApplySteeringActionAsync(project.ProjectId, task.TaskId,
                new SteeringActionRequest(1, "park-engine-1", "park", task.Version, 0, "capacity pause"), default);
            Assert.Equal("bootstrap-engine", receipt.Actor);
            Assert.Equal("capacity pause", receipt.Reason);

            var readBack = await engine.GetSteeringActionAsync(
                project.ProjectId, task.TaskId, receipt.CommandId, default);
            Assert.Equal(receipt, readBack);

            var stale = await Assert.ThrowsAsync<EngineTaskServerException>(() =>
                engine.ApplySteeringActionAsync(project.ProjectId, task.TaskId,
                    new SteeringActionRequest(1, "stale-engine-1", "queue", task.Version, 0, "old view"), default));
            Assert.Equal((int)HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Equal("0-backlog", (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!.State);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class SteeringFactory(string directory) : WebApplicationFactory<TaskServerAssembly::Program>
    {
        public const string EngineToken = "engine-steering-test-token-000000000000000001";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AUTH"] = "bearer",
                    ["TaskServer:DataDirectory"] = directory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["ENGINE_AUTH_TOKEN"] = EngineToken,
                }));
        }
    }
}
