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
            SELECT id, run_id, name, media_type, sha256, content, size_bytes,
                   source_path, pointer_only
              FROM artifacts
             WHERE run_id = $run AND id = $artifact;
            """, ("$run", runId), ("$artifact", artifactId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var content = (byte[])reader[5];
        return new ArtifactContentDto(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), Convert.ToBase64String(content),
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt64(8) != 0);
    }
}
