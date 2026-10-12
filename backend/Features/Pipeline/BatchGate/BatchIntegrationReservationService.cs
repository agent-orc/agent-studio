using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Pipeline;

public sealed record PendingIntegrationPush(string JobId, string? ApprovedSha,
    string? JobFolderPath, string? RepositoryPath, string? IntegrationBranch,
    string? WatchPath, DateTimeOffset SinceUtc, string? Project = null);

public sealed record PendingIntegrationPushWait(string Reason, string JobId,
    string? ApprovedSha, DateTimeOffset SinceUtc, bool AgeAlert);

/// <summary>Serializes code integration and batch execution per project.</summary>
public sealed class BatchIntegrationReservationService
{
    private readonly string? _root;
    private readonly TimeProvider _time;
    private readonly TimeSpan _alertAfter;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projects =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingPushes> _pushes =
        new(StringComparer.OrdinalIgnoreCase);

    public BatchIntegrationReservationService(string? root = null,
        TimeProvider? timeProvider = null, TimeSpan? alertAfter = null)
    {
        _root = root;
        _time = timeProvider ?? TimeProvider.System;
        _alertAfter = alertAfter ?? TimeSpan.FromMinutes(15);
        LoadPersistedProjects();
    }

    private void LoadPersistedProjects()
    {
        if (_root is null || !Directory.Exists(_root)) return;
        foreach (var folder in Directory.EnumerateDirectories(_root))
        foreach (var path in Directory.EnumerateFiles(folder, "*.pending"))
        {
            var marker = ReadMarker(path);
            if (!string.IsNullOrWhiteSpace(marker?.Project)
                && string.Equals(folder, ProjectDirectory(marker.Project),
                    StringComparison.OrdinalIgnoreCase))
                _ = ForProject(marker.Project);
        }
    }

    public void LoadPersistedProjects(IEnumerable<string> projects)
    {
        foreach (var project in projects)
            _ = ForProject(project);
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

    public async Task<IDisposable> AcquireAsync(string project, CancellationToken ct)
    {
        var semaphore = _projects.GetOrAdd(project, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Grant(semaphore);
    }

    public IDisposable? TryAcquire(string project)
    {
        var semaphore = _projects.GetOrAdd(project, _ => new SemaphoreSlim(1, 1));
        return semaphore.Wait(0) ? new Grant(semaphore) : null;
    }

    public Task<IDisposable> AcquireBatchAsync(string project, CancellationToken ct)
        => AcquireBatchAsync(project, null, ct);

    public async Task<IDisposable> AcquireBatchAsync(string project,
        Func<PendingIntegrationPush, bool>? isStale, CancellationToken ct,
        Action? beginPass = null)
    {
        while (true)
        {
            var grant = await AcquireAsync(project, ct).ConfigureAwait(false);
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
    {
        var pending = ForProject(project);
        lock (pending)
        {
            var marker = pending.Jobs.Values.MinBy(item => item.SinceUtc);
            if (marker is null) return null;
            var reason = $"waiting on push {marker.JobId}@{marker.ApprovedSha} since {marker.SinceUtc:O}";
            return new PendingIntegrationPushWait(reason, marker.JobId, marker.ApprovedSha,
                marker.SinceUtc, _time.GetUtcNow() - marker.SinceUtc >= _alertAfter);
        }
    }

    public IReadOnlyList<string> ProjectsWithPending()
        => _pushes.Where(item =>
        {
            lock (item.Value) return item.Value.Jobs.Count > 0;
        }).Select(item => item.Key).ToArray();

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

    private sealed class Grant(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) semaphore.Release();
        }
    }
}
