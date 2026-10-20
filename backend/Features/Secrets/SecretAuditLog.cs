using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Secrets;

/// <summary>
/// Append-only audit of every put, rotate, revoke, delete and resolve, with
/// actor, scope, reference, purpose and outcome. Entries never carry values.
/// One JSON line per entry in <c>audit/&lt;yyyy-MM&gt;.jsonl</c> next to the store.
/// Identical refused writes by one actor are merged per
/// <see cref="RefusalMergeWindow"/> (see <see cref="AppendRefusal"/>).
/// </summary>
public sealed class SecretAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>How long after a recorded refusal identical refusals are counted instead of written.</summary>
    public static readonly TimeSpan RefusalMergeWindow = TimeSpan.FromMinutes(1);

    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Dictionary<RefusalKey, RefusalWindow> _refusals = [];

    public SecretAuditLog(IConfiguration configuration)
        : this(Path.Combine(SecretStorePaths.DataRoot(configuration), "audit"))
    {
    }

    public SecretAuditLog(string directory) => _directory = directory;

    public void Append(SecretAuditEntry entry)
    {
        lock (_gate)
        {
            FlushClosedRefusals(entry.At);
            Write(entry);
        }
    }

    /// <summary>
    /// Records a refused write. The first refusal is written at once. Further
    /// refusals identical in actor, action, scope, reference, version and
    /// outcome within <see cref="RefusalMergeWindow"/> of it are only counted.
    /// Once the window has closed, the next audit call writes one entry for
    /// them with <see cref="SecretAuditEntry.Repeated"/> set to their count and
    /// the time of the last one, so a scripted retry loop cannot flood the log.
    /// </summary>
    public void AppendRefusal(SecretAuditEntry entry)
    {
        var key = new RefusalKey(entry.Action, entry.Actor, entry.Scope, entry.Reference, entry.Version, entry.Outcome, entry.Purpose, entry.Workload);
        lock (_gate)
        {
            FlushClosedRefusals(entry.At);
            if (_refusals.TryGetValue(key, out var open))
            {
                _refusals[key] = open with { Repeated = open.Repeated + 1, Last = entry };
                return;
            }
            Write(entry);
            _refusals[key] = new RefusalWindow(entry.At, 0, entry);
        }
    }

    private void FlushClosedRefusals(DateTime now)
    {
        foreach (var (key, window) in _refusals.Where(pair => now - pair.Value.RecordedAt >= RefusalMergeWindow).ToList())
        {
            _refusals.Remove(key);
            if (window.Repeated > 0) Write(window.Last with { Repeated = window.Repeated });
        }
    }

    private void Write(SecretAuditEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, Json) + "\n";
        OwnerOnlyAccess.CreateDirectory(_directory);
        var path = Path.Combine(_directory, entry.At.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl");
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        if (!File.Exists(path)) OwnerOnlyAccess.ApplyCreateMode(options);
        using var stream = new FileStream(path, options);
        stream.Write(Encoding.UTF8.GetBytes(line));
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Newest entries first, optionally only those of one scope.</summary>
    public IReadOnlyList<SecretAuditEntry> Read(SecretScope? scope, int limit)
    {
        limit = Math.Clamp(limit, 1, 1000);
        lock (_gate)
        {
            if (!Directory.Exists(_directory)) return [];
            var result = new List<SecretAuditEntry>();
            foreach (var file in Directory.EnumerateFiles(_directory, "*.jsonl").OrderDescending(StringComparer.Ordinal))
            {
                foreach (var line in File.ReadLines(file).Reverse())
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var entry = JsonSerializer.Deserialize<SecretAuditEntry>(line, Json);
                    if (entry is null || (scope is not null && !string.Equals(entry.Scope, scope.Key, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    result.Add(entry);
                    if (result.Count >= limit) return result;
                }
            }
            return result;
        }
    }

    private sealed record RefusalKey(string Action, string Actor, string Scope, string Reference, int? Version, string Outcome,
        string? Purpose, string? Workload);

    private sealed record RefusalWindow(DateTime RecordedAt, int Repeated, SecretAuditEntry Last);
}
