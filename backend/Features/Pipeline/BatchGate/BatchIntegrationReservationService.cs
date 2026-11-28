using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Pipeline;

public sealed record PendingIntegrationPush(string JobId, string? ApprovedSha,
    string? JobFolderPath, string? RepositoryPath, string? IntegrationBranch,
    string? WatchPath, DateTimeOffset SinceUtc, string? Project = null);

public sealed record PendingIntegrationPushWait(string Reason, string JobId,
    string? ApprovedSha, DateTimeOffset SinceUtc, bool AgeAlert, double AgeMinutes);

/// <summary>Who takes or waits for the project reservation (AGT-3882).</summary>
public static class IntegrationReservationKinds
{
    public const string BatchFlight = "batch-flight";
    public const string IntegrationPush = "integration-push";
    public const string PerTaskIntegration = "per-task-integration";
    public const string PerTaskFallbackIntegration = "per-task-fallback-integration";
    public const string Reconciliation = "push-reconciliation";
    public const string Unnamed = "unnamed";

    /// <summary>A per-task integration does not start while a batch flight reserves the project.</summary>
    public static bool YieldsToBatch(string kind)
        => kind is PerTaskIntegration or PerTaskFallbackIntegration;
}

/// <summary>A job that holds or waits for the project reservation; <see cref="SinceUtc"/> is when it took or asked for it.</summary>
public sealed record IntegrationReservationJob(string Kind, string? TaskKey, DateTimeOffset SinceUtc);

/// <summary>
/// What a reserving batch flight waits for (AGT-3882): the job that holds the
/// reservation, jobs queued before it, the per-task integrations held back
/// until it has its reservation, and approved pushes that must reach origin.
/// </summary>
public sealed record BatchReservationWait(string? BatchId, DateTimeOffset ReservingSinceUtc,
    double WaitMinutes, IntegrationReservationJob? Holder,
    IReadOnlyList<IntegrationReservationJob> QueuedAhead,
    IReadOnlyList<IntegrationReservationJob> HeldBack,
    IReadOnlyList<PendingIntegrationPushWait> PendingPushes, string Reason);

/// <summary>Serializes code integration and batch execution per project.</summary>
public sealed class BatchIntegrationReservationService
{
    // Direct integration mutates the branch before its gate verdict and must
    // retain this reservation through rollback. Bound that gate so formed
    // batches cannot wait behind one card for the project gate's full budget.
    public static readonly TimeSpan IntegrationGateLeaseCap = TimeSpan.FromMinutes(27);
    public static readonly TimeSpan IntegrationGateQueueCap = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan IntegrationGateRunCap = IntegrationGateLeaseCap - IntegrationGateQueueCap;

    public static TimeSpan GateBudget(TimeSpan requested, bool reservationHeld)
        => reservationHeld ? TimeSpan.FromTicks(Math.Min(requested.Ticks,
            IntegrationGateRunCap.Ticks)) : requested;

    private readonly string? _root;
    private readonly TimeProvider _time;
    private readonly TimeSpan _alertAfter;
    private readonly ConcurrentDictionary<string, ProjectGate> _projects =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingPushes> _pushes =
        new(StringComparer.OrdinalIgnoreCase);

    public BatchIntegrationReservationService(string? root = null,
        TimeProvider? timeProvider = null, TimeSpan? alertAfter = null)
    {
        _root = root;
        _time = timeProvider ?? TimeProvider.System;
        _alertAfter = alertAfter ?? TimeSpan.FromMinutes(3);
        LoadPersistedProjects();
    }

