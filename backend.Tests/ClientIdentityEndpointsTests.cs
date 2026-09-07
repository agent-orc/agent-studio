using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ClientIdentityEndpointsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agent-studio-client-endpoints-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task CorruptIdentity_IsVisibleInTheRegistry_AndReturnsARepairableConflict()
    {
        var identities = Path.Combine(_root, "identities");
        Directory.CreateDirectory(identities);
        File.WriteAllBytes(Path.Combine(identities, "agent-runner-01.json"), new byte[4481]);
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

        var summaries = await client.GetFromJsonAsync<List<ClientSummary>>("/api/clients/");

        Assert.NotNull(summaries);
        Assert.Contains(summaries!, summary => summary.Id == DefaultClientIdentity.Id);
        var diagnostic = Assert.Single(summaries!, summary => summary.Id == "agent-runner-01");
        Assert.Equal("identity file corrupt: agent-runner-01.json", diagnostic.IdentityFileError);
        Assert.Contains("POST /api/clients/register", diagnostic.IdentityRestoreHint, StringComparison.Ordinal);

        var response = await client.GetAsync("/api/clients/agent-runner-01");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IdentityConflictResponse>();
        Assert.Equal("identity-file-corrupt", body?.Error);
        Assert.Equal("agent-runner-01.json", body?.File);
        Assert.Contains("POST /api/clients/register", body?.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_RemovesRetiredIdentity_AndWritesAuditEntry()
    {
        await using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<ClientIdentityStore>();
        var runner = store.Register(new RegisterClientRequest { DisplayName = "e2e-delete-one", Kind = ClientIdentityKinds.Service });
        store.RequestDrain(runner.Id, retireAfterDrain: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

        var response = await client.DeleteAsync($"/api/clients/{runner.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(store.Find(runner.Id));
        Assert.Contains(runner.Id, File.ReadAllText(Path.Combine(_root, ".audit", "client-identities.jsonl")));
    }

    [Fact]
    public async Task Delete_RefusesOnlineRetiredRunner()
    {
        await using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<ClientIdentityStore>();
        var runner = store.Register(new RegisterClientRequest { DisplayName = "e2e-online", Kind = ClientIdentityKinds.Service });
        store.RecordRunnerActivity(runner.Id, 0, 1, claimed: false);
        store.RequestDrain(runner.Id, retireAfterDrain: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

        var response = await client.DeleteAsync($"/api/clients/{runner.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("online", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(store.Find(runner.Id));
    }

    [Fact]
    public async Task Delete_RefusesRetiredRunnerWithActiveLease()
    {
        await using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<ClientIdentityStore>();
        var attempts = factory.Services.GetRequiredService<AgentStudio.Runner.AttemptAuthorityService>();
        var runner = store.Register(new RegisterClientRequest { DisplayName = "e2e-leased", Kind = ClientIdentityKinds.Service });
        store.RequestDrain(runner.Id, retireAfterDrain: true);
        attempts.AcquireRun("AGT-LEASE", "repo", null, runner.Id, runner.Id, 120, "lease-test", clientId: runner.Id);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

        var response = await client.DeleteAsync($"/api/clients/{runner.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("active-lease", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Delete_RefusesRetiredRunnerWithProcessUnknownAuthority()
    {
        await using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<ClientIdentityStore>();
        var attempts = factory.Services.GetRequiredService<AgentStudio.Runner.AttemptAuthorityService>();
        var runner = store.Register(new RegisterClientRequest { DisplayName = "e2e-process-unknown", Kind = ClientIdentityKinds.Service });
        store.RequestDrain(runner.Id, retireAfterDrain: true);
        attempts.AcquireRun("AGT-UNKNOWN", "repo", null, runner.Id, runner.Id, 120, "unknown-test", clientId: runner.Id);
        attempts.RotateAuthorityEpoch("test restart quarantine");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

        var response = await client.DeleteAsync($"/api/clients/{runner.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("process-unknown-attempt", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PurgeRetired_PreviewsThenDeletesOnlyMatchingPrefix()
    {
        await using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<ClientIdentityStore>();
        foreach (var name in new[] { "e2e-purge-a", "e2e-purge-b", "keep-retired" })
        {
            var runner = store.Register(new RegisterClientRequest { DisplayName = name, Kind = ClientIdentityKinds.Service });
            store.RequestDrain(runner.Id, retireAfterDrain: true);
        }
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

        var previewResponse = await client.PostAsJsonAsync("/api/clients/retired/purge", new { namePrefix = "e2e-", dryRun = true });
        var preview = await previewResponse.Content.ReadFromJsonAsync<PurgeRetiredClientsResponse>();
        Assert.Equal(2, preview?.Clients.Count);
        Assert.Equal(0, preview?.DeletedCount);

        var applyResponse = await client.PostAsJsonAsync("/api/clients/retired/purge", new { namePrefix = "e2e-", dryRun = false });
        var apply = await applyResponse.Content.ReadFromJsonAsync<PurgeRetiredClientsResponse>();
        Assert.Equal(2, apply?.DeletedCount);
        Assert.DoesNotContain(store.ListAll(), identity => identity.DisplayName.StartsWith("e2e-", StringComparison.Ordinal));
        Assert.Contains(store.ListAll(), identity => identity.DisplayName == "keep-retired");
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["Logging:BackendFile:LogDirectory"] = Path.Combine(_root, "logs"),
                }));
        });

    private sealed record IdentityConflictResponse
    {
        public string Error { get; init; } = "";
        public string File { get; init; } = "";
        public string Hint { get; init; } = "";
    }
}
