using System.Text.Json;

namespace AgentStudio.Watcher;

/// <summary>
/// AGT-3207: durable intervention records, one JSON file per record under
/// <c>&lt;TaskRepository&gt;/.watcher/interventions/</c>, written through a
/// temporary file so a crash never leaves a torn receipt. The record id is
/// derived from the write key, so a replayed receipt maps to the same record.
/// Settled records older than the retention window are pruned.
/// </summary>
public sealed class StallInterventionStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Func<string?> _root;
    private readonly ILogger<StallInterventionStore>? _logger;
    private readonly object _gate = new();

    public StallInterventionStore(IConfiguration configuration, ILogger<StallInterventionStore> logger)
        : this(() => configuration["TaskRepository"], logger) { }

    public StallInterventionStore(Func<string?> workspaceRoot, ILogger<StallInterventionStore>? logger = null)
    {
        _root = workspaceRoot;
        _logger = logger;
    }

    public static string IdFor(string writeKey)
        => "si-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(writeKey)).AsSpan(0, 5));

    public IReadOnlyList<StallInterventionRecord> All()
    {
        var directory = Directory();
        if (directory is null || !System.IO.Directory.Exists(directory)) return [];
        lock (_gate)
        {
            var records = new List<StallInterventionRecord>();
            foreach (var file in System.IO.Directory.EnumerateFiles(directory, "si-*.json"))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<StallInterventionRecord>(File.ReadAllText(file), Json);
                    if (record is not null) records.Add(record);
                }
                catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
                {
                    _logger?.LogWarning(exception, "stall-intervention-record-unreadable file={File}", file);
                }
            }
            return records.OrderBy(item => item.DecidedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        }
    }

    public StallInterventionRecord? Find(string id) => All().FirstOrDefault(item => item.Id == id);

    public StallInterventionRecord? FindByWriteKey(string writeKey) => Find(IdFor(writeKey));

    public StallInterventionRecord Save(StallInterventionRecord record)
    {
        var directory = Directory() ?? throw new InvalidOperationException("TaskRepository is not configured.");
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, record.Id + ".json");
            WriteDurably(path, JsonSerializer.Serialize(record, Json));
            return record;
        }
    }

    /// <summary>Removes settled records older than <see cref="Retention"/>; open ones stay.</summary>
    public int Prune(DateTime nowUtc)
    {
        var directory = Directory();
        if (directory is null) return 0;
        var removed = 0;
        foreach (var record in All().Where(item => !StallInterventionStatuses.Pending.Contains(item.Status)
            && !item.AdmissionInForce
            && !(item.Action == StallInterventionActions.RestartService && item.EffectObserved != true)
            && nowUtc - (item.ResolvedAtUtc ?? item.DecidedAtUtc) > Retention))
        {
            try
            {
                lock (_gate) File.Delete(Path.Combine(directory, record.Id + ".json"));
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(exception, "stall-intervention-prune-failed id={Id}", record.Id);
            }
        }
        return removed;
    }

    /// <summary>
    /// Writes through a flushed temporary file and renames it, so a crash
    /// leaves either the old or the new record, never a torn or unflushed one.
    /// </summary>
    internal static void WriteDurably(string path, string content)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private string? Directory()
    {
        var root = _root();
        return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, ".watcher", "interventions");
    }
}
