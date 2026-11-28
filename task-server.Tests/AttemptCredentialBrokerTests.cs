using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.AttemptCredentials;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class AttemptCredentialBrokerTests
{
    [Fact]
    public async Task GitHub_installation_token_reads_one_private_repository_and_is_denied_push()
    {
        const string repositoryId = "test-repository";
        const string repositoryUrl = "https://github.com/example/private.git";
        var roots = new MemoryRoots();
        using var key = RSA.Create();
        var rootKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repositoryId))).ToLowerInvariant();
        await roots.WriteAsync("github-app-" + rootKey, JsonSerializer.Serialize(
            new GitHubAppRoot(1, 10, 123, repositoryUrl, key.ExportRSAPrivateKeyPem())), default);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 28, 10, 0, 0, TimeSpan.Zero));
        using var github = new GitHubInstallationDouble(clock);
        using var http = new HttpClient(github);
        var issuer = new GitHubAppTokenIssuer(http, roots, clock: clock);

        var token = await issuer.IssueReadAsync(repositoryId, repositoryUrl, default);
        Assert.Equal(123, token.RepositoryId);
        Assert.Equal([123L], github.RepositoryIds);
        Assert.Equal(new KeyValuePair<string, string>("contents", "read"), Assert.Single(github.Permissions!));

        async Task<HttpStatusCode> AccessAsync(string url, string service)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url + "/info/refs?service=" + service);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
            using var response = await http.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await AccessAsync(repositoryUrl, "git-upload-pack"));
        Assert.Equal(HttpStatusCode.Forbidden, await AccessAsync(repositoryUrl, "git-receive-pack"));
        Assert.Equal(HttpStatusCode.Forbidden, await AccessAsync("https://github.com/example/other.git", "git-upload-pack"));
    }

    private sealed class GitHubInstallationDouble(TimeProvider clock) : HttpMessageHandler
    {
        private readonly string _token = Guid.NewGuid().ToString("N");
        public long[]? RepositoryIds { get; private set; }
        public Dictionary<string, string>? Permissions { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!;
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("https://api.github.com/app/installations/10/access_tokens", url.AbsoluteUri);
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                RepositoryIds = body.GetProperty("repository_ids").Deserialize<long[]>();
                Permissions = body.GetProperty("permissions").Deserialize<Dictionary<string, string>>();
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new
                    {
                        token = _token, expires_at = clock.GetUtcNow().AddMinutes(30), permissions = Permissions,
                        repositories = RepositoryIds!.Select(id => new { id }),
                    }),
                };
            }

            var id = url.AbsolutePath == "/example/private.git/info/refs" ? 123 : 456;
            var contents = Permissions!.GetValueOrDefault("contents");
            var allowed = request.Headers.Authorization?.Parameter == _token && RepositoryIds!.Contains(id)
                && (contents == "write" || contents == "read" && url.Query == "?service=git-upload-pack");
            return new HttpResponseMessage(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
        }
    }

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
            claim.Run!.RunId, "runner", "instance", claim.Lease!.LeaseId, claim.Lease.Fence, claim.Lease.Fence,
            RepositorySource: repositoryUrl);
        var roots = new MemoryRoots();
        var issuer = new FakeIssuer();
        var wrongRepository = identity with { RepositorySource = "https://github.com/example/other.git" };
        Assert.Equal("credential-repository-mismatch", (await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.CreateAttemptCredentialCapabilityAsync(wrongRepository, default))).Code);
        Assert.Equal("stale-fence", (await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.CreateAttemptCredentialCapabilityAsync(identity with { LeaseId = "different-lease" }, default))).Code);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.CreateAttemptCredentialCapabilityAsync(identity with { Role = "review" }, default));
        Assert.Equal(0, issuer.Issued);
        var capability = await store.CreateAttemptCredentialCapabilityAsync(identity, default);
        Assert.Equal("credential-repository-mismatch", (await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ExchangeAttemptCredentialAsync(wrongRepository, capability.IssuanceId, capability.Capability,
                issuer, roots, default))).Code);
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
        Assert.Equal("credential-repository-mismatch", (await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.RenewAttemptCredentialAsync(wrongRepository, capability.IssuanceId, issuer, roots, default))).Code);
        var refreshed = await store.RenewAttemptCredentialAsync(identity, capability.IssuanceId, issuer, roots, default);
        Assert.NotEqual(material.Token, refreshed.Token);
        Assert.Equal(capability.IssuanceId, refreshed.IssuanceId);
        Assert.Single(issuer.Revoked);
        var receipt = await store.RevokeAttemptCredentialAsync(identity, capability.IssuanceId, issuer, roots, default);
        Assert.Equal("revoked", receipt.RevocationStatus);
        Assert.Null(await roots.ReadAsync("issued-" + capability.IssuanceId, default));
        var deletion = await store.RecordAttemptCredentialDeletionAsync(new AttemptCredentialDeletionRequest(
            identity, capability.IssuanceId, "container-tmpfs-removed"), default);
        Assert.Equal("container-tmpfs-removed", deletion.DeletionStatus);
        Assert.Equal(capability.IssuanceId, deletion.IssuanceId);
    }

    private sealed record LiveClaim(TaskServerStore Store, AttemptCredentialIdentity Identity, ClaimResponse Claim, string ProjectId);

    [Fact]
    public async Task Failed_refresh_write_preserves_the_current_root_and_can_retry_on_the_same_lease()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero));
        var (store, identity, claim, _) = await ClaimAsync(temp.Path, clock);
        var roots = new MemoryRoots();
        var issuer = new FakeIssuer();
        var current = await IssueAsync(store, identity, issuer, roots);
        await store.RenewLeaseAsync(claim.Run!.RunId, new LeaseRenewRequest(
            "runner", "instance", claim.Lease!.LeaseId, claim.Lease.Fence, 240), "test", default);

        roots.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() =>
            store.RenewAttemptCredentialAsync(identity, current.IssuanceId, issuer, roots, default));
        Assert.Equal(current.Token, await roots.ReadAsync("issued-" + current.IssuanceId, default));
        Assert.DoesNotContain(current.Token, issuer.Revoked);

        roots.FailWrites = false;
        var refreshed = await store.RenewAttemptCredentialAsync(identity, current.IssuanceId, issuer, roots, default);
        Assert.Equal(current.IssuanceId, refreshed.IssuanceId);
        Assert.Equal(refreshed.Token, await roots.ReadAsync("issued-" + current.IssuanceId, default));
        Assert.Contains(current.Token, issuer.Revoked);
    }

    private static async Task<LiveClaim> ClaimAsync(string dataDirectory, TimeProvider clock, string prefix = "PROJ")
    {
        var store = new TaskServerStore(Options.Create(new TaskServerOptions
            { DataDirectory = dataDirectory, AttemptCredentialCanaryEnabled = true }), clock);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Project " + prefix, prefix), "test", default);
        var repositoryUrl = $"https://github.com/example/attempt-proof-{prefix.ToLowerInvariant()}.git";
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

    [Fact]
    public async Task Interrupted_intent_batch_is_resumed_after_restart()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        var (store, identity, claim, _) = await ClaimAsync(temp.Path, clock);
        var roots = new MemoryRoots();
        var issuer = new FakeIssuer();
        var first = await IssueAsync(store, identity, issuer, roots);
        await FinishAsync(store, claim);
        var second = await IssueFinishedAsync(temp.Path, clock, issuer, roots);
        using var interrupted = new CancellationTokenSource();
        roots.ReadFailure = _ =>
        {
            interrupted.Cancel();
            return new OperationCanceledException("Simulated process interruption after intent commit.");
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.RevokeFinishedAttemptCredentialsAsync(issuer, roots, interrupted.Token));
        Assert.Equal("revocation-pending", (await ReadCleanupAsync(store, first.IssuanceId)).Revocation);
        Assert.Equal("revocation-pending", (await ReadCleanupAsync(store, second.IssuanceId)).Revocation);
        Assert.Empty(issuer.Revoked);

        roots.ReadFailure = null;
        store = await RestartAsync(temp.Path, clock);
        Assert.Equal(2, await store.RevokeFinishedAttemptCredentialsAsync(issuer, roots, default));
        Assert.Equal(new[] { first.Token, second.Token }.Order(), issuer.Revoked.Order());
        Assert.Empty(roots.Values);
        Assert.Equal(2, issuer.Issued);
        Assert.Equal(0, await store.RevokeFinishedAttemptCredentialsAsync(issuer, roots, default));
    }

    [Fact]
    public async Task Unreadable_first_root_does_not_suppress_later_items_and_is_retried()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        var (store, identity, claim, _) = await ClaimAsync(temp.Path, clock);
        var roots = new MemoryRoots();
        var issuer = new FakeIssuer();
        var first = await IssueAsync(store, identity, issuer, roots);
        await FinishAsync(store, claim);
        var second = await IssueFinishedAsync(temp.Path, clock, issuer, roots);
        roots.ReadFailure = name => name == "issued-" + first.IssuanceId ? new IOException("Unreadable root.") : null;

        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            store.RevokeFinishedAttemptCredentialsAsync(issuer, roots, default));
        Assert.Contains(first.IssuanceId, failure.ToString());
        Assert.Equal([second.Token], issuer.Revoked);
        Assert.Equal("revocation-pending", (await ReadCleanupAsync(store, first.IssuanceId)).Revocation);
        Assert.Equal("revoked-at-completion", (await ReadCleanupAsync(store, second.IssuanceId)).Revocation);
        Assert.Null(await roots.ReadAsync("issued-" + second.IssuanceId, default));

        roots.ReadFailure = null;
        Assert.Equal(1, await store.RevokeFinishedAttemptCredentialsAsync(issuer, roots, default));
        Assert.Equal(new[] { first.Token, second.Token }.Order(), issuer.Revoked.Order());
        Assert.Empty(roots.Values);
        Assert.Equal(2, issuer.Issued);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Root_deletion_resumes_without_repeating_issuer_work(bool explicitRevoke, bool deletedBeforeInterruption)
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        var (store, identity, claim, _) = await ClaimAsync(temp.Path, clock);
        var roots = new MemoryRoots();
        var issuer = new FakeIssuer();
        var material = await IssueAsync(store, identity, issuer, roots);
        roots.DeleteBeforeFailure = deletedBeforeInterruption;
        roots.DeleteFailure = _ => new IOException("Root deletion interrupted.");
        if (explicitRevoke)
            await Assert.ThrowsAsync<IOException>(() => store.RevokeAttemptCredentialAsync(
                identity, material.IssuanceId, issuer, roots, default));
        else
        {
            await FinishAsync(store, claim);
            await Assert.ThrowsAsync<AggregateException>(() =>
                store.RevokeFinishedAttemptCredentialsAsync(issuer, roots, default));
        }
        Assert.Equal([material.Token], issuer.Revoked);
        Assert.EndsWith("-root-delete-pending", (await ReadCleanupAsync(store, material.IssuanceId)).Revocation);
        // Host deletion evidence must not erase the independent root cleanup phase.
        await store.RecordAttemptCredentialDeletionAsync(new AttemptCredentialDeletionRequest(
            identity, material.IssuanceId, "container-tmpfs-removed"), default);

        roots.DeleteFailure = null;
        roots.ReadFailure = _ => new IOException("Deletion retry must not read the token again.");
        store = await RestartAsync(temp.Path, clock);
        var receipt = await store.RevokeAttemptCredentialAsync(identity, material.IssuanceId, issuer, roots, default);
        Assert.Equal(explicitRevoke ? "revoked" : "revoked-at-completion", receipt.RevocationStatus);
        Assert.Equal("container-tmpfs-removed", receipt.DeletionStatus);
        Assert.Empty(roots.Values);
        Assert.Equal([material.Token], issuer.Revoked);
        Assert.Equal(1, issuer.Issued);
        Assert.Equal(0, await store.RevokeFinishedAttemptCredentialsAsync(issuer, roots, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issuer_failure_keeps_terminal_expiry_backstop_while_root_deletion_is_retried(bool issuerThrows)
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        var (store, identity, claim, _) = await ClaimAsync(temp.Path, clock);
        var roots = new MemoryRoots();
        var issuer = new FakeIssuer { RevocationSucceeds = false, RevocationThrows = issuerThrows };
        var material = await IssueAsync(store, identity, issuer, roots);
        await FinishAsync(store, claim);
        roots.DeleteFailure = _ => new IOException("Cannot delete root.");
        await Assert.ThrowsAsync<AggregateException>(() =>
            store.RevokeFinishedAttemptCredentialsAsync(issuer, roots, default));
        Assert.Equal("expiry-backstop-root-delete-pending", (await ReadCleanupAsync(store, material.IssuanceId)).Revocation);

        roots.DeleteFailure = null;
        store = await RestartAsync(temp.Path, clock);
        Assert.Equal(1, await store.RevokeFinishedAttemptCredentialsAsync(issuer, roots, default));
        var receipt = await store.RevokeAttemptCredentialAsync(identity, material.IssuanceId, issuer, roots, default);
        Assert.Equal("expiry-backstop", receipt.RevocationStatus);
        Assert.Equal([material.Token], issuer.Revoked);
        Assert.Empty(roots.Values);
        Assert.Equal(0, await store.RevokeFinishedAttemptCredentialsAsync(issuer, roots, default));
    }

    [Theory]
    [InlineData(null, null, "revoked")]
    [InlineData("revoked", "root-delete-pending", "revoked")]
    [InlineData("expiry-backstop", "root-delete-pending", "expiry-backstop")]
    public async Task Sweep_resumes_legacy_pending_cleanup_even_with_a_live_lease(
        string? revocation, string? deletion, string expected)
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        var (store, identity, _, _) = await ClaimAsync(temp.Path, clock);
        var roots = new MemoryRoots();
        var issuer = new FakeIssuer();
        var material = await IssueAsync(store, identity, issuer, roots);
        // Only this isolated fixture database is seeded with pre-upgrade persisted state.
        await using (var connection = new SqliteConnection("Data Source=" + store.DatabasePath))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE attempt_credential_issuances
                   SET revoked_at = $now, revocation_status = $revocation, deletion_status = $deletion
                 WHERE issuance_id = $id;
                """;
            command.Parameters.AddWithValue("$now", clock.GetUtcNow().UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$revocation", (object?)revocation ?? DBNull.Value);
            command.Parameters.AddWithValue("$deletion", (object?)deletion ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", material.IssuanceId);
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(1, await store.RevokeFinishedAttemptCredentialsAsync(issuer, roots, default));
        Assert.Equal(revocation is null ? 1 : 0, issuer.Revoked.Count);
        Assert.Empty(roots.Values);
        Assert.Equal(expected, (await store.RevokeAttemptCredentialAsync(
            identity, material.IssuanceId, issuer, roots, default)).RevocationStatus);
    }

    private static Task FinishAsync(TaskServerStore store, ClaimResponse claim)
        => store.ReleaseLeaseAsync(claim.Run!.RunId, new LeaseReleaseRequest(
            "runner", "instance", claim.Lease!.LeaseId, claim.Lease.Fence, "failed"), "test", default);

    private static async Task<TaskServerStore> RestartAsync(string directory, TimeProvider clock)
    {
        var store = new TaskServerStore(Options.Create(new TaskServerOptions
            { DataDirectory = directory, AttemptCredentialCanaryEnabled = true }), clock);
        await store.InitializeAsync();
        return store;
    }

    private static async Task<AttemptCredentialMaterial> IssueAsync(TaskServerStore store,
        AttemptCredentialIdentity identity, FakeIssuer issuer, MemoryRoots roots)
    {
        var capability = await store.CreateAttemptCredentialCapabilityAsync(identity, default);
        return await store.ExchangeAttemptCredentialAsync(identity,
            capability.IssuanceId, capability.Capability, issuer, roots, default);
    }

    private static async Task<AttemptCredentialMaterial> IssueFinishedAsync(string directory, TimeProvider clock,
        FakeIssuer issuer, MemoryRoots roots)
    {
        var (store, identity, claim, _) = await ClaimAsync(directory, clock, "NEXT");
        var material = await IssueAsync(store, identity, issuer, roots);
        await FinishAsync(store, claim);
        return material;
    }

    private static async Task<(string? Revocation, string? Deletion)> ReadCleanupAsync(TaskServerStore store, string issuanceId)
    {
        await using var connection = new SqliteConnection("Data Source=" + store.DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT revocation_status, deletion_status FROM attempt_credential_issuances WHERE issuance_id = $id;";
        command.Parameters.AddWithValue("$id", issuanceId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private sealed class MemoryRoots : IAttemptRootSecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public Func<string, Exception?>? ReadFailure { get; set; }
        public Func<string, Exception?>? DeleteFailure { get; set; }
        public bool DeleteBeforeFailure { get; set; }
        public bool FailWrites { get; set; }
        public Task<string?> ReadAsync(string name, CancellationToken ct)
        {
            if (ReadFailure?.Invoke(name) is { } failure) throw failure;
            return Task.FromResult(Values.GetValueOrDefault(name));
        }
        public Task WriteAsync(string name, string value, CancellationToken ct)
        {
            if (FailWrites) throw new IOException("Root file write failed.");
            Values[name] = value;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string name, CancellationToken ct)
        {
            if (DeleteBeforeFailure) Values.Remove(name);
            if (DeleteFailure?.Invoke(name) is { } failure) throw failure;
            Values.Remove(name);
            return Task.CompletedTask;
        }
        public Task EnsureWritableAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeIssuer : IRepositoryTokenIssuer
    {
        public int Issued { get; private set; }
        public bool RevocationSucceeds { get; set; } = true;
        public bool RevocationThrows { get; set; }
        public string? RepositoryUrl { get; private set; }
        public List<string> Revoked { get; } = [];
        public Task<IssuedRepositoryToken> IssueReadAsync(string repositoryKey, string repositoryUrl, CancellationToken ct)
        {
            RepositoryUrl = repositoryUrl;
            return Task.FromResult(new IssuedRepositoryToken("opaque-" + ++Issued,
                new DateTime(2026, 10, 28, 10, 30, 0, DateTimeKind.Utc), 123));
        }
        public Task<bool> RevokeAsync(string token, CancellationToken ct)
        {
            Revoked.Add(token);
            if (RevocationThrows) throw new HttpRequestException("Issuer unavailable.");
            return Task.FromResult(RevocationSucceeds);
        }
    }
}
