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
    }
}
