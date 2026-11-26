using System.Collections.Concurrent;
using System.Text.Json;

namespace AgentStudio.Dispatch;

public sealed partial class DispatchStore
{
    private static readonly ConcurrentDictionary<string, object> ConflictLocks = new(StringComparer.Ordinal);

    internal static string ConflictId(string project, string card, string kind, string evidenceId)
        => Key($"{project.ToUpperInvariant()}\n{card}\n{kind}\n{evidenceId}").ToLowerInvariant();

    private string ConflictPath(string project, string id)
        => Path.Combine(Project(project), "conflicts", Key(id) + ".json");

    internal DispatchConflictRecordV1? ReadConflict(string project, string id)
        => Read<DispatchConflictRecordV1>(ConflictPath(project, id));

    internal bool HasConflict(string project, string id) => File.Exists(ConflictPath(project, id));

    /// <summary>Write once by evidence identity, including across retries and backfill reruns.</summary>
    public bool AppendConflict(DispatchConflictRecordV1 record)
    {
        var folder = Path.Combine(Project(record.Project), "conflicts");
        lock (ConflictLocks.GetOrAdd(folder, _ => new object()))
        {
            Directory.CreateDirectory(folder);
            var path = ConflictPath(record.Project, record.Id);
            if (File.Exists(path)) return false;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json));
                File.Move(temporary, path, overwrite: false);
                return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    /// <summary>UTC half-open interval, consistent with the integration evidence API.</summary>
    public IReadOnlyList<DispatchConflictRecordV1> ReadConflicts(string project,
        DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var folder = Path.Combine(Project(project), "conflicts");
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.json")
            .Select(TryReadConflictFile).OfType<DispatchConflictRecordV1>()
            .Where(row => (from is null || row.At >= from) && (to is null || row.At < to))
            .OrderBy(row => row.At).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
    }

    private static DispatchConflictRecordV1? TryReadConflictFile(string path)
    {
        try { return Read<DispatchConflictRecordV1>(path); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            AgentStudio.Diagnostics.SilentCatch.Warn(ex, $"Dispatch conflict record could not be read: {path}");
            return null;
        }
    }
}
