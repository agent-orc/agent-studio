using AgentStudio.TaskServer;
using Microsoft.Data.Sqlite;
using Xunit;

namespace TaskServer.Tests;

public sealed partial class TaskServerStoreTests
{
    [Fact]
    public async Task ExistingStore_RemovesRetiredParentTablesAndTaskField()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var legacy = connection.CreateCommand();
            legacy.CommandText = """
                CREATE TABLE epics(id TEXT PRIMARY KEY);
                CREATE TABLE epic_tasks(epic_id TEXT, task_id TEXT);
                ALTER TABLE task_studio_fields ADD COLUMN epic_id TEXT;
                INSERT INTO epics(id) VALUES ('old-parent');
                UPDATE meta SET value = '36' WHERE key = 'schema_version';
                DELETE FROM schema_migrations WHERE version = 37;
                """;
            await legacy.ExecuteNonQueryAsync();
        }

        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        await using var verify = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('epics','epic_tasks'))
                 + (SELECT COUNT(*) FROM pragma_table_info('task_studio_fields') WHERE name='epic_id');
            """;
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        Assert.Equal("37", (string)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Version36BinaryRejectsStoreAfterRetiredFieldRemoval()
    {
        const int previousBinarySchemaVersion = 36;
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        var storedVersion = int.Parse((string)(await command.ExecuteScalarAsync())!);

        Assert.Equal(37, storedVersion);
        var error = Assert.Throws<InvalidOperationException>(() =>
            TaskServerStore.RejectUnsupportedSchemaVersion(storedVersion, previousBinarySchemaVersion));
        Assert.Contains("newer than this service supports (36)", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedRetiredSchemaRemovalRejectsVersion36AndFinishesOnRestart(bool fieldRemoved)
    {
        const int previousBinarySchemaVersion = 36;
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();

        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var legacy = connection.CreateCommand();
            legacy.CommandText = """
                CREATE TABLE epics(id TEXT PRIMARY KEY);
                CREATE TABLE epic_tasks(epic_id TEXT, task_id TEXT);
                ALTER TABLE task_studio_fields ADD COLUMN epic_id TEXT;
                UPDATE meta SET value = '36' WHERE key = 'schema_version';
                DELETE FROM schema_migrations WHERE version = 37;
                """;
            await legacy.ExecuteNonQueryAsync();

            await TaskServerStore.GuardRetiredSchemaRemovalAsync(connection, default);
            await store.ApplyStudioProjectMetaMigrationAsync(connection, default);
            if (fieldRemoved)
                await store.ApplyStudioTaskMetadataMigrationAsync(connection, default);
            // Simulate termination after table removal or after all retired
            // schema is removed, before the remaining startup work completes.
        }

        await using (var interrupted = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await interrupted.OpenAsync();
            await using var check = interrupted.CreateCommand();
            check.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
            var storedVersion = int.Parse((string)(await check.ExecuteScalarAsync())!);
            var error = Assert.Throws<InvalidOperationException>(() =>
                TaskServerStore.RejectUnsupportedSchemaVersion(storedVersion, previousBinarySchemaVersion));
            Assert.Contains("newer than this service supports (36)", error.Message);
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('task_studio_fields') WHERE name = 'epic_id';";
            Assert.Equal(fieldRemoved ? 0L : 1L, (long)(await check.ExecuteScalarAsync())!);
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('epics', 'epic_tasks');";
            Assert.Equal(0L, (long)(await check.ExecuteScalarAsync())!);
        }

        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        await using var verify = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('task_studio_fields') WHERE name = 'epic_id';";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        Assert.Equal("37", (string)(await command.ExecuteScalarAsync())!);
    }
}
