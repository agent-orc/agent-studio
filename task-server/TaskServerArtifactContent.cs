using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    public async Task<ArtifactContentDto?> GetArtifactContentAsync(
        string runId,
        string artifactId,
        CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT a.id, a.run_id, a.name, a.media_type, a.sha256, a.content, a.size_bytes,
                   a.archived, t.id, t.task_key, a.source_path, a.pointer_only
              FROM artifacts a
              JOIN runs r ON r.id = a.run_id
              JOIN tasks t ON t.id = r.task_id
             WHERE a.run_id = $run AND a.id = $artifact;
            """, ("$run", runId), ("$artifact", artifactId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        if (reader.GetInt64(7) != 0)
            throw new ArtifactArchivedException(reader.GetString(0), reader.GetString(8), reader.GetString(9));
        var content = (byte[])reader[5];
        return new ArtifactContentDto(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), Convert.ToBase64String(content),
            reader.GetInt64(6),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetInt64(11) != 0);
    }
}
