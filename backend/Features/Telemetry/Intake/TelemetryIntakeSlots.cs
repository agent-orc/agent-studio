namespace AgentStudio.Telemetry;

/// <summary>
/// Request slots of the OTLP intake: at most <c>total</c> requests at once and
/// at most <c>perEnvironment</c> of them for one project environment, so slow
/// uploads with one environment's token cannot take every slot. Acquiring never
/// waits; a request without a slot gets 429.
/// </summary>
internal sealed class TelemetryIntakeSlots(int total, int perEnvironment)
{
    private readonly object _gate = new();
    private readonly Dictionary<TelemetryEnvironmentKey, int> _held = [];
    private int _total;

    /// <summary>A lease that frees the slot when disposed, or null when no slot is free.</summary>
    public IDisposable? TryAcquire(TelemetryEnvironmentKey environment)
    {
        lock (_gate)
        {
            _held.TryGetValue(environment, out var held);
            if (_total >= total || held >= perEnvironment) return null;
            _held[environment] = held + 1;
            _total++;
        }
        return new Lease(this, environment);
    }

    private void Release(TelemetryEnvironmentKey environment)
    {
        lock (_gate)
        {
            _total--;
            var held = _held[environment] - 1;
            if (held == 0) _held.Remove(environment);
            else _held[environment] = held;
        }
    }

    private sealed class Lease(TelemetryIntakeSlots slots, TelemetryEnvironmentKey environment) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) slots.Release(environment);
        }
    }
}
