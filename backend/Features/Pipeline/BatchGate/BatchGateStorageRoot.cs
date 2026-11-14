using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Pipeline;

/// <summary>Locates instance state by an identity carried with the workspace backup.</summary>
public static class BatchGateStorageRoot
{
    private const string IdentityFile = "batch-state-installation-id";
    private const string OriginFile = "batch-state-origin";

    public static string ForInstance(string contentRoot, string? taskRepository, string kind,
        string? stateRoot = null)
    {
        var key = IdentityKey(taskRepository) ?? LegacyKey(contentRoot, taskRepository);
        return Path.Combine(InstancesRoot(stateRoot), key, kind);
    }

    /// <summary>Creates the workspace identity and moves existing local state before services open it.</summary>
    public static void Prepare(string contentRoot, string? taskRepository, ILogger logger,
        string? stateRoot = null)
    {
        if (string.IsNullOrWhiteSpace(taskRepository)) return;
        var workspace = Path.GetFullPath(taskRepository);
        // A workspace that does not exist yet has no identity to carry; its
        // state stays under the path-derived key until the folder is created.
        if (!Directory.Exists(workspace)) return;
        var identityPath = Path.Combine(workspace, "." + IdentityFile);
        var legacyKey = LegacyKey(contentRoot, taskRepository);
        if (!File.Exists(identityPath))
        {
            using var stream = new FileStream(identityPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None);
            if (stream.Length == 0)
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
                writer.Write(Guid.NewGuid().ToString("N"));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
        }
        var key = IdentityKey(taskRepository)
            ?? throw new InvalidDataException("Batch state installation id is invalid: " + identityPath);
        var instances = InstancesRoot(stateRoot);
        var oldRoot = Path.Combine(instances, legacyKey);
        var newRoot = Path.Combine(instances, key);
        if (Directory.Exists(oldRoot) && !Directory.Exists(newRoot))
        {
            Directory.Move(oldRoot, newRoot);
            File.WriteAllText(Path.Combine(newRoot, OriginFile), oldRoot);
        }
        Directory.CreateDirectory(newRoot);
        var originPath = Path.Combine(newRoot, OriginFile);
        if (!File.Exists(originPath))
            File.WriteAllText(originPath, newRoot);
        else
        {
            var origin = File.ReadAllText(originPath).Trim();
            if (!string.Equals(origin, newRoot, StringComparison.Ordinal))
            {
                RelocateEvidence(newRoot, origin);
                File.WriteAllText(originPath, newRoot);
            }
        }
        if (!Directory.Exists(instances)) return;
        foreach (var other in Directory.EnumerateDirectories(instances))
        {
            if (string.Equals(other, newRoot, StringComparison.Ordinal) ||
                !Directory.Exists(Path.Combine(other, "batch-gates"))) continue;
            if (Directory.EnumerateFiles(Path.Combine(other, "batch-gates"), "manifest.json",
                    SearchOption.AllDirectories).Any())
                logger.LogWarning("batch-state-unmatched-instance path={Path} current={Current}; batch manifests may require restore migration",
                    other, newRoot);
        }
    }

    private static void RelocateEvidence(string root, string oldRoot)
    {
        var oldPrefix = JsonSerializer.Serialize(Path.GetFullPath(oldRoot) + Path.DirectorySeparatorChar)[1..^1];
        var newPrefix = JsonSerializer.Serialize(Path.GetFullPath(root) + Path.DirectorySeparatorChar)[1..^1];
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                         || path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
        {
            var content = File.ReadAllText(path);
            var updated = content.Replace(oldPrefix, newPrefix, StringComparison.Ordinal);
            if (updated == content) continue;
            var temporary = path + ".relocating";
            File.WriteAllText(temporary, updated);
            File.Move(temporary, path, overwrite: true);
        }
    }

    private static string? IdentityKey(string? taskRepository)
    {
        if (string.IsNullOrWhiteSpace(taskRepository)) return null;
        var path = Path.Combine(Path.GetFullPath(taskRepository), "." + IdentityFile);
        if (!File.Exists(path)) return null;
        var id = File.ReadAllText(path).Trim();
        if (id.Length != 32 || !id.All(Uri.IsHexDigit))
            throw new InvalidDataException("Batch state installation id is invalid: " + path);
        return id.ToLowerInvariant();
    }

    private static string LegacyKey(string contentRoot, string? taskRepository)
    {
        var identity = Path.GetFullPath(contentRoot) + "\n"
            + (string.IsNullOrWhiteSpace(taskRepository) ? "" : Path.GetFullPath(taskRepository));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
    }

    private static string InstancesRoot(string? stateRoot) =>
        Path.Combine(stateRoot ?? LocalStateRoot.Resolve(), "agentstudio", "instances");
}
