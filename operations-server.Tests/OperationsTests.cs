using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Operations.Agent;
using AgentStudio.Operations.Contracts;
using AgentStudio.Operations.Server;
using AgentStudio.Operations.Server.Features.Access;
using AgentStudio.Operations.Server.Features.Dispatch;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OperationsServer.Tests;

public sealed class OperationsTests
{
    [Theory]
    [InlineData("Origin", "https://studio.example")]
    [InlineData("Cookie", "session=value")]
    [InlineData("Sec-Fetch-Site", "same-origin")]
    [InlineData("Forwarded", "host=studio.example")]
    [InlineData("X-Forwarded-Host", "studio.example")]
    public async Task Browser_and_forwarded_headers_are_denied_even_with_a_valid_bearer(string header, string value)
    {
        await using var fixture = await Fixture.Start();
        using var request = new HttpRequestMessage(HttpMethod.Get, Fixture.Api + "catalogue");
        request.Headers.Add(header, value);
        using var response = await fixture.Service.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Audience_protocol_rotation_revocation_and_cross_role_denials()
    {
        await using var f = await Fixture.Start();
        using var unknown = f.Client("unknown");
        Assert.Equal(HttpStatusCode.Unauthorized, (await unknown.GetAsync(Fixture.Api + "catalogue")).StatusCode);
        using var wrongAudience = f.Client("other-audience-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongAudience.GetAsync(Fixture.Api + "catalogue")).StatusCode);
        f.Service.DefaultRequestHeaders.Remove(OperationsProtocol.Header);
        Assert.Equal(HttpStatusCode.UpgradeRequired, (await f.Service.GetAsync(Fixture.Api + "catalogue")).StatusCode);
        f.Service.DefaultRequestHeaders.Add(OperationsProtocol.Header, "1");
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Agent.GetAsync(Fixture.Api + "catalogue")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Service.PostAsJsonAsync(Fixture.Api + "agents/register", f.Capability)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Agent.PostAsJsonAsync(Fixture.Api + "commands", f.Command())).StatusCode);
        f.SavePrincipals(f.Principals.Select(p => p.Id == "edge" ? p with { TokenSha256 = OperationsProtocol.Digest("rotated-token") } : p).ToArray());
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Service.GetAsync(Fixture.Api + "catalogue")).StatusCode);
        using var rotated = f.Client("rotated-token");
        Assert.Equal(HttpStatusCode.OK, (await rotated.GetAsync(Fixture.Api + "catalogue")).StatusCode);
        f.SavePrincipals(f.Principals.Select(p => p with { Revoked = true }).ToArray());
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Agent.PostAsJsonAsync(Fixture.Api + "agents/register", f.Capability)).StatusCode);
    }

    [Fact]
    public async Task Independent_frontend_principals_cannot_read_or_cancel_each_others_operations()
    {
        await using var f = await Fixture.Start();
        await f.Register();
        var attempt = await f.Submit();
        using var second = f.Client("second-edge-token");
        Assert.Equal(HttpStatusCode.Forbidden, (await second.GetAsync(Fixture.Api + $"attempts/{attempt.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await second.PostAsync(Fixture.Api + $"attempts/{attempt.Id}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Real_outbound_agent_executes_and_returns_typed_result_without_browser_or_task_store()
    {
        await using var f = await Fixture.Start();
        var agent = new OperationsAgentClient(f.Agent, "host-a", "boot-a", Path.Combine(f.Directory, "spool"), f.Clock);
        await agent.RegisterAsync(default);
        var attempt = await f.Submit();
        await agent.PollOnceAsync(default);
        var completed = await f.Service.GetFromJsonAsync<OperationAttempt>(Fixture.Api + $"attempts/{attempt.Id}");
        Assert.Equal("succeeded", completed!.State);
        Assert.True(completed.Result!.Output.GetProperty("processors").GetInt32() > 0);
        Assert.Equal(OperationsProtocol.Digest(completed.Result), completed.ResultDigest);
        Assert.Equal(new long[] { 1, 2, 3 }, completed.Events.Select(e => e.Cursor));
        Assert.Empty(System.IO.Directory.EnumerateFiles(Path.Combine(f.Directory, "spool")));
    }

    [Fact]
    public async Task Durable_idempotency_rejects_changed_payload_and_survives_service_reconstruction()
    {
        await using var f = await Fixture.Start();
        await f.Register();
        var command = f.Command();
        var first = await f.Submit(command);
        var replay = await f.Submit(command);
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Service.PostAsJsonAsync(Fixture.Api + "commands", command with { Actor = "different" })).StatusCode);
        var rebuilt = new OperationsCoordinator(new OperationsStore(f.Directory), f.Permits, f.Clock);
        Assert.Equal(first.Id, (await rebuilt.SubmitAsync(f.Principals[0], command, default)).Id);
    }

    [Fact]
    public async Task Compromised_agent_cannot_enroll_roots_other_hosts_or_complete_other_attempts()
    {
        await using var f = await Fixture.Start();
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Agent.PostAsJsonAsync(Fixture.Api + "agents/register", f.Capability with { AgentId = "host-b" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Agent.PostAsJsonAsync(Fixture.Api + "agents/register", f.Capability with { Roots = ["/etc"] })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Agent.PostAsJsonAsync(Fixture.Api + "agents/register", f.Capability with { Operations = new() { ["shell.exec"] = 1 } })).StatusCode);
        await f.Register();
        var attempt = await f.Submit();
        var dispatched = await f.Poll();
        using var other = f.Client("other-agent-token");
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Fixture.Api + "agents/host-a/poll", new AgentPollRequest("boot-a"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Fixture.Api + $"attempts/{attempt.Id}/result", f.Report(dispatched!))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Agent.PostAsync("/api/v1/tasks/arbitrary/move", null)).StatusCode);
    }

    [Fact]
    public async Task Lost_dispatch_and_report_replies_replay_without_duplicate_attempts()
    {
        await using var f = await Fixture.Start();
        await f.Register();
        await f.Submit();
        var first = await f.Poll();
        Assert.Equal(first!.Id, (await f.Poll())!.Id);
        var report = f.Report(first);
        var path = Fixture.Api + $"attempts/{first.Id}/result";
        Assert.Equal(HttpStatusCode.OK, (await f.Agent.PostAsJsonAsync(path, report)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Agent.PostAsJsonAsync(path, report)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Agent.PostAsJsonAsync(path, report with { Result = report.Result with { ExitClassification = "changed" } })).StatusCode);
        Assert.Null(await f.Poll());
    }

    [Fact]
    public async Task Lease_loss_is_unreachable_and_never_reassigns_without_no_overlap_evidence()
    {
        await using var f = await Fixture.Start();
        await f.Register();
        await f.Submit();
        var dispatched = (await f.Poll())!;
        f.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(await f.Poll());
        Assert.Equal("unreachable", (await f.Service.GetFromJsonAsync<OperationAttempt>(Fixture.Api + $"attempts/{dispatched.Id}"))!.State);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Agent.PostAsJsonAsync(Fixture.Api + $"attempts/{dispatched.Id}/result", f.Report(dispatched))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Agent.PostAsJsonAsync(Fixture.Api + $"attempts/{dispatched.Id}/renew", new AttemptLeaseRequest("boot-a", dispatched.Fence))).StatusCode);
    }

    [Fact]
    public async Task Cancellation_is_visible_to_agent_and_prevents_success_acceptance()
    {
        await using var f = await Fixture.Start();
        await f.Register();
        var queued = await f.Submit();
        var cancelled = await (await f.Service.PostAsync(Fixture.Api + $"attempts/{queued.Id}/cancel", null)).Content.ReadFromJsonAsync<OperationAttempt>();
        Assert.Equal("cancelled", cancelled!.State);
        Assert.Null(await f.Poll());
        await f.Submit(f.Command("second"));
        var active = (await f.Poll())!;
        await f.Service.PostAsync(Fixture.Api + $"attempts/{active.Id}/cancel", null);
        Assert.Equal("cancelling", (await f.Poll())!.State);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Agent.PostAsJsonAsync(Fixture.Api + $"attempts/{active.Id}/result", f.Report(active))).StatusCode);
        var report = f.Report(active);
        Assert.Equal(HttpStatusCode.OK, (await f.Agent.PostAsJsonAsync(Fixture.Api + $"attempts/{active.Id}/result", report with { Result = report.Result with { Outcome = "cancelled", Output = JsonSerializer.SerializeToElement(new { }) } })).StatusCode);
    }

    [Fact]
    public async Task Task_authority_is_checked_at_admission_dispatch_renewal_and_result()
    {
        await using var f = await Fixture.Start();
        await f.Register();
        var command = f.Command() with { Subject = new("task", "run", 4), Permit = "permit" };
        f.Permits.Active = false;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Service.PostAsJsonAsync(Fixture.Api + "commands", command)).StatusCode);
        f.Permits.Active = true;
        var queued = await f.Submit(command);
        f.Permits.Active = false;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Agent.PostAsJsonAsync(Fixture.Api + "agents/host-a/poll", new AgentPollRequest("boot-a"))).StatusCode);
        f.Permits.Active = true;
        var dispatched = (await f.Poll())!;
        f.Permits.Active = false;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Agent.PostAsJsonAsync(Fixture.Api + $"attempts/{queued.Id}/renew", new AttemptLeaseRequest("boot-a", dispatched.Fence))).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Agent.PostAsJsonAsync(Fixture.Api + $"attempts/{queued.Id}/result", f.Report(dispatched))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Service.GetAsync(Fixture.Api + $"attempts/{queued.Id}")).StatusCode);
    }

    [Theory]
    [InlineData("scope", "insufficient-scope")]
    [InlineData("agent", "agent-identity-mismatch")]
    [InlineData("operation", "unknown-operation")]
    [InlineData("deadline", "invalid-deadline")]
    [InlineData("digest", "input-digest-mismatch")]
    [InlineData("permit", "permit-required")]
    [InlineData("maintenance", "maintenance-scope-required")]
    [InlineData("input", "invalid-input")]
    public void Admission_policy_covers_each_denial(string scenario, string expected)
    {
        var principal = Fixture.Edge;
        var now = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
        var command = Fixture.NewCommand(now);
        switch (scenario)
        {
            case "scope": principal = principal with { Scopes = [] }; break;
            case "agent": command = command with { AgentId = "host-b" }; break;
            case "operation": command = command with { OperationId = "shell.exec" }; break;
            case "deadline": command = command with { Deadline = now }; break;
            case "digest": command = command with { InputDigest = "bad" }; break;
            case "permit": command = command with { Subject = new("task", "run", 1) }; break;
            case "maintenance": principal = principal with { Scopes = ["host.inspect"] }; break;
            case "input": command = command with { Input = JsonSerializer.SerializeToElement(new { path = "/etc" }) }; break;
        }
        Assert.Equal(expected, OperationPolicy.Admission(principal, command, now));
    }
}

