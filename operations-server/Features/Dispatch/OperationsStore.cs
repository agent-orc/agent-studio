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
