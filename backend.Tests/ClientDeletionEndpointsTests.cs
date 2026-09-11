using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// HTTP-level coverage for <c>DELETE /api/clients/{id}/permanent</c> and
/// <c>POST /api/clients/retired/purge</c>: the 409 guards
/// (<see cref="ClientDeletionPolicy"/>) wired against real lease/attempt
/// facts, plus the dry-run vs. applied purge sweep.
/// </summary>
public sealed class ClientDeletionEndpointsTests : IDisposable
{
    private const string ProjectName = "delete-guard-project";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "agent-studio-client-delete-" + Guid.NewGuid().ToString("N"));
    private readonly string _watchPath;

    public ClientDeletionEndpointsTests()
    {
        _watchPath = Path.Combine(_root, "projects", ProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Delete_Refuses400_WhenTheClientIsNotRetired()
    {
        await using var factory = BuildFactory();
        using var client = NewHttpClient(factory);
        var id = await RegisterAsync(client, "not-retired-runner");

        var response = await client.DeleteAsync($"/api/clients/{id}/permanent");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("client-must-be-retired-before-delete", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Delete_Refuses409_WhenARetiredClientIsStillReportingActiveSlots()
    {
        await using var factory = BuildFactory();
        using var client = NewHttpClient(factory);
        var id = await RegisterAsync(client, "still-online-runner");
        await RetireAsync(client, id);

        // A stray heartbeat landing right after retirement can still bump
        // active slots back up - exactly the race the online guard defends
        // against.
        var store = factory.Services.GetRequiredService<ClientIdentityStore>();
        store.RecordRunnerActivity(id, activeSlots: 1, availableSlots: 0, claimed: false);

        var response = await client.DeleteAsync($"/api/clients/{id}/permanent");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("client-online", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Delete_Refuses409_WhenTheClientHoldsAnActiveRunLease()
    {
        await using var factory = BuildFactory();
        using var client = NewHttpClient(factory);
        var id = await RegisterAsync(client, "leased-runner");
        await RetireAsync(client, id);
        var taskKey = SeedProgressTask(factory, "AGT-DELETE-LEASE-1");

        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var write = authority.AcquireRun(
            taskKey, "repo", null, "executor-1", "host-1", 120, "idem-active", clientId: id);
        Assert.Equal(AttemptWriteStatus.Accepted, write.Status);

        var response = await client.DeleteAsync($"/api/clients/{id}/permanent");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("client-has-active-lease", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Delete_Refuses409_WhenTheClientsAttemptExpiredWithoutAConfirmedOutcome()
    {
        await using var factory = BuildFactory();
        using var client = NewHttpClient(factory);
        var id = await RegisterAsync(client, "process-unknown-runner");
        await RetireAsync(client, id);
        var taskKey = SeedProgressTask(factory, "AGT-DELETE-LEASE-2");

        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var write = authority.AcquireRun(
            taskKey, "repo", null, "executor-1", "host-1", 30, "idem-unknown", clientId: id);
        Assert.Equal(AttemptWriteStatus.Accepted, write.Status);
        authority.AgeRunLeaseForTests(write.RunAttempt!.AttemptId, TimeSpan.FromMinutes(5));

        var response = await client.DeleteAsync($"/api/clients/{id}/permanent");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("client-attempt-process-unknown", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Delete_Succeeds_AndTheIdentityIsGoneAfterward()
    {
        await using var factory = BuildFactory();
        using var client = NewHttpClient(factory);
        var id = await RegisterAsync(client, "clean-retired-runner");
        await RetireAsync(client, id);

        var response = await client.DeleteAsync($"/api/clients/{id}/permanent");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/clients/{id}")).StatusCode);
    }

    [Fact]
    public async Task Purge_DryRun_PreviewsWithoutDeleting_ThenApplyDeletesOnlyThePrefixMatch()
    {
        await using var factory = BuildFactory();
        using var client = NewHttpClient(factory);
        var a = await RegisterAsync(client, "e2e-leftover-a");
        var b = await RegisterAsync(client, "e2e-leftover-b");
        var kept = await RegisterAsync(client, "keep-me");
        await RetireAsync(client, a);
        await RetireAsync(client, b);
        await RetireAsync(client, kept);

        var dryRun = await client.PostAsJsonAsync(
            "/api/clients/retired/purge", new { prefix = "e2e-", dryRun = true });
        Assert.Equal(HttpStatusCode.OK, dryRun.StatusCode);
        var dryRunBody = await dryRun.Content.ReadFromJsonAsync<PurgeRetiredClientsResponse>();
        Assert.True(dryRunBody!.DryRun);
        Assert.Equal(0, dryRunBody.DeletedCount);
        Assert.Equal(2, dryRunBody.Candidates.Count);
        Assert.All(dryRunBody.Candidates, candidate => Assert.True(candidate.Eligible));
        Assert.All(dryRunBody.Candidates, candidate => Assert.False(candidate.Deleted));
        // A dry run never mutates state.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/clients/{a}")).StatusCode);

        var apply = await client.PostAsJsonAsync(
            "/api/clients/retired/purge", new { prefix = "e2e-", dryRun = false });
        var applyBody = await apply.Content.ReadFromJsonAsync<PurgeRetiredClientsResponse>();
        Assert.False(applyBody!.DryRun);
        Assert.Equal(2, applyBody.DeletedCount);
        Assert.All(applyBody.Candidates, candidate => Assert.True(candidate.Deleted));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/clients/{a}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/clients/{b}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/clients/{kept}")).StatusCode);
    }

    [Fact]
    public async Task Purge_ReportsAGuardedCandidateAsIneligible_AndLeavesItInPlace()
    {
        await using var factory = BuildFactory();
        using var client = NewHttpClient(factory);
        var blocked = await RegisterAsync(client, "e2e-blocked");
        await RetireAsync(client, blocked);
        var store = factory.Services.GetRequiredService<ClientIdentityStore>();
        store.RecordRunnerActivity(blocked, activeSlots: 1, availableSlots: 0, claimed: false);

        var apply = await client.PostAsJsonAsync(
            "/api/clients/retired/purge", new { prefix = "e2e-", dryRun = false });
        var body = await apply.Content.ReadFromJsonAsync<PurgeRetiredClientsResponse>();

        Assert.Equal(0, body!.DeletedCount);
        var candidate = Assert.Single(body.Candidates);
        Assert.False(candidate.Eligible);
        Assert.Equal("client-online", candidate.RefusalReason);
        Assert.False(candidate.Deleted);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/clients/{blocked}")).StatusCode);
    }

    /// <summary>
    /// Seeds a real in-progress task folder and forces the scanner's index
    /// cache to pick it up, then returns the exact <see cref="TaskInfo.TaskKey"/>
    /// the scanner assigned - the internal <c>watchPath::id</c> composite
    /// both <see cref="LeaseEndpoints"/> and the guard's own scan key off,
    /// distinct from the human-readable folder id.
    /// </summary>
    private string SeedProgressTask(WebApplicationFactory<Program> factory, string id)
    {
        var dir = Path.Combine(_watchPath, TaskStates.Progress, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            title = "Delete guard fixture",
            state = TaskStates.Progress,
            order = 1,
            agent = "claude",
        }));
        File.WriteAllText(Path.Combine(dir, "prompt.md"), "Prompt.");
        File.WriteAllText(Path.Combine(dir, "status.md"), "Result: pending.");

        factory.Services.GetRequiredService<TaskIndexCache>().ForceRefresh();
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var seeded = scanner.ScanAllJobs().Single(task => task.Id == id);
        return seeded.TaskKey;
    }

    private static async Task<string> RegisterAsync(HttpClient client, string displayName)
    {
        var response = await client.PostAsJsonAsync(
            "/api/clients/register", new { displayName, kind = "service" });
        response.EnsureSuccessStatusCode();
        var summary = await response.Content.ReadFromJsonAsync<ClientSummary>();
        return summary!.Id;
    }

    private static async Task RetireAsync(HttpClient client, string id)
    {
        var response = await client.PostAsync($"/api/clients/{id}/retire", content: null);
        response.EnsureSuccessStatusCode();
    }

    private static HttpClient NewHttpClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        // Every mutation needs a registered, non-retired actor identity; the
        // bootstrap default always exists.
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        return client;
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
                    ["WatchPaths:0:Name"] = ProjectName,
                    ["WatchPaths:0:Path"] = _watchPath,
                }));
        });
}
