using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Registry;
using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The persona routes under <c>/api/workspaces/{id}/personas</c> in the
/// networked profile (persona team PT-1): activation without a step-up answers
/// <c>403 step-up-required</c>; after <c>POST /api/auth/step-up</c> it records
/// the descriptor revision, digest and shadow state; pause and deactivation
/// need no step-up; every change is in the IAM audit.
/// </summary>
public sealed class PersonaEndpointsTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "persona-endpoints-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));
    private string _ownerTotpSecret = string.Empty;

    [Fact]
    public async Task Activation_needs_a_step_up_and_pause_and_deactivation_do_not()
    {
        using var factory = BuildFactory();
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var workspaceId = factory.Services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock).Id;
        using var owner = await OwnerClientAsync(factory);
        var auditStart = security.ControlPlaneAudit.HeadSequence;
        var personas = $"/api/workspaces/{workspaceId}/personas";

        using (var refused = await owner.PostAsJsonAsync($"{personas}/operations-warden/activate", new { }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("step-up-required", (await Body(refused)).GetProperty("error").GetString());
        }

        await ReadOk(owner.PostAsJsonAsync("/api/auth/step-up", new { code = CurrentCode() }));
        var activation = await ReadOk(owner.PostAsJsonAsync($"{personas}/operations-warden/activate",
            new { allowance = new { tokensPerWeek = 120_000 }, reason = "Start in shadow" }));
        Assert.Equal(("shadow", "2026.11.1", 120_000), (activation.GetProperty("state").GetString(),
            activation.GetProperty("descriptorRevision").GetString(), activation.GetProperty("allowance").GetProperty("tokensPerWeek").GetInt32()));
        Assert.StartsWith("sha256:", activation.GetProperty("descriptorDigest").GetString());

        Assert.Equal("paused", (await ReadOk(owner.PostAsJsonAsync($"{personas}/operations-warden/pause", new { })))
            .GetProperty("state").GetString());
        Assert.Equal("inactive", (await ReadOk(owner.PostAsJsonAsync($"{personas}/operations-warden/deactivate", new { reason = "Retune" })))
            .GetProperty("state").GetString());

        var list = await ReadOk(owner.GetAsync(personas));
        Assert.Equal(workspaceId, list.GetProperty("workspaceId").GetString());
        var entries = list.GetProperty("personas").EnumerateArray().ToList();
        Assert.Equal(11, entries.Count);
        var warden = entries.Single(entry => entry.GetProperty("id").GetString() == "operations-warden");
        Assert.Equal("inactive", warden.GetProperty("activation").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null,
            entries.Single(entry => entry.GetProperty("id").GetString() == "security-officer").GetProperty("activation").ValueKind);

        using (var unknown = await owner.PostAsJsonAsync($"/api/workspaces/elsewhere/personas/operations-warden/pause", new { }))
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(["persona.activate", "persona.pause", "persona.deactivate"],
            security.ControlPlaneAudit.Read(auditStart + 1, 100)
                .Where(entry => entry.Target == $"persona:{workspaceId}/operations-warden").Select(entry => entry.Operation));
    }

    private static async Task<JsonElement> ReadOk(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private string CurrentCode()
    {
        _clock.Advance(TimeSpan.FromSeconds(30));
        return SecondFactorCodes.CurrentTotp(_ownerTotpSecret, _clock.GetUtcNow().UtcDateTime);
    }

    private async Task<HttpClient> OwnerClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = true });
        client.DefaultRequestHeaders.Add("Origin", "https://studio.test");
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "first.owner", password = Password, secondFactorCode = CurrentCode(),
        });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("X-CSRF-Token", (await response.Content.ReadFromJsonAsync<AuthStatusResponse>())!.CsrfToken!);
        return client;
    }

    private WebApplicationFactory<Program> BuildFactory()
    {
        Directory.CreateDirectory(_workspace);
        var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = "networked",
                ["AllowedHosts"] = "studio.test",
                ["DeliveryChain:Guarded"] = "false",
                ["Secrets:KeyFile"] = Path.Combine(_workspace + "-keys", "secrets.kek"),
                ["Secrets:KeyEnvironmentVariable"] = "AGENT_STUDIO_TEST_UNSET_KEK_" + Guid.NewGuid().ToString("N"),
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
            });
        });
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var bootstrap = store.Bootstrap(new BootstrapRequest("first.owner", Password, null));
        var owner = store.AuthenticateSession(bootstrap.SessionToken)!;
        _ownerTotpSecret = store.BeginTotpEnrollment(owner);
        store.ConfirmTotpEnrollment(owner, SecondFactorCodes.CurrentTotp(_ownerTotpSecret, _clock.GetUtcNow().UtcDateTime));
        return factory;
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
        if (Directory.Exists(_workspace + "-keys")) Directory.Delete(_workspace + "-keys", true);
    }
}
