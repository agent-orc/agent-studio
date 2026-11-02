using System.Security.Cryptography;
using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>Only durable facts which can change a task's Git verdict.</summary>
internal static class TaskGitSignature
{
    internal static string For(TaskInfo task)
    {
        // Hash the UTF-8 JSON directly: the same bytes as encoding the
        // serialized string, without the intermediate string. This runs for
        // every card on every Git index run (AGT-3369).
        var input = JsonSerializer.SerializeToUtf8Bytes(new
        {
            task.Id,
            task.TaskKey,
            task.State,
            task.WatchPath,
            task.FolderPath,
            task.IntegrationBranch,
            task.NoBranchExpected,
            task.Commits,
            task.Commit,
            task.IntegrationRecords,
            task.Provenance,
        });
        return Convert.ToHexString(SHA256.HashData(input));
    }
}
