using System.Collections.Concurrent;
using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>Typed, append-only delivery evidence owned by the Task Server.</summary>
public sealed record IntegrationLaneEvent(
    string Project, string JobId, long Sequence, DateTimeOffset AtUtc, string Kind,
    DateTimeOffset? DeliveredAtUtc = null, long? MergeLockWaitMs = null,
    long? QueueWaitMs = null, long? DurationMs = null, long? PublicationLockWaitMs = null,
    string? Level = null,
    string? Outcome = null, string? ReuseToken = null, string? Reason = null,
    string? Receipt = null, string? HoldingProject = null, bool? CollisionDetected = null);

public static class IntegrationLaneEvents
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.Ordinal);
    public const string FileName = "integration-lane-events.jsonl";

    public static void Append(string folder, string project, string jobId, string kind,
        long? sequence = null, DateTimeOffset? deliveredAtUtc = null,
        long? mergeLockWaitMs = null, long? queueWaitMs = null, long? durationMs = null,
        long? publicationLockWaitMs = null,
        string? level = null, string? outcome = null, string? reuseToken = null,
        string? reason = null, string? receipt = null, string? holdingProject = null,
        bool? collisionDetected = null, DateTimeOffset? atUtc = null)
    {
        if (!Directory.Exists(folder)) return;
        try
        {
            var path = Path.Combine(folder, FileName);
            lock (Locks.GetOrAdd(path, _ => new object()))
            {
                var currentSequence = sequence ?? Read(folder).LastOrDefault()?.Sequence ?? 0;
                var row = new IntegrationLaneEvent(project, jobId, currentSequence,
                    atUtc ?? DateTimeOffset.UtcNow, kind, deliveredAtUtc, mergeLockWaitMs,
                    queueWaitMs, durationMs, publicationLockWaitMs, level, outcome, reuseToken, reason,
                    receipt, holdingProject, collisionDetected);
                File.AppendAllText(path, JsonSerializer.Serialize(row) + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SilentCatch.Note(ex, "IntegrationLaneEvents: event append unavailable"); }
    }

    public static IReadOnlyList<IntegrationLaneEvent> Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return [];
        var events = new List<IntegrationLaneEvent>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    if (JsonSerializer.Deserialize<IntegrationLaneEvent>(line) is { } row)
                        events.Add(row);
                }
                catch (JsonException ex) { SilentCatch.Note(ex, "IntegrationLaneEvents: torn event ignored"); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SilentCatch.Note(ex, "IntegrationLaneEvents: event read unavailable"); }
        return events;
    }
}
