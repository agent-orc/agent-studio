using System.Net;
using System.Text;
using System.Text.Json;
using AgentStudio.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ExternalSecretStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "external-secret-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly string _token = "token-" + Guid.NewGuid().ToString("N");
    private readonly BuiltInSecretStore _builtin;
    private readonly SecretStoreSettings _settings;
    private readonly SecretAuditLog _audit;
    private static readonly SecretActor Owner = SecretActor.Human("owner");

    public ExternalSecretStoreTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["Secrets:KeyFile"] = Path.Combine(_root, "keys", "secret.kek")
        }).Build();
        _builtin = new BuiltInSecretStore(config, new ConfiguredKeyEncryptionKeySource(config, _ => null, _ => { }));
        _settings = new SecretStoreSettings(config);
        _audit = new SecretAuditLog(config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<SecretAdapterCredentials> Credentials()
    {
        await _builtin.AppendVersionAsync(SecretScope.Workspace, "ADAPTER_TOKEN", Encoding.UTF8.GetBytes(_token),
            current => SecretVersionPolicy.Put(current, current?.Id ?? "", SecretScope.Workspace, "ADAPTER_TOKEN", Owner,
                _clock.GetUtcNow().UtcDateTime), default);
        return new SecretAdapterCredentials(_builtin);
    }

    private static string Value() => "value-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Vault_kv2_auth_versions_rotation_revocation_cache_ttl_and_audit()
    {
        using var server = new FakeServer("vault", _token, _clock);
        var credentials = await Credentials();
        var scope = SecretScope.ForProject("alpha");
        var assignment = new SecretStoreAssignment(scope.Key, "vault", "http://localhost", "secret", null,
            "secret://workspace/ADAPTER_TOKEN", 2);
        var store = new VaultKv2SecretStore(assignment, credentials, server.Client(), _clock);
        var provider = new SecretProvider(store, _audit, new SecretValueScrubber(_clock), _clock, NullLogger<SecretProvider>.Instance);
        await store.ProbeAsync(default);
        var first = Value();
        var second = Value();
        Assert.Equal(1, (await provider.PutAsync(scope, "API_KEY", first, Owner)).Versions.Max(x => x.Version));
        Assert.Equal(2, (await provider.RotateAsync(scope, "API_KEY", second, Owner)).Versions.Max(x => x.Version));
        var workload = new SecretWorkload(SecretWorkload.TaskRun, "task-1", "alpha", null);
        Assert.Equal(first, (await provider.GetForWorkloadAsync(SecretReference.Parse("secret://alpha/API_KEY@1"), workload, "task:build")).Value);
        Assert.Equal(second, (await provider.GetForWorkloadAsync(SecretReference.Parse("secret://alpha/API_KEY"), workload, "task:build")).Value);
        Assert.Equal(2, server.Reads);
        Assert.Equal("workload:task-1", Assert.Single((await provider.ListAsync(scope)).Records).LastUsedBy);
        Assert.Equal(second, Encoding.UTF8.GetString(await store.ReadValueAsync(scope, "API_KEY", 2, default)));
        Assert.Equal(2, server.Reads);
        _clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(second, Encoding.UTF8.GetString(await store.ReadValueAsync(scope, "API_KEY", 2, default)));
        Assert.Equal(3, server.Reads);
        await provider.RevokeAsync(scope, "API_KEY", 1, Owner);
        await Assert.ThrowsAsync<SecretRevokedException>(() => provider.GetForWorkloadAsync(
            SecretReference.Parse("secret://alpha/API_KEY@1"), workload, "task:build"));
        Assert.Contains(_audit.Read(scope, 20), x => x.Action == SecretAuditActions.Resolve && x.Version == 2 && x.Outcome == "ok");
        Assert.DoesNotContain(first, JsonSerializer.Serialize(_audit.Read(scope, 20)), StringComparison.Ordinal);
        Assert.DoesNotContain(second, JsonSerializer.Serialize(_audit.Read(scope, 20)), StringComparison.Ordinal);
        Assert.True(server.Authenticated);
    }

    [Fact]
    public async Task OnePassword_connect_maps_item_version_and_refuses_unsupported_history_revoke()
    {
        using var server = new FakeServer("onepassword", _token, _clock);
        var credentials = await Credentials();
        var scope = SecretScope.ForEnvironment("alpha", "stage");
        var assignment = new SecretStoreAssignment(scope.Key, "onepassword", "http://localhost", null, "vault-id",
            "secret://workspace/ADAPTER_TOKEN", 2);
        var store = new OnePasswordSecretStore(assignment, credentials, server.Client(), _clock);
        var provider = new SecretProvider(store, _audit, new SecretValueScrubber(_clock), _clock, NullLogger<SecretProvider>.Instance);
        await store.ProbeAsync(default);
        var first = Value();
        var second = Value();
        Assert.Equal(1, (await provider.PutAsync(scope, "API_KEY", first, Owner)).Versions.Max(x => x.Version));
        Assert.Equal(2, (await provider.RotateAsync(scope, "API_KEY", second, Owner)).Versions.Max(x => x.Version));
        var workload = new SecretWorkload(SecretWorkload.DeployJob, "deploy-1", "alpha", "stage", ["API_KEY"]);
        Assert.Equal(second, (await provider.GetForWorkloadAsync(SecretReference.Parse("secret://alpha/stage/API_KEY"), workload, "deploy:stage")).Value);
        await Assert.ThrowsAsync<SecretRevokedException>(() => provider.GetForWorkloadAsync(
            SecretReference.Parse("secret://alpha/stage/API_KEY@1"), workload, "deploy:stage"));
        var unsupported = await Assert.ThrowsAsync<SecretProviderUnsupportedException>(() => provider.RevokeAsync(scope, "API_KEY", 2, Owner));
        Assert.Equal("provider-operation-unsupported", unsupported.Code);
        Assert.Equal("workload:deploy-1", Assert.Single((await provider.ListAsync(scope)).Records).LastUsedBy);
        var reads = server.Reads;
        Assert.Equal(second, Encoding.UTF8.GetString(await store.ReadValueAsync(scope, "API_KEY", 2, default)));
        Assert.Equal(reads + 1, server.Reads);
        _clock.Advance(TimeSpan.FromSeconds(3));
        await store.ReadValueAsync(scope, "API_KEY", 2, default);
        Assert.Equal(reads + 3, server.Reads);
        Assert.True(server.Authenticated);
    }

    [Theory]
    [InlineData("vault")]
    [InlineData("onepassword")]
    public async Task Authentication_errors_hide_server_bodies_from_logs_and_audit(string kind)
    {
        using var server = new FakeServer(kind, Value(), _clock);
        var credentials = await Credentials();
        var scope = SecretScope.ForProject("alpha");
        var assignment = new SecretStoreAssignment(scope.Key, kind, "http://localhost",
            kind == "vault" ? "secret" : null, kind == "onepassword" ? "vault-id" : null,
            "secret://workspace/ADAPTER_TOKEN", 2);
        ISecretStore store = kind == "vault"
            ? new VaultKv2SecretStore(assignment, credentials, server.Client(), _clock)
            : new OnePasswordSecretStore(assignment, credentials, server.Client(), _clock);
        var logger = new CaptureLogger();
        var provider = new SecretProvider(store, _audit, new SecretValueScrubber(_clock), _clock, logger);
        var value = Value();
        await Assert.ThrowsAsync<SecretProviderUnavailableException>(() => provider.PutAsync(scope, "API_KEY", value, Owner));
        var emitted = string.Join("\n", logger.Messages) + JsonSerializer.Serialize(_audit.Read(scope, 20));
        Assert.DoesNotContain(server.ErrorValue, emitted, StringComparison.Ordinal);
        Assert.DoesNotContain(value, emitted, StringComparison.Ordinal);
        Assert.DoesNotContain(_token, emitted, StringComparison.Ordinal);
        Assert.Contains("failed:error", emitted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scope_assignment_fails_closed_without_affecting_builtin_scopes()
    {
        using var server = new FakeServer("vault", _token, _clock);
        var credentials = await Credentials();
        var bad = SecretScope.ForProject("alpha");
        var good = SecretScope.ForProject("beta");
        _settings.Set(new SecretStoreAssignment(bad.Key, "vault", "http://localhost", "secret", null,
            "env:UNSET_AGT3249_" + Guid.NewGuid().ToString("N"), 2));
        var routed = new RoutedSecretStore(_builtin, _settings, credentials, new ClientFactory(server.Client()), _clock);
        var provider = new SecretProvider(routed, _audit, new SecretValueScrubber(_clock), _clock, NullLogger<SecretProvider>.Instance);
        var value = Value();
        await provider.PutAsync(good, "API_KEY", value, Owner);
        await provider.PutAsync(SecretScope.Workspace, "API_KEY", Value(), Owner);
        await Assert.ThrowsAsync<SecretProviderUnavailableException>(() => provider.PutAsync(bad, "API_KEY", Value(), Owner));
        await Assert.ThrowsAsync<SecretProviderUnavailableException>(() => provider.GetForWorkloadAsync(
            SecretReference.Parse("secret://alpha/API_KEY"),
            new SecretWorkload(SecretWorkload.TaskRun, "task-1", "alpha", null), "task:build"));
        var listing = await provider.ListAsync();
        Assert.Contains(listing.Records, x => x.Scope == good.Key && x.Name == "API_KEY");
        Assert.Contains(listing.Unreadable, x => x.Scope == bad.Key);
        Assert.Contains(await routed.HealthAsync(default), x => x.Scope == bad.Key && x.State == "unavailable");
        Assert.Equal(value, (await provider.GetForWorkloadAsync(SecretReference.Parse("secret://beta/API_KEY"),
            new SecretWorkload(SecretWorkload.TaskRun, "task-2", "beta", null), "task:build")).Value);
        Assert.DoesNotContain(value, JsonSerializer.Serialize(_audit.Read(null, 30)), StringComparison.Ordinal);
    }

    private sealed class CaptureLogger : ILogger<SecretProvider>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception) + exception?.ToString());
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FakeServer : IDisposable
    {
        private readonly string _kind;
        private readonly string _token;
        private readonly FakeTimeProvider _clock;
        private readonly TestServer _server;
        private readonly Dictionary<string, List<Entry>> _vault = new();
        private readonly Dictionary<string, Entry> _items = new();
        public int Reads { get; private set; }
        public string ErrorValue { get; } = Value();
        public bool Authenticated { get; private set; }

        public FakeServer(string kind, string token, FakeTimeProvider clock)
        {
            _kind = kind;
            _token = token;
            _clock = clock;
            _server = new TestServer(new WebHostBuilder().Configure(app => app.Run(Handle)));
        }
        public HttpClient Client() => _server.CreateClient();
        public void Dispose() => _server.Dispose();

        private async Task Handle(HttpContext context)
        {
            var token = _kind == "vault" ? context.Request.Headers["X-Vault-Token"].ToString()
                : context.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.Ordinal);
            if (token != _token)
            {
                context.Response.StatusCode = 403;
                await Write(context, new { message = ErrorValue, credential = _token });
                return;
            }
            Authenticated = true;
            if (_kind == "vault") await Vault(context);
            else await OnePassword(context);
        }

        private async Task Vault(HttpContext ctx)
        {
            var path = ctx.Request.Path.Value ?? "";
            if (path == "/v1/auth/token/lookup-self") { await Write(ctx, new { data = new { id = "self" } }); return; }
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) { ctx.Response.StatusCode = 404; return; }
            var operation = parts[2];
            var key = string.Join('/', parts.Skip(3));
            if (ctx.Request.Method == "LIST")
            {
                var prefix = key + "/";
                var names = _vault.Keys.Where(x => x.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(x => x[prefix.Length..]).Where(x => !x.Contains('/')).ToArray();
                if (names.Length == 0) { ctx.Response.StatusCode = 404; return; }
                await Write(ctx, new { data = new { keys = names } }); return;
            }
            if (operation == "data" && ctx.Request.Method == "POST")
            {
                using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
                var cas = body.RootElement.GetProperty("options").GetProperty("cas").GetInt32();
                var entries = _vault.GetValueOrDefault(key) ?? [];
                if (cas != entries.Count) { ctx.Response.StatusCode = 400; return; }
                entries.Add(new Entry("id", key, entries.Count + 1,
                    body.RootElement.GetProperty("data").GetProperty("value").GetString()!, _clock.GetUtcNow().UtcDateTime));
                _vault[key] = entries;
                await Write(ctx, new { data = new { version = entries.Count } }); return;
            }
            if (!_vault.TryGetValue(key, out var versions)) { ctx.Response.StatusCode = 404; return; }
            if (operation == "metadata" && ctx.Request.Method == "GET")
            {
                await Write(ctx, new { data = new
                {
                    created_time = versions[0].At, updated_time = versions[^1].At,
                    versions = versions.ToDictionary(x => x.Version.ToString(), x => new
                    { created_time = x.At, deletion_time = x.Revoked ? x.At.ToString("O") : "", destroyed = false })
                } }); return;
            }
            if (operation == "data" && ctx.Request.Method == "GET")
            {
                var number = int.Parse(ctx.Request.Query["version"].ToString());
                var entry = versions.FirstOrDefault(x => x.Version == number);
                if (entry is null || entry.Revoked) { ctx.Response.StatusCode = 404; return; }
                Reads++;
                await Write(ctx, new { data = new { data = new { value = entry.Value }, metadata = new { version = entry.Version } } }); return;
            }
            if (operation == "delete" && ctx.Request.Method == "POST")
            {
                using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
                foreach (var number in body.RootElement.GetProperty("versions").EnumerateArray())
                    versions[number.GetInt32() - 1].Revoked = true;
                ctx.Response.StatusCode = 204; return;
            }
            if (operation == "metadata" && ctx.Request.Method == "DELETE")
            { _vault.Remove(key); ctx.Response.StatusCode = 204; return; }
            ctx.Response.StatusCode = 404;
        }

        private async Task OnePassword(HttpContext ctx)
        {
            var parts = (ctx.Request.Path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[0] == "v1" && parts[1] == "vaults")
            { await Write(ctx, new { id = parts[2] }); return; }
            if (parts.Length < 4 || parts[3] != "items") { ctx.Response.StatusCode = 404; return; }
            if (parts.Length == 4 && ctx.Request.Method == "GET")
            { await Write(ctx, _items.Values.Select(ToWire).ToArray()); return; }
            if (parts.Length == 4 && ctx.Request.Method == "POST")
            {
                using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
                var entry = new Entry(Guid.NewGuid().ToString("N"), body.RootElement.GetProperty("title").GetString()!, 1,
                    body.RootElement.GetProperty("fields")[0].GetProperty("value").GetString()!, _clock.GetUtcNow().UtcDateTime);
                _items[entry.Id] = entry;
                await Write(ctx, ToWire(entry)); return;
            }
            var id = parts.Length == 5 ? parts[4] : "";
            if (!_items.TryGetValue(id, out var found)) { ctx.Response.StatusCode = 404; return; }
            if (ctx.Request.Method == "GET") { Reads++; await Write(ctx, ToWire(found)); return; }
            if (ctx.Request.Method == "PUT")
            {
                using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
                found.Version++;
                found.Value = body.RootElement.GetProperty("fields")[0].GetProperty("value").GetString()!;
                found.At = _clock.GetUtcNow().UtcDateTime;
                await Write(ctx, ToWire(found)); return;
            }
            if (ctx.Request.Method == "DELETE") { _items.Remove(id); ctx.Response.StatusCode = 204; return; }
            ctx.Response.StatusCode = 405;
        }

        private static object ToWire(Entry item) => new
        {
            id = item.Id, title = item.Name, version = item.Version, category = "PASSWORD",
            tags = new[] { "agent-studio-secret" }, createdAt = item.At, updatedAt = item.At,
            fields = new[] { new { purpose = "PASSWORD", label = "password", value = item.Value } }
        };
        private static Task Write(HttpContext ctx, object value)
        {
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync(JsonSerializer.Serialize(value));
        }
        private sealed class Entry(string id, string name, int version, string value, DateTime at)
        {
            public string Id { get; } = id;
            public string Name { get; } = name;
            public int Version { get; set; } = version;
            public string Value { get; set; } = value;
            public DateTime At { get; set; } = at;
            public bool Revoked { get; set; }
        }
    }
}
