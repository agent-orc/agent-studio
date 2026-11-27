using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>Cross-process project ref mutation boundary with a durable fence.</summary>
public sealed class RefMutationLeaseService
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _local = new();

    private readonly Action<string>? _writeBoundary;

    public RefMutationLeaseService(string? root = null) : this(root, null) { }

    internal RefMutationLeaseService(string? root, Action<string>? writeBoundary)
    {
        _writeBoundary = writeBoundary;
        _root = root ?? LocalStateRoot.Combine("agentstudio", "ref-mutation-leases");
    }

    public async Task<Lease> AcquireAsync(
        string project, string repository, string branch, CancellationToken ct)
    {
        // All publish targets of one project share the same mutation boundary.
        // Branch remains an audit argument at the call site, but is not part of
        // the lock key: develop, main and the candidate promotion train exclude
        // one another.
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", project, repository))));
        var semaphore = _local.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var folder = Path.Combine(_root, key);
            Directory.CreateDirectory(folder);
            FileStream? stream = null;
            while (stream is null)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    stream = new FileStream(Path.Combine(folder, "lock"),
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
            }
            try
            {
                var path = Path.Combine(folder, "fence");
                var historyPath = Path.Combine(folder, "fence.history");
                long previous;
                try
                {
                    previous = ReadHighWater(path, historyPath, project, repository);
                }
                catch (Exception ex) when (ex is not RepositoryHoldException
                    && (ex is IOException || ex is UnauthorizedAccessException))
                {
                    throw new RepositoryHoldException(project, repository, path,
                        "Fence evidence could not be read.", ex);
                }
                if (previous == long.MaxValue)
                    throw new RepositoryHoldException(project, repository, path, "Fence sequence exhausted.");
                var fence = previous + 1;
                var generations = $"v1\n{previous.ToString(CultureInfo.InvariantCulture)}\n{fence.ToString(CultureInfo.InvariantCulture)}";
                var history = generations + "\n" + Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(generations)));
                // Reserve durably before publishing: a stopped writer may skip a
                // generation, but can never reissue a fence that reached a caller.
                ReplaceDurably(historyPath, history, "history");
                ReplaceDurably(path, fence.ToString(CultureInfo.InvariantCulture), "fence");
                return new Lease(stream, semaphore, fence, path);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch
        {
            semaphore.Release();
            throw;
        }
    }

    private static long ReadHighWater(string path, string historyPath, string project, string repository)
    {
        var authority = ReadOptional(path);
        var history = ReadOptional(historyPath);
        var validFence = TryReadFence(authority ?? "", out var current);
        if (!validFence) current = 0;
        if (history is null)
        {
            // A valid plain fence is the legacy format. An invalid legacy file
            // cannot establish an upper bound and must never restart at zero.
            if (authority is not null && !validFence)
                throw new RepositoryHoldException(project, repository, path, "No validated fence history is available.");
            return current;
        }

        var parts = history.Split('\n');
        if (parts.Length != 4 || parts[0] != "v1"
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var previous)
            || !TryReadFence(parts[2], out var reserved) || previous < 0 || previous == long.MaxValue || reserved != previous + 1
            || parts[3] != Convert.ToHexString(SHA256.HashData(
                Encoding.ASCII.GetBytes(string.Join("\n", parts.Take(3))))))
            throw new RepositoryHoldException(project, repository, path, "Fence history failed validation.");
        return Math.Max(current, reserved);
    }

    private static string? ReadOptional(string path)
    {
        try { return File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
    }

    private static bool TryReadFence(string value, out long fence) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out fence) && fence > 0;

    private void ReplaceDurably(string path, string contents, string stage)
    {
        // Same-directory rename is the publication boundary. A leftover prepared
        // file is never evidence of a grant and is safe to overwrite under lock.
        var prepared = path + ".prepared";
        using (var value = new FileStream(prepared, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            _writeBoundary?.Invoke(stage + "-created");
            var bytes = Encoding.ASCII.GetBytes(contents);
            value.Write(bytes.AsSpan(0, bytes.Length / 2));
            _writeBoundary?.Invoke(stage + "-partial");
            value.Write(bytes.AsSpan(bytes.Length / 2));
            _writeBoundary?.Invoke(stage + "-written");
            value.Flush(flushToDisk: true);
            _writeBoundary?.Invoke(stage + "-flushed");
        }
        File.Move(prepared, path, overwrite: true);
        _writeBoundary?.Invoke(stage + "-published");
    }

    /// <summary>A repository custody hold; no lease is granted until durable evidence is repaired.</summary>
    public sealed class RepositoryHoldException : IOException
    {
        internal RepositoryHoldException(string project, string repository, string fencePath, string reason, Exception? inner = null)
            : base($"Repository ref mutation hold for '{project}' at '{repository}': {reason} Evidence: {fencePath}", inner)
        {
            Project = project;
            Repository = repository;
            FencePath = fencePath;
        }

        public string Code => "ref-mutation-fence-hold";
        public string Project { get; }
        public string Repository { get; }
        public string FencePath { get; }
    }

    /// <summary>
    /// True while the grant is held and no later grant advanced the durable
    /// fence. Publication rechecks this immediately before it mutates a ref.
    /// </summary>
    public static bool IsCurrent(Lease lease)
    {
        if (lease.Disposed) return false;
        try
        {
            return long.TryParse(File.ReadAllText(lease.FencePath),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var fence)
                && fence == lease.Fence;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public sealed class Lease : IDisposable
    {
        private readonly FileStream _stream;
        private readonly SemaphoreSlim _semaphore;
        internal Lease(FileStream stream, SemaphoreSlim semaphore, long fence, string fencePath)
        {
            _stream = stream;
            _semaphore = semaphore;
            Fence = fence;
            FencePath = fencePath;
        }

        public long Fence { get; }
        internal string FencePath { get; }
        internal bool Disposed { get; private set; }
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            _stream.Dispose();
            _semaphore.Release();
        }
    }
}
