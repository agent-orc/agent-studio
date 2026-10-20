using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed class ExternalSecretStoreTests
{
    private static readonly SecretActor Owner = SecretActor.Human("owner");
    private static readonly SecretScope Scope = SecretScope.Workspace;
    private static string Value() => "value-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Vault_uses_token_native_versions_rotation_revocation_and_bounded_cache()
    {
        using var remote = new FakeRemote("vault");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
        var binding = Bind("vault", remote.Url, ttl: 5);
        var store = new VaultKvSecretStore(binding, remote.Client, (_, _) => Task.FromResult(remote.Token), clock);
        var first = Value();
        var second = Value();
        var provider = Provider(store, clock);

        await store.HealthAsync(default);
        var created = await provider.PutAsync(Scope, "API_KEY", first, Owner);
        Assert.Equal(1, Assert.Single(created.Versions).Version);
        var rotated = await provider.RotateAsync(Scope, "API_KEY", second, Owner);
        Assert.Equal(new[] { 1, 2 }, rotated.Versions.Select(item => item.Version));
        Assert.Equal(first, (await Resolve(provider, "secret://workspace/API_KEY@1")).Value);
        Assert.Equal(second, (await Resolve(provider, "secret://workspace/API_KEY")).Value);
        Assert.Equal(2, remote.ValueReads);
        Assert.Equal(second, (await Resolve(provider, "secret://workspace/API_KEY")).Value);
        Assert.Equal(2, remote.ValueReads);
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(second, (await Resolve(provider, "secret://workspace/API_KEY")).Value);
        Assert.Equal(3, remote.ValueReads);
        await provider.RevokeAsync(Scope, "API_KEY", 1, Owner);
        await Assert.ThrowsAsync<SecretRevokedException>(() => Resolve(provider, "secret://workspace/API_KEY@1"));
        Assert.Equal("vault", store.StoreId);
        Assert.Contains(remote.Requests, path => path.Contains("/delete/", StringComparison.Ordinal));
        await provider.DeleteAsync(Scope, "API_KEY", Owner);
        Assert.Null(await store.FindAsync(Scope, "API_KEY", default));
    }

    [Fact]
    public async Task Vault_rejects_an_unknown_mount_instead_of_reporting_an_empty_scope()
    {
        using var remote = new FakeRemote("vault");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
        var binding = Bind("vault", remote.Url) with { Mount = "missing" };
        var store = new VaultKvSecretStore(binding, remote.Client, (_, _) => Task.FromResult(remote.Token), clock);

        var health = await Assert.ThrowsAsync<SecretBackendException>(() => store.HealthAsync(default));
        Assert.Equal("backend-misconfigured", health.Code);
        var read = await Assert.ThrowsAsync<SecretBackendException>(() => store.FindAsync(Scope, "API_KEY", default));
        Assert.Equal("backend-misconfigured", read.Code);
        Assert.DoesNotContain(remote.Requests, path => path.Contains("/metadata/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnePassword_connect_auth_current_read_replace_and_clear_unsupported_errors()
    {
        using var remote = new FakeRemote("onepassword");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
        var store = new OnePasswordConnectSecretStore(Bind("onepassword", remote.Url, ttl: 4), remote.Client,
            (_, _) => Task.FromResult(remote.Token), clock);
        var provider = Provider(store, clock);
        var first = Value();
        var replacement = Value();

        await store.HealthAsync(default);
        await provider.PutAsync(Scope, "API_KEY", first, Owner);
        Assert.Equal(first, (await Resolve(provider, "secret://workspace/API_KEY")).Value);
        var persistedUse = await store.FindAsync(Scope, "API_KEY", default);
        Assert.Equal("workload:runner", persistedUse!.LastUsedBy);
        Assert.Equal("task:test", persistedUse.LastUsedPurpose);
        Assert.Equal(1, Assert.Single(persistedUse.Versions).Version);
        Assert.Contains(remote.Requests, request => request.StartsWith("PATCH ", StringComparison.Ordinal));
        var firstReads = remote.ValueReads;
        Assert.Equal(first, (await Resolve(provider, "secret://workspace/API_KEY")).Value);
        Assert.Equal(firstReads + 2, remote.ValueReads);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(first, (await Resolve(provider, "secret://workspace/API_KEY")).Value);
        Assert.Equal(firstReads + 5, remote.ValueReads);
        var replaced = await provider.PutAsync(Scope, "API_KEY", replacement, Owner);
        Assert.True(replaced.Versions.Max(item => item.Version) > 2);
        Assert.Equal(replacement, (await Resolve(provider, "secret://workspace/API_KEY")).Value);
        var rotate = await Assert.ThrowsAsync<SecretBackendException>(() => provider.RotateAsync(Scope, "API_KEY", Value(), Owner));
        Assert.Equal("backend-rotation-unsupported", rotate.Code);
        var revoke = await Assert.ThrowsAsync<SecretBackendException>(() => provider.RevokeAsync(Scope, "API_KEY", replaced.Versions.Max(item => item.Version), Owner));
        Assert.Equal("backend-revoke-unsupported", revoke.Code);
        Assert.Equal("onepassword", store.StoreId);
        await provider.DeleteAsync(Scope, "API_KEY", Owner);
        Assert.Null(await store.FindAsync(Scope, "API_KEY", default));
    }

    [Fact]
    public async Task OnePassword_metadata_update_failure_fails_the_resolution_closed()
    {
        using var remote = new FakeRemote("onepassword");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
        var store = new OnePasswordConnectSecretStore(Bind("onepassword", remote.Url), remote.Client,
            (_, _) => Task.FromResult(remote.Token), clock);
        var provider = Provider(store, clock);
        await provider.PutAsync(Scope, "API_KEY", Value(), Owner);
        remote.FailMetadataUpdates = true;

        var error = await Assert.ThrowsAsync<SecretBackendException>(() => Resolve(provider, "secret://workspace/API_KEY"));
        Assert.Equal("backend-unavailable", error.Code);
        Assert.Null((await store.FindAsync(Scope, "API_KEY", default))!.LastUsedAt);
    }

    [Theory]
    [InlineData("vault")]
    [InlineData("onepassword")]
    public async Task Bad_auth_and_remote_failure_are_sanitized_and_do_not_log_values(string kind)
    {
        using var remote = new FakeRemote(kind);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
        ISecretStore store = kind == "vault"
            ? new VaultKvSecretStore(Bind(kind, remote.Url), remote.Client, (_, _) => Task.FromResult("wrong"), clock)
            : new OnePasswordConnectSecretStore(Bind(kind, remote.Url), remote.Client, (_, _) => Task.FromResult("wrong"), clock);
        var root = Path.Combine(Path.GetTempPath(), "external-secret-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new CapturingLogger();
            var provider = new SecretProvider(store, new SecretAuditLog(root), new SecretValueScrubber(clock), clock,
                logger);
            var value = Value();
            var error = await Assert.ThrowsAsync<SecretBackendException>(() => provider.PutAsync(Scope, "API_KEY", value, Owner));
            Assert.Equal("backend-auth-failed", error.Code);
            var audit = File.ReadAllText(Directory.EnumerateFiles(root).Single());
            Assert.DoesNotContain(value, audit, StringComparison.Ordinal);
            Assert.Contains("backend-auth-failed", audit, StringComparison.Ordinal);
            Assert.DoesNotContain(value, string.Join("\n", logger.Messages), StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Scope_selection_is_exact_and_a_broken_adapter_does_not_fall_back()
    {
        using var remote = new FakeRemote("vault");
        var root = Path.Combine(Path.GetTempPath(), "external-secret-scope-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = Path.Combine(root, "workspace"),
                ["Secrets:KeyFile"] = Path.Combine(root, "keys", "secrets.kek")
            }).Build();
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
            var builtin = new BuiltInSecretStore(config, new ConfiguredKeyEncryptionKeySource(config, _ => null, _ => { }));
            var wrongBytes = Encoding.UTF8.GetBytes(Value());
            try
            {
                await builtin.AppendVersionAsync(Scope, "VAULT_TOKEN", wrongBytes,
                    current => SecretVersionPolicy.Put(current, current?.Id ?? "", Scope, "VAULT_TOKEN", Owner,
                        clock.GetUtcNow().UtcDateTime), default);
            }
            finally { Array.Clear(wrongBytes); }
            var settings = new SecretBackendSettings(Path.Combine(root, "workspace", ".security", "secrets", "backends.json"));
            await settings.SaveAsync(Bind("vault", remote.Url) with
            { Scope = "project-a", CredentialReference = "secret://workspace/VAULT_TOKEN", CredentialEnvironmentVariable = null }, default);
            var http = new SingleClientFactory(remote.Client);
            var scoped = new ScopedSecretStore(builtin, settings, http, clock, new SecretAuditLog(Path.Combine(root, "audit")));
            var provider = Provider(scoped, clock);
            await provider.PutAsync(Scope, "API_KEY", Value(), Owner);
            var project = SecretScope.ForProject("project-a");
            var failure = await Assert.ThrowsAsync<SecretBackendException>(() => provider.PutAsync(project, "API_KEY", Value(), Owner));
            Assert.Equal("backend-auth-failed", failure.Code);
            Assert.NotNull(await builtin.FindAsync(Scope, "API_KEY", default));
            Assert.Null(await builtin.FindAsync(project, "API_KEY", default));
            Assert.Equal("unavailable", Assert.Single(await scoped.HealthAsync(default)).State);
            var listing = await provider.ListAsync();
            Assert.Equal(2, listing.Records.Count);
            Assert.All(listing.Records, record => Assert.Equal(Scope.Key, record.Scope));
            Assert.Single(listing.Unreadable);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Scoped_adapter_uses_a_builtin_workspace_credential_without_exposing_it()
    {
        using var remote = new FakeRemote("vault");
        var root = Path.Combine(Path.GetTempPath(), "external-secret-credential-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = Path.Combine(root, "workspace"),
                ["Secrets:KeyFile"] = Path.Combine(root, "keys", "secrets.kek")
            }).Build();
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
            var builtin = new BuiltInSecretStore(config, new ConfiguredKeyEncryptionKeySource(config, _ => null, _ => { }));
            var tokenBytes = Encoding.UTF8.GetBytes(remote.Token);
            try
            {
                await builtin.AppendVersionAsync(Scope, "VAULT_TOKEN", tokenBytes,
                    current => SecretVersionPolicy.Put(current, current?.Id ?? "", Scope, "VAULT_TOKEN", Owner,
                        clock.GetUtcNow().UtcDateTime), default);
            }
            finally { Array.Clear(tokenBytes); }
            var settings = new SecretBackendSettings(Path.Combine(root, "workspace", ".security", "secrets", "backends.json"));
            var project = SecretScope.ForProject("project-a");
            await settings.SaveAsync(Bind("vault", remote.Url) with
            { Scope = project.Key, CredentialReference = "secret://workspace/VAULT_TOKEN", CredentialEnvironmentVariable = null }, default);
            var scoped = new ScopedSecretStore(builtin, settings, new SingleClientFactory(remote.Client), clock,
                new SecretAuditLog(Path.Combine(root, "audit")));
            var provider = Provider(scoped, clock);
            var value = Value();
            await provider.PutAsync(project, "API_KEY", value, Owner);
            Assert.Equal(value, (await Resolve(provider, "secret://project-a/API_KEY")).Value);
            Assert.DoesNotContain(remote.Token, JsonSerializer.Serialize(await scoped.ListAsync(default)), StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static SecretBackendBinding Bind(string kind, string url, int ttl = 30) =>
        new("workspace", kind, url, kind == "vault" ? "secret" : null, kind == "onepassword" ? "vault-id" : null,
            null, "BACKEND_TOKEN", ttl);

    private static SecretProvider Provider(ISecretStore store, TimeProvider clock) =>
        new(store, new SecretAuditLog(Path.Combine(Path.GetTempPath(), "external-secret-" + Guid.NewGuid().ToString("N"))),
            new SecretValueScrubber(clock), clock, NullLogger<SecretProvider>.Instance);

    private static Task<ResolvedSecret> Resolve(SecretProvider provider, string reference) =>
        provider.GetForWorkloadAsync(SecretReference.Parse(reference),
            new SecretWorkload(SecretWorkload.TaskRun, "runner", "project-a", null), "task:test");

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingLogger : ILogger<SecretProvider>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class FakeRemote : IDisposable
    {
        private readonly TestServer _server;
        private readonly Dictionary<int, string> _values = [];
        private readonly HashSet<int> _deleted = [];
        private string? _record;
        private JsonElement? _item;
        private int _itemVersion;
        public string Token { get; } = "token-" + Guid.NewGuid().ToString("N");
        public HttpClient Client { get; }
        public string Url => Client.BaseAddress!.ToString();
        public int ValueReads { get; private set; }
        public bool FailMetadataUpdates { get; set; }
        public List<string> Requests { get; } = [];

        public FakeRemote(string kind)
        {
            _server = new TestServer(new WebHostBuilder().Configure(app => app.Run(context => Handle(context, kind))));
            Client = _server.CreateClient();
        }

        private async Task Handle(HttpContext context, string kind)
        {
            var path = context.Request.Path.Value ?? "";
            Requests.Add(context.Request.Method + " " + path);
            var authenticated = kind == "vault"
                ? context.Request.Headers["X-Vault-Token"] == Token
                : context.Request.Headers.Authorization == "Bearer " + Token;
            if (!authenticated) { context.Response.StatusCode = 403; return; }
            if (kind == "vault") await Vault(context, path);
            else await OnePassword(context, path);
        }

        private async Task Vault(HttpContext context, string path)
        {
            if (context.Request.Method == "GET" && path.EndsWith("/config", StringComparison.Ordinal))
            {
                if (path != "/v1/secret/config") { context.Response.StatusCode = 404; return; }
                await context.Response.WriteAsJsonAsync(new { data = new { max_versions = 0, cas_required = false } });
                return;
            }
            if (context.Request.Method == "LIST")
            {
                if (_record is null) { context.Response.StatusCode = 404; return; }
                await context.Response.WriteAsJsonAsync(new { data = new { keys = new[] { "API_KEY", "__studio_metadata__/" } } });
                return;
            }
            if (path.Contains("/metadata/", StringComparison.Ordinal))
            {
                if (context.Request.Method == "GET")
                {
                    if (_record is null) { context.Response.StatusCode = 404; return; }
                    var versions = _values.Keys.ToDictionary(version => version.ToString(),
                        version => new { destroyed = false, deletion_time = _deleted.Contains(version) ? "2026-10-20T09:00:00Z" : "" });
                    await context.Response.WriteAsJsonAsync(new { data = new { versions } });
                    return;
                }
                if (context.Request.Method == "DELETE") { _record = null; _values.Clear(); context.Response.StatusCode = 204; return; }
            }
            if (path.Contains("/data/", StringComparison.Ordinal))
            {
                if (path.Contains("/__studio_metadata__/", StringComparison.Ordinal))
                {
                    if (context.Request.Method == "GET")
                    {
                        if (_record is null) { context.Response.StatusCode = 404; return; }
                        await context.Response.WriteAsJsonAsync(new { data = new { data = new { record = _record } } });
                        return;
                    }
                    if (context.Request.Method == "POST")
                    {
                        using var document = await JsonDocument.ParseAsync(context.Request.Body);
                        _record = document.RootElement.GetProperty("data").GetProperty("record").GetString();
                        await context.Response.WriteAsJsonAsync(new { data = new { version = 1 } });
                        return;
                    }
                }
                if (context.Request.Method == "POST")
                {
                    using var document = await JsonDocument.ParseAsync(context.Request.Body);
                    var cas = document.RootElement.GetProperty("options").GetProperty("cas").GetInt32();
                    if (cas != _values.Count) { context.Response.StatusCode = 400; return; }
                    var version = _values.Count + 1;
                    _values[version] = document.RootElement.GetProperty("data").GetProperty("value").GetString()!;
                    await context.Response.WriteAsJsonAsync(new { data = new { version } });
                    return;
                }
                if (context.Request.Method == "GET")
                {
                    var version = int.Parse(context.Request.Query["version"]!);
                    if (!_values.TryGetValue(version, out var value) || _deleted.Contains(version))
                    { context.Response.StatusCode = 404; return; }
                    ValueReads++;
                    await context.Response.WriteAsJsonAsync(new { data = new { data = new { value } } });
                    return;
                }
            }
            if (path.Contains("/delete/", StringComparison.Ordinal))
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body);
                foreach (var version in document.RootElement.GetProperty("versions").EnumerateArray()) _deleted.Add(version.GetInt32());
                context.Response.StatusCode = 204;
                return;
            }
            context.Response.StatusCode = 404;
        }

        private async Task OnePassword(HttpContext context, string path)
        {
            if (context.Request.Method == "GET" && path.EndsWith("/items", StringComparison.Ordinal))
            {
                var list = _item is null ? Array.Empty<object>() : new object[] { new { id = "item-id", title =
                    _item.Value.GetProperty("title").GetString() } };
                await context.Response.WriteAsJsonAsync(list);
                return;
            }
            if (context.Request.Method == "GET")
            {
                if (_item is null) { context.Response.StatusCode = 404; return; }
                ValueReads++;
                await context.Response.WriteAsJsonAsync(_item.Value);
                return;
            }
            if (context.Request.Method is "POST" or "PUT")
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body);
                var node = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
                node["version"] = ++_itemVersion;
                _item = JsonSerializer.SerializeToElement(node);
                await context.Response.WriteAsJsonAsync(_item.Value);
                return;
            }
            if (context.Request.Method == "PATCH")
            {
                if (FailMetadataUpdates) { context.Response.StatusCode = 503; return; }
                if (_item is null) { context.Response.StatusCode = 404; return; }
                using var document = await JsonDocument.ParseAsync(context.Request.Body);
                var operation = document.RootElement.EnumerateArray().Single();
                if (operation.GetProperty("op").GetString() != "replace"
                    || operation.GetProperty("path").GetString() != "/notesPlain")
                { context.Response.StatusCode = 400; return; }
                var node = JsonNode.Parse(_item.Value.GetRawText())!.AsObject();
                node["notesPlain"] = operation.GetProperty("value").GetString();
                node["version"] = ++_itemVersion;
                _item = JsonSerializer.SerializeToElement(node);
                await context.Response.WriteAsJsonAsync(_item.Value);
                return;
            }
            if (context.Request.Method == "DELETE") { _item = null; context.Response.StatusCode = 204; return; }
            context.Response.StatusCode = 404;
        }

        public void Dispose() { Client.Dispose(); _server.Dispose(); }
    }
}
