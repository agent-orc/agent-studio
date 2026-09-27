using AgentStudio.Orchestrator;
using AgentStudio.Runner;

namespace AgentStudio.Tokens;

/// <summary>Projects durable chat receipts together with current host occupancy.</summary>
public static class ChatUsageReader
{
    public static async Task<IReadOnlyList<RemoteChatUsage>> ReadAsync(
        IReadOnlyList<(string Name, string StorageLocation)> projects,
        IOrchestratorChatPersistence persistence,
        RemoteChatWorkBroker broker,
        LocalChatTurnActivity localChats,
        CancellationToken ct)
    {
        var receipts = new List<ChatUsageReceipt>();
        var byName = projects.ToDictionary(project => project.Name,
            StringComparer.OrdinalIgnoreCase);
        var contexts = await persistence.ListContextsAsync(false, ct);
        foreach (var context in contexts)
        {
            if (!byName.TryGetValue(context.ProjectName, out var project)) continue;
            if (!OrchestratorContextKey.TryParse(context.ContextKey, out var key)) continue;
            var turns = await persistence.ReadAsync(project.Name,
                project.StorageLocation, key, 1000, ct);
            receipts.AddRange(turns.Where(turn => turn.Metadata is not null)
                .Select(turn => new ChatUsageReceipt(project.Name, turn.Metadata!)));
        }
        var names = byName.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var active = broker.GetUsage().Concat(localChats.Snapshot(now))
            .Where(item => names.Contains(item.ProjectName)).ToArray();
        return ChatUsageProjection.Build(receipts, active);
    }
}