internal sealed class Fixture : IAsyncDisposable
{
    public const string Api = "/api/operations/v1/";
    public static OperationsPrincipal Edge => new("edge", OperationsProtocol.Audience, OperationsProtocol.Digest("service-token"), "service",
        ["host.inspect", "maintenance.execute", "operations.read"], ["host-a"], []);
    public OperationsPrincipal[] Principals { get; } = [Edge,
        new("agent", OperationsProtocol.Audience, OperationsProtocol.Digest("agent-token"), "agent", ["agents.connect", "host.inspect"], ["host-a"], []),
        new("agent-b", OperationsProtocol.Audience, OperationsProtocol.Digest("other-agent-token"), "agent", ["agents.connect", "host.inspect"], ["host-b"], []),
        Edge with { Id = "edge-b", TokenSha256 = OperationsProtocol.Digest("second-edge-token") },
        Edge with { Id = "foreign", Audience = "task-server", TokenSha256 = OperationsProtocol.Digest("other-audience-token") }];
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "operations-tests", Guid.NewGuid().ToString("N"));
    public TestClock Clock { get; } = new();
    public TestPermits Permits { get; } = new();
    private WebApplication app = null!;
    public HttpClient Service { get; private set; } = null!;
    public HttpClient Agent { get; private set; } = null!;
    public AgentCapability Capability => new("host-a", "boot-a", "diagnostics", [], [], new() { ["host.inspect"] = 1 }, 1, 1, 1);
    public static async Task<Fixture> Start()
    {
        var fixture = new Fixture();
        System.IO.Directory.CreateDirectory(fixture.Directory);
        fixture.SavePrincipals(fixture.Principals);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Operations:PrincipalsFile"] = Path.Combine(fixture.Directory, "principals.json"),
            ["Operations:DataDirectory"] = fixture.Directory,
        });
        builder.Services.AddOperationsServer(builder.Configuration);
        builder.Services.AddSingleton<TimeProvider>(fixture.Clock);
        builder.Services.AddSingleton<IOperationPermitAuthority>(fixture.Permits);
        fixture.app = builder.Build();
        fixture.app.MapOperationsServer();
        await fixture.app.StartAsync();
        fixture.Service = fixture.Client("service-token");
        fixture.Agent = fixture.Client("agent-token");
        return fixture;
    }
    public void SavePrincipals(OperationsPrincipal[] principals) => File.WriteAllText(Path.Combine(Directory, "principals.json"), JsonSerializer.Serialize(new OperationsAccessDocument(principals), OperationsProtocol.Json));
    public HttpClient Client(string token)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add(OperationsProtocol.Header, "1");
        return client;
    }
    public async Task Register() => (await Agent.PostAsJsonAsync(Api + "agents/register", Capability)).EnsureSuccessStatusCode();
    public static OperationCommand NewCommand(DateTimeOffset now, string key = "key")
    {
        var input = JsonSerializer.SerializeToElement(new { });
        return new(key, "operator", "host-a", "host.inspect", 1, input, OperationsProtocol.Digest(input), now.AddMinutes(2), "correlation");
    }
    public OperationCommand Command(string key = "key") => NewCommand(Clock.GetUtcNow(), key);
    public async Task<OperationAttempt> Submit(OperationCommand? command = null)
    {
        var response = await Service.PostAsJsonAsync(Api + "commands", command ?? Command());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OperationAttempt>())!;
    }
    public async Task<OperationAttempt?> Poll()
    {
        var response = await Agent.PostAsJsonAsync(Api + "agents/host-a/poll", new AgentPollRequest("boot-a"));
        response.EnsureSuccessStatusCode();
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<OperationAttempt>();
    }
    public AttemptReportRequest Report(OperationAttempt attempt) => new("boot-a", attempt.Fence,
        new("succeeded", attempt.Command.InputDigest, "completed", JsonSerializer.SerializeToElement(new { os = "test", architecture = "x64", processors = 1 }), [], true, Clock.GetUtcNow(), Clock.GetUtcNow()));
    public async ValueTask DisposeAsync()
    {
        Service.Dispose(); Agent.Dispose();
        await app.DisposeAsync();
        System.IO.Directory.Delete(Directory, recursive: true);
    }
}
internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan value) => now += value;
}
internal sealed class TestPermits : IOperationPermitAuthority
{
    public bool Active { get; set; } = true;
    public Task<DateTimeOffset?> ValidateAsync(string principalId, OperationCommand command, CancellationToken cancellationToken) =>
        Task.FromResult<DateTimeOffset?>(command.Subject is null || Active ? command.Deadline : null);
}
