using System.Collections.Concurrent;
using System.Text.Json;

namespace AgentStudio.Watcher;

/// <summary>
/// Durable store for rejected-fingerprint suppressions (§10.4). One JSON
/// dictionary file per workspace; suppressions are few and short-lived so a
/// single file (rewritten atomically) is simpler than per-fingerprint files.
/// Expired entries are pruned lazily on read so the visible list only ever
/// shows what is currently in force.
/// </summary>
public sealed class WatcherSuppressionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogger<WatcherSuppressionStore> _logger;
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);

    public WatcherSuppressionStore(ILogger<WatcherSuppressionStore> logger)
    {
        _logger = logger;
    }

    private static string Path_(string workspaceRoot) =>
        System.IO.Path.Combine(workspaceRoot, "logs", "watcher", "suppressions.json");

    public IReadOnlyList<WatcherSuppression> Active(string workspaceRoot, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        return ReadAll(workspaceRoot).Where(s => s.ExpiresAtUtc > now).ToList();
    }

    public bool IsSuppressed(string workspaceRoot, string fingerprint, DateTime? nowUtc = null) =>
        Active(workspaceRoot, nowUtc).Any(s => string.Equals(s.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));

    public void Suppress(string workspaceRoot, string fingerprint, string reason, TimeSpan duration, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var entry = new WatcherSuppression
        {
            Fingerprint = fingerprint,
            Reason = reason,
            SuppressedAtUtc = now,
            ExpiresAtUtc = now + duration,
        };
        lock (_locks.GetOrAdd(workspaceRoot, static _ => new object()))
        {
            var all = ReadAll(workspaceRoot)
                .Where(s => s.ExpiresAtUtc > now && !string.Equals(s.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                .Append(entry)
                .ToList();
            WriteAll(workspaceRoot, all);
        }
    }

    private List<WatcherSuppression> ReadAll(string workspaceRoot)
    {
        var path = Path_(workspaceRoot);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<WatcherSuppression>>(File.ReadAllText(path), JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read watcher suppression list at {Path}", path);
            return [];
        }
    }

    private void WriteAll(string workspaceRoot, List<WatcherSuppression> entries)
    {
        var path = Path_(workspaceRoot);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist watcher suppression list at {Path}", path);
        }
    }
}
