namespace AgentStudio.Tests;

/// <summary>
/// Counts the requests a test host is still serving so the host is disposed
/// only after the last one has finished (AGT-2859). A request that is still
/// inside the pipeline when the host's service provider is disposed dies with
/// <see cref="ObjectDisposedException"/> on <c>IServiceProvider</c>, and on a
/// host's first request that happens inside
/// <c>RequestDelegateFactory.InferMetadata</c>, because building the endpoints
/// is the slowest step of the first request.
/// </summary>
public sealed class InFlightRequestTracker
{
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<long, string> _inFlight = new();
    private readonly TaskCompletionSource _closedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _drained;
    private long _nextId;
    private bool _closed;

    public InFlightRequestTracker(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>Completes once <see cref="DrainAsync"/> has closed the tracker.</summary>
    public Task Closed => _closedSignal.Task;

    public int InFlightCount
    {
        get { lock (_gate) return _inFlight.Count; }
    }

    /// <summary>
    /// Registers a request. A request that arrives after the host began to stop
    /// is refused loudly: serving it would race the provider disposal again.
    /// </summary>
    public IDisposable Enter(string description)
    {
        lock (_gate)
        {
            if (_closed)
            {
                throw new InvalidOperationException(
                    $"Request '{description}' arrived after the test host began to stop. "
                    + "Send every request before disposing the factory.");
            }
            var id = _nextId++;
            _inFlight[id] = description;
            return new Registration(this, id);
        }
    }

    /// <summary>
    /// Closes the tracker and waits until every in-flight request has left the
    /// pipeline. Throws <see cref="TimeoutException"/> naming the requests that
    /// are still running after <paramref name="timeout"/>, measured on the
    /// injected clock, so a stuck request fails its test instead of being cut
    /// off by the disposal.
    /// </summary>
    public async Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task drained;
        lock (_gate)
        {
            _closed = true;
            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_inFlight.Count == 0) _drained.TrySetResult();
            drained = _drained.Task;
        }
        _closedSignal.TrySetResult();

        if (drained.IsCompleted) return;

        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deadline = Task.Delay(timeout, _clock, expiry.Token);
        var finished = await Task.WhenAny(drained, deadline).ConfigureAwait(false);
        await expiry.CancelAsync().ConfigureAwait(false);
        if (finished == drained) return;

        string[] stillRunning;
        lock (_gate) stillRunning = _inFlight.Values.Order(StringComparer.Ordinal).ToArray();
        if (stillRunning.Length == 0) return;
        throw new TimeoutException(
            $"The test host stopped with {stillRunning.Length} request(s) still in flight after "
            + $"{timeout.TotalSeconds:0.#} s: {string.Join(", ", stillRunning)}.");
    }

    private void Exit(long id)
    {
        lock (_gate)
        {
            _inFlight.Remove(id);
            if (_inFlight.Count == 0) _drained?.TrySetResult();
        }
    }

    private sealed class Registration(InFlightRequestTracker owner, long id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Exit(id);
        }
    }
}
