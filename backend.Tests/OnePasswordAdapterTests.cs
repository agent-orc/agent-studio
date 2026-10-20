using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OnePasswordAdapterTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "op-adapter-" + Guid.NewGuid().ToString("N"));
    private readonly string _tokenVariable = "AGT_TEST_OP_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
    private readonly string _token = "test-op-" + Guid.NewGuid().ToString("N");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, JsonObject> _items = [];
    private WebApplication _server = null!;
    private bool _deny;
    private int _gets;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _server = builder.Build();
        _server.MapPost("/op", Handle);
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Service_account_auth_current_version_rotation_ttl_and_unsupported_history()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["Secrets:KeyFile"] = Path.Combine(_root, "keys", "secrets.kek")
        }).Build();
        var settings = new SecretAdapterConfig("1password", null, null, "Team", _tokenVariable, 30);
        var backend = new OnePasswordBackend(settings, new FakeOpCommands(new HttpClient(), _server.Urls.Single()), _ => _token);
        await backend.ProbeAsync(default);
        var keys = new ConfiguredKeyEncryptionKeySource(configuration, _ => null, _ => { });
        var store = new ExternalSecretStore(new BuiltInSecretStore(Path.Combine(_root, "state"), keys), backend, _clock,
            TimeSpan.FromSeconds(30));
        var audit = new SecretAuditLog(configuration);
        var provider = new SecretProvider(store, audit, new SecretValueScrubber(_clock), _clock,
            NullLogger<SecretProvider>.Instance);
        var owner = SecretActor.Human("owner");
        var scope = SecretScope.ForProject("demo");
        var first = "first-" + Guid.NewGuid().ToString("N");
        var second = "second-" + Guid.NewGuid().ToString("N");
        await provider.PutAsync(scope, "TOKEN", first, owner);
        await provider.RotateAsync(scope, "TOKEN", second, owner);
        var workload = new SecretWorkload(SecretWorkload.TaskRun, "run", "demo", null);
        Assert.Equal(second, (await provider.GetForWorkloadAsync(new SecretReference(scope, "TOKEN"), workload, "test")).Value);
        var reads = _gets;
        await provider.GetForWorkloadAsync(new SecretReference(scope, "TOKEN"), workload, "test");
        Assert.Equal(reads, _gets);
        _clock.Advance(TimeSpan.FromSeconds(31));
        await provider.GetForWorkloadAsync(new SecretReference(scope, "TOKEN"), workload, "test");
        Assert.Equal(reads + 1, _gets);
        var old = await Assert.ThrowsAsync<SecretAdapterException>(() => provider.GetForWorkloadAsync(
            new SecretReference(scope, "TOKEN", 1), workload, "test"));
        Assert.Equal("adapter-version-unsupported", old.Code);
        var revoke = await Assert.ThrowsAsync<SecretAdapterException>(() => provider.RevokeAsync(scope, "TOKEN", 2, owner));
        Assert.Equal("adapter-revoke-unsupported", revoke.Code);
        var entries = JsonSerializer.Serialize(await provider.ReadAuditAsync(null, 50));
        Assert.DoesNotContain(first, entries);
        Assert.DoesNotContain(second, entries);
    }

    [Fact]
    public async Task Service_account_auth_failure_is_redacted()
    {
        var backend = new OnePasswordBackend(new("1password", null, null, "Team", _tokenVariable),
            new FakeOpCommands(new HttpClient(), _server.Urls.Single()), _ => _token);
        _deny = true;
        var error = await Assert.ThrowsAsync<SecretAdapterException>(() => backend.ProbeAsync(default));
        Assert.Equal("adapter-auth-failed", error.Code);
        Assert.DoesNotContain(_token, error.Message);
    }

    private async Task Handle(HttpContext context)
    {
        if (_deny || context.Request.Headers["X-Test-Token"] != _token)
        { context.Response.StatusCode = 403; return; }
        using var body = await JsonDocument.ParseAsync(context.Request.Body);
        var args = body.RootElement.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()).ToArray();
        var input = body.RootElement.GetProperty("input");
        if (args is ["vault", "get", ..])
        { await context.Response.WriteAsJsonAsync(new { id = "team" }); return; }
        if (args is ["item", "create", ..])
        {
            var item = JsonNode.Parse(input.GetString()!)!.AsObject();
            item["version"] = 1;
            _items[item["title"]!.GetValue<string>()] = item;
            await context.Response.WriteAsJsonAsync(item);
            return;
        }
        if (args is ["item", "get", var getTitle, ..])
        {
            _gets++;
            if (!_items.TryGetValue(getTitle!, out var item)) { context.Response.StatusCode = 404; return; }
            await context.Response.WriteAsJsonAsync(item);
            return;
        }
        if (args is ["item", "edit", var editTitle, ..])
        {
            if (!_items.ContainsKey(editTitle!)) { context.Response.StatusCode = 404; return; }
            var item = JsonNode.Parse(input.GetString()!)!.AsObject();
            item["version"] = item["version"]!.GetValue<int>() + 1;
            _items[editTitle!] = item;
            await context.Response.WriteAsJsonAsync(item);
            return;
        }
        if (args is ["item", "delete", var deleteTitle, ..])
        { _items.Remove(deleteTitle!); await context.Response.WriteAsJsonAsync(new { }); return; }
        context.Response.StatusCode = 400;
    }

    private sealed class FakeOpCommands(HttpClient client, string address) : IOnePasswordCommandClient
    {
        public async Task<string> RunAsync(IReadOnlyList<string> arguments, string? input, string token, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, address + "/op")
            { Content = JsonContent.Create(new { arguments, input }) };
            request.Headers.TryAddWithoutValidation("X-Test-Token", token);
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                throw new SecretAdapterException("adapter-auth-failed", "The service account was rejected.");
            if (!response.IsSuccessStatusCode)
                throw new SecretAdapterException("adapter-cli-failed", "The 1Password operation failed.");
            return await response.Content.ReadAsStringAsync(ct);
        }
    }
}
