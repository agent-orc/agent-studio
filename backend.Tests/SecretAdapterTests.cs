using System.Net;
using System.Text;
using System.Text.Json;
using AgentStudio.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class SecretAdapterTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "secret-adapters-" + Guid.NewGuid().ToString("N"));
    private readonly string _tokenVariable = "AGT_TEST_SECRET_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
    private readonly string _token = "test-token-" + Guid.NewGuid().ToString("N");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
    private WebApplication _server = null!;
    private readonly Dictionary<string, List<string?>> _vault = [];
    private int _reads;
    private bool _deny;
    private string Address => _server.Urls.Single();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _server = builder.Build();
        _server.Map("/v1/{**path}", VaultRequest);
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Vault_uses_auth_native_versions_rotation_and_ttl()
    {
        var scope = SecretScope.ForProject("demo");
        var (provider, audit) = Provider(new VaultKv2Backend(VaultConfig(), new HttpClient(), _ => _token), "vault");
        var owner = SecretActor.Human("owner");
        var first = "first-" + Guid.NewGuid().ToString("N");
        var second = "second-" + Guid.NewGuid().ToString("N");
        Assert.Equal(1, (await provider.PutAsync(scope, "TOKEN", first, owner)).Versions.Last().Version);
        Assert.Equal(2, (await provider.RotateAsync(scope, "TOKEN", second, owner)).Versions.Last().Version);
        var workload = new SecretWorkload(SecretWorkload.TaskRun, "run", "demo", null);
        var pinned = new SecretReference(scope, "TOKEN", 1);
        Assert.Equal(first, (await provider.GetForWorkloadAsync(pinned, workload, "test", default)).Value);
        Assert.Equal(second, (await provider.GetForWorkloadAsync(new SecretReference(scope, "TOKEN"), workload, "test", default)).Value);
        var reads = _reads;
        await provider.GetForWorkloadAsync(pinned, workload, "test", default);
        Assert.Equal(reads, _reads);
        _clock.Advance(TimeSpan.FromSeconds(31));
        await provider.GetForWorkloadAsync(pinned, workload, "test", default);
        Assert.Equal(reads + 1, _reads);
        await provider.RevokeAsync(scope, "TOKEN", 1, owner);
        await Assert.ThrowsAsync<SecretRevokedException>(() => provider.GetForWorkloadAsync(pinned, workload, "test", default));
        var entries = await provider.ReadAuditAsync(null, 30);
        Assert.Contains(entries, item => item.Action == SecretAuditActions.Rotate && item.Version == 2);
        var json = JsonSerializer.Serialize(entries);
        Assert.DoesNotContain(first, json);
        Assert.DoesNotContain(second, json);
    }

    [Fact]
    public async Task Vault_auth_failure_is_clear_and_does_not_log_values()
    {
        var (provider, audit) = Provider(new VaultKv2Backend(VaultConfig(), new HttpClient(), _ => _token), "auth");
        _deny = true;
        var value = "value-" + Guid.NewGuid().ToString("N");
        var error = await Assert.ThrowsAsync<SecretAdapterException>(() => provider.PutAsync(
            SecretScope.Workspace, "TOKEN", value, SecretActor.Human("owner")));
        Assert.Equal("adapter-auth-failed", error.Code);
        Assert.DoesNotContain(value, JsonSerializer.Serialize(audit.Read(null, 10)));
        Assert.DoesNotContain(value, error.Message);
    }

    [Fact]
    public async Task Scope_selection_is_exact_and_bad_adapter_does_not_affect_other_scopes()
    {
        var configuration = Configuration();
        var keys = new ConfiguredKeyEncryptionKeySource(configuration, _ => null, _ => { });
        var builtIn = new BuiltInSecretStore(configuration, keys);
        var settings = new SecretAdapterSettings(Path.Combine(_root, "adapters.json"));
        var project = SecretScope.ForProject("demo");
        settings.Set(project, VaultConfig());
        var store = new ScopedSecretStore(builtIn, settings, keys, configuration,
            new ClientFactory(), _clock, _ => _token);
        var audit = new SecretAuditLog(configuration);
        var provider = new SecretProvider(store, audit, new SecretValueScrubber(_clock), _clock, NullLogger<SecretProvider>.Instance);
        var actor = SecretActor.Human("owner");
        await provider.PutAsync(SecretScope.Workspace, "SHARED", "workspace-value", actor);
        await provider.PutAsync(project, "PRIVATE", "project-value", actor);
        _deny = true;
        Assert.False((await store.StatusAsync(project, default)).Healthy);
        Assert.True((await store.StatusAsync(SecretScope.Workspace, default)).Healthy);
        var workload = new SecretWorkload(SecretWorkload.TaskRun, "run", "demo", null);
        var workspaceValue = await provider.GetForWorkloadAsync(new SecretReference(SecretScope.Workspace, "SHARED"), workload, "test");
        Assert.Equal("workspace-value", workspaceValue.Value);
        await Assert.ThrowsAsync<SecretAdapterException>(() => provider.GetForWorkloadAsync(
            new SecretReference(project, "SHARED"), workload, "test"));
        await Assert.ThrowsAsync<SecretAdapterException>(() => provider.GetForWorkloadAsync(
            new SecretReference(project, "PRIVATE"), workload, "test"));
    }

    private (SecretProvider Provider, SecretAuditLog Audit) Provider(IExternalSecretBackend backend, string suffix)
    {
        var configuration = Configuration();
        var keys = new ConfiguredKeyEncryptionKeySource(configuration, _ => null, _ => { });
        var metadata = new BuiltInSecretStore(Path.Combine(_root, suffix), keys);
        var store = new ExternalSecretStore(metadata, backend, _clock, TimeSpan.FromSeconds(30));
        var audit = new SecretAuditLog(configuration);
        return (new SecretProvider(store, audit, new SecretValueScrubber(_clock), _clock,
            NullLogger<SecretProvider>.Instance), audit);
    }

    private IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["TaskRepository"] = Path.Combine(_root, "workspace"),
        ["Secrets:KeyFile"] = Path.Combine(_root, "keys", "secrets.kek")
    }).Build();

    private SecretAdapterConfig VaultConfig() => new("vault", Address, "secret", null, _tokenVariable, 30);

    private async Task VaultRequest(HttpContext context)
    {
        if (_deny || context.Request.Headers["X-Vault-Token"] != _token)
        { context.Response.StatusCode = 403; return; }
        var path = context.Request.RouteValues["path"]?.ToString() ?? "";
        if (path == "auth/token/lookup-self") { await context.Response.WriteAsJsonAsync(new { data = new { id = "test" } }); return; }
        var slash = path.IndexOf('/', "secret/".Length);
        var action = slash < 0 ? "" : path["secret/".Length..slash];
        var key = slash < 0 ? "" : path[(slash + 1)..];
        if (action == "data" && context.Request.Method == "POST")
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            var cas = body.RootElement.GetProperty("options").GetProperty("cas").GetInt32();
            if (!_vault.TryGetValue(key, out var versions)) _vault[key] = versions = [];
            if (cas != versions.Count) { context.Response.StatusCode = 400; return; }
            versions.Add(body.RootElement.GetProperty("data").GetProperty("value").GetString());
            await context.Response.WriteAsJsonAsync(new { data = new { version = versions.Count } });
            return;
        }
        if (action == "data" && context.Request.Method == "GET")
        {
            _reads++;
            var version = int.Parse(context.Request.Query["version"]!);
            if (!_vault.TryGetValue(key, out var versions) || version > versions.Count || versions[version - 1] is null)
            { context.Response.StatusCode = 404; return; }
            await context.Response.WriteAsJsonAsync(new { data = new { data = new { value = versions[version - 1] }, metadata = new { version } } });
            return;
        }
        if (action == "destroy")
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            foreach (var version in body.RootElement.GetProperty("versions").EnumerateArray())
                _vault[key][version.GetInt32() - 1] = null;
            context.Response.StatusCode = 204;
            return;
        }
        if (action == "metadata" && context.Request.Method == "DELETE")
        { _vault.Remove(key); context.Response.StatusCode = 204; return; }
        context.Response.StatusCode = 404;
    }

    private sealed class ClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
