using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Contracts.GitHubApp;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    internal Task RecordWorkspacePushAsync(IEnumerable<GitHubWriteObservation> observations, CancellationToken ct)
        => InWriteTransactionAsync((connection, transaction) =>
            RecordGitHubWritesAsync(connection, transaction, observations, ct), ct);

    private static async Task RecordGitHubWritesAsync(SqliteConnection connection, SqliteTransaction transaction,
        IEnumerable<GitHubWriteObservation> observations, CancellationToken ct)
    {
        foreach (var observation in observations)
            await ExecuteAsync(connection, """
                INSERT INTO github_write_observations(caller, repository, transport, succeeded_at,
                    fetch_repository, fetch_transport, commits_to_publish)
                VALUES ($caller, $repository, $transport, $at, $fetch_repository, $fetch_transport, $commits)
                ON CONFLICT(caller, repository, transport) DO UPDATE SET
                    succeeded_at = excluded.succeeded_at, fetch_repository = excluded.fetch_repository,
                    fetch_transport = excluded.fetch_transport, commits_to_publish = excluded.commits_to_publish
                WHERE excluded.succeeded_at > github_write_observations.succeeded_at;
                """, ct, transaction, ("$caller", observation.Caller), ("$repository", observation.Target.Repository),
                ("$transport", observation.Target.Transport), ("$at", Iso(observation.SucceededAt.UtcDateTime)),
                ("$fetch_repository", observation.Fetch?.Repository), ("$fetch_transport", observation.Fetch?.Transport),
                ("$commits", observation.CommitsToPublish));
    }

    private async Task<GitHubOperationManifest> ReadGitHubWriteObservationsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT caller, repository, transport, succeeded_at, fetch_repository, fetch_transport, commits_to_publish
              FROM github_write_observations ORDER BY caller, repository, transport;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<GitHubWriteObservation>();
        while (await reader.ReadAsync(ct))
        {
            var fetch = reader.IsDBNull(4) ? null : new GitHubOperationTarget(reader.GetString(5), reader.GetString(4));
            result.Add(new(reader.GetString(0), new(reader.GetString(2), reader.GetString(1)),
                Parse(reader.GetString(3)), fetch, reader.IsDBNull(6) ? null : reader.GetInt32(6)));
        }
        return new([], [], result, new Dictionary<string, string>
        {
            ["workspace-export"] = string.IsNullOrWhiteSpace(_options.WorkspaceExportRoot) || !_options.WorkspaceExportPush
                ? "disabled" : "configured",
            ["task-server-ref-gc"] = !_options.ResultRefGcEnabled || _mode is TaskServerMode.ReadOnly or TaskServerMode.Maintenance
                ? "disabled" : "configured",
        }, []);
    }
}
