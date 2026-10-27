using System.Runtime.CompilerServices;

namespace AgentStudio.TaskServer;

/// <summary>One card with everything the workspace export writes into its task folder.</summary>
public sealed record WorkspaceExportTask(
    string ProjectName,
    string TaskKey,
    string Title,
    string? Body,
    string State,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    long Rank,
    string? LegacyTaskJson,
    IReadOnlyList<WorkspaceExportFile> Files,
    IReadOnlyList<WorkspaceExportFile> Artifacts,
    int PointerOnlyArtifacts);

/// <summary>A task file (path relative to the task folder) or a run artifact (artifact name) with its bytes.</summary>
public sealed record WorkspaceExportFile(string Path, string Sha256, byte[] Content);

public sealed partial class TaskServerStore
{
    /// <summary>
    /// Streams every card of every project, archive included, with its current
    /// task files and the newest hot content of each run artifact name. One
    /// card is held in memory at a time; reads never take the write gate, so
    /// an export never delays a writer.
    /// </summary>
    public async IAsyncEnumerable<WorkspaceExportTask> ReadWorkspaceExportAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var cards = new List<(string Id, string Project, string Key, string Title, string? Body, string State,
            string Created, string Updated, long Rank)>();
        await using (var command = Command(connection, """
            SELECT t.id, p.name, t.task_key, t.title, t.body, t.state, t.created_at, t.updated_at, t.rank
              FROM tasks t
              JOIN projects p ON p.id = t.project_id
             ORDER BY p.name, t.task_key;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                cards.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetString(7), reader.GetInt64(8)));
        }

        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();
            var legacy = await ScalarAsync(connection, """
                SELECT payload_json FROM legacy_migration_entities
                 WHERE entity_kind = 'task' AND entity_key = $task
                 ORDER BY rowid DESC LIMIT 1;
                """, ct, ("$task", card.Id)) as string;

            var files = new List<WorkspaceExportFile>();
            await using (var command = Command(connection, """
                SELECT path, content_base64, object_stored, sha256
                  FROM task_files WHERE task_id = $task ORDER BY path;
                """, ("$task", card.Id)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var sha = reader.IsDBNull(3) ? null : reader.GetString(3);
                    var content = ReadTaskContent(reader.GetString(1), reader.GetInt64(2) != 0, sha);
                    files.Add(new WorkspaceExportFile(reader.GetString(0), sha ?? TaskContentObjectStore.Sha256(content), content));
                }
            }

            var artifacts = new List<WorkspaceExportFile>();
            var pointerOnly = 0;
            await using (var command = Command(connection, """
                SELECT a.name, a.content, a.object_stored, a.sha256, a.pointer_only, a.archived
                  FROM artifacts a
                  JOIN runs r ON r.id = a.run_id
                 WHERE r.task_id = $task
                 ORDER BY a.created_at DESC, a.id DESC;
                """, ("$task", card.Id)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (await reader.ReadAsync(ct))
                {
                    var name = reader.GetString(0);
                    if (!seen.Add(name)) continue;
                    var objectStored = reader.GetInt64(2) != 0;
                    if (reader.GetInt64(4) != 0 || reader.GetInt64(5) != 0 || (reader.IsDBNull(1) && !objectStored))
                    {
                        pointerOnly++;
                        continue;
                    }
                    artifacts.Add(new WorkspaceExportFile(name, reader.GetString(3),
                        ReadArtifactBytes(reader[1], objectStored, reader.GetString(3))));
                }
            }

            yield return new WorkspaceExportTask(card.Project, card.Key, card.Title, card.Body, card.State,
                Parse(card.Created), Parse(card.Updated), card.Rank, legacy, files, artifacts, pointerOnly);
        }
    }
}
