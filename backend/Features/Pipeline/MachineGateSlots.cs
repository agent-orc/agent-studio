using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace AgentStudio.Pipeline;

/// <summary>Cross-process gate admission. Each slot has its own file lock.</summary>
internal sealed class MachineGateSlots
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessSlots = new();
    private readonly string _lockPrefix;
    private readonly TimeProvider _clock;

    internal MachineGateSlots(string lockPrefix, TimeProvider? clock = null)
    {
        _lockPrefix = lockPrefix;
        _clock = clock ?? TimeProvider.System;
    }

    internal async Task<Lease?> AcquireAsync(int count, TimeSpan timeout,
        Action<int, int>? waiting, CancellationToken ct)
    {
        if (count is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(count));
        var started = _clock.GetTimestamp();
        var collision = false;
        var lastReportedBusy = -1;
        using var deadline = new CancellationTokenSource(timeout, _clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            while (true)
            {
                bounded.Token.ThrowIfCancellationRequested();
                var busy = 0;
                for (var slot = 0; slot < count; slot++)
                {
                    var path = SlotPath(slot);
                    var semaphore = ProcessSlots.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
                    if (!await semaphore.WaitAsync(0, bounded.Token).ConfigureAwait(false))
                    {
                        busy++;
                        continue;
                    }
                    var passedToLease = false;
                    try
                    {
                        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                            OperatingSystem.IsWindows() ? FileShare.None : FileShare.ReadWrite);
                        try
                        {
                            if (!OperatingSystem.IsWindows() && !TryLock(stream))
                            {
                                stream.Dispose();
                                busy++;
                                continue;
                            }
                            passedToLease = true;
                            return new Lease(slot, count, stream, semaphore,
                                (long)_clock.GetElapsedTime(started).TotalMilliseconds, collision);
                        }
                        catch { stream.Dispose(); throw; }
                    }
                    catch (IOException) { busy++; }
                    finally
                    {
                        // Ownership passes to the lease only when a stream was returned.
                        if (!passedToLease) semaphore.Release();
                    }
                }
                collision = true;
                if (busy != lastReportedBusy)
                {
                    waiting?.Invoke(busy, count);
                    lastReportedBusy = busy;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), _clock, bounded.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    internal string SlotPath(int slot) => _lockPrefix + "." + slot + ".lock";

    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fd, int operation);

    private static bool TryLock(FileStream stream)
    {
        if (Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), LockExclusive | LockNonBlocking) == 0)
            return true;
        var error = Marshal.GetLastPInvokeError();
        if (error is 4 or 11 or 35) return false;
        throw new IOException($"Could not acquire a gate slot (flock errno {error}).");
    }

    internal sealed class Lease(int slot, int count, FileStream stream, SemaphoreSlim semaphore,
        long queueWaitMs, bool collision) : IDisposable
    {
        private int _disposed;
        public int Slot { get; } = slot;
        public int Count { get; } = count;
        public long QueueWaitMs { get; } = queueWaitMs;
        public bool CollisionDetected { get; } = collision;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { stream.Dispose(); }
            finally { semaphore.Release(); }
        }
    }
}
