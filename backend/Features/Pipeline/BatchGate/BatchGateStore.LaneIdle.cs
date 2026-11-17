using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3857: the lane idle ledger of a project. The open interval is one
/// replaced file, so a restarted process can close it at its last
/// observation; closed intervals are appended one line each.
/// </summary>
public sealed partial class BatchGateStore
{
    private const int LaneIdleRetainedLines = 1000;

    private string LaneIdleFolder(string project)
        => Path.Combine(_root, "lane-idle", SafeName(project.ToLowerInvariant()));

    public BatchLaneIdleInterval? ReadOpenLaneIdle(string project)
    {
        var path = Path.Combine(LaneIdleFolder(project), "open.json");
        if (!File.Exists(path)) return null;
        try { return Read<BatchLaneIdleInterval>(path); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(ex, "Open lane idle interval unreadable");
            return null;
        }
    }

    /// <summary>Every project's open interval, including those a previous process left.</summary>
    public IReadOnlyList<BatchLaneIdleInterval> ListOpenLaneIdle()
    {
        var root = Path.Combine(_root, "lane-idle");
        if (!Directory.Exists(root)) return [];
        var open = new List<BatchLaneIdleInterval>();
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var path = Path.Combine(folder, "open.json");
            if (!File.Exists(path)) continue;
            try { open.Add(Read<BatchLaneIdleInterval>(path)); }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                AgentStudio.Diagnostics.SilentCatch.Note(ex, "Open lane idle interval unreadable");
            }
        }
        return open;
    }

    public void WriteOpenLaneIdle(BatchLaneIdleInterval interval)
    {
        var folder = LaneIdleFolder(interval.Project);
        Directory.CreateDirectory(folder);
        WriteAtomic(Path.Combine(folder, "open.json"), interval);
    }

    /// <summary>Appends a closed interval and removes the open record in one guarded step.</summary>
    public void CloseLaneIdle(string project, IReadOnlyList<BatchLaneIdleInterval> closed, bool clearOpen)
    {
        var folder = LaneIdleFolder(project);
        Directory.CreateDirectory(folder);
        using var guard = ExclusiveGuard(folder, "lane-idle.lock");
        var path = Path.Combine(folder, "intervals.jsonl");
        if (closed.Count > 0)
        {
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                foreach (var interval in closed)
                {
                    JsonSerializer.Serialize(stream, interval, LineJson);
                    stream.WriteByte((byte)'\n');
                }
                stream.Flush(flushToDisk: true);
            }
            var lines = File.ReadAllLines(path);
            if (lines.Length > LaneIdleRetainedLines)
            {
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllLines(temporary, lines[^(LaneIdleRetainedLines / 2)..]);
                File.Move(temporary, path, overwrite: true);
            }
        }
        if (clearOpen && File.Exists(Path.Combine(folder, "open.json")))
            File.Delete(Path.Combine(folder, "open.json"));
    }

    /// <summary>Closed intervals of the project that ended at or after <paramref name="sinceUtc"/>, oldest first.</summary>
    public IReadOnlyList<BatchLaneIdleInterval> ListLaneIdle(string project, DateTimeOffset sinceUtc)
    {
        var path = Path.Combine(LaneIdleFolder(project), "intervals.jsonl");
        if (!File.Exists(path)) return [];
        var intervals = new List<BatchLaneIdleInterval>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<BatchLaneIdleInterval>(line, LineJson) is { } interval
                    && (interval.EndedAtUtc ?? interval.LastObservedAtUtc) >= sinceUtc)
                    intervals.Add(interval);
            }
            catch (JsonException ex)
            {
                AgentStudio.Diagnostics.SilentCatch.Note(ex, "Lane idle interval line unreadable");
            }
        }
        return intervals;
    }

    private static readonly JsonSerializerOptions LineJson = new(JsonSerializerDefaults.Web);
}
