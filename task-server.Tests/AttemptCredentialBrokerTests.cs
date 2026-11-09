using AgentStudio.TaskServer;
using AgentStudio.TaskServer.AttemptCredentials;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class AttemptCredentialBrokerTests
{
    [Fact]
    public async Task One_use_exchange_is_bound_to_live_claim_and_stale_fence_cannot_fetch_or_renew()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero));
        var store = new TaskServerStore(Options.Create(new TaskServerOptions
            { DataDirectory = temp.Path, AttemptCredentialCanaryEnabled = true }), clock);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Project", "PROJ"), "test", default);
        const string repositoryUrl = "https://github.com/example/attempt-proof.git";
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
        Assert.True(claim.Status == "claimed", $"Claim was {claim.Status}: {claim.PlacementReason}");
        var identity = new AttemptCredentialIdentity("host", "coding", claim.Task!.TaskId,
            claim.Run!.RunId, "runner", "instance", claim.Lease!.LeaseId, claim.Lease.Fence, claim.Lease.Fence);
        var roots = new MemoryRoots();
        var issuer = new FakeIssuer();
        var capability = await store.CreateAttemptCredentialCapabilityAsync(identity, default);
        var material = await store.ExchangeAttemptCredentialAsync(identity,
            capability.IssuanceId, capability.Capability, issuer, roots, default);
        Assert.Equal("linux-tmpfs", material.StorageMode);
        Assert.Equal(repositoryUrl, issuer.RepositoryUrl);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.ExchangeAttemptCredentialAsync(identity,
            capability.IssuanceId, capability.Capability, issuer, roots, default));
        var stale = identity with { Fence = identity.Fence + 1, Epoch = identity.Epoch + 1 };
        var refused = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.CreateAttemptCredentialCapabilityAsync(stale, default));
        Assert.Equal("stale-fence", refused.Code);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.RenewAttemptCredentialAsync(
            stale, capability.IssuanceId, issuer, roots, default));
        var notRenewed = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.RenewAttemptCredentialAsync(identity, capability.IssuanceId, issuer, roots, default));
        Assert.Equal("lease-not-renewed", notRenewed.Code);
        await store.RenewLeaseAsync(claim.Run.RunId, new LeaseRenewRequest(
            "runner", "instance", claim.Lease.LeaseId, claim.Lease.Fence, 240), "test", default);
        var refreshed = await store.RenewAttemptCredentialAsync(identity, capability.IssuanceId, issuer, roots, default);
        Assert.NotEqual(material.Token, refreshed.Token);
        Assert.Single(issuer.Revoked);
        var receipt = await store.RevokeAttemptCredentialAsync(identity, capability.IssuanceId, issuer, roots, default);
        Assert.Equal("revoked", receipt.RevocationStatus);
        Assert.Null(await roots.ReadAsync("issued-" + capability.IssuanceId, default));
        var deletion = await store.RecordAttemptCredentialDeletionAsync(new AttemptCredentialDeletionRequest(
            identity, capability.IssuanceId, "container-tmpfs-removed"), default);
        Assert.Equal("container-tmpfs-removed", deletion.DeletionStatus);
    }

    private sealed record LiveClaim(TaskServerStore Store, AttemptCredentialIdentity Identity, ClaimResponse Claim, string ProjectId);

    private static async Task<LiveClaim> ClaimAsync(string dataDirectory, TimeProvider clock)
    {
        var store = new TaskServerStore(Options.Create(new TaskServerOptions
            { DataDirectory = dataDirectory, AttemptCredentialCanaryEnabled = true }), clock);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Project", "PROJ"), "test", default);
        const string repositoryUrl = "https://github.com/example/attempt-proof.git";
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
        Assert.True(claim.Status == "claimed", $"Claim was {claim.Status}: {claim.PlacementReason}");
        var identity = new AttemptCredentialIdentity("host", "coding", claim.Task!.TaskId,
            claim.Run!.RunId, "runner", "instance", claim.Lease!.LeaseId, claim.Lease.Fence, claim.Lease.Fence);
        return new LiveClaim(store, identity, claim, project.ProjectId);
    }

    /// <summary>SM-11: a finished attempt's token is revoked by the server even when its host never tears down.</summary>
    [Fact]
    public async Task Token_taken_from_a_finished_attempt_is_refused()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        var (store, identity, claim, _) = await ClaimAsync(temp.Path, clock);
        var roots = new MemoryRoots();
        var issuer = new FakeIssuer();
        var capability = await store.CreateAttemptCredentialCapabilityAsync(identity, default);
        var material = await store.ExchangeAttemptCredentialAsync(identity,
            capability.IssuanceId, capability.Capability, issuer, roots, default);
        Assert.Equal(0, await store.RevokeFinishedAttemptCredentialsAsync(issuer, roots, default));
        Assert.Empty(issuer.Revoked);

        await store.ReleaseLeaseAsync(claim.Run!.RunId, new LeaseReleaseRequest(
            "runner", "instance", claim.Lease!.LeaseId, claim.Lease.Fence, "failed"), "test", default);

        Assert.Equal(1, await store.RevokeFinishedAttemptCredentialsAsync(issuer, roots, default));
        Assert.Equal([material.Token], issuer.Revoked);
        Assert.Null(await roots.ReadAsync("issued-" + capability.IssuanceId, default));
        Assert.Equal(0, await store.RevokeFinishedAttemptCredentialsAsync(issuer, roots, default));

        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.RenewAttemptCredentialAsync(
            identity, capability.IssuanceId, issuer, roots, default));
        await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.CreateAttemptCredentialCapabilityAsync(identity, default));
        // The host's own teardown still gets a receipt instead of a conflict.
        var receipt = await store.RevokeAttemptCredentialAsync(identity, capability.IssuanceId, issuer, roots, default);
        Assert.Equal("revoked-at-completion", receipt.RevocationStatus);
        Assert.Single(issuer.Revoked);
    }

    /// <summary>SM-11: an attempt token is bound to a project the host still holds.</summary>
    [Fact]
    public async Task Exchange_is_refused_when_the_host_no_longer_binds_the_project()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        var (store, identity, _, projectId) = await ClaimAsync(temp.Path, clock);
        var capability = await store.CreateAttemptCredentialCapabilityAsync(identity, default);
        await store.UpdateHostProjectPolicyAsync("host",
            new UpdateHostProjectPolicyRequest(false, [], 0), "owner", default);

        var refused = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.ExchangeAttemptCredentialAsync(
            identity, capability.IssuanceId, capability.Capability, new FakeIssuer(), new MemoryRoots(), default));
        Assert.Equal("project-not-bound", refused.Code);
        Assert.False(string.IsNullOrEmpty(projectId));
    }

    private sealed class MemoryRoots : IAttemptRootSecretStore
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
        public Task EnsureWritableAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeIssuer : IRepositoryTokenIssuer
    {
        private int _next;
        public string? RepositoryUrl { get; private set; }
        public List<string> Revoked { get; } = [];
        public Task<IssuedRepositoryToken> IssueReadAsync(string repositoryKey, string repositoryUrl, CancellationToken ct)
        {
            RepositoryUrl = repositoryUrl;
            return Task.FromResult(new IssuedRepositoryToken("opaque-" + ++_next,
                new DateTime(2026, 10, 28, 10, 30, 0, DateTimeKind.Utc), 123));
        }
        public Task<bool> RevokeAsync(string token, CancellationToken ct)
        {
            Revoked.Add(token);
            return Task.FromResult(true);
        }
    }
}
