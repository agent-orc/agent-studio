using AgentStudio.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelRoutingPolicyStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agent-studio-model-routing-state-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Auto_migration_switch_defaults_on_and_preserves_economy_mode()
    {
        var store = CreateStore();

        Assert.True(store.AutoModelMigrationsEnabled);
        store.SetEconomyMode(true);
        store.SetAutoModelMigrations(false);

        Assert.True(store.EconomyMode);
        Assert.False(store.AutoModelMigrationsEnabled);

        var reloaded = CreateStore();
        Assert.True(reloaded.EconomyMode);
        Assert.False(reloaded.AutoModelMigrationsEnabled);
    }

    [Fact]
    public void Economy_mode_update_preserves_auto_migration_switch()
    {
        var store = CreateStore();
        store.SetAutoModelMigrations(false);

        store.SetEconomyMode(true);

        Assert.True(store.EconomyMode);
        Assert.False(store.AutoModelMigrationsEnabled);
    }

    private ModelRoutingPolicyStateStore CreateStore()
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
            })
            .Build();
        return new ModelRoutingPolicyStateStore(
            configuration,
            NullLogger<ModelRoutingPolicyStateStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
