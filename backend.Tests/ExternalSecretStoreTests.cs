using System.Net;
using System.Text;
using System.Text.Json;
using AgentStudio.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ExternalSecretStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "secret-adapter-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly string _token = "token-" + Guid.NewGuid().ToString("N");
    private static readonly SecretScope Scope = SecretScope.ForEnvironment("demo", "stage");
    private static readonly SecretActor Owner = SecretActor.Human("owner");
    private static readonly SecretWorkload Workload = new(SecretWorkload.DeployJob, "deploy-1", "demo", "stage", ["API_KEY"]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Theory]
    [InlineData("vault")]
    [InlineData("onepassword")]
    public async Task Reads_versions_rotates_revokes_and_bounds_cache(string kind)
    {
        var fixture = await Setup(kind, 30);
        var first = "first-" + Guid.NewGuid().ToString("N");
        var second = "second-" + Guid.NewGuid().ToString("N");
        await fixture.Provider.PutAsync(Scope, "API_KEY", first, Owner);
        await fixture.Provider.RotateAsync(Scope, "API_KEY", second, Owner);
        Assert.Equal(second, (await Resolve(fixture.Provider)).Value);
        Assert.Equal(first, (await Resolve(fixture.Provider, 1)).Value);
        Assert.Equal(first, (await Resolve(fixture.Provider, 1)).Value);
        Assert.Equal(2, fixture.Server.ValueReads);
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(first, (await Resolve(fixture.Provider, 1)).Value);
        Assert.Equal(3, fixture.Server.ValueReads);
        await fixture.Provider.RevokeAsync(Scope, "API_KEY", 1, Owner);
        await Assert.ThrowsAsync<SecretRevokedException>(() => Resolve(fixture.Provider, 1));
        Assert.Equal(second, (await Resolve(fixture.Provider)).Value);
        Assert.DoesNotContain(first, JsonSerializer.Serialize(await fixture.Provider.ReadAuditAsync(null, 100)));
        Assert.DoesNotContain(second, JsonSerializer.Serialize(await fixture.Provider.ReadAuditAsync(null, 100)));
        Assert.All(fixture.Server.Authorization, header => Assert.Equal(_token, header));
        Assert.DoesNotContain(first, File.ReadAllText(fixture.SettingsPath));
    }

    [Theory]
    [InlineData("vault")]
    [InlineData("onepassword")]
    public async Task Bad_credential_and_network_errors_fail_closed_without_leaking_values(string kind)
    {
        var fixture = await Setup(kind, 0);
        var value = "value-" + Guid.NewGuid().ToString("N");
        await fixture.Provider.PutAsync(Scope, "API_KEY", value, Owner);
        fixture.Server.Deny = true;
        var denied = await Assert.ThrowsAsync<SecretAdapterException>(() => Resolve(fixture.Provider));
        Assert.Equal("adapter-auth", denied.Code);
        Assert.DoesNotContain(value, denied.Message);
        fixture.Server.Deny = false;
        fixture.Server.Unreachable = true;
        var unreachable = await Assert.ThrowsAsync<SecretAdapterException>(() => Resolve(fixture.Provider));
        Assert.Equal("adapter-unreachable", unreachable.Code);
        Assert.DoesNotContain(value, unreachable.Message);
        var audit = await fixture.Provider.ReadAuditAsync(null, 100);
        Assert.Contains(audit, entry => entry.Outcome == "failed:adapter-auth");
        Assert.Contains(audit, entry => entry.Outcome == "failed:adapter-unreachable");
        Assert.DoesNotContain(value, string.Join('\n', fixture.Logs));
    }

    [Fact]
    public async Task Scope_binding_is_exact_and_a_bad_adapter_does_not_hide_builtin_siblings()
    {
        var fixture = await Setup("vault", 0);
        var local = new SecretProvider(fixture.BuiltIn, fixture.Audit, new SecretValueScrubber(_clock), _clock,
            NullLogger<SecretProvider>.Instance);
        await local.PutAsync(SecretScope.ForProject("other"), "API_KEY", "local-" + Guid.NewGuid().ToString("N"), Owner);
        await local.PutAsync(SecretScope.Workspace, "API_KEY", "parent-" + Guid.NewGuid().ToString("N"), Owner);
        fixture.Server.Deny = true;
        var listing = await fixture.Router.ListAsync(default);
        Assert.Contains(listing.Records, item => item.Scope == "other");
        Assert.Contains(listing.Unreadable, item => item.Scope == Scope.Key && item.Problem == "adapter-unavailable");
        await Assert.ThrowsAsync<SecretAdapterException>(() => fixture.Router.FindAsync(Scope, "API_KEY", default));
        await Assert.ThrowsAsync<SecretAdapterException>(() => Resolve(fixture.Provider));
        Assert.NotNull(await fixture.Router.FindAsync(SecretScope.ForProject("other"), "API_KEY", default));
    }

    [Fact]
    public async Task Switching_a_scope_during_resolution_refuses_the_old_record()
    {
        var fixture = await Setup("vault", 0);
        await fixture.Provider.PutAsync(Scope, "API_KEY", "value-" + Guid.NewGuid().ToString("N"), Owner);
        var record = await fixture.Router.FindAsync(Scope, "API_KEY", default);
        Assert.NotNull(record);
        var settings = new SecretAdapterSettings(fixture.SettingsPath);
        await settings.RemoveAsync(Scope);
        var ex = await Assert.ThrowsAsync<SecretAdapterException>(() => fixture.Router.ReadValueForRecordAsync(record, 1, default));
        Assert.Equal("adapter-binding-changed", ex.Code);
    }

    [Fact]
    public async Task Vault_reports_a_remote_CAS_conflict_without_storing_a_local_version()
    {
        var fixture = await Setup("vault", 0);
        fixture.Server.SeedVaultVersion();
        var conflict = await Assert.ThrowsAsync<SecretAdapterException>(() =>
            fixture.Provider.PutAsync(Scope, "API_KEY", "value-" + Guid.NewGuid().ToString("N"), Owner));
        Assert.Equal("version-conflict", conflict.Code);
        Assert.Null(await fixture.Router.FindAsync(Scope, "API_KEY", default));
    }

    [Theory]
    [InlineData("vault")]
    [InlineData("onepassword")]
    public async Task Delete_cleans_up_versions_left_by_a_failed_replace(string kind)
    {
        var fixture = await Setup(kind, 0);
        await fixture.Provider.PutAsync(Scope, "API_KEY", "first-" + Guid.NewGuid().ToString("N"), Owner);
        fixture.Server.FailDelete = true;
        await Assert.ThrowsAsync<SecretAdapterException>(() =>
            fixture.Provider.PutAsync(Scope, "API_KEY", "second-" + Guid.NewGuid().ToString("N"), Owner));
        fixture.Server.FailDelete = false;
        await fixture.Provider.DeleteAsync(Scope, "API_KEY", Owner);
        Assert.Equal(0, fixture.Server.StoredVersionCount);
    }

    private async Task<(SecretProvider Provider, ScopedSecretStore Router, BuiltInSecretStore BuiltIn,
        SecretAuditLog Audit, FakeRemoteServer Server, string SettingsPath, List<string> Logs)> Setup(string kind, int ttl)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["Secrets:KeyFile"] = Path.Combine(_root, "keys", "key"),
        }).Build();
        var keys = new ConfiguredKeyEncryptionKeySource(config, _ => null, _ => { });
        var builtIn = new BuiltInSecretStore(config, keys);
        await builtIn.AppendVersionAsync(SecretScope.Workspace, "ADAPTER_TOKEN", Encoding.UTF8.GetBytes(_token),
            current => SecretVersionPolicy.Put(current, "", SecretScope.Workspace, "ADAPTER_TOKEN", Owner, _clock.GetUtcNow().UtcDateTime), default);
        var settingsPath = Path.Combine(_root, "workspace", ".security", "secrets", "adapter-bindings.json");
        var settings = new SecretAdapterSettings(settingsPath);
        await settings.SetAsync(new SecretAdapterBinding(Scope.Key, kind, "http://127.0.0.1:8765", "secret",
            "secret://workspace/ADAPTER_TOKEN", ttl));
        var server = new FakeRemoteServer(kind, _token);
        var router = new ScopedSecretStore(builtIn, settings, keys, new FakeHttpClientFactory(server), _clock, config);
        var audit = new SecretAuditLog(config);
        var logs = new List<string>();
        var provider = new SecretProvider(router, audit, new SecretValueScrubber(_clock), _clock, new RecordingLogger(logs));
        return (provider, router, builtIn, audit, server, settingsPath, logs);
    }

    private static Task<ResolvedSecret> Resolve(SecretProvider provider, int? version = null)
        => provider.GetForWorkloadAsync(new SecretReference(Scope, "API_KEY", version), Workload, "deploy:stage");

    private sealed class FakeHttpClientFactory(FakeRemoteServer server) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(server, disposeHandler: false);
    }

    private sealed class RecordingLogger(List<string> entries) : ILogger<SecretProvider>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => entries.Add(formatter(state, exception) + exception?.Message);
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class FakeRemoteServer(string kind, string token) : HttpMessageHandler
    {
        private readonly Dictionary<int, string> _vault = [];
        private readonly Dictionary<string, (string Title, string Value)> _items = [];
        public List<string?> Authorization { get; } = [];
        public int ValueReads { get; private set; }
        public bool Deny { get; set; }
        public bool Unreachable { get; set; }
        public bool FailDelete { get; set; }
        public int StoredVersionCount => kind == "vault" ? _vault.Count : _items.Count;
        private string? _lastValue;
        private int _version;

        public void SeedVaultVersion() => _vault[++_version] = "external-" + Guid.NewGuid().ToString("N");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Unreachable) throw new HttpRequestException("fake remote offline: " + _lastValue);
            var auth = kind == "vault" ? request.Headers.GetValues("X-Vault-Token").Single() : request.Headers.Authorization?.Parameter;
            Authorization.Add(auth);
            if (Deny || auth != token) return Reply(HttpStatusCode.Forbidden, new { message = _lastValue });
            var path = request.RequestUri!.AbsolutePath;
            if (kind == "vault")
            {
                if (path.EndsWith("/config", StringComparison.Ordinal)) return Reply(HttpStatusCode.OK, new { data = new { max_versions = 0 } });
                if (request.Method == HttpMethod.Post && path.Contains("/data/", StringComparison.Ordinal))
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    if (body.RootElement.GetProperty("options").GetProperty("cas").GetInt32() != _version)
                        return Reply(HttpStatusCode.BadRequest, new { });
                    _vault[++_version] = body.RootElement.GetProperty("data").GetProperty("value").GetString()!;
                    _lastValue = _vault[_version];
                    return Reply(HttpStatusCode.OK, new { data = new { version = _version } });
                }
                if (request.Method == HttpMethod.Get && path.Contains("/data/", StringComparison.Ordinal))
                {
                    var version = int.Parse(request.RequestUri.Query.TrimStart('?').Split('=')[1]);
                    if (!_vault.TryGetValue(version, out var value)) return Reply(HttpStatusCode.NotFound, new { });
                    ValueReads++;
                    return Reply(HttpStatusCode.OK, new { data = new { data = new { value }, metadata = new { version } } });
                }
                if (request.Method == HttpMethod.Put && path.Contains("/destroy/", StringComparison.Ordinal))
                {
                    if (FailDelete) return Reply(HttpStatusCode.InternalServerError, new { });
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    foreach (var version in body.RootElement.GetProperty("versions").EnumerateArray()) _vault.Remove(version.GetInt32());
                    return Reply(HttpStatusCode.NoContent, new { });
                }
                return Reply(HttpStatusCode.NoContent, new { });
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/v1/vaults/secret", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { id = "secret" });
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var id = Guid.NewGuid().ToString("N");
                _items[id] = (body.RootElement.GetProperty("title").GetString()!,
                    body.RootElement.GetProperty("fields")[0].GetProperty("value").GetString()!);
                _lastValue = _items[id].Value;
                return Reply(HttpStatusCode.OK, new { id });
            }
            var idPath = path.Split('/').Last();
            if (request.Method == HttpMethod.Get && request.RequestUri.Query.Length > 0)
            {
                var title = Uri.UnescapeDataString(request.RequestUri.Query).Split('"')[1];
                return Reply(HttpStatusCode.OK, _items.Where(item => item.Value.Title == title)
                    .Select(item => new { id = item.Key, title = item.Value.Title }).ToArray());
            }
            if (request.Method == HttpMethod.Get && _items.TryGetValue(idPath, out var item))
            {
                ValueReads++;
                return Reply(HttpStatusCode.OK, new { fields = new[] { new { label = "value", value = item.Value } } });
            }
            if (request.Method == HttpMethod.Delete)
            {
                if (FailDelete) return Reply(HttpStatusCode.InternalServerError, new { });
                _items.Remove(idPath);
                return Reply(HttpStatusCode.NoContent, new { });
            }
            return Reply(HttpStatusCode.NotFound, new { });
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, object body)
            => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
