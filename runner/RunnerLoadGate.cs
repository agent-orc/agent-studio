namespace AgentRunner;

/// <summary>
/// Sustained load/core admission invariant for a remote host. Existing slots
/// keep running; only new claims stop while the normalized load remains high.
/// </summary>
public sealed class RunnerLoadGate
{
    private readonly double _threshold;
    private readonly TimeSpan _sustainedFor;
    private DateTime? _highSince;
    private DateTime? _closedSince;
    private DateTime? _openSince;
    private double _lastNormalized;
    private bool _reported;

    public RunnerLoadGate(double threshold = 1.5, TimeSpan? sustainedFor = null)
    {
        _threshold = threshold;
        _sustainedFor = sustainedFor ?? TimeSpan.FromMinutes(2);
    }

    public RunnerLoadGateDecision Observe(HostTelemetrySample? sample, DateTime now)
    {
        if (sample?.Load1 is not { } load || sample.CpuCores <= 0)
        {
            return Decide(now);
        }

        var normalized = load / sample.CpuCores;
        _lastNormalized = normalized;
        if (normalized <= _threshold)
        {
            var wasClosed = _closedSince is not null;
            var closedFor = wasClosed ? now - _closedSince!.Value : TimeSpan.Zero;
            _highSince = null;
            _closedSince = null;
            _openSince = wasClosed ? now : _openSince ?? now;
            _reported = false;
            return new(false, normalized, closedFor, false, _openSince, wasClosed);
        }

        _highSince ??= now;
        _openSince ??= now;
        return Decide(now);
    }

    private RunnerLoadGateDecision Decide(DateTime now)
    {
        if (_highSince is null)
            return new(false, _lastNormalized, TimeSpan.Zero, false, _openSince ??= now);
        var duration = now - _highSince.Value;
        var throttled = duration >= _sustainedFor;
        var emit = throttled && !_reported;
        if (emit)
        {
            _reported = true;
            _closedSince = now;
            _openSince = null;
        }
        return new(throttled, _lastNormalized, duration, emit,
            throttled ? _closedSince : _openSince);
    }
}

public sealed record RunnerLoadGateDecision(
    bool Throttle,
    double LoadPerCore,
    TimeSpan SustainedFor,
    bool EmitEvent,
    DateTime? Since = null,
    bool EmitOpenEvent = false);
