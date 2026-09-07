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
/// Endpoint-level coverage for permanent client-identity deletion
/// (<c>DELETE /api/clients/{id}/permanent</c>) and bulk retired-identity
/// purge (<c>POST /api/clients/retired/purge</c>): the not-found /
/// must-be-retired / active-lease guards, the JSONL audit trail, and
/// dry-run vs. apply purge semantics including prefix filtering, the
/// default-identity exclusion, and per-candidate active-lease skipping.
///
/// <see cref="ClientIdentityTests.RetiredHost_CanBeRevived_ThenPermanentlyDeleted"/>
/// already covers <c>ClientIdentityStore.PermanentlyDelete</c>'s own
/// kind-must-be-retired rule at the store level; this file stays at the
/// HTTP/endpoint level and focuses on the guards <c>ClientEndpoints</c>
/// itself adds on top (404/409 bodies, the active-lease check, the purge
/// batch semantics).
/// </summary>
public sealed class ClientIdentityDeletionEndpointsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agent-studio-client-deletion-" + Guid.NewGuid().ToString("N"));
    private readonly string _watchPath;

    public ClientIdentityDeletionEndpointsTests()
    {
        _watchPath = Path.Combine(_root, "projects", "demo");
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
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
                    ["WatchPaths:0:Name"] = "demo",
                    ["WatchPaths:0:Path"] = _watchPath,
                }));
        });

    /// <summary>
    /// local-default is bootstrapped on first load and is never retired, so
    /// sending it on every request clears the X-Client-Id write boundary
    /// (<c>ClientIdentityMiddleware</c>) without a separate registration
    /// round-trip, and doubles as a stable "actor" for the audit assertions.
    /// </summary>
    private static HttpClient BuildClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        return client;
    }

    private static async Task<string> RegisterAsync(HttpClient client, string displayName)
    {
        var register = await client.PostAsJsonAsync("/api/clients/register", new RegisterClientRequest
        {
            DisplayName = displayName,
            Kind = ClientIdentityKinds.Service,
        });
        register.EnsureSuccessStatusCode();
        var summary = await register.Content.ReadFromJsonAsync<ClientSummary>();
        Assert.NotNull(summary);
        return summary!.Id;
    }

    private static async Task RetireAsync(HttpClient client, string id)
    {
        // A freshly registered identity has no RunnerActiveSlots reported yet,
        // so RequestDrain(retireAfterDrain: true) flips it straight to Retired
        // in this same call - the same path ClientIdentityTests exercises at
        // the store level (RetiredHost_CanBeRevived_ThenPermanentlyDeleted).
        var retire = await client.PostAsync($"/api/clients/{id}/retire", null);
        retire.EnsureSuccessStatusCode();
        var retired = await retire.Content.ReadFromJsonAsync<ClientSummary>();
        Assert.Equal(ClientIdentityKinds.Retired, retired?.Kind);
    }

    private static async Task<string> RegisterAndRetireAsync(HttpClient client, string displayName)
    {
        var id = await RegisterAsync(client, displayName);
        await RetireAsync(client, id);
        return id;
    }

    /// <summary>
    /// Drops a task folder directly into <c>3-progress</c> (same minimal
    /// task.json shape as RunnerSlotWiringTests) and acquires a live run
    /// lease on it via <c>POST /api/runner/lease/acquire</c> attributed to
    /// <paramref name="clientId"/>, so <c>ClientEndpoints.HasActiveLease</c>
    /// (which mirrors <c>LeaseEndpoints.CountHostLeases</c>) sees the
    /// identity as occupied. The lease is keyed by <c>TaskInfo.TaskKey</c>
    /// (watchPath + job id), not the raw folder name, so the actual scanned
    /// key is read back from <see cref="TaskScannerService"/> rather than
    /// re-derived by hand - that is what HasActiveLease itself looks up.
    ///
    /// <para>
    /// <c>/api/runner/lease/acquire</c> stamps <c>Lease.ClientId</c> from the
    /// caller's <c>X-Client-Id</c> header, not from the request body's
    /// <c>ClientId</c> field, so this sends its own header rather than
    /// relying on the ambient client default - and <paramref name="clientId"/>
    /// must therefore still be a live (non-retired) identity when this runs,
    /// or the write boundary itself rejects the call. Callers acquire the
    /// lease before retiring the identity, mirroring the real scenario this
    /// guard exists for: an operator retires a host whose cached
    /// <c>RunnerActiveSlots</c> already reads zero while the true lease
    /// authority (<see cref="RunLeaseService"/>) still shows it occupied.
    /// </para>
    /// </summary>
    private async Task AcquireActiveLeaseAsync(
        WebApplicationFactory<Program> factory, HttpClient client, string taskId, string clientId)
    {
        var jobDir = Path.Combine(_watchPath, TaskStates.Progress, taskId);
        Directory.CreateDirectory(jobDir);
        await File.WriteAllTextAsync(Path.Combine(jobDir, "task.json"),
            $$"""{"id":"{{taskId}}","title":"Active task","state":"{{TaskStates.Progress}}","agent":"codex","cliType":"codex"}""");

        // ScanAllJobs() reads through TaskIndexCache in production wiring, and
        // the cache only sees a folder written directly to disk (bypassing the
        // API) once the FileSystemWatcher debounce fires or something calls
        // Invalidate. ForceRefresh is the documented test hook for exactly
        // this: force a synchronous rescan so the task is visible immediately.
        factory.Services.GetService<TaskIndexCache>()?.ForceRefresh();
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var scanned = scanner.ScanAllJobs().Single(t => t.Id == taskId);
        var taskKey = scanned.Key ?? scanned.TaskKey ?? scanned.Id;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/runner/lease/acquire")
        {
            Content = JsonContent.Create(new RunLeaseAcquireRequest(
                TaskKey: taskKey,
                RunnerId: "test-runner-" + taskId,
                RunnerName: "test-runner",
                Hostname: "test-host",
                Pid: 4242,
                BackendName: "test")),
        };
        request.Headers.Remove("X-Client-Id");
        request.Headers.Add("X-Client-Id", clientId);

        var acquire = await client.SendAsync(request);
        acquire.EnsureSuccessStatusCode();
        var result = await acquire.Content.ReadFromJsonAsync<RunLeaseResponse>();
        Assert.True(result?.Granted == true, $"lease acquire must succeed for test setup: {result?.Outcome} {result?.Message}");
        Assert.Equal(clientId, result!.Lease?.ClientId);
    }

    [Fact]
    public async Task DeletePermanent_OnUnknownId_Returns404()
    {
        await using var factory = BuildFactory();
        using var client = BuildClient(factory);

        var response = await client.DeleteAsync("/api/clients/does-not-exist/permanent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal("client-not-found", body?.Error);
    }

    [Fact]
    public async Task DeletePermanent_OnNonRetiredIdentity_Returns409_AndIdentitySurvives()
    {
        await using var factory = BuildFactory();
        using var client = BuildClient(factory);
        var register = await client.PostAsJsonAsync("/api/clients/register", new RegisterClientRequest
        {
            DisplayName = "live-service",
            Kind = ClientIdentityKinds.Service,
        });
        var summary = await register.Content.ReadFromJsonAsync<ClientSummary>();

        var response = await client.DeleteAsync($"/api/clients/{summary!.Id}/permanent");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal("client-must-be-retired-before-delete", body?.Error);

        var getResponse = await client.GetAsync($"/api/clients/{summary.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeletePermanent_OnRetiredIdentityWithNoActiveLease_Returns204_WritesAuditRecord_AndIdentityIsGone()
    {
        await using var factory = BuildFactory();
        using var client = BuildClient(factory);
        var id = await RegisterAndRetireAsync(client, "retiring-quietly");

        var response = await client.DeleteAsync($"/api/clients/{id}/permanent");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await client.GetAsync($"/api/clients/{id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);

        var auditPath = Path.Combine(_root, "identities", ".audit", "identity-deletions.jsonl");
        Assert.True(File.Exists(auditPath), "expected the identity-deletion audit file to be written");
        var lines = await File.ReadAllLinesAsync(auditPath);
        var line = Assert.Single(lines);
        Assert.Contains(id, line);
        var record = JsonSerializer.Deserialize<AuditLine>(line, JsonReadOpts);
        Assert.Equal(id, record?.Id);
        Assert.Equal("single", record?.Reason);
    }

    [Fact]
    public async Task DeletePermanent_OnRetiredIdentityWithActiveLease_Returns409_AndIdentitySurvives()
    {
        await using var factory = BuildFactory();
        using var client = BuildClient(factory);
        var id = await RegisterAsync(client, "retiring-but-busy");
        await AcquireActiveLeaseAsync(factory, client, "task-busy", id);
        await RetireAsync(client, id);

        var response = await client.DeleteAsync($"/api/clients/{id}/permanent");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal("client-has-active-lease", body?.Error);

        var getResponse = await client.GetAsync($"/api/clients/{id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task PurgeRetired_DryRun_PreviewsMatchesWithoutDeleting()
    {
        await using var factory = BuildFactory();
        using var client = BuildClient(factory);
        var first = await RegisterAndRetireAsync(client, "purge-preview-alpha");
        var second = await RegisterAndRetireAsync(client, "purge-preview-beta");

        var response = await client.PostAsJsonAsync("/api/clients/retired/purge", new PurgeRetiredClientsRequest
        {
            Prefix = "purge-preview",
            DryRun = true,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PurgeRetiredClientsResponse>();
        Assert.NotNull(body);
        Assert.True(body!.DryRun);
        Assert.Equal(2, body.Results.Count);
        Assert.All(body.Results, r => Assert.Equal("would-delete", r.Outcome));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/clients/{first}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/clients/{second}")).StatusCode);
    }

    [Fact]
    public async Task PurgeRetired_Apply_DeletesOnlyMatchingPrefix_LeavesOthersUntouched()
    {
        await using var factory = BuildFactory();
        using var client = BuildClient(factory);
        var matching1 = await RegisterAndRetireAsync(client, "purge-apply-alpha");
        var matching2 = await RegisterAndRetireAsync(client, "purge-apply-beta");
        var untouched = await RegisterAndRetireAsync(client, "keep-me-retired");

        var response = await client.PostAsJsonAsync("/api/clients/retired/purge", new PurgeRetiredClientsRequest
        {
            Prefix = "purge-apply",
            DryRun = false,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PurgeRetiredClientsResponse>();
        Assert.NotNull(body);
        Assert.False(body!.DryRun);
        Assert.Equal(2, body.Results.Count);
        Assert.All(body.Results, r => Assert.Equal("deleted", r.Outcome));
        Assert.DoesNotContain(body.Results, r => r.Id == untouched);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/clients/{matching1}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/clients/{matching2}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/clients/{untouched}")).StatusCode);
    }

    [Fact]
    public async Task PurgeRetired_WithEmptyPrefix_NeverIncludesTheDefaultIdentity()
    {
        await using var factory = BuildFactory();
        using var client = BuildClient(factory);
        await RegisterAndRetireAsync(client, "purge-default-guard-companion");

        var response = await client.PostAsJsonAsync("/api/clients/retired/purge", new PurgeRetiredClientsRequest
        {
            DryRun = true,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PurgeRetiredClientsResponse>();
        Assert.NotNull(body);
        // The default identity cannot itself be retired (/retire refuses it,
        // see ClientEndpoints), so this also documents that invariant: an
        // empty-prefix purge over other retired identities never surfaces it.
        Assert.NotEmpty(body!.Results);
        Assert.DoesNotContain(body.Results, r => r.Id == DefaultClientIdentity.Id);
    }

    [Fact]
    public async Task PurgeRetired_SkipsCandidateWithActiveLease_InBothDryRunAndApplyModes()
    {
        await using var factory = BuildFactory();
        using var client = BuildClient(factory);
        var busy = await RegisterAsync(client, "purge-lease-busy");
        var free = await RegisterAndRetireAsync(client, "purge-lease-free");
        await AcquireActiveLeaseAsync(factory, client, "task-purge-busy", busy);
        await RetireAsync(client, busy);

        var dryRunResponse = await client.PostAsJsonAsync("/api/clients/retired/purge", new PurgeRetiredClientsRequest
        {
            Prefix = "purge-lease",
            DryRun = true,
        });
        Assert.Equal(HttpStatusCode.OK, dryRunResponse.StatusCode);
        var dryRunBody = await dryRunResponse.Content.ReadFromJsonAsync<PurgeRetiredClientsResponse>();
        Assert.Equal("skipped-active-lease", dryRunBody!.Results.Single(r => r.Id == busy).Outcome);
        Assert.Equal("would-delete", dryRunBody.Results.Single(r => r.Id == free).Outcome);

        var applyResponse = await client.PostAsJsonAsync("/api/clients/retired/purge", new PurgeRetiredClientsRequest
        {
            Prefix = "purge-lease",
            DryRun = false,
        });
        Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);
        var applyBody = await applyResponse.Content.ReadFromJsonAsync<PurgeRetiredClientsResponse>();
        Assert.Equal("skipped-active-lease", applyBody!.Results.Single(r => r.Id == busy).Outcome);
        Assert.Equal("deleted", applyBody.Results.Single(r => r.Id == free).Outcome);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/clients/{busy}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/clients/{free}")).StatusCode);
    }

    private static readonly JsonSerializerOptions JsonReadOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record ErrorBody
    {
        public string Error { get; init; } = "";
    }

    private sealed record AuditLine
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Actor { get; init; } = "";
        public DateTime At { get; init; }
        public string Reason { get; init; } = "";
    }
}