    // Start-up preloads must never stop the host: an unreadable reservation
    // store leaves the project unloaded, and ForProject loads it lazily on first
    // use, where the error surfaces to the caller.
    private void LoadPersistedProjects()
    {
        if (_root is null) return;
        try
        {
            if (!Directory.Exists(_root)) return;
            foreach (var folder in Directory.EnumerateDirectories(_root))
            foreach (var path in Directory.EnumerateFiles(folder, "*.pending"))
            {
                var marker = ReadMarker(path);
                if (!string.IsNullOrWhiteSpace(marker?.Project)
                    && string.Equals(folder, ProjectDirectory(marker.Project),
                        StringComparison.OrdinalIgnoreCase))
                    TryPreload(marker.Project);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AgentStudio.Diagnostics.SilentCatch.Warn(ex, "preload persisted push markers at start-up");
        }
    }

    public void LoadPersistedProjects(IEnumerable<string> projects)
    {
        foreach (var project in projects)
            TryPreload(project);
    }

    private void TryPreload(string project)
    {
        try { _ = ForProject(project); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AgentStudio.Diagnostics.SilentCatch.Warn(ex, $"preload push markers for project {project}");
        }
    }

    private sealed class PendingPushes(IEnumerable<KeyValuePair<string, PendingIntegrationPush>> jobs)
    {
        public Dictionary<string, PendingIntegrationPush> Jobs { get; } =
            new(jobs, StringComparer.Ordinal);
        public TaskCompletionSource Cleared { get; set; } = Completion(jobs.Any());

        private static TaskCompletionSource Completion(bool pending)
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending) source.SetResult();
            return source;
        }
    }

    private PendingPushes ForProject(string project)
        => _pushes.GetOrAdd(project, name => new PendingPushes(
            _root is null || !Directory.Exists(ProjectDirectory(name))
                ? [] : Directory.EnumerateFiles(ProjectDirectory(name), "*.pending")
                    .Select(ReadMarker).Where(item => item is not null)
                    .Select(item => new KeyValuePair<string, PendingIntegrationPush>(
                        Hash(PushKey(item!.JobId, item.ApprovedSha)), item)).ToArray()));

    private static PendingIntegrationPush? ReadMarker(string path)
    {
        try
        {
            var value = File.ReadAllText(path);
            if (value.StartsWith('{'))
                return JsonSerializer.Deserialize<PendingIntegrationPush>(value);
            var parts = value.Split('\n', 2);
            return new PendingIntegrationPush(parts[0], parts.Length == 2 ? parts[1] : null,
                null, null, null, null, File.GetLastWriteTimeUtc(path));
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private string ProjectDirectory(string project)
        => Path.Combine(_root!, Hash(project.ToUpperInvariant()));

    private string PendingPath(string project, string pushKey)
        => Path.Combine(ProjectDirectory(project), Hash(pushKey) + ".pending");

    private static string PushKey(string jobId, string? approvedSha)
        => jobId + "\n" + (approvedSha ?? string.Empty);

    private static string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// Takes the project reservation for an unnamed job that does not yield to
    /// a reserving batch. Production paths name their job with the overload
    /// that takes a kind.
    /// </summary>
    public Task<IDisposable> AcquireAsync(string project, CancellationToken ct)
        => AcquireAsync(project, IntegrationReservationKinds.Unnamed, null, ct);

    /// <summary>
    /// Waits for the project reservation (AGT-3882). An integration push goes
    /// first, then a reserving batch flight; a per-task integration starts only
    /// while no batch flight reserves the project, so a stream of per-task
    /// gates cannot hold a formed batch back. A running holder always finishes.
    /// </summary>
    public Task<IDisposable> AcquireAsync(string project, string kind, string? taskKey,
        CancellationToken ct)
    {
        var gate = GateFor(project);
        var waiter = new Waiter(new IntegrationReservationJob(kind, taskKey, _time.GetUtcNow()));
        lock (gate)
        {
            gate.Waiters.Add(waiter);
            Pump(gate);
        }
        if (waiter.Granted.Task.IsCompleted || !ct.CanBeCanceled) return waiter.Granted.Task;
        var registration = ct.Register(() =>
        {
            lock (gate)
                if (gate.Waiters.Remove(waiter))
                {
                    waiter.Granted.TrySetCanceled(ct);
                    Pump(gate);
                }
        });
        _ = waiter.Granted.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return waiter.Granted.Task;
    }

    public IDisposable? TryAcquire(string project)
    {
        var gate = GateFor(project);
        lock (gate)
        {
            if (gate.Holder is not null) return null;
            var job = new IntegrationReservationJob(IntegrationReservationKinds.Reconciliation, null,
                _time.GetUtcNow());
            gate.Holder = job;
            return new Grant(this, gate, job);
        }
    }

    public Task<IDisposable> AcquireBatchAsync(string project, CancellationToken ct)
        => AcquireBatchAsync(project, null, ct);

    public async Task<IDisposable> AcquireBatchAsync(string project,
        Func<PendingIntegrationPush, bool>? isStale, CancellationToken ct,
        Action? beginPass = null, string? batchId = null)
    {
        var gate = GateFor(project);
        var reserving = new IntegrationReservationJob(IntegrationReservationKinds.BatchFlight, batchId,
            _time.GetUtcNow());
        // From here until the flight holds its reservation no per-task
        // integration of the project starts.
        lock (gate) gate.ReservingBatches.Add(reserving);
        try
        {
            while (true)
            {
                var grant = await AcquireAsync(project, IntegrationReservationKinds.BatchFlight, batchId, ct)
                    .ConfigureAwait(false);
                Task wait;
                try
                {
                    beginPass?.Invoke();
                    Reconcile(project, isStale);
                    var pending = ForProject(project);
                    lock (pending)
                    {
                        if (pending.Jobs.Count == 0) return grant;
                        wait = pending.Cleared.Task;
                    }
                }
                catch
                {
                    grant.Dispose();
                    throw;
                }
                grant.Dispose();
                await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(30), ct))
                    .ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            lock (gate)
            {
                gate.ReservingBatches.Remove(reserving);
                Pump(gate);
            }
        }
    }

    /// <summary>
    /// What a reserving batch flight of <paramref name="project"/> waits for,
    /// or null while no flight reserves the project (AGT-3882).
    /// </summary>
    public BatchReservationWait? GetBatchWait(string project)
    {
        var gate = GateFor(project);
        IntegrationReservationJob reserving;
        IntegrationReservationJob? holder;
        IntegrationReservationJob[] queued;
        lock (gate)
        {
            if (gate.ReservingBatches.Count == 0) return null;
            reserving = gate.ReservingBatches.MinBy(job => job.SinceUtc)!;
            holder = gate.Holder;
            queued = gate.Waiters.Select(waiter => waiter.Job).ToArray();
        }
        var heldBack = queued.Where(job => IntegrationReservationKinds.YieldsToBatch(job.Kind)).ToArray();
        var ahead = queued.Where(job => !IntegrationReservationKinds.YieldsToBatch(job.Kind)
            && job.Kind != IntegrationReservationKinds.BatchFlight).ToArray();
        var pushes = GetWaits(project);
        var now = _time.GetUtcNow();
        var parts = new List<string>();
        if (holder is not null && holder.Kind != IntegrationReservationKinds.BatchFlight)
            parts.Add($"{Describe(holder)} holds the project reservation since {holder.SinceUtc:O}");
        if (ahead.Length > 0)
            parts.Add($"{string.Join(", ", ahead.Select(Describe))} queued first");
        if (pushes.Count > 0)
            parts.Add($"{pushes.Count} approved integration push(es) must reach origin, oldest "
                + $"{pushes[0].JobId} since {pushes[0].SinceUtc:O}");
        if (parts.Count == 0) parts.Add("the reservation is being granted");
        var reason = "batch flight reserving since " + reserving.SinceUtc.ToString("O") + ": "
            + string.Join("; ", parts)
            + (heldBack.Length == 0 ? string.Empty
                : $"; {heldBack.Length} per-task integration(s) wait until the flight has its reservation");
        return new BatchReservationWait(reserving.TaskKey, reserving.SinceUtc,
            Math.Max(0, (now - reserving.SinceUtc).TotalMinutes),
            holder is { Kind: IntegrationReservationKinds.BatchFlight } ? null : holder,
            ahead, heldBack, pushes, reason);
    }

    /// <summary>
    /// The per-task integration that holds the reservation of
    /// <paramref name="project"/>, or null when none does.
    /// </summary>
    public IntegrationReservationJob? PerTaskHolder(string project)
    {
        var gate = GateFor(project);
        lock (gate)
            return gate.Holder is { } holder && IntegrationReservationKinds.YieldsToBatch(holder.Kind)
                ? holder : null;
    }

    internal IntegrationReservationJob? ReadHolder(string project)
    {
        if (!_projects.TryGetValue(project, out var gate)) return null;
        lock (gate) return gate.Holder;
    }

    private static string Describe(IntegrationReservationJob job)
        => job.TaskKey is null ? job.Kind : $"{job.Kind} {job.TaskKey}";

    private ProjectGate GateFor(string project) => _projects.GetOrAdd(project, _ => new ProjectGate());

    // Callers hold the gate lock.
    private void Pump(ProjectGate gate)
    {
        if (gate.Holder is not null) return;
        var batchReserving = gate.ReservingBatches.Count > 0;
        Waiter? next = null;
        foreach (var waiter in gate.Waiters)
        {
            if (batchReserving && IntegrationReservationKinds.YieldsToBatch(waiter.Job.Kind)) continue;
            if (next is null || Rank(waiter.Job.Kind) < Rank(next.Job.Kind)) next = waiter;
        }
        if (next is null) return;
        gate.Waiters.Remove(next);
        var granted = next.Job with { SinceUtc = _time.GetUtcNow() };
        gate.Holder = granted;
        if (!next.Granted.TrySetResult(new Grant(this, gate, granted)))
        {
            gate.Holder = null;
            Pump(gate);
        }
    }

    // A push clears what the batch waits for; the batch goes before per-task work.
    private static int Rank(string kind) => kind switch
    {
        IntegrationReservationKinds.IntegrationPush => 0,
        IntegrationReservationKinds.BatchFlight => 1,
        _ => 2,
    };

    private void Release(ProjectGate gate, IntegrationReservationJob job)
    {
        lock (gate)
        {
            if (!ReferenceEquals(gate.Holder, job)) return;
            gate.Holder = null;
            Pump(gate);
        }
    }

    private sealed class ProjectGate
    {
        public IntegrationReservationJob? Holder;
        public readonly List<Waiter> Waiters = [];
        public readonly List<IntegrationReservationJob> ReservingBatches = [];
    }

    private sealed class Waiter(IntegrationReservationJob job)
    {
        public IntegrationReservationJob Job { get; } = job;
        public TaskCompletionSource<IDisposable> Granted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Reconcile(string project, Func<PendingIntegrationPush, bool>? isStale)
    {
        if (isStale is null) return;
        var pending = ForProject(project);
        KeyValuePair<string, PendingIntegrationPush>[] snapshot;
        lock (pending) snapshot = pending.Jobs.ToArray();
        foreach (var (key, marker) in snapshot)
        {
            if (!isStale(marker)) continue;
            lock (pending)
                if (pending.Jobs.TryGetValue(key, out var current) && current == marker)
                    Remove(project, pending, key, marker);
        }
    }

    public PendingIntegrationPushWait? GetWait(string project)
        => GetWaits(project).FirstOrDefault();

    /// <summary>The age at which a pending push raises its age alert (<c>BatchGate:PendingPushAlertMinutes</c>).</summary>
    public TimeSpan AlertAfter => _alertAfter;

    public IReadOnlyList<PendingIntegrationPushWait> GetWaits(string project)
    {
        var pending = ForProject(project);
        lock (pending)
        {
            var now = _time.GetUtcNow();
            return pending.Jobs.Values.OrderBy(item => item.SinceUtc).Select(marker =>
            {
                var age = now - marker.SinceUtc;
                var reason = $"waiting on approved integration push {marker.JobId}@{marker.ApprovedSha} "
                    + $"since {marker.SinceUtc:O}; the push worker or restart recovery must publish it to origin";
                return new PendingIntegrationPushWait(reason, marker.JobId, marker.ApprovedSha,
                    marker.SinceUtc, age >= _alertAfter, Math.Max(0, age.TotalMinutes));
            }).ToArray();
        }
    }

    public IReadOnlyList<string> ProjectsWithPending()
        => _pushes.Where(item =>
        {
            lock (item.Value) return item.Value.Jobs.Count > 0;
        }).Select(item => item.Key).ToArray();

    public IReadOnlyList<PendingIntegrationPush> PendingForProject(string project)
    {
        var pending = ForProject(project);
        lock (pending) return pending.Jobs.Values.OrderBy(item => item.SinceUtc).ToArray();
    }

    public void MarkPushPending(string project, string jobId, string? approvedSha,
        string? jobFolderPath = null, string? repositoryPath = null,
        string? integrationBranch = null, string? watchPath = null)
    {
        var pushKey = PushKey(jobId, approvedSha);
        var key = Hash(pushKey);
        var pending = ForProject(project);
        lock (pending)
        {
            foreach (var (oldKey, old) in pending.Jobs.ToArray())
                if (old.JobId == jobId && oldKey != key)
                    Remove(project, pending, oldKey, old);
            if (pending.Jobs.ContainsKey(key)) return;
            var marker = new PendingIntegrationPush(jobId, approvedSha,
                jobFolderPath, repositoryPath, integrationBranch, watchPath,
                _time.GetUtcNow(), project);
            if (_root is not null)
            {
                var path = PendingPath(project, pushKey);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(marker));
            }
            if (pending.Jobs.Count == 0)
                pending.Cleared = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Jobs.Add(key, marker);
        }
    }

    public void MarkPushVerified(string project, string jobId, string? approvedSha)
    {
        var pending = ForProject(project);
        lock (pending)
        {
            var key = Hash(PushKey(jobId, approvedSha));
            if (pending.Jobs.TryGetValue(key, out var marker))
                Remove(project, pending, key, marker);
        }
    }

    private void Remove(string project, PendingPushes pending, string key,
        PendingIntegrationPush marker)
    {
        if (_root is not null)
        {
            var path = PendingPath(project, PushKey(marker.JobId, marker.ApprovedSha));
            if (File.Exists(path)) File.Delete(path);
        }
        pending.Jobs.Remove(key);
        if (pending.Jobs.Count == 0) pending.Cleared.TrySetResult();
    }

    private sealed class Grant(BatchIntegrationReservationService owner, ProjectGate gate,
        IntegrationReservationJob job) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(gate, job);
        }
    }
}
