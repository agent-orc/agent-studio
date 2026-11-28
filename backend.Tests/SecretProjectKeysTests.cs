using AgentStudio.Registry;
using AgentStudio.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3987: a project's secret scopes are keyed by its registry id. The
/// display name stays an alias, so references written before keep resolving.
/// </summary>
public sealed class SecretProjectKeysTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "secret-project-keys-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Id_scope_reads_each_level_under_the_id_then_the_current_display_name()
    {
        var (registry, id) = Registry("Night Shop");
        var keys = new SecretProjectKeys(registry);

        Assert.Equal(id, keys.Canonical("Night Shop"));
        Assert.Equal(id, keys.Canonical(id));
        Assert.Equal("Night Shop", keys.DisplayName(id));
        Assert.Equal(SecretScope.ForEnvironment(id, "prod"), keys.Canonical(SecretScope.ForEnvironment("night shop", "prod")));
        Assert.Equal(
            [$"{id}/prod", "Night Shop/prod", id, "Night Shop", "workspace"],
            keys.LookupChain(SecretScope.ForEnvironment(id, "prod")).Select(scope => scope.Key));
        Assert.True(keys.Owns("Night Shop", id));
        Assert.True(keys.Owns(id, "night shop"));
        Assert.False(keys.Owns("Night Shop", "other"));
    }

    [Fact]
    public void After_a_rename_the_id_keeps_its_scope_and_the_new_name_is_the_alias()
    {
        var (registry, id) = Registry("demo");
        registry.Rename(id, "shop");
        var keys = new SecretProjectKeys(registry);

        Assert.Equal(id, keys.Canonical("shop"));
        Assert.Equal([id, "shop", "demo"], keys.Keys("shop"));
        Assert.Equal(id, keys.Canonical("demo"));
    }

    [Fact]
    public void Multiple_renames_and_registry_updates_retain_persisted_secret_aliases()
    {
        var (registry, id) = Registry("original");
        registry.Rename(id, "middle");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["TaskRepository"] = _root }).Build();
        registry.Update(id, new AgentStudio.Registry.UpdateProjectRequest { DisplayName = "current" },
            new AgentStudio.Registry.WorkspaceRegistry(config, NullLogger<AgentStudio.Registry.WorkspaceRegistry>.Instance));
        registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance, clock: _clock);
        Assert.Equal([id, "current", "original", "middle"], new SecretProjectKeys(registry).Keys(id));
    }

    [Fact]
    public void A_reused_name_cannot_expose_legacy_scopes_to_either_project()
    {
        var (registry, id) = Registry("original");
        registry.Rename(id, "current");
        var other = registry.EnsureProjectForStorage(Path.Combine(_root, "projects", "other"), "original", "ws-default", _clock);
        var keys = new SecretProjectKeys(registry);
        Assert.Equal([id, "current"], keys.Keys(id));
        Assert.Equal([other.Id], keys.Keys(other.Id));
        Assert.False(keys.Owns(id, "original"));
        Assert.False(keys.Owns(other.Id, "original"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["TaskRepository"] = _root }).Build();
        var directory = Path.Combine(_root, ".security", "secret-bindings");
        Directory.CreateDirectory(directory);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("original\nprod"))).ToLowerInvariant();
        var legacy = Path.Combine(directory, hash + ".json");
        File.WriteAllText(legacy, System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new ProjectSecretBinding("API_KEY", "secret://original/prod/API_KEY", "human:owner",
                _clock.GetUtcNow().UtcDateTime, "owner"),
        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        var bindings = new ProjectSecretBindings(config, null!, null!, _clock, keys);
        Assert.Empty(bindings.List("original", "prod"));
        Assert.Empty(bindings.List("current", "prod"));
        Assert.True(File.Exists(legacy));
    }

    [Fact]
    public void A_project_the_registry_does_not_know_keeps_its_route_name()
    {
        var keys = SecretProjectKeys.Identity;

        Assert.Equal("memowikis", keys.Canonical("memowikis"));
        Assert.Equal(["memowikis/prod", "memowikis", "workspace"],
            keys.LookupChain(SecretScope.ForEnvironment("memowikis", "prod")).Select(scope => scope.Key));
    }

    private (ProjectRegistry Registry, string Id) Registry(string displayName)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
        }).Build();
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance, clock: _clock);
        var record = registry.EnsureProjectForStorage(Path.Combine(_root, "projects", "p"), displayName, "ws-default", _clock);
        return (registry, record.Id);
    }
}
