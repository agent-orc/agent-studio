using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private static async Task ApplyOperationsPrincipalMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        var sql = await ScalarAsync(connection, "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'principals'", ct) as string;
        if (sql?.Contains("'operations'", StringComparison.Ordinal) == true) return;
        // Rebuild the CHECK constraint without cascading deletion of credentials.
        // Foreign keys are re-enabled only after the replacement commits.
        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF", ct);
        try
        {
            using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, """
                CREATE TABLE principals_operations_upgrade(
                    principal_id TEXT PRIMARY KEY,
                    kind TEXT NOT NULL CHECK(kind IN ('studio', 'engine', 'runner', 'operations')),
                    scopes_json TEXT NOT NULL,
                    runner_id TEXT UNIQUE,
                    created_at TEXT NOT NULL,
                    revoked_at TEXT,
                    last_seen_at TEXT,
                    CHECK(kind = 'runner' OR runner_id IS NULL)
                );
                INSERT INTO principals_operations_upgrade SELECT * FROM principals;
                DROP TABLE principals;
                ALTER TABLE principals_operations_upgrade RENAME TO principals;
                """, ct, transaction);
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "PRAGMA foreign_key_check";
            using var reader = await check.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) throw new InvalidDataException("Operations principal migration broke a foreign key.");
            reader.Close();
            transaction.Commit();
        }
        finally { await ExecuteAsync(connection, "PRAGMA foreign_keys = ON", ct); }
    }
}
