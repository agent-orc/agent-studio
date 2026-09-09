using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class ManagementApiTests : IDisposable
{
    private readonly string _root = CreateServerDataDirectory();
    private readonly string _backups;
    private readonly string _logs;

    public ManagementApiTests()
    {
        _backups = _root + "-backups";
        _logs = _root + "-logs";
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_root, state));
        Directory.CreateDirectory(Path.Combine(_root, ".metadata"));
        File.WriteAllText(Path.Combine(_root, ".metadata", "server-evidence.jsonl"), "{}\n");
    }

    [Fact]
    public async Task StatusAndCommands_RequireActor_AndLeaveDurableAudit()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/management/status")).StatusCode);

        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/management/status");
        Assert.Equal("healthy", status.GetProperty("health").GetProperty("state").GetString());
        Assert.True(
            status.GetProperty("store").GetProperty("eventCount").GetInt64() >= 1,
            "The seeded server evidence event must remain visible when startup emits additional runtime events.");

        const string key = "maintenance-test-key";
        var request = new { kind = "maintenance-enter", dryRun = false, confirmation = "maintenance-enter", idempotencyKey = key, reason = "test rehearsal" };
        var first = await client.PostAsJsonAsync("/api/v1/management/commands", request);
        first.EnsureSuccessStatusCode();
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var second = await client.PostAsJsonAsync("/api/v1/management/commands", request);
        second.EnsureSuccessStatusCode();
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(firstBody.GetProperty("commandId").GetString(), secondBody.GetProperty("commandId").GetString());
        Assert.True(File.Exists(Path.Combine(_root, ".audit", "management.jsonl")));
        var audit = File.ReadLines(Path.Combine(_root, ".audit", "management.jsonl")).ToArray();
        Assert.Equal(2, audit.Length);
        Assert.Contains("\"outcome\":\"started\"", audit[0]);
        Assert.Contains("\"outcome\":\"completed\"", audit[1]);
    }

    [Fact]
    public async Task RemoteHosts_ReturnsTheLatestRunnerCapabilitySnapshot()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var registry = factory.Services.GetRequiredService<V1ReviewExecutorRegistry>();
        const string runnerId = "agent-runner-capability-snapshot";
        const string instanceId = "agent-runner-host:2498";
        registry.Register(
            runnerId,
            new Contract.RegisterRunnerRequest(
                "Agent Runner Snapshot",
                "agent-runner-host",
                instanceId,
                "1.2.3",
                Contract.TaskServerProtocol.Current,
                [Contract.ReviewCapabilities.CodingExecutor],
                BootstrapMaxParallelism: 6));
        registry.AdvertiseCapabilities(
            runnerId,
            new Contract.CapabilityAdvertisementRequest(
                runnerId,
                instanceId,
                Contract.CapabilityProtocol.CurrentSchemaVersion,
                DateTime.UtcNow,
                180,
                1,
                [
                    new Contract.AdvertisedCapabilityDto(
                        Contract.CapabilityProtocol.CliExecution("claude"),
                        "cli-execution",
                        "ready",
                        "available",
                        "/usr/bin/claude"),
                    new Contract.AdvertisedCapabilityDto(
                        Contract.CapabilityProtocol.ProviderAuthentication("claude"),
                        "provider-auth",
                        "ready",
                        Identity: "claude"),
                ]));
        registry.ReportCapabilityFailure(
            runnerId,
            new Contract.CapabilityFailureRequest(
                runnerId,
                instanceId,
                Contract.CapabilityProtocol.ProviderAuthentication("claude"),
                "ProviderUnauthorized",
                "Claude login is unavailable for the coding service user.",
                DateTime.UtcNow,
                "snapshot-provider-auth-failure"));

        using var response = await client.GetAsync("/api/v1/management/remote-hosts");

        response.EnsureSuccessStatusCode();
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty);
        var snapshots = await response.Content.ReadFromJsonAsync<
            IReadOnlyList<Contract.RunnerCapabilitySnapshotDto>>();
        var snapshot = Assert.Single(snapshots!);
        Assert.Equal(runnerId, snapshot.RunnerId);
        Assert.Equal(instanceId, snapshot.InstanceId);
        Assert.Equal(6, snapshot.RoleMaxParallelism);
        Assert.Contains(
            snapshot.Capabilities,
            capability => capability.Key == "cli-execution:claude"
                          && capability.AdvertisedStatus == "ready"
                          && capability.IsFresh
                          && capability.Identity == "/usr/bin/claude");
        Assert.Contains(
            snapshot.Capabilities,
            capability => capability.Key == "provider-auth:claude"
                          && capability.AdvertisedStatus == "ready"
                          && capability.HealthState == Contract.CapabilityHealthStates.Suspect
                          && capability.ConsecutiveFailures == 1
                          && capability.FirstFailureAt is not null);

        var expiresAt = DateTime.UtcNow.AddDays(10);
        registry.AdvertiseCapabilities(
            runnerId,
            new Contract.CapabilityAdvertisementRequest(
                runnerId,
                instanceId,
                Contract.CapabilityProtocol.CurrentSchemaVersion,
                DateTime.UtcNow,
                180,
                2,
                [
                    new Contract.AdvertisedCapabilityDto(
                        Contract.CapabilityProtocol.CliExecution("claude"),
                        "cli-execution"),
                    new Contract.AdvertisedCapabilityDto(
                        Contract.CapabilityProtocol.ProviderAuthentication("claude"),
                        "provider-auth",
                        "ready",
                        Signal: "credentials-expiring",
                        ExpiresAt: expiresAt),
                ]));

        var recovered = Assert.Single(registry.ListCapabilitySnapshots());
        var recoveredAuth = Assert.Single(
            recovered.Capabilities,
            capability => capability.Key == "provider-auth:claude");
        Assert.Equal(Contract.CapabilityHealthStates.Healthy, recoveredAuth.HealthState);
        Assert.Equal(0, recoveredAuth.ConsecutiveFailures);
        Assert.Equal("credentials-expiring", recoveredAuth.Signal);
        Assert.Equal(expiresAt, recoveredAuth.ExpiresAt);
        Assert.Contains(
            recoveredAuth.RecoveryHistory,
            item => item.FromState == Contract.CapabilityHealthStates.Suspect
                    && item.ToState == Contract.CapabilityHealthStates.Healthy);
    }

    [Fact]
    public async Task RunnerLinks_AreAuthorizedAuditedAndOperable()
    {
        await using var factory = BuildFactory(runnerLinks: true);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/management/links")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var links = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/management/links");
        var link = Assert.Single(links!);
        Assert.Equal("agent-runner-01", link.GetProperty("runnerId").GetString());
        foreach (var property in new[]
                 {
                     "kind", "state", "since", "lastHeartbeatAt", "lastProbe", "lastError",
                     "attempt", "nextRetryAt", "childPid", "unreachableSince",
                 })
            Assert.True(link.TryGetProperty(property, out _), $"Link resource is missing '{property}'.");

        var reconnect = await client.PostAsJsonAsync("/api/v1/management/links/agent-runner-01/reconnect", new { });
        reconnect.EnsureSuccessStatusCode();
        var reconnectResource = await reconnect.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("reconnecting", reconnectResource.GetProperty("state").GetString());
        Assert.Equal("listener-cleanup", reconnectResource.GetProperty("lastProbe").GetProperty("kind").GetString());

        var paused = await client.PostAsJsonAsync("/api/v1/management/links/agent-runner-01/pause", new { });
        paused.EnsureSuccessStatusCode();
        Assert.Equal("paused", (await paused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        var resumed = await client.PostAsJsonAsync("/api/v1/management/links/agent-runner-01/resume", new { });
        resumed.EnsureSuccessStatusCode();
        Assert.Equal("reconnecting", (await resumed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        var audit = await File.ReadAllTextAsync(Path.Combine(_root, ".audit", "runner-links.jsonl"));
        Assert.Contains("\"action\":\"reconnect\"", audit);
        Assert.Contains("\"action\":\"pause\"", audit);
        Assert.Contains("\"action\":\"resume\"", audit);
    }

    [Fact]
    public void RunnerLinkPolicy_CoversHeartbeatLateExitPauseAndBackoff()
    {
        Assert.Equal(RunnerLinkStates.Up, RunnerLinkPolicy.Decide("connecting", false, true, true, false, false).State);
        Assert.True(RunnerLinkPolicy.Decide("up", false, true, false, true, false).Probe);
        Assert.True(RunnerLinkPolicy.Decide("connecting", false, true, false, true, false).Probe);
        Assert.True(RunnerLinkPolicy.Decide("connecting", false, false, false, true, false).Recover);
        Assert.True(RunnerLinkPolicy.Decide("reconnecting", false, false, false, true, true).Start);
        Assert.Equal(RunnerLinkStates.Paused, RunnerLinkPolicy.Decide("up", true, true, true, false, false).State);
    }

    [Fact]
    public async Task LinkSupervisor_RecoversStartsSilentlyUsesForwardsAndStopsItsChild()
    {
        var launcher = new FakeLinkProcessLauncher();
        await using var factory = BuildFactory(runnerLinks: true, launcher: launcher);
        var supervisor = factory.Services.GetRequiredService<LinkSupervisor>();

        await supervisor.ReconnectAsync("agent-runner-01", "test", CancellationToken.None);
        await supervisor.TickAsync("agent-runner-01");
        await supervisor.TickAsync("agent-runner-01");
        var connecting = Assert.Single(supervisor.Snapshot());
        Assert.Equal(RunnerLinkStates.Connecting, connecting.State);
        Assert.NotNull(connecting.ChildPid);
        var forward = Assert.Single(launcher.Commands, command => command.Arguments.Contains("-N"));
        var cleanup = Assert.Single(launcher.Commands, command => !command.Arguments.Contains("-N"));
        Assert.Contains("ss -H -ltnp", cleanup.Arguments[^1]);
        Assert.Contains("kill -KILL", cleanup.Arguments[^1]);
        Assert.Contains("ExitOnForwardFailure=yes", forward.Arguments);
        Assert.Contains("15031:127.0.0.1:5031", forward.Arguments);
        Assert.Contains("5031:127.0.0.1:5031", forward.Arguments);
        Assert.Contains("4011:localhost:4011", forward.Arguments);

        var registry = factory.Services.GetRequiredService<V1ReviewExecutorRegistry>();
        registry.Register("agent-runner-01", new Contract.RegisterRunnerRequest(
            "agent-runner-01", "host-01", "instance-01", "1.0", Contract.TaskServerProtocol.Current,
            [Contract.ReviewCapabilities.CodingExecutor]));
        registry.AdvertiseCapabilities("agent-runner-01", new Contract.CapabilityAdvertisementRequest(
            "agent-runner-01", "instance-01", Contract.CapabilityProtocol.CurrentSchemaVersion,
            DateTime.UtcNow.AddMinutes(-2), 90, 1,
            [new Contract.AdvertisedCapabilityDto("cli-execution:codex", "cli-execution", "ready")]));
        await supervisor.TickAsync("agent-runner-01");
        var degraded = Assert.Single(supervisor.Snapshot());
        Assert.Equal(RunnerLinkStates.Degraded, degraded.State);
        Assert.Equal("late-heartbeat", degraded.LastProbe?.Kind);
        Assert.True(degraded.LastProbe?.Succeeded);

        registry.AdvertiseCapabilities("agent-runner-01", new Contract.CapabilityAdvertisementRequest(
            "agent-runner-01", "instance-01", Contract.CapabilityProtocol.CurrentSchemaVersion,
            DateTime.UtcNow, 90, 2,
            [new Contract.AdvertisedCapabilityDto("cli-execution:codex", "cli-execution", "ready")]));
        await supervisor.TickAsync("agent-runner-01");
        var recovered = Assert.Single(supervisor.Snapshot());
        Assert.Equal(RunnerLinkStates.Up, recovered.State);
        Assert.Equal(0, recovered.Attempt);
        Assert.Null(recovered.NextRetryAt);

        var killedForward = launcher.Forward!;
        killedForward.Exit(255);
        await supervisor.TickAsync("agent-runner-01");
        Assert.Equal(RunnerLinkStates.Reconnecting, Assert.Single(supervisor.Snapshot()).State);
        await supervisor.TickAsync("agent-runner-01");
        Assert.Equal(RunnerLinkStates.Connecting, Assert.Single(supervisor.Snapshot()).State);
        Assert.NotSame(killedForward, launcher.Forward);
        registry.AdvertiseCapabilities("agent-runner-01", new Contract.CapabilityAdvertisementRequest(
            "agent-runner-01", "instance-01", Contract.CapabilityProtocol.CurrentSchemaVersion,
            DateTime.UtcNow, 90, 3,
            [new Contract.AdvertisedCapabilityDto("cli-execution:codex", "cli-execution", "ready")]));
        await supervisor.TickAsync("agent-runner-01");
        Assert.Equal(RunnerLinkStates.Up, Assert.Single(supervisor.Snapshot()).State);
        await supervisor.PauseAsync("agent-runner-01", CancellationToken.None);
        Assert.Equal(RunnerLinkStates.Paused, Assert.Single(supervisor.Snapshot()).State);
        Assert.True(launcher.Forward.Terminated);
        var feed = ReadWorkspaceBus();
        Assert.Contains("\"topic\":\"link_down\"", feed);
        Assert.Contains("\"topic\":\"link_up\"", feed);
        Assert.Contains("\"runnerId\":\"agent-runner-01\"", feed);
    }

    [Fact]
    public async Task LinkSupervisor_BackoffsWhenZombieCleanupOrForwardStartFails()
    {
        var launcher = new FakeLinkProcessLauncher();
        launcher.BoundedExitCodes.Enqueue(1);
        await using var factory = BuildFactory(runnerLinks: true, launcher: launcher);
        var supervisor = factory.Services.GetRequiredService<LinkSupervisor>();

        await supervisor.ReconnectAsync("agent-runner-01", "test", CancellationToken.None);
        await supervisor.TickAsync("agent-runner-01");
        var cleanupFailure = Assert.Single(supervisor.Snapshot());
        Assert.Equal(RunnerLinkStates.Reconnecting, cleanupFailure.State);
        Assert.Contains("listener cleanup failed", cleanupFailure.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(cleanupFailure.NextRetryAt);

        var forwardLauncher = new FakeLinkProcessLauncher { ForwardExitCodeOnStart = 1 };
        await using var forwardFactory = BuildFactory(runnerLinks: true, launcher: forwardLauncher);
        var forwardSupervisor = forwardFactory.Services.GetRequiredService<LinkSupervisor>();
        await forwardSupervisor.ReconnectAsync("agent-runner-01", "test", CancellationToken.None);
        await forwardSupervisor.TickAsync("agent-runner-01");
        await forwardSupervisor.TickAsync("agent-runner-01");
        var forwardFailure = Assert.Single(forwardSupervisor.Snapshot());
        Assert.Equal(RunnerLinkStates.Reconnecting, forwardFailure.State);
        Assert.Contains("before a heartbeat", forwardFailure.LastError);
        Assert.Equal(1, forwardFailure.Attempt);
        Assert.Contains("\"topic\":\"link_reconnect_failed\"", ReadWorkspaceBus());
    }

    [Fact]
    public async Task LinkSupervisor_AdoptsOneHealthyForwardAndReleasesItWhenPaused()
    {
        var launcher = new FakeLinkProcessLauncher();
        await using var factory = BuildFactory(runnerLinks: true, launcher: launcher);
        var supervisor = factory.Services.GetRequiredService<LinkSupervisor>();

        await supervisor.StartAsync(CancellationToken.None);
        for (var attempt = 0;
             attempt < 100 && supervisor.Snapshot().Single().State == RunnerLinkStates.Down;
             attempt++)
            await Task.Delay(10);
        var adopted = Assert.Single(supervisor.Snapshot());
        Assert.Equal(RunnerLinkStates.Connecting, adopted.State);
        Assert.Null(adopted.ChildPid);
        Assert.Equal("adoption", adopted.LastProbe?.Kind);

        await supervisor.PauseAsync("agent-runner-01", CancellationToken.None);
        var paused = Assert.Single(supervisor.Snapshot());
        Assert.Equal(RunnerLinkStates.Paused, paused.State);
        Assert.Equal("adopted-listener-release", paused.LastProbe?.Kind);
        Assert.Equal(2, launcher.Commands.Count(command => !command.Arguments.Contains("-N")));
        await supervisor.StopAsync(CancellationToken.None);
    }

    [SkippableFact]
    [Trait("Category", "MachineBound")]
    public async Task RunnerLinkChild_IsSilentRedirectedAndTerminatedWithItsOwner()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The portable fake executable fixture uses the Linux sleep binary.");
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(_root, "fake-ssh");
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "ssh");
        File.Copy("/bin/sleep", executable);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var command = new SshCommand(executable, ["30"], Path.Combine(directory, "link.log"));
        var start = RunnerLinkProcessLauncher.BuildStartInfo(command);
        Assert.True(start.CreateNoWindow);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);

        using var launcher = new RunnerLinkProcessLauncher();
        var child = launcher.Start(command);
        Assert.False(child.HasExited);
        await child.TerminateAsync(CancellationToken.None);
        Assert.True(child.HasExited);
        await child.DisposeAsync();
    }

    [Fact]
    public async Task RunnerRegistration_ProjectsItsHeartbeatIntoClients()
    {
        var identities = Path.Combine(_root, "identities");
        Directory.CreateDirectory(identities);
        await File.WriteAllTextAsync(Path.Combine(identities, "agent-runner-heartbeat.json"), """
            {
              "id": "agent-runner-heartbeat",
              "displayName": "agent-runner-heartbeat",
              "kind": "service",
              "registeredAt": "2026-09-06T08:00:00Z"
            }
            """);
        await using var factory = BuildFactory();
        using var runner = factory.CreateClient();

        var registration = await runner.PutAsJsonAsync(
            "/api/v1/runners/agent-runner-heartbeat",
            new Contract.RegisterRunnerRequest(
                "agent-runner-heartbeat", "host-heartbeat", "coding-1", "1.2.3",
                Contract.TaskServerProtocol.Current,
                [Contract.ReviewCapabilities.CodingExecutor]));
        registration.EnsureSuccessStatusCode();

        var clients = await runner.GetFromJsonAsync<JsonElement[]>("/api/clients");
        var projected = Assert.Single(clients!, item =>
            item.GetProperty("id").GetString() == "agent-runner-heartbeat");
        Assert.Equal(JsonValueKind.String, projected.GetProperty("lastSeenAt").ValueKind);
        Assert.True(projected.GetProperty("lastSeenAt").GetDateTime() > new DateTime(2026, 9, 6, 8, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task ProviderAuthProvisioning_RequiresOperator_AndDoesNotEchoTheSecret()
    {
        const string secret = "sk-ant-oat01-provider-secret-fixture";
        var provisioner = new RecordingProviderAuthProvisioner();
        await using var factory = BuildFactory(provisioner: provisioner);
        using var client = factory.CreateClient();
        var request = new ProviderAuthProvisioningRequest(
            "agent@runner-01",
            "agent-runner-01",
            "CLAUDE_CODE_OAUTH_TOKEN",
            secret);

        var denied = await client.PostAsJsonAsync(
            "/api/v1/management/remote-hosts/provider-auth",
            request);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Null(provisioner.LastRequest);

        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var accepted = await client.PostAsJsonAsync(
            "/api/v1/management/remote-hosts/provider-auth",
            request);

        accepted.EnsureSuccessStatusCode();
        Assert.Equal(secret, provisioner.LastRequest?.Secret);
        var body = await accepted.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.Contains("awaiting-probe", body, StringComparison.Ordinal);
        Assert.Contains("no-store", accepted.Headers.CacheControl?.ToString() ?? "");
    }

    [Theory]
    [InlineData("runner;touch /tmp/x", "CLAUDE_CODE_OAUTH_TOKEN", "valid-provider-secret-fixture")]
    [InlineData("agent@runner", "UNSUPPORTED_TOKEN", "valid-provider-secret-fixture")]
    [InlineData("agent@runner", "ANTHROPIC_API_KEY", "secret with whitespace")]
    public async Task ProviderAuthProvisioning_RejectsUnsafeInputBeforeTransport(
        string sshTarget,
        string environmentVariable,
        string secret)
    {
        var provisioner = new RecordingProviderAuthProvisioner();
        await using var factory = BuildFactory(provisioner: provisioner);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

        var response = await client.PostAsJsonAsync(
            "/api/v1/management/remote-hosts/provider-auth",
            new ProviderAuthProvisioningRequest(
                sshTarget,
                "agent-runner-01",
                environmentVariable,
                secret));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(provisioner.LastRequest);
    }

    [Fact]
    public async Task BackupCreate_VerifiesRealArchive_OutsideDataDirectory()
    {
        await using var factory = BuildFactory(Environments.Production);
        using var client = factory.CreateClient();
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        Assert.Equal(Environments.Production, environment.EnvironmentName);
        Assert.False(environment.IsDevelopment());
        var relativeToTemp = Path.GetRelativePath(Path.GetFullPath(Path.GetTempPath()), _root);
        Assert.False(
            Path.IsPathRooted(relativeToTemp)
            || relativeToTemp == ".."
            || relativeToTemp.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        await EnterMaintenance(client, "backup-maintenance-key");
        var response = await client.PostAsJsonAsync("/api/v1/management/commands", new
        {
            kind = "backup-create", dryRun = false, confirmation = "backup-create", idempotencyKey = "backup-test-key"
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("affected").GetInt32());
        var backups = Directory.GetFiles(_backups, "backup-*.zip");
        Assert.Single(backups);
        Assert.True(File.Exists(backups[0] + ".manifest.json"));
        Assert.True(new FileInfo(backups[0]).Length > 0);
        Assert.DoesNotContain(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, Path.GetFullPath(backups[0]));

        var verify = await client.PostAsJsonAsync("/api/v1/management/commands", new
        {
            kind = "restore-verify", dryRun = false, confirmation = "restore-verify", idempotencyKey = "restore-test-key"
        });
        verify.EnsureSuccessStatusCode();
        var verifyBody = await verify.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, verifyBody.GetProperty("affected").GetInt32());
        var stagingRoot = Path.Combine(_backups, ".restore-verification");
        Assert.False(Directory.Exists(stagingRoot) && Directory.EnumerateFileSystemEntries(stagingRoot).Any());
    }

    [Fact]
    public async Task RestoreVerification_RejectsValidArchiveWhoseCreationChecksumChanged()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        await EnterMaintenance(client, "tamper-maintenance-key");
        (await client.PostAsJsonAsync("/api/v1/management/commands", new
        {
            kind = "backup-create", dryRun = false, confirmation = "backup-create", idempotencyKey = "tamper-backup-key"
        })).EnsureSuccessStatusCode();

        var archivePath = Assert.Single(Directory.GetFiles(_backups, "backup-*.zip"));
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(archive.CreateEntry("tampered-after-creation.txt").Open());
            writer.Write("changed");
        }

        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/management/status");
        Assert.Equal("failed", status.GetProperty("backups").GetProperty("items")[0].GetProperty("verificationState").GetString());
        var verify = await client.PostAsJsonAsync("/api/v1/management/commands", new
        {
            kind = "restore-verify", dryRun = false, confirmation = "restore-verify", idempotencyKey = "tamper-restore-key"
        });
        verify.EnsureSuccessStatusCode();
        var body = await verify.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("affected").GetInt32());
        Assert.Contains("failed before extraction", body.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task MigrationProjection_IsDurableAndControlsReadiness()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var migrations = factory.Services.GetRequiredService<MigrationStateStore>();

        migrations.Begin("schema-regression", "Applying schema regression fixture.");
        var running = await client.GetFromJsonAsync<JsonElement>("/api/v1/management/status");
        Assert.False(running.GetProperty("health").GetProperty("ready").GetBoolean());
        Assert.Equal("maintenance", running.GetProperty("health").GetProperty("state").GetString());
        Assert.Contains("migration-running", running.GetProperty("health").GetProperty("reasons").EnumerateArray().Select(x => x.GetString()));

        migrations.Fail("schema-regression", "fixture failed");
        var failed = await client.GetFromJsonAsync<JsonElement>("/api/v1/management/status");
        Assert.Equal("degraded", failed.GetProperty("health").GetProperty("state").GetString());
        Assert.Equal("failed", failed.GetProperty("migrations")[0].GetProperty("state").GetString());

        migrations.Complete("schema-regression");
        var recovered = await client.GetFromJsonAsync<JsonElement>("/api/v1/management/status");
        Assert.True(recovered.GetProperty("health").GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task ConflictingHeaderAndBodyIdempotencyKeys_AreRejectedBeforeAudit()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/management/commands")
        {
            Content = JsonContent.Create(new
            {
                kind = "archive-sweep", dryRun = true, idempotencyKey = "body-key",
            }),
        };
        request.Headers.Add("Idempotency-Key", "header-key");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(_root, ".audit", "management.jsonl")));
    }

    [Fact]
    public async Task WhitespacePaddedOwnerCommand_IsRejectedForOperatorBeforeAudit()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

        var response = await client.PostAsJsonAsync("/api/v1/management/commands", new
        {
            kind = " runner-credential-rotate ",
            dryRun = true,
            idempotencyKey = "padded-owner-command-key",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("owner-required", body.GetProperty("error").GetString());
        Assert.False(File.Exists(Path.Combine(_root, ".audit", "management.jsonl")));
    }

    [Fact]
    public async Task ReusedIdempotencyKey_WithDifferentPayload_IsRejected()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        const string key = "retention-payload-key";

        var first = await client.PostAsJsonAsync("/api/v1/management/commands", new
        {
            kind = "backup-retention", dryRun = true, idempotencyKey = key, retentionCount = 2,
        });
        first.EnsureSuccessStatusCode();

        var conflicting = await client.PostAsJsonAsync("/api/v1/management/commands", new
        {
            kind = "backup-retention", dryRun = true, idempotencyKey = key, retentionCount = 3,
        });
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        var body = await conflicting.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("idempotency-key-conflict", body.GetProperty("error").GetString());

        var audit = File.ReadLines(Path.Combine(_root, ".audit", "management.jsonl")).ToArray();
        Assert.Equal(2, audit.Length);
        Assert.All(audit, row => Assert.Contains("requestFingerprint", row));
    }

    [Fact]
    public async Task MaintenanceMode_RefusesOrdinaryMutations_ButManagementRemainsAvailable()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        await EnterMaintenance(client, "maintenance-admission-key");

        var blocked = await client.PostAsJsonAsync("/api/tasks", new { id = "blocked-in-maintenance" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/management/status");
        Assert.Equal("maintenance", status.GetProperty("maintenance").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task RecoveryConsole_IsServerHosted_AndNamesServiceManagerBoundary()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/recovery");
        Assert.Contains("Task Server bootstrap and recovery", html);
        Assert.Contains("service manager owns process start and restart", html);
        Assert.Contains("/api/v1/management/status", html);
    }

    private WebApplicationFactory<Program> BuildFactory(
        string environment = "Test",
        IProviderAuthProvisioner? provisioner = null,
        bool runnerLinks = false,
        IRunnerLinkProcessLauncher? launcher = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Management:BackupDirectory"] = _backups,
            ["Logging:BackendFile:LogDirectory"] = _logs,
            ["WatchPaths:0:Name"] = "Management Test",
            ["WatchPaths:0:Path"] = _root,
            ["WatchPaths:0:RootPath"] = _root,
            ["Supervisor:StuckResumeWindowMinutes"] = "0",
            ["CodexModels:WarmupOnBoot"] = "false",
            ["RunnerLinks:0:Enabled"] = runnerLinks ? "true" : "false",
            ["RunnerLinks:0:RunnerId"] = "agent-runner-01",
            ["RunnerLinks:0:Kind"] = "ssh-reverse",
            ["RunnerLinks:0:SshTarget"] = "agent-runner",
            ["RunnerLinks:0:RemotePort"] = "15031",
            ["RunnerLinks:0:LocalPort"] = "5031",
            ["RunnerLinks:0:HeartbeatTimeoutSeconds"] = "90",
            ["RunnerLinks:0:ExtraForwards:0"] = "5031:127.0.0.1:5031",
            ["RunnerLinks:0:ExtraForwards:1"] = "4011:localhost:4011",
            ["RunnerLinks:0:BackoffSeconds:0"] = "5",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            if (provisioner is not null)
            {
                services.RemoveAll<IProviderAuthProvisioner>();
                services.AddSingleton(provisioner);
            }
            if (runnerLinks || launcher is not null)
            {
                services.RemoveAll<IRunnerLinkProcessLauncher>();
                services.AddSingleton<IRunnerLinkProcessLauncher>(launcher ?? new FakeLinkProcessLauncher());
            }
        });
    });

    private string ReadWorkspaceBus()
    {
        var root = Path.Combine(_root, "logs", "bus");
        return Directory.Exists(root)
            ? string.Concat(Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories).Select(File.ReadAllText))
            : string.Empty;
    }

    private sealed class FakeLinkProcessLauncher : IRunnerLinkProcessLauncher
    {
        private int _pid = 1000;
        public List<SshCommand> Commands { get; } = [];
        public FakeLinkProcess? Forward { get; private set; }
        public Queue<int> BoundedExitCodes { get; } = [];
        public int? ForwardExitCodeOnStart { get; init; }

        public IRunnerLinkProcess Start(SshCommand command)
        {
            Commands.Add(command);
            var process = new FakeLinkProcess(++_pid, command.Arguments.Contains("-N"));
            if (command.Arguments.Contains("-N"))
            {
                Forward = process;
                if (ForwardExitCodeOnStart is { } code) process.Exit(code);
            }
            else process.Exit(BoundedExitCodes.TryDequeue(out var code) ? code : 0);
            return process;
        }

        public void Dispose() { }
    }

    private sealed class FakeLinkProcess(int pid, bool longRunning) : IRunnerLinkProcess
    {
        private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Pid { get; } = pid;
        public bool HasExited { get; private set; } = !longRunning;
        public int? ExitCode { get; private set; } = longRunning ? null : 0;
        public bool Terminated { get; private set; }

        public void Exit(int code)
        {
            HasExited = true;
            ExitCode = code;
            _completion.TrySetResult(code);
        }

        public Task<int> WaitAsync(CancellationToken cancellationToken)
            => HasExited ? Task.FromResult(ExitCode ?? 0) : _completion.Task.WaitAsync(cancellationToken);

        public Task TerminateAsync(CancellationToken cancellationToken)
        {
            Terminated = true;
            Exit(137);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingProviderAuthProvisioner : IProviderAuthProvisioner
    {
        public ProviderAuthProvisioningRequest? LastRequest { get; private set; }

        public Task<ProviderAuthProvisioningResponse> ProvisionAsync(
            ProviderAuthProvisioningRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new ProviderAuthProvisioningResponse(
                "claude",
                request.EnvironmentVariable,
                request.SshTarget,
                "awaiting-probe",
                "Credential installed and daemon environment verified.",
                DateTime.UtcNow,
                ["agent-runner.service"],
                true));
        }
    }

    private static string CreateServerDataDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-studio-server-data", "AGT-2194",
            "server-" + Guid.NewGuid().ToString("N"));
        return Path.GetFullPath(root);
    }

    private static async Task EnterMaintenance(HttpClient client, string key)
    {
        var response = await client.PostAsJsonAsync("/api/v1/management/commands", new
        {
            kind = "maintenance-enter", dryRun = false,
            confirmation = "maintenance-enter", idempotencyKey = key,
        });
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception ex) { SilentCatch.Note(ex, "ManagementApiTests root cleanup"); }
        try { Directory.Delete(_backups, true); } catch (Exception ex) { SilentCatch.Note(ex, "ManagementApiTests backup cleanup"); }
        try { Directory.Delete(_logs, true); } catch (Exception ex) { SilentCatch.Note(ex, "ManagementApiTests log cleanup"); }
    }
}
