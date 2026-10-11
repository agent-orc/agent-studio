using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Pipeline;

/// <summary>
/// Serializes code integration and batch execution per project. A pending push
/// marker fences the gap between a local merge and verified origin publication.
/// </summary>
public sealed class BatchIntegrationReservationService
{
    private readonly string? _root;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projects =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingPushes> _pushes =
        new(StringComparer.OrdinalIgnoreCase);

    public BatchIntegrationReservationService(string? root = null) => _root = root;

    private sealed class PendingPushes(IEnumerable<string> jobs)
    {
        public HashSet<string> Jobs { get; } = new(jobs, StringComparer.Ordinal);
        public TaskCompletionSource Cleared { get; set; } = Completed(jobs.Any());

        private static TaskCompletionSource Completed(bool pending)
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending) source.SetResult();
            return source;
        }
    }

    private PendingPushes ForProject(string project)
        => _pushes.GetOrAdd(project, name => new PendingPushes(
            _root is null ? [] : Directory.Exists(ProjectDirectory(name))
                ? Directory.EnumerateFiles(ProjectDirectory(name), "*.pending")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(value => value is not null)
                    .Select(value => value!).ToArray()
                : []));

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

    public async Task<IDisposable> AcquireBatchAsync(string project, CancellationToken ct)
    {
        while (true)
        {
            var grant = await AcquireAsync(project, ct).ConfigureAwait(false);
            var pending = ForProject(project);
            Task wait;
            lock (pending)
            {
                if (pending.Jobs.Count == 0) return grant;
                wait = pending.Cleared.Task;
            }
            grant.Dispose();
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    // Called while the code merge owns the reservation, before its push enters
    // the background queue. A failed push remains pending for the backstop.
    public void MarkPushPending(string project, string jobId, string? approvedSha)
    {
        var pushKey = PushKey(jobId, approvedSha);
        var pending = ForProject(project);
        lock (pending)
        {
            if (_root is not null)
            {
                var path = PendingPath(project, pushKey);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path)) File.WriteAllText(path, pushKey);
            }
            if (pending.Jobs.Count == 0)
                pending.Cleared = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Jobs.Add(_root is null ? pushKey : Hash(pushKey));
        }
    }

    public void MarkPushVerified(string project, string jobId, string? approvedSha)
    {
        var pushKey = PushKey(jobId, approvedSha);
        var pending = ForProject(project);
        lock (pending)
        {
            if (_root is not null)
            {
                var path = PendingPath(project, pushKey);
                if (File.Exists(path)) File.Delete(path);
            }
            pending.Jobs.Remove(_root is null ? pushKey : Hash(pushKey));
            if (pending.Jobs.Count == 0) pending.Cleared.TrySetResult();
        }
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
