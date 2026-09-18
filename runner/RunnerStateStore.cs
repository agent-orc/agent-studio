using System.Text.Json;

namespace AgentRunner;

/// <summary>
/// Durable host-side description of one claimed attempt. The Task Server remains
/// the task authority; this file is only the execution evidence a replacement
/// daemon needs to prove that the exact process still exists before renewing its
/// fenced lease.
/// </summary>
public sealed record PersistedRunnerSlot(
    string TaskKey,
    string AttemptId,
    RunLeaseInfoDto Lease,
    string? RunId,
    string? LeaseInstanceId,
    string? ProjectId,
    string? RepositoryUrl,
    string? DefaultBranch,
    string? TaskKind,
    string WorktreePath,
    string WorkerDirectory,
    int? ProcessId,
    DateTime? ProcessStartedAtUtc,
    long LastOutputSequence,
    string Phase,
    DateTime UpdatedAtUtc,
    // Commit the task worktree started from, recorded once the workspace has been
    // prepared. It is the Result-Envelope's BaseSha and only the preparing process
    // knows it, so a replacement daemon that reattaches to a detached worker can
    // only complete with a full envelope trio if the value survived here. Optional
    // for persistence compatibility: state written before this field simply loads
    // as null and behaves exactly as it did before.
    string? BaseSha = null,
    // The execution spec includes the server-composed mode and enrichment
    // framing. Persisting the one spec keeps daemon replacement deterministic.
    RunSpecDto? RunSpec = null,
    // AGT-2869: set while a finalization waits for a Task Server that is
    // restarting. It carries the retry bookkeeping and the delivery this
    // attempt already secured, so the daemon's own poll loop can re-drive the
    // slot without a daemon restart. Optional for persistence compatibility:
    // state written before this field loads as null and behaves as before.
    PendingFinalization? Finalization = null,
    // Explicitly records that the detached worker has written its durable
    // result and the runner has entered finalization. Transport failures may
    // only create Finalization retry state after this marker is persisted.
    // Optional so startup reconciliation can adopt slots written by an older
    // runner exactly as it did before this guard existed.
    string? FinalizationStage = null);

/// <summary>Atomic JSON persistence under RUNNER_STATE_DIR.</summary>
public sealed class RunnerStateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();

    public RunnerStateStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public PersistedRunnerSlot Create(
        string taskKey,
        RunLeaseInfoDto lease,
        string worktreePath,
        string? runId = null,
        string? leaseInstanceId = null,
        string? projectId = null,
        string? repositoryUrl = null,
        string? defaultBranch = null,
        string? taskKind = null,
        RunSpecDto? runSpec = null)
    {
        var workerDirectory = Path.Combine(Root, GitWorkspace.SafeSegment(lease.LeaseId));
        Directory.CreateDirectory(workerDirectory);
        var slot = new PersistedRunnerSlot(
            taskKey,
            runId ?? lease.LeaseId,
            lease,
            runId,
            leaseInstanceId,
            projectId,
            repositoryUrl,
            defaultBranch,
            taskKind,
            Path.GetFullPath(worktreePath),
            workerDirectory,
            null,
            null,
            0,
            "claimed",
            DateTime.UtcNow,
            RunSpec: runSpec);
        Save(slot);
        return slot;
    }

    public PersistedRunnerSlot Save(PersistedRunnerSlot slot)
    {
        slot = slot with { UpdatedAtUtc = DateTime.UtcNow };
        var path = StatePath(slot.TaskKey);
        var temp = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        lock (_gate)
        {
            using (var stream = new FileStream(
                       temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(JsonSerializer.Serialize(slot, Json));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        return slot;
    }

    public IReadOnlyList<PersistedRunnerSlot> LoadAll()
    {
        lock (_gate)
        {
            var slots = new List<PersistedRunnerSlot>();
            foreach (var path in Directory.EnumerateFiles(Root, "*.slot.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                try
                {
                    var slot = JsonSerializer.Deserialize<PersistedRunnerSlot>(File.ReadAllText(path), Json);
                    if (slot is not null) slots.Add(slot);
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    throw new InvalidDataException($"Runner state is unreadable: {path}", ex);
                }
            }
            return slots;
        }
    }

    public void Delete(PersistedRunnerSlot slot)
    {
        lock (_gate)
        {
            var path = StatePath(slot.TaskKey);
            if (File.Exists(path)) File.Delete(path);
        }
        try
        {
            if (Directory.Exists(slot.WorkerDirectory)) Directory.Delete(slot.WorkerDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A just-exited worker may still have a file handle open. The small
            // attempt directory is harmless and is reused/removed on recovery.
        }
    }

    /// <summary>All writes are atomic and synchronous, so flush is an explicit lifecycle marker.</summary>
    public void Flush() { }

    private string StatePath(string taskKey)
        => Path.Combine(Root, $"{GitWorkspace.SafeSegment(taskKey)}.slot.json");
}
