namespace AgentStudio.Management;

/// <summary>
/// Holds ordinary HTTP mutations while a backup reads the stores, so the
/// archive is one point in time for everything written through the API
/// (cards, access state, secrets, settings). <see cref="ManagementModeMiddleware"/>
/// counts mutations in flight and makes new ones wait; the backup waits for
/// the ones in flight to finish before it reads. Background writers in the
/// process are not stopped: their files go through the stable-read path of
/// <see cref="BackupSnapshotReader"/>.
/// </summary>
public sealed class BackupWritePause
{
    private readonly object _gate = new();
    private TaskCompletionSource _released = Completed();
    private int _inFlight;
    private bool _held;

    public bool IsHeld { get { lock (_gate) return _held; } }

    public int InFlight { get { lock (_gate) return _inFlight; } }

    /// <summary>
    /// Admits one mutation. Waits up to <paramref name="maxWait"/> while a
    /// backup holds writes; false means the hold outlasted the wait and the
    /// caller answers 503.
    /// </summary>
    public async Task<IDisposable?> EnterAsync(TimeSpan maxWait, CancellationToken cancellationToken)
    {
        Task released;
        lock (_gate)
        {
            if (!_held)
            {
                _inFlight++;
                return new Admission(this);
            }
            released = _released.Task;
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var finished = await Task.WhenAny(released, Task.Delay(maxWait, wait.Token)).ConfigureAwait(false);
        wait.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
        if (finished != released) return null;
        return await EnterAsync(maxWait, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Holds new mutations and waits up to <paramref name="drainTimeout"/> for
    /// those in flight. Dispose the result to release the hold.
    /// </summary>
    public async Task<Hold> HoldAsync(TimeSpan drainTimeout, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_held) throw new InvalidOperationException("A backup already holds writes.");
            _held = true;
            _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        // A short real-time wait: the requests in flight run on the real clock too.
        var drain = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (InFlight > 0 && drain.Elapsed < drainTimeout)
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release();
            throw;
        }
        return new Hold(this, InFlight == 0);
    }

    private void Release()
    {
        TaskCompletionSource released;
        lock (_gate)
        {
            if (!_held) return;
            _held = false;
            released = _released;
        }
        released.TrySetResult();
    }

    private void Leave()
    {
        lock (_gate) _inFlight--;
    }

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }

    public sealed class Hold(BackupWritePause owner, bool drained) : IDisposable
    {
        private int _disposed;

        /// <summary>False when mutations were still in flight at the drain timeout.</summary>
        public bool Drained { get; } = drained;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release();
        }
    }

    private sealed class Admission(BackupWritePause owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Leave();
        }
    }
}
