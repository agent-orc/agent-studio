using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.Operations.Agent;
using AgentStudio.Operations.Contracts;
using AgentStudio.Operations.Server.Features.Dispatch;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace OperationsServer.Tests;

public sealed class TaskAuthorityChannelTests
{
    [Fact]
    public async Task Task_permit_flows_over_authenticated_http_to_outbound_agent_and_lease_loss_denies_dispatch()
    {
        var directory = Path.Combine(Path.GetTempPath(), "operations-authority-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var clock = new TestClock();
            var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = directory }), clock);
            await store.InitializeAsync();
            var studio = await store.CreatePrincipalAsync(new("studio", TaskServerPrincipalKinds.Studio), "test", default);
            var operations = await store.CreatePrincipalAsync(new("operations", TaskServerPrincipalKinds.Operations), "test", default);
            var workspace = await store.CreateWorkspaceAsync(new("Workspace"), "test", default);
            var project = await store.CreateProjectAsync(new(workspace.WorkspaceId, "Project", "TS"), "test", default);
            var task = await store.CreateTaskAsync(project.ProjectId, new("Task", "Inspect host", "2-ready"), "test", default);
            await store.RegisterRunnerAsync("runner", new("runner", "host-a", "runner-boot", "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]), "test", default);
            var claim = await store.ClaimAsync(new("runner", "runner-boot"), "test", default);
            Assert.Equal("claimed", claim.Status);
            var subject = new OperationSubject(task.TaskId, claim.Run!.RunId, claim.Lease!.Fence);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["AUTH"] = "bearer" });
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton(TaskServerBootstrapOptions.Load(builder.Configuration));
            await using var taskApp = builder.Build();
            taskApp.UseRouting();
            taskApp.UseMiddleware<TaskServerAuthenticationMiddleware>();
            taskApp.UseMiddleware<TaskServerProtocolMiddleware>();
            taskApp.MapOperationPermitEndpoints();
            await taskApp.StartAsync();
            using var issuer = taskApp.GetTestClient();
            issuer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", studio.Credential);
            issuer.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
            using var transport = new InspectResponseHandler(taskApp.GetTestServer().CreateHandler());
            using var introspection = new HttpClient(transport);
            var credentialFile = Path.Combine(directory, "introspection-token");
            await File.WriteAllTextAsync(credentialFile, operations.Credential);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Operations:PermitIntrospectionUrl"] = "https://localhost/api/v1/operations/permits/introspect",
                ["Operations:PermitCredentialFile"] = credentialFile,
            }).Build();
            var authority = new OperationPermitAuthority(introspection, configuration);
            await using var f = await Fixture.Start(authority);
            var agent = new OperationsAgentClient(f.Agent, "host-a", "boot-a", Path.Combine(f.Directory, "spool"), clock);
            await agent.RegisterAsync(default);

            async Task<OperationCommand> AuthorizedCommand(string key)
            {
                var command = f.Command(key) with { Subject = subject, Deadline = clock.GetUtcNow().AddSeconds(20) };
                using var response = await issuer.PostAsJsonAsync("/api/v1/operations/permits", new IssueOperationPermitRequest(
                    "edge", command.Actor, subject, command.OperationId, command.Version, command.InputDigest,
                    command.AgentId, OperationsProtocol.Audience, command.CorrelationId, command.Deadline));
                response.EnsureSuccessStatusCode();
                var permit = (await response.Content.ReadFromJsonAsync<IssuedOperationPermitResponse>())!;
                return command with { Permit = permit.Permit, Deadline = permit.ValidUntil };
            }

            var command = await AuthorizedCommand("complete");
            var validation = await authority.ValidateAsync("edge", command, default);
            Assert.True(validation == command.Deadline, transport.LastResponse);
            var completed = await f.Submit(command);
            await agent.PollOnceAsync(default);
            var receipt = await f.Service.GetFromJsonAsync<OperationAttempt>(Fixture.Api + $"attempts/{completed.Id}");
            Assert.Equal("succeeded", receipt!.State);
            Assert.NotNull(receipt.ResultDigest);

            var queued = await f.Submit(await AuthorizedCommand("lease-loss"));
            await store.ReleaseLeaseAsync(claim.Run.RunId,
                new("runner", "runner-boot", claim.Lease.LeaseId, claim.Lease.Fence, "runner-process-missing"), "test", default);
            using var denied = await f.Agent.PostAsJsonAsync(Fixture.Api + "agents/host-a/poll", new AgentPollRequest("boot-a"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
            Assert.Equal("permit-unavailable", (await denied.Content.ReadFromJsonAsync<OperationError>())!.Code);
            var unchanged = await f.Service.GetFromJsonAsync<OperationAttempt>(Fixture.Api + $"attempts/{queued.Id}");
            Assert.Equal("queued", unchanged!.State);
            Assert.Null(unchanged.Result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class InspectResponseHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public string? LastResponse { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            LastResponse = $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}";
            return response;
        }
    }
}
