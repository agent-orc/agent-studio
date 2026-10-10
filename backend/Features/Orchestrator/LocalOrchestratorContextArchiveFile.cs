using System.Text.Json;

namespace AgentStudio.Orchestrator;

/// <summary>
/// Operator archive marks for the monolith profile's file-backed chat
/// sessions (AGT-3032). One small JSON map per project,
/// <c>&lt;watchPath&gt;/.orchestrator/context-archive.json</c>, keyed by the
/// canonical context key. The Task Server profile keeps the same mark in the
/// <c>orchestrator_contexts.archived_at</c> column instead.
/// </summary>
internal static class LocalOrchestratorContextArchiveFile
{
    private static readonly object Gate = new();

    public static string ResolvePath(string watchPath)
        => Path.Combine(watchPath, ".orchestrator", "context-archive.json");

    public static IReadOnlyDictionary<string, DateTime> Read(string watchPath)
    {
        lock (Gate) return ReadUnlocked(watchPath);
    }

    /// <summary>Sets (<paramref name="archivedAt"/> non-null) or clears one context's mark.</summary>
    public static void Write(string watchPath, string contextKey, DateTime? archivedAt)
    {
        lock (Gate)
        {
            var marks = new Dictionary<string, DateTime>(ReadUnlocked(watchPath), StringComparer.Ordinal);
            var changed = archivedAt is { } mark
                ? !(marks.TryGetValue(contextKey, out var existing) && existing == mark)
                : marks.ContainsKey(contextKey);
            if (!changed) return;
            if (archivedAt is { } value) marks[contextKey] = value;
            else marks.Remove(contextKey);

            var path = ResolvePath(watchPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(marks));
            File.Move(temp, path, overwrite: true);
        }
    }

    private static IReadOnlyDictionary<string, DateTime> ReadUnlocked(string watchPath)
    {
        var path = ResolvePath(watchPath);
        if (!File.Exists(path)) return new Dictionary<string, DateTime>(StringComparer.Ordinal);
        try
        {
            var marks = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(path));
            return marks is null
                ? new Dictionary<string, DateTime>(StringComparer.Ordinal)
                : new Dictionary<string, DateTime>(marks, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // A damaged mark file only loses archive state; every session
            // stays readable and the next archive rewrites the file.
            return new Dictionary<string, DateTime>(StringComparer.Ordinal);
        }
    }
}
