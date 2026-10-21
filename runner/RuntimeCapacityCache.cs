using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Host-local last-known-good cache for the server-owned runtime capacity.
/// It is neither an editable policy source nor task authority. A valid Task
/// Server response always replaces it; bootstrap options win only before this
/// host has received any central configuration.
/// </summary>
internal sealed class RuntimeCapacityCache
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();

    public RuntimeCapacityCache(string runnerStateRoot)
    {
        _path = Path.Combine(
            RunnerStateRootGuard.Validate(runnerStateRoot),
            "configuration",
            "runtime-capacity.json");
    }

    public RuntimeCapacityCacheEntry? Load(string hostId, Action<string>? warn = null)
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return null;
            try
            {
                var entry = JsonSerializer.Deserialize<RuntimeCapacityCacheEntry>(
                    File.ReadAllText(_path),
                    Json);
                if (IsValid(entry, hostId)) return entry;
                warn?.Invoke("Discarding runtime capacity cache with a foreign host or invalid server version.");
                return null;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                warn?.Invoke("Discarding unreadable runtime capacity cache.");
                return null;
            }
        }
    }

    public void Save(RuntimeCapacitySettingsDto capacity, DateTime adoptedAt)
    {
        if (!IsValid(new RuntimeCapacityCacheEntry(capacity, adoptedAt), capacity.HostId))
            return;
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                var temporary = _path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
                try
                {
                    using (var stream = new FileStream(
                               temporary,
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.None,
                               4096,
                               FileOptions.WriteThrough))
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.Write(JsonSerializer.Serialize(
                            new RuntimeCapacityCacheEntry(capacity, adoptedAt),
                            Json));
                        writer.Flush();
                        stream.Flush(flushToDisk: true);
                    }
                    File.Move(temporary, _path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            catch (IOException)
            {
                // The in-memory central value remains effective. A cache write
                // failure must not stop already admitted work or claim polling.
            }
            catch (UnauthorizedAccessException)
            {
                // Bootstrap remains available on a read-only state volume.
            }
        }
    }

    private static bool IsValid(RuntimeCapacityCacheEntry? entry, string hostId)
        => entry is
           {
               Capacity.Version: > 0,
               Capacity.MaxParallelism: >= 1 and <= 256,
               Capacity.TargetLoadPercent: >= 50 and <= 95,
           }
           && string.Equals(
               entry.Capacity.HostId,
               hostId,
               StringComparison.OrdinalIgnoreCase)
           && entry.Capacity.RampStrategy is "conservative" or "balanced" or "aggressive";
}

internal sealed record RuntimeCapacityCacheEntry(
    RuntimeCapacitySettingsDto Capacity,
    DateTime AdoptedAt);
