extern alias Runner;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;
using RAcquire = Runner::AgentRunner.RunLeaseAcquireRequest;
using RClient = Runner::AgentRunner.TaskServerClient;
using RClaim = Runner::AgentRunner.RunnerClaimRequest;
using RClaimStatus = Runner::AgentRunner.RunnerClaimStatus;
using RLease = Runner::AgentRunner.RunLeaseResponse;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2939: the backend lease boundary enforces host-class placement and the
/// per-project concurrency limit on both admission paths: the daemon claim loop
/// (<c>/api/runner/claim</c>) and the direct task-key lease
/// (<c>/api/runner/lease/acquire</c>). Both paths read one occupancy policy
/// and apply it to pinned and class-placed projects alike.
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class LeasePlacementEndpointsTests : IDisposable
{
    private const string ProjectName = "lease-placement";
    private const string RunnerId = "lease-placement-runner";

    private readonly string _workspace;
    private readonly string _watchPath;

    public LeasePlacementEndpointsTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-lease-placement-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        SeedOrchestratorSessions();
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Execution_runner_accepts_only_known_host_classes()
    {
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await client.RegisterAsync(ProjectName, "service", CancellationToken.None);

        var unknown = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/execution-runner",
            new { executionRunner = "class:solaris", remoteExecutionEnabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var known = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/execution-runner",
            new { executionRunner = "class:Linux", remoteExecutionEnabled = true });
        known.EnsureSuccessStatusCode();
        var settings = await known.Content.ReadFromJsonAsync<ProjectSettings>();
        Assert.Equal("class:linux", settings!.ExecutionLocation);
    }

    [Fact]
    public async Task Direct_lease_on_a_class_project_requires_a_fresh_class_capability()
    {
        SeedTask(TaskStates.Ready, "LP-CLASS-1");
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http, platformClass: null);
        await AssignAsync(http, "class:linux");

        var refused = await AcquireAsync(http, client, "LP-CLASS-1");
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("ProjectDenied", refused.Body.Outcome);
        Assert.Contains("platform:linux", refused.Body.Message, StringComparison.Ordinal);

        await AdvertiseAsync(http, client, "platform:linux");
        var granted = await AcquireAsync(http, client, "LP-CLASS-1");
        Assert.Equal(HttpStatusCode.OK, granted.Status);
        Assert.True(granted.Body.Granted);
    }

    [Theory]
    [InlineData(ProjectName)]
    [InlineData("class:linux")]
    public async Task Direct_lease_is_refused_when_the_project_has_no_free_slot(string location)
    {
        SeedTask(TaskStates.Ready, "LP-BUSY-1", order: 1);
        SeedTask(TaskStates.Ready, "LP-NEXT-2", order: 2);
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http, platformClass: "platform:linux");
        await AssignAsync(http, location);
        await AddRepositoryUrlAsync(http, "https://github.com/example/lease-placement.git");
        var busy = await ClaimWithSuccessfulPreflightAsync(client, Claim("busy"));
        Assert.Equal(RClaimStatus.Claimed, busy.Status);

        var full = await AcquireAsync(http, client, "LP-NEXT-2");
        Assert.Equal(HttpStatusCode.Conflict, full.Status);
        Assert.Equal("ProjectCapacityFull", full.Body.Outcome);
        Assert.False(full.Body.Granted);

        await SetMaxParallelismAsync(http, 2);
        var authorized = await AcquireAsync(http, client, "LP-NEXT-2");
        Assert.Equal(HttpStatusCode.OK, authorized.Status);
        Assert.True(authorized.Body.Granted);
    }

    [Fact]
    public async Task Claim_keeps_a_pinned_project_sequential_until_parallelism_is_configured()
    {
        SeedTask(TaskStates.Ready, "LP-PIN-1", order: 1);
        SeedTask(TaskStates.Ready, "LP-PIN-2", order: 2);
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http, platformClass: null);
        await AssignAsync(http, ProjectName);
        await AddRepositoryUrlAsync(http, "https://github.com/example/lease-placement.git");
        var first = await ClaimWithSuccessfulPreflightAsync(client, Claim("sequential-1"));
        Assert.Equal(RClaimStatus.Claimed, first.Status);

        var held = await client.ClaimAsync(Claim("sequential-2"), CancellationToken.None);
        Assert.Equal(RClaimStatus.Empty, held.Status);
        var rejection = AgentStudio.Runner.RemoteDispatchRejectionStore.Read(
            Path.Combine(_watchPath, TaskStates.Ready, "LP-PIN-2"));
        Assert.Equal("project-concurrency-full", rejection?.Code);

        await SetMaxParallelismAsync(http, 2);
        var second = await client.ClaimAsync(Claim("sequential-3"), CancellationToken.None);
        Assert.Contains(second.Status, new[] { RClaimStatus.Claimed, RClaimStatus.PreflightRequired });
    }

    [Fact]
    public async Task Class_only_runner_keeps_the_host_ceiling_it_declared()
    {
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http, platformClass: "platform:linux", hostCeiling: null);
        await AssignAsync(http, "class:linux");
        await SetMaxParallelismAsync(http, 2);

        var poll = await client.ClaimAsync(
            Claim("ceiling") with { BootstrapMaxParallelism = 3 }, CancellationToken.None);

        // A class project is shared by every matching host, so its project
        // limit neither seeds nor narrows one host's ceiling.
        Assert.Equal(3, poll.DesiredMaxParallelism);
    }

    private static async Task<Runner::AgentRunner.RunnerClaimResponse> ClaimWithSuccessfulPreflightAsync(
        RClient client, RClaim request)
    {
        var offered = await client.ClaimAsync(request, CancellationToken.None);
        Assert.Equal(RClaimStatus.PreflightRequired, offered.Status);
        return await client.ClaimAsync(request with
        {
            ProjectPreflight = new Runner::AgentRunner.RunnerProjectPreflightReport(
                offered.ProjectId!, offered.RegistrationFingerprint!, true,
                "clone/fetch URLs match registration; write probe succeeded",
                DateTime.UtcNow, offered.RepositoryUrl!, offered.RepositoryUrl!),
        }, CancellationToken.None);
    }

    private static RClaim Claim(string idempotencyKey) =>
        new(RunnerId, ProjectName, "host", 1, "remote-runner", IdempotencyKey: idempotencyKey);

    private static async Task<(HttpStatusCode Status, RLease Body)> AcquireAsync(
        HttpClient http, RClient client, string taskKey)
    {
        using var response = await http.PostAsJsonAsync("/api/runner/lease/acquire",
            new RAcquire(taskKey, RunnerId, ProjectName, "host", Environment.ProcessId,
                "remote-runner", LeaseInstanceId: client.RunnerInstanceId));
        var body = await response.Content.ReadFromJsonAsync<RLease>();
        return (response.StatusCode, body!);
    }

    private static async Task RegisterCodingRunnerAsync(
        RClient client, HttpClient http, string? platformClass, int? hostCeiling = 4)
    {
        var clientId = await client.RegisterAsync(ProjectName, "service", CancellationToken.None);
        // The host has room for more runs than one project may use, so only
        // the project limit can hold a second claim.
        if (hostCeiling is not null)
        {
            var capacity = await http.PutAsJsonAsync(
                $"/api/clients/{clientId}/runner-capacity", new { maxParallelism = hostCeiling });
            capacity.EnsureSuccessStatusCode();
        }
        var registration = await http.PutAsJsonAsync(
            $"/api/v1/runners/{RunnerId}",
            new Contract.RegisterRunnerRequest(
                ProjectName, "test-host", client.RunnerInstanceId, "1.0.0", Contract.TaskServerProtocol.Current,
                [Contract.ReviewCapabilities.CodingExecutor]));
        registration.EnsureSuccessStatusCode();
        await AdvertiseAsync(http, client, platformClass);
    }

    private static async Task AdvertiseAsync(HttpClient http, RClient client, string? platformClass)
    {
        var capabilities = new List<Contract.AdvertisedCapabilityDto>
        {
            new(Contract.CapabilityProtocol.CodingExecutor, "executor"),
            new(Contract.CapabilityProtocol.GitFetch, "source"),
            new(Contract.CapabilityProtocol.GitPush, "source"),
            new(Contract.CapabilityProtocol.RepositoryAccess, "source"),
            new(Contract.CapabilityProtocol.Disk, "foundation"),
            new(Contract.CapabilityProtocol.TaskServerConnectivity, "foundation"),
            new(Contract.CapabilityProtocol.CliExecution(CliTypes.Claude), "cli-execution", "ready"),
            new(Contract.CapabilityProtocol.ProviderAuthentication(CliTypes.Claude), "provider-auth", "ready"),
        };
        if (platformClass is not null)
            capabilities.Add(new Contract.AdvertisedCapabilityDto(platformClass, "platform", "ready"));
        var advertised = await http.PutAsJsonAsync(
            $"/api/v1/runners/{RunnerId}/capabilities",
            new Contract.CapabilityAdvertisementRequest(
                RunnerId, client.RunnerInstanceId, Contract.CapabilityProtocol.CurrentSchemaVersion,
                DateTime.UtcNow, 180, DateTime.UtcNow.Ticks, capabilities));
        advertised.EnsureSuccessStatusCode();
    }

    private static async Task AssignAsync(HttpClient http, string executionRunner)
    {
        var assignment = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/execution-runner",
            new { executionRunner, remoteExecutionEnabled = true });
        assignment.EnsureSuccessStatusCode();
    }

    private static async Task SetMaxParallelismAsync(HttpClient http, int maxParallelism)
    {
        var response = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/max-parallelism", new { maxParallelism });
        response.EnsureSuccessStatusCode();
    }

    private static async Task AddRepositoryUrlAsync(HttpClient http, string repositoryUrl)
    {
        var response = await http.PostAsJsonAsync(
            "/api/projects/PROJ-001/urls", new { label = "repo", url = repositoryUrl });
        response.EnsureSuccessStatusCode();
    }

    private void SeedTask(string state, string key, int order = 1)
    {
        var dir = Path.Combine(_watchPath, state, key);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            id = key,
            title = key,
            state,
            order,
            agent = "claude",
            kind = TaskKinds.Task,
            cliType = CliTypes.Claude,
        }));
        File.WriteAllText(Path.Combine(dir, "prompt.md"), "Do the placed work.");
        File.WriteAllText(Path.Combine(dir, "status.md"), "Result: pending.");
    }

    private void SeedOrchestratorSessions()
    {
        var projectSession = Path.Combine(_watchPath, ".orchestrator", "orchestrator-session.json");
        var globalSession = Path.Combine(_workspace, ".runtime", "global-orchestrator-session.json");
        Directory.CreateDirectory(Path.GetDirectoryName(projectSession)!);
        Directory.CreateDirectory(Path.GetDirectoryName(globalSession)!);
        const string session = "{\"sessionId\":\"lease-placement-session\",\"model\":\"test\"}";
        File.WriteAllText(projectSession, session);
        File.WriteAllText(globalSession, session);
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Test");
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["WatchPaths:0:Name"] = ProjectName,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath,
                        ["WatchPaths:0:RepositoryPath"] = _watchPath,
                        ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    });
                });
            });
}
