using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private static async Task<MechanicalRoundDelta?> ReadUnclaimedMechanicalDeltaAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskId, CancellationToken ct)
    {
        var json = Convert.ToString(await ScalarAsync(connection, """
            SELECT delta_json FROM task_mechanical_deltas
             WHERE task_id = $task AND claimed_run_id IS NULL;
            """, ct, transaction, ("$task", taskId)));
        return DeserializeMechanicalDelta(json);
    }

    private static async Task<MechanicalRoundDelta?> ReadClaimedMechanicalDeltaAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskId,
        string runId, CancellationToken ct)
    {
        var json = Convert.ToString(await ScalarAsync(connection, """
            SELECT delta_json FROM task_mechanical_deltas
             WHERE task_id = $task AND claimed_run_id = $run;
            """, ct, transaction, ("$task", taskId), ("$run", runId)));
        return DeserializeMechanicalDelta(json);
    }

    private static async Task ClaimMechanicalDeltaAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskId,
        string runId, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            UPDATE task_mechanical_deltas SET claimed_run_id = $run
             WHERE task_id = $task AND claimed_run_id IS NULL;
            """, ct, transaction, ("$task", taskId), ("$run", runId));
    }

    private static MechanicalRoundDelta? DeserializeMechanicalDelta(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<MechanicalRoundDelta>(json); }
        catch (JsonException) { return null; }
    }
}
