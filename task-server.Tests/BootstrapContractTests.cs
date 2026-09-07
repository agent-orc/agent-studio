using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TaskServer.Tests;

public sealed class BootstrapContractTests
{
    [Fact]
    public void Server_env_keys_override_legacy_settings_and_resolve_external_paths()
    {
        using var store = new TempDirectory();
        using var backups = new TempDirectory();
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["LISTEN_URL"] = "http://127.0.0.1:6123",
            ["STORE_PATH"] = store.Path,
            ["BACKUP_PATH"] = backups.Path,
            ["AUTH"] = "none",
            ["TaskServer:DataDirectory"] = "ignored",
        });

        var options = TaskServerBootstrapOptions.Load(configuration);

        Assert.Equal("http://127.0.0.1:6123", options.ListenUrl);
        Assert.Equal(Path.GetFullPath(store.Path), options.StorePath);
        Assert.Equal(Path.GetFullPath(backups.Path), options.BackupPath);
        Assert.False(options.RequiresAuthentication);
    }

    [Fact]
    public void Public_listener_without_authentication_fails_closed()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["LISTEN_URL"] = "http://0.0.0.0:5071",
            ["STORE_PATH"] = Path.GetTempPath(),
            ["AUTH"] = "none",
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => TaskServerBootstrapOptions.Load(configuration));

        Assert.Contains("loopback", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Effective_server_urls_cannot_bypass_loopback_auth_guard()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["urls"] = "http://0.0.0.0:7000",
            ["LISTEN_URL"] = "http://127.0.0.1:5071",
            ["STORE_PATH"] = Path.GetTempPath(),
            ["AUTH"] = "none",
        });

        Assert.Throws<InvalidOperationException>(
            () => TaskServerBootstrapOptions.Load(configuration));
    }

    [Fact]
    public void Backup_path_defaults_beside_the_resolved_store()
    {
        using var store = new TempDirectory();
        var options = TaskServerBootstrapOptions.Load(
            Configuration(new Dictionary<string, string?>
            {
                ["LISTEN_URL"] = "http://127.0.0.1:5071",
                ["STORE_PATH"] = store.Path,
                ["AUTH"] = "none",
            }));

        Assert.Equal(
            Path.Combine(Path.GetFullPath(store.Path), "backups"),
            options.BackupPath);
    }

    [Fact]
    public void Bearer_authentication_requires_a_nontrivial_secret()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["LISTEN_URL"] = "http://0.0.0.0:5071",
            ["STORE_PATH"] = Path.GetTempPath(),
            ["AUTH"] = "bearer",
            ["AUTH_TOKEN"] = "short",
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => TaskServerBootstrapOptions.Load(configuration));

        Assert.Contains("at least 32", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bearer_authentication_can_generate_initial_principals_on_an_empty_store()
    {
        using var store = new TempDirectory();
        var options = TaskServerBootstrapOptions.Load(
            Configuration(new Dictionary<string, string?>
            {
                ["LISTEN_URL"] = "http://0.0.0.0:5071",
                ["STORE_PATH"] = store.Path,
                ["AUTH"] = "bearer",
            }));

        Assert.True(options.RequiresAuthentication);
        Assert.Null(options.StudioAuthenticationToken);
        Assert.Null(options.EngineAuthenticationToken);
    }

    [Fact]
    public void Command_line_has_explicit_version_backup_inventory_and_import_modes()
    {
        var version = TaskServerCommandLine.Parse(["--version"]);
        var backup = TaskServerCommandLine.Parse(
            ["backup", "--name", "nightly", "--TaskServer:MinimumLeaseSeconds", "10"]);
        var inventory = TaskServerCommandLine.Parse(["inventory", "--source", "/frozen"]);
        var import = TaskServerCommandLine.Parse(
            ["import", "--source", "/frozen", "--inventory", "/tmp/inventory.json", "--workspace", "Workspace", "--mode", "maintenance"]);

        Assert.Equal(TaskServerCommandKind.Version, version.Kind);
        Assert.Equal(TaskServerCommandKind.Backup, backup.Kind);
        Assert.Equal("nightly", backup.BackupName);
        Assert.Equal(TaskServerCommandKind.Inventory, inventory.Kind);
        Assert.Equal("/frozen", inventory.Source);
        Assert.Equal(TaskServerCommandKind.Import, import.Kind);
        Assert.Equal("/tmp/inventory.json", import.InventoryPath);
        Assert.Equal("Workspace", import.WorkspaceName);
        Assert.Equal(TaskServerMode.Maintenance, import.Mode);
        Assert.Equal(
            ["--TaskServer:MinimumLeaseSeconds", "10"],
            backup.HostArguments);
    }

    [Fact]
    public void Command_line_supports_sqlite_retention_and_full_backup_modes()
    {
        var retention = TaskServerCommandLine.Parse(
            ["retention", "apply", "--store", "/tmp/store", "--task", "AGT-1", "--confirm-cold-delete", "--json"]);
        var full = TaskServerCommandLine.Parse(
            ["backup", "full", "--TaskServer:DataDirectory", "/tmp/store", "--json"]);
        var restore = TaskServerCommandLine.Parse(
            ["backup", "restore-full", "backup-1", "--TaskServer:DataDirectory", "/tmp/store"]);

        Assert.Equal(TaskServerCommandKind.Retention, retention.Kind);
        Assert.Equal("/tmp/store", retention.Retention!.Store);
        Assert.True(retention.Retention.ConfirmColdDelete);
        Assert.Equal(TaskServerCommandKind.FullBackup, full.Kind);
        Assert.Null(full.FullBackup!.BackupId);
        Assert.Equal(["--TaskServer:DataDirectory", "/tmp/store"], full.HostArguments);
        Assert.Equal("backup-1", restore.FullBackup!.BackupId);
        Assert.Equal(["--TaskServer:DataDirectory", "/tmp/store"], restore.HostArguments);
    }

    [Fact]
    public void Import_command_rejects_a_non_maintenance_mode()
    {
        var error = Assert.Throws<ArgumentException>(() => TaskServerCommandLine.Parse(
            ["import", "--source", "/frozen", "--inventory", "/tmp/inventory.json", "--mode", "normal"]));

        Assert.Contains("only accepts maintenance", error.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(
        IReadOnlyDictionary<string, string?> values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
