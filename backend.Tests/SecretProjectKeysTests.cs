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
        Assert.Equal([id, "shop"], keys.Keys("shop"));
        Assert.Equal("demo", keys.Canonical("demo"));
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
