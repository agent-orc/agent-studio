using System.Text.Json;
using AgentStudio.Persistence;

namespace AgentStudio.Tasks;

/// <summary>
/// Shared low-level helper for the job services in this folder. All four
/// (<see cref="TaskScannerService"/>, <see cref="TaskStateMachine"/>,
/// <see cref="TaskMutationService"/>, <see cref="TaskSessionLog"/>) need to
/// rewrite a single field in a job's <c>task.json</c> while preserving the
/// original key order; centralising that read-modify-write avoids each
/// service growing its own near-identical copy and drifting on details
/// like indent, encoding, or legacy-field handling. Rewrites replace the file
/// atomically so the watcher/scanner never observes the truncate window of a
/// direct <c>File.WriteAllText</c>.
/// </summary>
internal static class TaskJsonFile
{
    internal static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly IAtomicJsonFileWriter FileWriter = new AtomicJsonFileWriter();

    /// <summary>
    /// Reads <c>task.json</c>, replaces or adds a single top-level field, writes
    /// back preserving the existing field order.
    /// </summary>
    internal static void UpdateField(string jobDir, string fieldName, object value, ILogger logger)
    {
        if (!File.Exists(Path.Combine(jobDir, "task.json"))) return;
        try
        {
            UpdateFieldOrThrow(jobDir, fieldName, value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update field {Field} in task.json at {Dir}", fieldName, jobDir);
        }
    }

    /// <summary>
    /// Strict counterpart used by multi-file mutations that must roll back
    /// when any write fails. Unlike <see cref="UpdateField"/>, this method
    /// never turns a failed write into an apparent success.
    /// </summary>
    internal static void UpdateFieldOrThrow(string jobDir, string fieldName, object value)
    {
        var jobJsonPath = Path.Combine(jobDir, "task.json");
        if (!File.Exists(jobJsonPath))
            throw new FileNotFoundException("task.json was not found", jobJsonPath);

        var json = File.ReadAllText(jobJsonPath);
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, ReadOpts)
                  ?? new Dictionary<string, JsonElement>();

        var updated = new Dictionary<string, object>();
        var inserted = false;
        foreach (var kv in doc)
        {
            if (kv.Key == fieldName)
            {
                updated[fieldName] = value;
                inserted = true;
            }
            else
            {
                updated[kv.Key] = kv.Value;
            }
        }
        if (!inserted) updated[fieldName] = value;

        Write(jobJsonPath, updated);
    }

    /// <summary>
    /// Reads a top-level string field. A missing field is a successful read
    /// with a <see langword="null"/> value; a missing or unreadable file is a
    /// failed read so callers can preserve their existing write path.
    /// </summary>
    internal static bool TryReadStringField(
        string jobDir,
        string fieldName,
        ILogger logger,
        out string? value)
    {
        value = null;
        var jobJsonPath = Path.Combine(jobDir, "task.json");
        if (!File.Exists(jobJsonPath)) return false;

        try
        {
            var json = File.ReadAllText(jobJsonPath);
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, ReadOpts)
                      ?? new Dictionary<string, JsonElement>();
            if (!doc.TryGetValue(fieldName, out var field) || field.ValueKind == JsonValueKind.Null)
                return true;
            if (field.ValueKind != JsonValueKind.String) return false;
            value = field.GetString();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read field {Field} from task.json at {Dir}", fieldName, jobDir);
            return false;
        }
    }

    /// <summary>
    /// Replaces or adds multiple top-level fields in one atomic rewrite.
    /// </summary>
    internal static void UpdateFields(
        string jobDir,
        IReadOnlyDictionary<string, object> values,
        ILogger logger)
    {
        var jobJsonPath = Path.Combine(jobDir, "task.json");
        if (!File.Exists(jobJsonPath)) return;

        try
        {
            var json = File.ReadAllText(jobJsonPath);
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, ReadOpts)
                      ?? new Dictionary<string, JsonElement>();
            var updated = new Dictionary<string, object>();
            foreach (var kv in doc)
            {
                updated[kv.Key] = values.TryGetValue(kv.Key, out var replacement)
                    ? replacement
                    : kv.Value;
            }
            foreach (var kv in values)
            {
                if (!updated.ContainsKey(kv.Key)) updated[kv.Key] = kv.Value;
            }

            Write(jobJsonPath, updated);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update fields in task.json at {Dir}", jobDir);
        }
    }

    /// <summary>
    /// Strict multi-field counterpart for a mutation that must not report
    /// success after a failed atomic rewrite.
    /// </summary>
    internal static void UpdateFieldsOrThrow(
        string jobDir,
        IReadOnlyDictionary<string, object> values)
    {
        var jobJsonPath = Path.Combine(jobDir, "task.json");
        if (!File.Exists(jobJsonPath))
            throw new FileNotFoundException("task.json was not found", jobJsonPath);

        var json = File.ReadAllText(jobJsonPath);
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, ReadOpts)
                  ?? new Dictionary<string, JsonElement>();
        var updated = new Dictionary<string, object>();
        foreach (var kv in doc)
        {
            updated[kv.Key] = values.TryGetValue(kv.Key, out var replacement)
                ? replacement
                : kv.Value;
        }
        foreach (var kv in values)
        {
            if (!updated.ContainsKey(kv.Key)) updated[kv.Key] = kv.Value;
        }

        Write(jobJsonPath, updated);
    }

    /// <summary>
    /// Remove a top-level key from <c>task.json</c> if present. No-op when the
    /// file or key is absent. Used to clean up obsolete fields after a feature
    /// is removed (e.g. the operator-override <c>excludedCommits</c> array).
    /// </summary>
    internal static bool RemoveField(string jobDir, string fieldName, ILogger logger)
    {
        var jobJsonPath = Path.Combine(jobDir, "task.json");
        if (!File.Exists(jobJsonPath)) return false;

        try
        {
            var json = File.ReadAllText(jobJsonPath);
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, ReadOpts)
                      ?? new Dictionary<string, JsonElement>();
            if (!doc.ContainsKey(fieldName)) return false;

            var updated = new Dictionary<string, object>();
            foreach (var kv in doc)
            {
                if (kv.Key == fieldName) continue;
                updated[kv.Key] = kv.Value;
            }

            Write(jobJsonPath, updated);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remove field {Field} from task.json at {Dir}", fieldName, jobDir);
            return false;
        }
    }

    /// <summary>
    /// Like <see cref="UpdateField"/> but also drops the legacy <c>priority</c>
    /// key and guarantees an <c>order</c> entry in the output. Used by
    /// <see cref="TaskStateMachine.ReorderJobs"/>.
    /// </summary>
    internal static void UpdateOrder(string jobDir, int order, ILogger logger)
    {
        var jobJsonPath = Path.Combine(jobDir, "task.json");
        if (!File.Exists(jobJsonPath)) return;

        try
        {
            var json = File.ReadAllText(jobJsonPath);
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, ReadOpts)
                      ?? new Dictionary<string, JsonElement>();

            var updated = new Dictionary<string, object>();
            foreach (var kv in doc)
            {
                if (kv.Key == "order") updated["order"] = order;
                else if (kv.Key == "priority") continue; // drop legacy priority
                else updated[kv.Key] = kv.Value;
            }
            if (!updated.ContainsKey("order")) updated["order"] = order;

            Write(jobJsonPath, updated);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update order in task.json at {Dir}", jobDir);
        }
    }

    private static void Write(string path, Dictionary<string, object> value) =>
        FileWriter.Write(path, JsonSerializer.Serialize(value, WriteOpts));
}
