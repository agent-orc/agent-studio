using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Host;
using AgentStudio.Management;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3954: the placement file declares structure apart from connectivity, the
/// route serves one cached projection with a revision, and a change reaches
/// subscribers as a delta of the changed nodes only.
/// </summary>
public sealed class StructureLiveEndpointTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "structure-live-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));

    public StructureLiveEndpointTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { }
    }

    private WebApplicationFactory<Program> Factory(Dictionary<string, string?> settings)
    {
        settings["TaskRepository"] = _workspace;
        settings["Security:Profile"] = "local";
        return new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(_time));
        });
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        return client;
    }

    [Fact]
    public async Task Placement_file_declares_machines_and_leaves_connectivity_untouched()
    {
        var placementPath = Path.Combine(_workspace, "placement.json");
        // Connectivity keys in a placement file are not part of its schema and are ignored.
        await File.WriteAllTextAsync(placementPath, """
            {
              "machines": [{ "id": "home-lab", "name": "Home lab machine", "kind": "physical", "components": [] }],
              "externals": [{ "id": "source-host", "name": "Source host" }],
              "structureLinks": [{ "from": "home-lab", "to": "external:source-host", "kind": "call", "label": "Clone" }],
              "mode": "reverse-ssh-transition", "enabledListeners": ["legacy-api", "runner-tunnel"], "linkOwner": "link-supervisor",
              "runners": [{ "runnerId": "role-runner", "route": "reverse-ssh", "sshTarget": "role-target", "remotePort": 15031 }]
            }
            """);
        var settings = new Dictionary<string, string?>
        {
            [StructurePlacement.PathKey] = placementPath,
            [StudioSelfObserver.MachineIdKey] = "studio",
        };
        using var factory = Factory(settings);
        using var client = Client(factory);

        using var response = await client.GetAsync("/api/v1/operations/structure?scope=workspace");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var nodes = body.GetProperty("nodes").EnumerateArray().ToDictionary(node => node.GetProperty("id").GetString()!);
        Assert.Equal("declared", nodes["machine:home-lab"].GetProperty("provenance").GetString());
        Assert.Equal("not observed", nodes["machine:home-lab"].GetProperty("observation").GetProperty("health").GetString());
        Assert.StartsWith(StructurePlacement.PathKey, nodes["machine:home-lab"].GetProperty("sourceRef").GetString());
        Assert.Equal("observed", nodes["machine:studio"].GetProperty("provenance").GetString());
        Assert.Equal("up", nodes["component:studio:api"].GetProperty("observation").GetProperty("health").GetString());
        Assert.Equal("link:machine:home-lab>external:source-host:call",
            body.GetProperty("links").EnumerateArray().Single().GetProperty("id").GetString());
        Assert.Equal(StructureResponse.CurrentSchemaVersion, body.GetProperty("schemaVersion").GetInt32());

        // Connectivity: no runner link, no manifest, the same configuration object as before.
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        Assert.Same(configuration, ConnectivityManifestLoader.RunnerLinkSource(configuration));
        Assert.Empty(RunnerLinkOptions.Read(configuration));
        Assert.Empty(factory.Services.GetRequiredService<LinkSupervisor>().Snapshot());
        using var environment = await client.GetAsync("/api/v1/management/environment");
        environment.EnsureSuccessStatusCode();
        var installation = await environment.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(!installation.TryGetProperty("mode", out var mode) || mode.ValueKind == JsonValueKind.Null);
        Assert.True(!installation.TryGetProperty("routes", out var routes) || routes.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Disconnected_task_server_expires_the_cached_report_during_repeated_reads()
    {
        var captured = JsonSerializer.Deserialize<StructureInstallationTests.Capture>(
            File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "testdata", "structure", "observations.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var host = captured.Hosts.First(h => h.Telemetry?.Structure is not null);
        _time.SetUtcNow(host.LastSeenAt);
        const string origin = "http://task-server.test";
        var upstream = new DisconnectingTaskServer(host);
        using var factory = Factory(new() { ["TaskServer:BaseUrl"] = origin })
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                TestHostFactory.RemoveBackgroundHostedServices(services);
                services.AddHttpClient(TaskServerPlaneProxy.ClientName, client => client.BaseAddress = new Uri(origin))
                    .ConfigurePrimaryHttpMessageHandler(() => upstream);
            }));
        var service = factory.Services.GetRequiredService<StructureSnapshotService>();

        async Task<StructureNode?> HostNode()
        {
            var snapshot = await service.ReadAsync(null, null, CancellationToken.None);
            return Assert.IsType<StructureResponse>(snapshot.Response).Nodes.SingleOrDefault(n => n.Id == $"machine:{host.HostId}");
        }

        Assert.NotNull(await HostNode());
        _time.Advance(TimeSpan.FromDays(14));
        Assert.Equal("stale", (await HostNode())!.Observation!.Health);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await HostNode());
        Assert.Equal(3, upstream.HostReads);
    }

    private sealed class DisconnectingTaskServer(RunnerCapabilitySnapshotDto host) : HttpMessageHandler
    {
        public int HostReads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/management/remote-hosts" && ++HostReads == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { host }) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    [Fact]
    public void The_documented_placement_example_is_valid_and_declares_no_connectivity()
    {
        var path = Path.Combine(RepositoryRoot.Find(), "deploy", "connectivity", "structure-placement.example.json");
        var json = File.ReadAllText(path);
        var placement = StructurePlacement.Parse(json);

        Assert.Empty(placement.Validate());
        var declared = StructureProjection.Build(null, [], _ => null, null, null, placement: placement);
        Assert.Equal("machine:home-lab", declared.Nodes.Single(node => node.Id == "machine:linux-vm").ParentId);
        Assert.Equal(4, declared.Nodes.Count(node => node.ParentId == "machine:studio-server"));
        Assert.Equal(4, declared.Links.Count);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(["externals", "machines", "structureLinks"], document.RootElement.EnumerateObject().Select(item => item.Name).Order());
    }

    [Fact]
    public async Task An_unreadable_placement_file_is_reported_instead_of_hiding_the_declarations()
    {
        var placementPath = Path.Combine(_workspace, "placement.json");
        await File.WriteAllTextAsync(placementPath, """{ "machines": [{ "id": "twice", "name": "A", "components": [] }, { "id": "twice", "name": "B", "components": [] }] }""");
        using var factory = Factory(new() { [StructurePlacement.PathKey] = placementPath });
        using var client = Client(factory);

        using var response = await client.GetAsync("/api/v1/operations/structure");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("placement file is invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task One_cached_revision_answers_readers_and_changes_reach_subscribers_as_small_deltas()
    {
        using var factory = Factory(new() { [StudioSelfObserver.MachineIdKey] = "studio" });
        using var client = Client(factory);
        var service = factory.Services.GetRequiredService<StructureSnapshotService>();
        var deltas = new List<StructureDelta>();
        service.DeltaPublished += delta => { lock (deltas) deltas.Add(delta); };

        using var first = await client.GetAsync("/api/v1/operations/structure");
        first.EnsureSuccessStatusCode();
        var tag = first.Headers.ETag!.Tag;
        var revision = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revision").GetInt64();
        Assert.Equal($"\"{revision}\"", tag);

        // A second reader gets the same cached projection; a reader that holds it gets 304.
        using var second = await client.GetAsync("/api/v1/operations/structure?scope=workspace");
        Assert.Equal(tag, second.Headers.ETag!.Tag);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/v1/operations/structure");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", tag);
        using var unchanged = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        lock (deltas) Assert.Empty(deltas);

        // The API samples itself again after its interval: only its own nodes change.
        _time.Advance(TimeSpan.FromSeconds(20));
        await service.RefreshDueAsync(CancellationToken.None);
        StructureDelta delta;
        lock (deltas) delta = Assert.Single(deltas);
        Assert.Equal(("workspace", (string?)null, revision, revision + 1),
            (delta.Scope, delta.Environment, delta.BaseRevision, delta.Revision));
        Assert.NotEmpty(delta.Nodes);
        Assert.All(delta.Nodes, node => Assert.True(node.Id is "machine:studio" || node.Id.StartsWith("component:studio:", StringComparison.Ordinal), node.Id));
        Assert.DoesNotContain(delta.Nodes, node => node.Id == "installation");
        Assert.Empty(delta.RemovedNodes);

        using var third = await client.GetAsync("/api/v1/operations/structure");
        Assert.Equal($"\"{delta.Revision}\"", third.Headers.ETag!.Tag);
        // Nothing changed since: no further delta.
        await service.RefreshDueAsync(CancellationToken.None);
        lock (deltas) Assert.Single(deltas);
        Assert.Equal("structure:workspace:", StructureSnapshotService.GroupName(null, null));
        Assert.Equal("structure:project:proj-001:staging", StructureSnapshotService.GroupName("PROJ-001", "staging"));
    }

    [Fact]
    public async Task Data_locations_carry_size_and_backup_or_git_remote_coverage()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, ".git"));
        await File.WriteAllTextAsync(Path.Combine(_workspace, ".git", "config"),
            "[core]\n\tbare = false\n[remote \"origin\"]\n\turl = role-remote\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n");
        await File.WriteAllBytesAsync(Path.Combine(_workspace, "state.bin"), new byte[4096]);
        var certificates = Path.Combine(_workspace, "edge-certificates");
        Directory.CreateDirectory(certificates);
        await File.WriteAllBytesAsync(Path.Combine(certificates, "leaf.pem"), new byte[100]);
        var outside = Path.Combine(Path.GetTempPath(), "structure-live-plain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            using var factory = Factory(new()
            {
                ["Backup:KeyFile"] = Path.Combine(_workspace, "backup.key"),
                [StructureDataLocationService.EdgeCertificatesKey] = certificates,
            });
            var locations = factory.Services.GetRequiredService<StructureDataLocationService>();
            locations.Refresh("studio");

            var workspace = locations.Current.Single(item => item.Role == "workspace-repository");
            Assert.Equal(("data:studio:workspace-repository", "TaskRepository", true, true, true, "both"),
                (workspace.Id, workspace.SourceRef, workspace.Exists, workspace.BackupCovered, workspace.GitRemoteCovered, workspace.Coverage));
            Assert.True(workspace.SizeBytes >= 4096 && workspace.SizeComplete);
            Assert.Equal(_time.GetUtcNow().UtcDateTime, workspace.MeasuredAt);
            // Inside the workspace source, so the backup covers them; they are not a git checkout.
            var edge = locations.Current.Single(item => item.Role == "edge-certificates");
            Assert.Equal((true, 100L, "backup"), (edge.Exists, edge.SizeBytes, edge.Coverage));
            var key = locations.Current.Single(item => item.Role == "secrets-key");
            Assert.Equal((false, (long?)null, "none"), (key.Exists, key.SizeBytes, key.Coverage));
            Assert.Equal(["workspace-repository", "project-checkouts", "application-state", "secrets-key", "edge-certificates"],
                locations.Current.Select(item => item.Role));
            Assert.All(locations.Current, item => Assert.DoesNotContain(_workspace, JsonSerializer.Serialize(item)));

            Assert.False(StructureDataLocationService.HasGitRemote(outside));
            Assert.False(StructureDataLocationService.IsUnder(_workspace + "-other", _workspace));
            Assert.True(StructureDataLocationService.IsUnder(certificates, _workspace));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }
}
