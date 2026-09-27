using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Pipeline;

/// <summary>Cross-process project ref mutation boundary with a durable fence.</summary>
public sealed class RefMutationLeaseService
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _local = new();

    public RefMutationLeaseService(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "agentstudio", "ref-mutation-leases");
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
                var previous = File.Exists(path)
                    ? long.Parse(File.ReadAllText(path), System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                var fence = checked(previous + 1);
                using (var value = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var bytes = Encoding.ASCII.GetBytes(fence.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    value.Write(bytes);
                    value.Flush(flushToDisk: true);
                }
                return new Lease(stream, semaphore, fence);
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

    public sealed class Lease : IDisposable
    {
        private readonly FileStream _stream;
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;
        internal Lease(FileStream stream, SemaphoreSlim semaphore, long fence)
        {
            _stream = stream;
            _semaphore = semaphore;
            Fence = fence;
        }

        public long Fence { get; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stream.Dispose();
            _semaphore.Release();
        }
    }
}
