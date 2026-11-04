using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.QualityStudio;
using AgentStudio.Bus;
using AgentStudio.Security;
using AgentStudio.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using Xunit;

namespace AgentStudio.Tests;

public sealed class QualityStudioIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quality-studio-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Handoff_claims_are_versioned_scoped_and_expire_in_sixty_seconds()
    {
        var now = DateTimeOffset.Parse("2026-11-02T12:00:00Z");
        var claims = QualityStudioHandoffClaims.Issue("https://studio.test", "user-1", "session-1",
            "operator", "proj-002", "/?repo=proj-002", now, "once-1");
        Assert.Equal("https://studio.test", claims.iss);
        Assert.Equal("quality-studio", claims.aud);
        Assert.Equal("user-1", claims.sub);
        Assert.Equal("session-1", claims.sid);
        Assert.Equal("operator", claims.role);
        Assert.Equal(["proj-002"], claims.repos);
        Assert.Equal("/?repo=proj-002", claims.target);
        Assert.Equal("once-1", claims.jti);
        Assert.Equal(60, claims.exp - claims.iat);
        Assert.Equal(1, claims.ver);

        if (OperatingSystem.IsLinux())
        {
            var seed = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
            var token = Ed25519Assertion.Sign(seed, claims);
            Assert.Equal(3, token.Split('.').Length);
            Assert.Equal(32, Ed25519Assertion.PublicKey(seed).Length);
            var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            Assert.Equal(claims.exp, document.RootElement.GetProperty("exp").GetInt64());
            Assert.Equal("proj-002", document.RootElement.GetProperty("repos")[0].GetString());
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    [Theory]
    [InlineData("https://evil.test/repo")]
    [InlineData("//evil.test/repo")]
    [InlineData("/\\evil.test/repo")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/%2Fevil.test/repo")]
    public void Absolute_or_protocol_relative_return_is_refused(string value)
        => Assert.False(QualityStudioEndpoints.ValidReturnPath(value));

    [Fact]
    public void Relative_return_is_accepted_and_handoff_field_is_redacted()
    {
        Assert.True(QualityStudioEndpoints.ValidReturnPath("/?repo=proj-002&view=dashboard"));
        var assertion = "header.payload." + Guid.NewGuid().ToString("N");
        Assert.DoesNotContain(assertion, CredentialRedactor.Redact("handoff=" + assertion));
        Assert.DoesNotContain(assertion, CredentialRedactor.Redact("{\"handoff\":\"" + assertion + "\"}"));
        Assert.Equal("quality-studio/api-token", SecretReference.Parse(QualityStudioEndpoints.TokenReference).Name);
        Assert.Equal("quality-studio/signing-key", SecretReference.Parse(QualityStudioEndpoints.KeyReference).Name);
        Assert.True(SecretAccessPolicy.IsPlatformOnly("quality-studio/signing-key"));
    }

    [Fact]
    public void Project_read_right_is_scoped_to_granted_projects()
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["TaskRepository"] = _root, ["Security:Profile"] = "networked" }).Build();
        var access = new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance);
        access.Bootstrap(new BootstrapRequest("owner", "correct horse battery staple!", null));
        var user = access.CreateUser(new CreateUserRequest("scoped", "Scoped", StudioRoles.Operator,
            ["PROJ-001"], "violet autumn compass 47!"));
        Assert.True(access.AuthorizeUser(user.User.Id, IamPermissions.ProjectRead,
            PermissionTarget.ForProject("PROJ-001")).Allowed);
        Assert.False(access.AuthorizeUser(user.User.Id, IamPermissions.ProjectRead,
            PermissionTarget.ForProject("PROJ-002")).Allowed);
    }

    [Fact]
    public async Task Revoke_queue_retries_after_a_quality_studio_restart_and_delivers_once()
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["TaskRepository"] = _root }).Build();
        var connections = new QualityStudioConnectionStore(configuration);
        connections.Set(new QualityStudioConnection("http://quality.test", "https://quality.test", "agent-studio"));
        var handler = new RevokeHandler();
        using var client = new HttpClient(handler);
        var factory = new FixedClientFactory(client);
        var secrets = new FixedSecretProvider();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-02T12:00:00Z"));
        using var first = new QualityStudioRevokeOutbox(configuration, connections, secrets, factory, clock,
            NullLogger<QualityStudioRevokeOutbox>.Instance);
        first.EnqueueSid("session-1");
        Assert.True(await first.DeliverDueOnceAsync(CancellationToken.None));
        Assert.Equal(1, first.PendingCount);
        using var restarted = new QualityStudioRevokeOutbox(configuration, connections, secrets, factory, clock,
            NullLogger<QualityStudioRevokeOutbox>.Instance);
        Assert.Equal(1, restarted.PendingCount);
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(await restarted.DeliverDueOnceAsync(CancellationToken.None));
        Assert.Equal(0, restarted.PendingCount);
        Assert.Equal(2, handler.Calls);
        var path = Path.Combine(_root, ".security", "quality-studio-revoke-outbox.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(0, document.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Verdict_ingest_replays_idempotently_after_restart_and_expires_after_seven_days()
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["TaskRepository"] = _root }).Build();
        var connections = new QualityStudioConnectionStore(configuration);
        connections.Set(new QualityStudioConnection("http://quality.test", "https://quality.test", "agent-studio"));
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-02T12:00:00Z"));
        var feed = new AgentMessageBusBridge(new AgentMessageBusStore(), configuration,
            NullLogger<AgentMessageBusBridge>.Instance, clock);
        var handler = new VerdictHandler { FailuresRemaining = 1 };
        using var client = new HttpClient(handler);
        var factory = new FixedClientFactory(client);
        var secrets = new FixedSecretProvider();
        using var parsed = JsonDocument.Parse("""
            {"repository":"repo","baseSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
             "headSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
             "policyHash":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
             "ruleSetHash":"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
             "catalogueVersion":"1","verdict":"pass","findings":[]}
            """);
        var document = parsed.RootElement.Clone();
        var round = new QualityChangeReviewRound(2, "attempt-2", "run-2", "repo",
            new string('a', 40), new string('b', 40), 2, "decide", clock.GetUtcNow().UtcDateTime, document);
        using (var first = new QualityChangeReviewOutbox(configuration, connections, secrets, factory, clock,
                   feed, NullLogger<QualityChangeReviewOutbox>.Instance))
        {
            first.Enqueue("repo", "AGT-2", round, document);
            first.Enqueue("repo", "AGT-2", round, document);
            Assert.Equal(1, first.PendingCount);
            Assert.True(await first.DeliverDueOnceAsync(CancellationToken.None));
            Assert.Equal(1, first.PendingCount);
        }
        using (var restarted = new QualityChangeReviewOutbox(configuration, connections, secrets, factory, clock,
                   feed, NullLogger<QualityChangeReviewOutbox>.Instance))
        {
            Assert.Equal(1, restarted.PendingCount);
            clock.Advance(TimeSpan.FromSeconds(6));
            Assert.True(await restarted.DeliverDueOnceAsync(CancellationToken.None));
            Assert.Equal(0, restarted.PendingCount);
            Assert.Equal(2, handler.Calls);
            using var changed = JsonDocument.Parse(document.GetRawText().Replace("\"verdict\":\"pass\"", "\"verdict\":\"block\""));
            // A different key simulates another reviewed range; its endpoint stays down.
            using var other = JsonDocument.Parse(changed.RootElement.GetRawText().Replace(new string('b', 40), new string('c', 40)));
            handler.FailuresRemaining = int.MaxValue;
            restarted.Enqueue("repo", "AGT-3", round with { AttemptId = "attempt-3" }, other.RootElement);
            clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
            Assert.True(await restarted.DeliverDueOnceAsync(CancellationToken.None));
            Assert.Equal(0, restarted.PendingCount);
            Assert.False(await restarted.DeliverDueOnceAsync(CancellationToken.None));
            Assert.Equal(2, handler.Calls);
        }
        var feedLines = Directory.GetFiles(AgentMessageBusPaths.BusRoot(_root), "*.jsonl", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Where(line => line.Contains("quality-change-review-delivery-expired", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(feedLines);
    }

    private sealed class VerdictHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int FailuresRemaining { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/api/v1/repos/repo/change-reviews", request.RequestUri?.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("agent-studio-review-plane", body.RootElement.GetProperty("provenance").GetProperty("producer").GetString());
            Calls++;
            return new HttpResponseMessage(FailuresRemaining-- > 0 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }
    }

    private sealed class RevokeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/api/v1/federation/revoke", request.RequestUri?.AbsolutePath);
            Assert.Equal("agent-studio", request.Headers.GetValues("X-Client-Id").Single());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("session-1", body.RootElement.GetProperty("sid").GetString());
            Calls++;
            return new HttpResponseMessage(Calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }
    }

    private sealed class FixedClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FixedSecretProvider : ISecretProvider
    {
        public Task<ResolvedSecret> GetForPlatformAsync(SecretReference reference, string purpose, CancellationToken ct = default)
            => Task.FromResult(new ResolvedSecret(reference, reference, "test-" + Guid.NewGuid().ToString("N")));
        public Task<SecretListing> ListAsync(SecretScope? scope = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SecretRecord> PutAsync(SecretScope scope, string name, string value, SecretActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SecretRecord> RotateAsync(SecretScope scope, string name, string value, SecretActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SecretRecord> RevokeAsync(SecretScope scope, string name, int? version, SecretActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(SecretScope scope, string name, SecretActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task EnsureWritableAsync(SecretScope scope, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SecretAuditEntry>> ReadAuditAsync(SecretScope? scope, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ResolvedSecret> GetForWorkloadAsync(SecretReference reference, SecretWorkload workload, string purpose, CancellationToken ct = default) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
