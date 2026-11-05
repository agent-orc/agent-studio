using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>
/// Reads and writes the per-project task index for the flat storage layout
/// (F45 restscope): <c>index/by-state.json</c> maps each lane to the list of
/// task locations in it, and <c>index/by-key.json</c> maps each task key to
/// its <c>&lt;bucket&gt;/&lt;taskId&gt;</c> location.
///
/// <para>
/// The index is a derived cache, never the authority: it is always
/// rebuildable from the <c>state</c> and <c>key</c> fields in each
/// <c>task.json</c>, so a missing or stale index can be regenerated at boot
/// without data loss. Writes go through a temp file + atomic replace so a
/// crash mid-write cannot leave a reader observing a half-written file.
/// </para>
///
/// <para>
/// This type writes only under <c>index/</c> and uses <c>File.Move</c> (not
/// <c>Directory.Move</c>) and never builds a lane-folder path, so it stays
/// outside the TaskFolderAccessIsolation whitelist.
/// </para>
/// </summary>
internal static class TaskLayoutIndex
{
    public const string ByStateFileName = "by-state.json";
    public const string ByKeyFileName = "by-key.json";

    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    public static string ByStatePath(string projectRoot) =>
        Path.Combine(TaskStorageLayout.IndexRoot(projectRoot), ByStateFileName);

    public static string ByKeyPath(string projectRoot) =>
        Path.Combine(TaskStorageLayout.IndexRoot(projectRoot), ByKeyFileName);

    /// <summary>
    /// Rebuilds both index maps from the <c>task.json</c> files under
    /// <c>jobs/*</c> in a deterministic order (lane order from
    /// <see cref="TaskStates.All"/>, then ascending key number), then writes
    /// them atomically. Returns the rebuilt maps so callers can log counts.
    /// </summary>
    public static IndexSnapshot Rebuild(string projectRoot, ILogger logger)
    {
        var entries = new List<(string state, int keyNum, string key, string location)>();

        foreach (var jobDir in TaskStorageLayout.EnumerateJobDirs(projectRoot))
        {
            var jobJsonPath = Path.Combine(jobDir, "task.json");
            if (!File.Exists(jobJsonPath)) continue;

            Dictionary<string, JsonElement>? doc;
            try
            {
                doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    File.ReadAllText(jobJsonPath), ReadOpts);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "task-index: unreadable task.json at {Dir}; skipped", jobDir);
                continue;
            }
            if (doc is null) continue;

            var state = GetString(doc, "state");
            if (string.IsNullOrWhiteSpace(state)) continue;
            var key = GetString(doc, "key");

            var bucketName = Path.GetFileName(Path.GetDirectoryName(jobDir)!);
            var folderName = Path.GetFileName(jobDir);
            var location = $"{bucketName}/{folderName}";

            TaskStorageLayout.TryParseKeyNumber(key, out var keyNum);
            entries.Add((state!, keyNum, key ?? "", location));
        }

        var byState = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var byKey = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var e in entries
                     .OrderBy(e => LaneOrder(e.state))
                     .ThenBy(e => e.keyNum)
                     .ThenBy(e => e.location, StringComparer.Ordinal))
        {
            if (!byState.TryGetValue(e.state, out var list))
                byState[e.state] = list = new List<string>();
            list.Add(e.location);
            if (!string.IsNullOrWhiteSpace(e.key))
                byKey[e.key] = e.location;
        }

        Write(projectRoot, byState, byKey, logger);
        return new IndexSnapshot(byState, byKey);
    }

    public static void Write(
        string projectRoot,
        IReadOnlyDictionary<string, List<string>> byState,
        IReadOnlyDictionary<string, string> byKey,
        ILogger logger)
    {
        Directory.CreateDirectory(TaskStorageLayout.IndexRoot(projectRoot));
        WriteAtomic(ByStatePath(projectRoot), JsonSerializer.Serialize(byState, WriteOpts), logger);
        WriteAtomic(ByKeyPath(projectRoot), JsonSerializer.Serialize(byKey, WriteOpts), logger);
    }

    public static void WriteByState(
        string projectRoot,
        IReadOnlyDictionary<string, List<string>> byState,
        ILogger logger)
    {
        Directory.CreateDirectory(TaskStorageLayout.IndexRoot(projectRoot));
        WriteAtomic(ByStatePath(projectRoot), JsonSerializer.Serialize(byState, WriteOpts), logger);
    }

    public static void Upsert(
        string projectRoot,
        string key,
        string location,
        string state,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(location))
            return;

        var byState = ReadByState(projectRoot);
        var byKey = ReadByKey(projectRoot);

        foreach (var list in byState.Values)
            list.RemoveAll(l => string.Equals(l, location, StringComparison.Ordinal));
        foreach (var emptied in byState.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
            byState.Remove(emptied);
        if (!byState.TryGetValue(state, out var stateEntries))
            byState[state] = stateEntries = new List<string>();
        stateEntries.Add(location);
        byKey[key] = location;

        Write(projectRoot, byState, byKey, logger);
    }

    public static Dictionary<string, List<string>> ReadByState(string projectRoot) =>
        ReadMap<List<string>>(ByStatePath(projectRoot));

    public static Dictionary<string, string> ReadByKey(string projectRoot) =>
        ReadMap<string>(ByKeyPath(projectRoot));

    private static void WriteAtomic(string path, string content, ILogger logger)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, content);
            // File.Move(overwrite) is the atomic replace on Windows
            // (MoveFileEx REPLACE_EXISTING) and POSIX (rename), so a reader
            // never observes a partial index file.
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "task-index: failed to write {Path}", path);
            throw;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception __ex) { SilentCatch.Note(__ex, "TaskLayoutIndex: best-effort cleanup"); /* best-effort cleanup */ }
        }
    }

    private static int LaneOrder(string state)
    {
        var i = Array.IndexOf(TaskStates.All, state);
        return i < 0 ? int.MaxValue : i;
    }

    private static Dictionary<string, T> ReadMap<T>(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, T>(StringComparer.Ordinal);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<Dictionary<string, T>>(stream, ReadOpts)
               ?? new Dictionary<string, T>(StringComparer.Ordinal);
    }

    private static string? GetString(Dictionary<string, JsonElement> doc, string field) =>
        doc.TryGetValue(field, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    public readonly record struct IndexSnapshot(
        IReadOnlyDictionary<string, List<string>> ByState,
        IReadOnlyDictionary<string, string> ByKey);
}
