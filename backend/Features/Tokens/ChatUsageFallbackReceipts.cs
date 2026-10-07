using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Bus;
using AgentStudio.Shared;

namespace AgentStudio.Tokens;

/// <summary>
/// Durable recovery source for a chat turn whose transcript was committed but
/// whose bus append failed. Files are keyed by turn id and folded into the same
/// project ledger as bus messages and task receipts.
/// </summary>
internal static class ChatUsageFallbackReceipts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string DirectoryFor(string root, string project)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project)));
        return Path.Combine(root, "chat-usage-fallback", key);
    }

    public static async Task WriteAsync(
        string root, string project, string turnId, DateTime at, OrchestratorTokenUsage usage)
    {
        var directory = DirectoryFor(root, project);
        Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(turnId)));
        var path = Path.Combine(directory, key + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var message = new AgentMessage
        {
            Id = turnId,
            CreatedAt = at,
            ParticipantId = AgentMessageBusBridge.ParticipantOrchestratorFor(project),
            Role = "evidence",
            Kind = "token-usage",
            Project = project,
            Topic = "orchestrator-chat",
            Tokens = new AgentMessageTokens(
                usage.InputTokens, usage.OutputTokens,
                usage.CacheReadTokens, usage.CacheCreationTokens,
                usage.Model, ThinkingLevel: usage.ThinkingLevel,
                InputIncludesCached: usage.InputIncludesCached,
                UsageNormalization: usage.UsageNormalization,
                PinnedModel: usage.PinnedModel,
                ModelMismatch: usage.ModelMismatch,
                CliType: usage.CliType, Host: usage.Host),
        };
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(message, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static (IReadOnlyList<OrchestratorLogEntry> Entries, string? Warning) Read(
        string root, string project)
    {
        var directory = DirectoryFor(root, project);
        if (!Directory.Exists(directory)) return ([], null);
        var entries = new List<OrchestratorLogEntry>();
        var failed = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    var message = JsonSerializer.Deserialize<AgentMessage>(File.ReadAllText(path), Json);
                    if (message?.Tokens is null || message.Project != project || message.Kind != "token-usage")
                    {
                        failed++;
                        continue;
                    }
                    entries.Add(BusTokenEntryConverter.ToEntry(message));
                }
                catch (Exception) { failed++; }
            }
        }
        catch (Exception)
        {
            return (entries, "Chat usage fallback receipts could not be enumerated. Token values may be incomplete.");
        }
        return (entries, failed == 0 ? null : $"{failed} chat usage fallback receipt(s) could not be read.");
    }
}
