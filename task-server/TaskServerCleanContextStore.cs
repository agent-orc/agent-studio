using System.Security.Cryptography;
using System.Text;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    internal async Task<IReadOnlyList<string>> GetTerminalCleanContextHashesAsync(
        string runnerId, IReadOnlyCollection<string> hashes, CancellationToken ct)
    {
        if (hashes.Count == 0) return [];
        var requested = hashes.ToHashSet(StringComparer.Ordinal);
        var terminal = new List<string>();
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT t.task_key
              FROM tasks t
             WHERE (t.state IN ($completed, $archive) OR t.archive_state = 'archived')
               AND EXISTS (SELECT 1 FROM runs r WHERE r.task_id = t.id AND r.runner_id = $runner);
            """, ("$completed", TaskStates.Completed), ("$archive", TaskStates.Archive), ("$runner", runnerId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reader.GetString(0)))).ToLowerInvariant();
            if (requested.Remove(hash)) terminal.Add(hash);
            if (requested.Count == 0) break;
        }
        return terminal;
    }
}
