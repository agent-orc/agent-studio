using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Secrets;

/// <summary>
/// Append-only audit of every put, rotate, revoke, delete and resolve, with
/// actor, scope, reference, purpose and outcome. Entries never carry values.
/// One JSON line per entry in <c>audit/&lt;yyyy-MM&gt;.jsonl</c> next to the store.
/// </summary>
public sealed class SecretAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _directory;
    private readonly object _gate = new();

    public SecretAuditLog(IConfiguration configuration)
        : this(Path.Combine(SecretStorePaths.DataRoot(configuration), "audit"))
    {
    }

    public SecretAuditLog(string directory) => _directory = directory;

    public void Append(SecretAuditEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, Json) + "\n";
        lock (_gate)
        {
            OwnerOnlyAccess.CreateDirectory(_directory);
            var path = Path.Combine(_directory, entry.At.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl");
            var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
            if (!File.Exists(path)) OwnerOnlyAccess.ApplyCreateMode(options);
            using var stream = new FileStream(path, options);
            stream.Write(Encoding.UTF8.GetBytes(line));
            stream.Flush(flushToDisk: true);
        }
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
}
