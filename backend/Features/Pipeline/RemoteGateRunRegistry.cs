using System.Text.Json;
using System.Diagnostics;

namespace AgentStudio.Pipeline;

internal sealed record RemoteGateRunRecord(
    string RunId, string Host, string Root, DateTimeOffset StartedAtUtc,
    string OwnerProcessInstance, string InstallationId,
    int OwnerProcessId = 0, long OwnerProcessStartTicks = 0,
    string? Project = null, string? WatchPath = null);

/// <summary>Durable ownership ledger for SSH workers that can outlive Studio.</summary>
internal sealed class RemoteGateRunRegistry
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Locks = new();
    private static readonly string CurrentProcessInstance = Guid.NewGuid().ToString("N");
    private readonly string _root;
    private readonly TimeProvider _time;
    internal string InstallationId { get; }
    internal string ProcessInstance { get; }
    internal int ProcessId { get; }
    internal long ProcessStartTicks { get; }

    internal RemoteGateRunRegistry(string root, TimeProvider time, string? installationId = null,
        string? processInstance = null, int? processId = null, long? processStartTicks = null)
    {
        _root = root;
        _time = time;
        Directory.CreateDirectory(root);
        InstallationId = installationId ?? LoadOrCreateInstallationId(root);
        ProcessInstance = processInstance ?? CurrentProcessInstance;
        using var process = Process.GetCurrentProcess();
        ProcessId = processId ?? process.Id;
        ProcessStartTicks = processStartTicks ?? process.StartTime.ToUniversalTime().Ticks;
    }

    internal RemoteGateRunRecord Record(string runId, string host, string remoteRoot,
        string? project = null, string? watchPath = null)
    {
        var record = new RemoteGateRunRecord(runId, host, remoteRoot, _time.GetUtcNow(),
            ProcessInstance, InstallationId, ProcessId, ProcessStartTicks, project, watchPath);
        Write(record);
        return record;
    }

    internal IReadOnlyList<RemoteGateRunRecord> ReadAll()
    {
        lock (Locks.GetOrAdd(_root, _ => new object()))
        {
            if (!Directory.Exists(_root)) return [];
            var records = new List<RemoteGateRunRecord>();
            foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<RemoteGateRunRecord>(File.ReadAllText(path));
                    if (record is not null && RemoteGateTransport.IsRunFolderName(record.RunId))
                        records.Add(record);
                }
                catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
                {
                    AgentStudio.Diagnostics.SilentCatch.Note(exception, "remote gate run registry read");
                }
            }
            return records;
        }
    }

    internal void Remove(string runId)
    {
        lock (Locks.GetOrAdd(_root, _ => new object()))
        {
            var path = Path.Combine(_root, runId + ".json");
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private void Write(RemoteGateRunRecord record)
    {
        lock (Locks.GetOrAdd(_root, _ => new object()))
        {
            var path = Path.Combine(_root, record.RunId + ".json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(record));
            File.Move(temporary, path, overwrite: true);
        }
    }

    private static string LoadOrCreateInstallationId(string root)
    {
        var path = Path.Combine(root, "installation-id");
        lock (Locks.GetOrAdd(path, _ => new object()))
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length is >= 16 and <= 64 && existing.All(Uri.IsHexDigit)) return existing;
            }
            var id = Guid.NewGuid().ToString("N");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, id);
            File.Move(temporary, path, overwrite: true);
            return id;
        }
    }
}

internal sealed class RemoteGateOrphanSweepService(RemoteGateTransport transport,
    ILogger<RemoteGateOrphanSweepService> logger, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await transport.CancelOrphanedRunsAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await transport.CancelOrphanedRunsAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning(exception, "remote_gate_orphan_sweep_failed"); }
        }
    }
}
