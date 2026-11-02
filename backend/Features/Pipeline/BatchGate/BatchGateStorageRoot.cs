using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>Separates dev and stable batch authority even on the same host.</summary>
public static class BatchGateStorageRoot
{
    public static string ForInstance(string contentRoot, string? taskRepository, string kind)
    {
        var identity = Path.GetFullPath(contentRoot) + "\n"
            + (string.IsNullOrWhiteSpace(taskRepository) ? "" : Path.GetFullPath(taskRepository));
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return LocalStateRoot.Combine("agentstudio", "instances", digest[..20], kind);
    }
}
