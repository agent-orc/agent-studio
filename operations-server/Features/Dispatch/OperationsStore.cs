using System.Text.Json;
using AgentStudio.Operations.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.Operations.Server.Features.Dispatch;

public sealed class OperationsStore
{
    private readonly string connectionString;
    public const int MaxAttempts = 1024;
    public const int MaxAgents = 128;

    public OperationsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "operations.db"), DefaultTimeout = 5, Pooling = false,
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS operations_state (id INTEGER PRIMARY KEY CHECK (id = 1), body TEXT NOT NULL);
            INSERT OR IGNORE INTO operations_state VALUES (1, '{"agents":{},"attempts":{},"nextFence":0}');
            CREATE TABLE IF NOT EXISTS operation_history (
                attempt_id TEXT NOT NULL,
                cursor INTEGER NOT NULL,
                actor TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                operation_id TEXT NOT NULL,
                input_digest TEXT NOT NULL,
                event_json TEXT NOT NULL,
                PRIMARY KEY (attempt_id, cursor));
            CREATE TRIGGER IF NOT EXISTS operation_history_no_update BEFORE UPDATE ON operation_history
                BEGIN SELECT RAISE(ABORT, 'operation-history-append-only'); END;
            CREATE TRIGGER IF NOT EXISTS operation_history_no_delete BEFORE DELETE ON operation_history
                BEGIN SELECT RAISE(ABORT, 'operation-history-append-only'); END;
            CREATE TABLE IF NOT EXISTS host_journal_receipts (
                attempt_id TEXT NOT NULL, at TEXT NOT NULL, outcome TEXT NOT NULL, entry_json TEXT NOT NULL,
                PRIMARY KEY (attempt_id, at, outcome));
            CREATE TRIGGER IF NOT EXISTS host_journal_no_update BEFORE UPDATE ON host_journal_receipts
                BEGIN SELECT RAISE(ABORT, 'host-journal-append-only'); END;
            CREATE TRIGGER IF NOT EXISTS host_journal_no_delete BEFORE DELETE ON host_journal_receipts
                BEGIN SELECT RAISE(ABORT, 'host-journal-append-only'); END;
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public async Task<T> TransactionAsync<T>(Func<OperationsState, Task<T>> action)
    {
        using var connection = Open();
        // Immediate transaction serializes admission, claims and reports across
        // process restarts as well as concurrent HTTP requests.
        using var transaction = connection.BeginTransaction(deferred: false);
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT body FROM operations_state WHERE id = 1";
        var state = JsonSerializer.Deserialize<OperationsState>((string)read.ExecuteScalar()!, OperationsProtocol.Json)
                    ?? throw new InvalidDataException("Operations state is missing.");
        var result = await action(state);
        foreach (var attempt in state.Attempts.Values)
        {
            using var cursor = connection.CreateCommand();
            cursor.Transaction = transaction;
            cursor.CommandText = "SELECT COALESCE(MAX(cursor), 0) FROM operation_history WHERE attempt_id = $id";
            cursor.Parameters.AddWithValue("$id", attempt.Id);
            var previous = (long)cursor.ExecuteScalar()!;
            foreach (var entry in attempt.Events.Where(item => item.Cursor > previous))
            {
                using var append = connection.CreateCommand();
                append.Transaction = transaction;
                append.CommandText = """
                    INSERT INTO operation_history (attempt_id, cursor, actor, agent_id, operation_id, input_digest, event_json)
                    VALUES ($id, $cursor, $actor, $agent, $operation, $digest, $event)
                    """;
                append.Parameters.AddWithValue("$id", attempt.Id);
                append.Parameters.AddWithValue("$cursor", entry.Cursor);
                append.Parameters.AddWithValue("$actor", attempt.Command.Actor);
                append.Parameters.AddWithValue("$agent", attempt.AgentId);
                append.Parameters.AddWithValue("$operation", attempt.Command.OperationId);
                append.Parameters.AddWithValue("$digest", attempt.Command.InputDigest);
                append.Parameters.AddWithValue("$event", JsonSerializer.Serialize(entry, OperationsProtocol.Json));
                append.ExecuteNonQuery();
            }
        }
        var body = JsonSerializer.Serialize(state, OperationsProtocol.Json);
        if (System.Text.Encoding.UTF8.GetByteCount(body) > 16 * 1024 * 1024)
            throw new OperationRejected("store-capacity", 507);
        using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = "UPDATE operations_state SET body = $body WHERE id = 1";
        write.Parameters.AddWithValue("$body", body);
        write.ExecuteNonQuery();
        transaction.Commit();
        return result;
    }

    public void ReceiveJournal(HostJournalEntry entry)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var match = connection.CreateCommand();
        match.Transaction = transaction;
        match.CommandText = """
            SELECT COUNT(*) FROM operation_history
            WHERE attempt_id = $id AND actor = $actor AND agent_id = $agent
              AND operation_id = $operation AND input_digest = $digest
              AND event_json LIKE '%"kind":"dispatched"%'
            """;
        match.Parameters.AddWithValue("$id", entry.AttemptId);
        match.Parameters.AddWithValue("$actor", entry.Actor);
        match.Parameters.AddWithValue("$agent", entry.AgentId);
        match.Parameters.AddWithValue("$operation", entry.OperationId);
        match.Parameters.AddWithValue("$digest", entry.InputDigest);
        if ((long)match.ExecuteScalar()! != 1) throw new OperationRejected("host-journal-without-central-audit", 409);
        using var append = connection.CreateCommand();
        append.Transaction = transaction;
        append.CommandText = "INSERT OR IGNORE INTO host_journal_receipts VALUES ($id, $at, $outcome, $entry)";
        append.Parameters.AddWithValue("$id", entry.AttemptId);
        append.Parameters.AddWithValue("$at", entry.At.ToString("O"));
        append.Parameters.AddWithValue("$outcome", entry.Outcome);
        append.Parameters.AddWithValue("$entry", JsonSerializer.Serialize(entry, OperationsProtocol.Json));
        append.ExecuteNonQuery();
        transaction.Commit();
    }
}

public sealed class OperationsState
{
    public Dictionary<string, AgentRegistration> Agents { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, OperationAttempt> Attempts { get; set; } = new(StringComparer.Ordinal);
    public long NextFence { get; set; }
}

public sealed class OperationRejected(string code, int status = 409) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
