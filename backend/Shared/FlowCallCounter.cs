namespace AgentStudio.Shared;

/// <summary>
/// Counts calls of one operation made on the observing async flow only, so a
/// test can assert that a read path performs no disk probe or index build
/// while unrelated work (other tests, background refreshes that do not flow
/// the caller's context) runs at the same time. Costs one async-local read per
/// call when nobody observes.
/// </summary>
public sealed class FlowCallCounter
{
    private readonly AsyncLocal<Observation?> _current = new();

    public void Hit()
    {
        if (_current.Value is { } observation) Interlocked.Increment(ref observation.Calls);
    }

    /// <summary>Starts counting calls on the current flow until disposed.</summary>
    public Observation Observe()
    {
        var observation = new Observation(this, _current.Value);
        _current.Value = observation;
        return observation;
    }

    public sealed class Observation(FlowCallCounter owner, Observation? previous) : IDisposable
    {
        internal int Calls;

        public int Count => Volatile.Read(ref Calls);

        public void Dispose()
        {
            if (ReferenceEquals(owner._current.Value, this)) owner._current.Value = previous;
        }
    }
}
