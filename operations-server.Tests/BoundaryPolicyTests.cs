using System.Net;
using System.Text.Json;
using AgentStudio.Operations.Contracts;
using AgentStudio.Operations.Server.Features.Access;
using AgentStudio.Operations.Server.Features.Dispatch;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace OperationsServer.Tests;

public sealed class BoundaryPolicyTests
{
    [Theory]
    [InlineData("agent", "stale-attempt")]
    [InlineData("boot", "stale-attempt")]
    [InlineData("fence", "stale-attempt")]
    [InlineData("state", "attempt-not-running")]
    [InlineData("lease", "lease-expired")]
    [InlineData("deadline", "lease-expired")]
    public void Lease_policy_checks_identity_generation_fence_state_and_both_expiries(string change, string expected)
    {
        var now = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
        var command = Fixture.NewCommand(now);
        var attempt = new OperationAttempt("attempt", "edge", command, "host-a", "boot", 7, now.AddSeconds(30), "running", []);
        Assert.Null(OperationPolicy.LeaseDenial(attempt, "host-a", "boot", 7, now));
        var agent = "host-a"; var boot = "boot"; long fence = 7;
        switch (change)
        {
            case "agent": agent = "host-b"; break;
            case "boot": boot = "old"; break;
            case "fence": fence = 6; break;
            case "state": attempt = attempt with { State = "succeeded" }; break;
            case "lease": attempt = attempt with { LeaseUntil = now }; break;
            case "deadline": attempt = attempt with { Command = command with { Deadline = now } }; break;
        }
        Assert.Equal(expected, OperationPolicy.LeaseDenial(attempt, agent, boot, fence, now));
    }

    [Fact]
    public void Bootstrap_reuses_secrets_and_separates_service_and_agent_authority()
    {
        var root = Path.Combine(Path.GetTempPath(), "operations-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var principals = Path.Combine(root, "principals"); var agent = Path.Combine(root, "agent"); var client = Path.Combine(root, "client");
            OperationsBootstrap.Run(principals, agent, client);
            var first = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllText);
            OperationsBootstrap.Run(principals, agent, client);
            foreach (var file in first) Assert.Equal(file.Value, File.ReadAllText(file.Key));
            var access = new OperationsAccess(Path.Combine(principals, "principals.json"));
            var service = access.Authenticate(File.ReadAllText(Path.Combine(client, "token")))!;
            var executor = access.Authenticate(File.ReadAllText(Path.Combine(agent, "token")))!;
            Assert.Equal("service", service.Kind);
            Assert.Equal("agent", executor.Kind);
            Assert.NotEqual(service.TokenSha256, executor.TokenSha256);
            Assert.DoesNotContain("agents.connect", service.Scopes);
            Assert.DoesNotContain("maintenance.execute", executor.Scopes);
            Assert.DoesNotContain(File.ReadAllText(Path.Combine(agent, "token")), first[Path.Combine(principals, "principals.json")]);
            if (!OperatingSystem.IsWindows())
                foreach (var file in first.Keys) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Permit_client_binds_correlation_uses_task_protocol_and_fails_closed_when_unconfigured()
    {
        var command = Fixture.NewCommand(DateTimeOffset.UtcNow) with { Subject = new("task", "run", 4), Permit = "opaque" };
        using var client = new HttpClient(new InspectPermitHandler());
        var absent = new OperationPermitAuthority(client, new ConfigurationBuilder().Build());
        Assert.Null(await absent.ValidateAsync("edge", command, default));
        var path = Path.Combine(Path.GetTempPath(), "operation-permit-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, "introspection-credential");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Operations:PermitIntrospectionUrl"] = "https://task-server.example/api/v1/operations/permits/introspect",
                ["Operations:PermitCredentialFile"] = path,
            }).Build();
            var authority = new OperationPermitAuthority(client, configuration);
            Assert.Equal(command.Deadline, await authority.ValidateAsync("edge", command, default));
            File.Delete(path);
            Assert.Null(await authority.ValidateAsync("edge", command, default));
            configuration["Operations:PermitIntrospectionUrl"] = "http://untrusted.example/introspect";
            Assert.Null(await authority.ValidateAsync("edge", command, default));
        }
        finally { File.Delete(path); }
    }

    private sealed class InspectPermitHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("introspection-credential", request.Headers.Authorization!.Parameter);
            Assert.Contains("X-Task-Protocol-Version", request.Headers.Select(h => h.Key));
            var probe = JsonSerializer.Deserialize<PermitIntrospectionRequest>(await request.Content!.ReadAsStringAsync(ct), OperationsProtocol.Json)!;
            Assert.Equal("correlation", probe.CorrelationId);
            Assert.Equal("operations-server", probe.ResultAudience);
            Assert.Equal("edge", probe.PrincipalId);
            Assert.Equal("host-a", probe.AgentId);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new PermitIntrospectionResponse(true, probe.Deadline), OperationsProtocol.Json), System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
