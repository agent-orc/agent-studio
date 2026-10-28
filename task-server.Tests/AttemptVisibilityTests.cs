using System.Text.Json;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.AttemptCredentials;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class AttemptVisibilityTests
{
    [Fact]
    public async Task Management_endpoint_returns_the_empty_attempt_snapshot()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);

        var snapshot = await client.GetFromJsonAsync<AttemptVisibilitySnapshotDto>(
            "/api/v1/management/attempt-visibility");

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Attempts);
    }

    [Fact]
    public async Task Lease_receipt_and_issuance_share_identity_without_exposing_material()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero));
        var store = new TaskServerStore(Options.Create(new TaskServerOptions
            { DataDirectory = temp.Path, AttemptCredentialCanaryEnabled = true }), clock);
        await store.InitializeAsync();
        Assert.Empty((await store.GetAttemptVisibilityAsync(default)).Attempts);
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Project", "PROJ"), "test", default);
        var repositoryUrl = "https://github.com/example/visibility.git";
        await store.RegisterProjectRepositoryAsync(new RegisterProjectRepositoryRequest(
            project.ProjectId, project.WorkspaceId, project.Name, project.TaskKeyPrefix,
            repositoryUrl, "develop"), "test", default);
        await store.CreateTaskAsync(project.ProjectId,
            new CreateTaskRequest("Task", "work", "2-ready"), "test", default);
        await store.RegisterRunnerAsync("runner", new RegisterRunnerRequest("runner", "host", "instance", "1.0",
            TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]), "test", default);
        await store.RecordProjectRepositoryProbeAsync(project.ProjectId, "runner",
            new ProjectRepositoryProbeRequest(repositoryUrl, repositoryUrl, true, true, false), default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner", "instance"), "test", default);
        Assert.Equal("claimed", claim.Status);
        var run = claim.Run!.RunId;
        var lease = claim.Lease!;
        var running = new AttemptExecutorReceiptRequest("runner", "instance", lease.LeaseId,
            lease.Fence, "container", "sha256:" + new string('a', 64), 3, "repo:miss", "running", clock.GetUtcNow().UtcDateTime);
        await store.RecordAttemptExecutorReceiptAsync(run, running, default);
        var visible = Assert.Single((await store.GetAttemptVisibilityAsync(default)).Attempts);
        Assert.Equal("host", visible.HostId);
        Assert.Equal("Project", visible.Project);
        Assert.Equal("container", visible.Executor);
        Assert.Equal("occupied", visible.Occupancy);
        Assert.Equal(3, visible.GrantHalfCores);
        Assert.Equal("sha256:" + new string('a', 64), visible.ImageDigest);
        await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.RecordAttemptExecutorReceiptAsync(run, running with { Fence = lease.Fence + 1 }, default));

        var identity = new AttemptCredentialIdentity("host", "coding", claim.Task!.TaskId,
            run, "runner", "instance", lease.LeaseId, lease.Fence, lease.Fence);
        var capability = await store.CreateAttemptCredentialCapabilityAsync(identity, default);
        var material = await store.ExchangeAttemptCredentialAsync(identity, capability.IssuanceId,
            capability.Capability, new Issuer(clock), new Roots(), default);
        var inventory = await store.GetCredentialInventoryAsync(StudioUserRoles.Owner, default);
        var child = Assert.Single(inventory.Items, item => item.Id == "attempt:" + capability.IssuanceId);
        Assert.Contains(run, child.Binding);
        Assert.Equal("github-app", child.Issuer);
        Assert.Equal("linux-tmpfs", child.StorageMode);
        Assert.Equal("pending · file pending", child.RevocationReceipt);
        var json = JsonSerializer.Serialize(inventory);
        Assert.DoesNotContain(capability.Capability, json, StringComparison.Ordinal);
        Assert.DoesNotContain(material.Token, json, StringComparison.Ordinal);
        Assert.DoesNotContain("capabilityHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", child.Detail, StringComparison.OrdinalIgnoreCase);

        clock.Advance(TimeSpan.FromHours(1));
        var stale = Assert.Single((await store.GetAttemptVisibilityAsync(default)).Attempts);
        Assert.Equal("stale", stale.LeaseState);
        Assert.Equal("orphan", stale.Occupancy);
        await store.RecordAttemptExecutorReceiptAsync(run, running with { CleanupStatus = "failed" }, default);
        Assert.Equal("failed", Assert.Single((await store.GetAttemptVisibilityAsync(default)).Attempts).CleanupStatus);
        await store.RecordAttemptExecutorReceiptAsync(run, running with { CleanupStatus = "verified" }, default);
        var settled = Assert.Single((await store.GetAttemptVisibilityAsync(default)).Attempts);
        Assert.Equal("released", settled.Occupancy);
        Assert.Equal("verified", settled.CleanupStatus);
    }

    private sealed class Roots : IAttemptRootSecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public Task<string?> ReadAsync(string name, CancellationToken ct)
            => Task.FromResult(_values.GetValueOrDefault(name));
        public Task WriteAsync(string name, string value, CancellationToken ct)
        {
            _values[name] = value;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string name, CancellationToken ct)
        {
            _values.Remove(name);
            return Task.CompletedTask;
        }
    }

    private sealed class Issuer(ManualTimeProvider clock) : IRepositoryTokenIssuer
    {
        public Task<IssuedRepositoryToken> IssueReadAsync(string repositoryKey,
            string repositoryUrl, CancellationToken ct)
            => Task.FromResult(new IssuedRepositoryToken("opaque-" + Guid.NewGuid().ToString("N"),
                clock.GetUtcNow().AddMinutes(30).UtcDateTime, 1));
        public Task<bool> RevokeAsync(string token, CancellationToken ct) => Task.FromResult(true);
    }
}
