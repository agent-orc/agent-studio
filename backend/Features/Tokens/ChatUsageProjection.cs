using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tokens;

public sealed record ChatUsageReceipt(string ProjectName, ChatTurnMetadataDto Metadata);

/// <summary>Separates durable chat consumption from live coding and chat occupancy.</summary>
public static class ChatUsageProjection
{
    public static IReadOnlyList<RemoteChatUsage> Build(
        IReadOnlyList<ChatUsageReceipt> receipts,
        IReadOnlyList<RemoteChatUsage> active)
    {
        var completed = new Dictionary<(string Host, string Project),
            (long Tokens, decimal Cost, bool Unpriced)>();
        foreach (var receipt in receipts)
        {
            var metadata = receipt.Metadata;
            if (metadata.FinishedAt is null) continue;
            var key = (metadata.ExecutingHost ?? "unknown", receipt.ProjectName);
            var previous = completed.GetValueOrDefault(key);
            completed[key] = (previous.Tokens + (metadata.InputTokens ?? 0)
                + (metadata.CachedInputTokens ?? 0) + (metadata.OutputTokens ?? 0)
                + (metadata.CacheCreationTokens ?? 0), previous.Cost + (metadata.Cost ?? 0),
                previous.Unpriced || metadata.Cost is null);
        }
        var activeByKey = active.GroupBy(item => (item.HostName, item.ProjectName))
            .ToDictionary(group => group.Key, group => new
            {
                Active = group.Sum(item => item.ActiveTurns),
                Heavy = group.Sum(item => item.HeavyTurns),
                Cpu = group.Any(item => item.CpuPercent.HasValue)
                    ? (double?)group.Sum(item => item.CpuPercent ?? 0) : null,
            });
        return completed.Keys.Union(activeByKey.Keys)
            .Select(key =>
            {
                var settled = completed.GetValueOrDefault(key);
                var occupancy = activeByKey.GetValueOrDefault(key);
                return new RemoteChatUsage(key.Item1, key.Item2,
                    occupancy?.Active ?? 0, occupancy?.Heavy ?? 0, occupancy?.Cpu,
                    settled.Tokens,
                    completed.ContainsKey(key) && !settled.Unpriced ? settled.Cost : null);
            })
            .OrderBy(row => row.HostName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
