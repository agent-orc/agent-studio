using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>Only durable facts which can change a task's Git verdict.</summary>
internal static class TaskGitSignature
{
    internal static string For(TaskInfo task)
    {
        var input = JsonSerializer.Serialize(new
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
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
