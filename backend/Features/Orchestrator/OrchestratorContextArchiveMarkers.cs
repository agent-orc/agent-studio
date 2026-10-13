using System.Text.Json;

namespace AgentStudio.Orchestrator;

/// <summary>
/// Per-project operator archive markers for the monolith (local) context
/// store. The Task Server keeps the same marker in its
/// <c>orchestrator_contexts.archived_at</c> column; the local store has no
/// database, so it keeps a small sidecar map of context key to archive time at
/// <c>&lt;watchPath&gt;/.orchestrator/context-archive.json</c>. Writes go
/// through a temp file and an atomic rename so a crash never leaves a torn
/// file, and a missing or unreadable file reads as "nothing archived".
/// </summary>
internal sealed class OrchestratorContextArchiveMarkers
{
    public const string FileName = "context-archive.json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // Static: every instance shares the on-disk sidecar, so serialize across instances too.
    private static readonly object Gate = new();

    public static string ResolvePath(string watchPath)
        => Path.Combine(watchPath, ".orchestrator", FileName);

    public IReadOnlyDictionary<string, DateTime> Read(string watchPath)
    {
        lock (Gate)
        {
            return ReadLocked(watchPath);
        }
    }

    /// <summary>
    /// Applies an archive-state change and returns the resulting marker.
    /// Archiving an already archived context keeps its original timestamp.
    /// </summary>
    public DateTime? Set(string watchPath, string contextKey, bool archived, DateTime now)
    {
        lock (Gate)
        {
            var markers = ReadLocked(watchPath);
            var current = markers.TryGetValue(contextKey, out var existing) ? existing : (DateTime?)null;
            var next = AgentStudio.TaskServer.Contracts.OrchestratorContextArchivePolicy
                .NextArchivedAt(current, archived, now);
            if (next == current) return next;
            if (next is null) markers.Remove(contextKey);
            else markers[contextKey] = next.Value;
            WriteLocked(watchPath, markers);
            return next;
        }
    }

    /// <summary>Removes the marker for a context, if any. A new turn unarchives.</summary>
    public void Clear(string watchPath, string contextKey)
    {
        lock (Gate)
        {
            if (!File.Exists(ResolvePath(watchPath))) return;
            var markers = ReadLocked(watchPath);
            if (!markers.Remove(contextKey)) return;
            WriteLocked(watchPath, markers);
        }
    }

    private static Dictionary<string, DateTime> ReadLocked(string watchPath)
    {
        var path = ResolvePath(watchPath);
        if (!File.Exists(path)) return new Dictionary<string, DateTime>(StringComparer.Ordinal);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(path));
            return parsed is null
                ? new Dictionary<string, DateTime>(StringComparer.Ordinal)
                : new Dictionary<string, DateTime>(
                    parsed.Select(pair => KeyValuePair.Create(pair.Key, DateTime.SpecifyKind(pair.Value.ToUniversalTime(), DateTimeKind.Utc))),
                    StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(exception, "OrchestratorContextArchiveMarkers: unreadable sidecar reads as empty.");
            return new Dictionary<string, DateTime>(StringComparer.Ordinal);
        }
    }

    private static void WriteLocked(string watchPath, Dictionary<string, DateTime> markers)
    {
        var path = ResolvePath(watchPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(
            markers.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            WriteOptions));
        File.Move(temp, path, overwrite: true);
    }
}
