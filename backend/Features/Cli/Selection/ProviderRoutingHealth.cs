namespace AgentStudio.Cli;

/// <summary>Bounded, process-local provider circuit. Expiry permits a recovery attempt.</summary>
public sealed class ProviderRoutingHealth(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly Dictionary<string, Queue<(DateTimeOffset At, bool Failed)>> _samples = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _blockedUntil = new(StringComparer.OrdinalIgnoreCase);

    public void Observe(string provider, bool providerFailure)
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            if (!_samples.TryGetValue(provider, out var samples)) _samples[provider] = samples = new();
            while (samples.TryPeek(out var first) && (now - first.At > TimeSpan.FromMinutes(15) || samples.Count >= 20))
                samples.Dequeue();
            samples.Enqueue((now, providerFailure));
            if (!providerFailure)
            {
                _blockedUntil.Remove(provider);
                return;
            }
            if (samples.Count >= 5 && samples.Count(sample => sample.Failed) * 2 >= samples.Count)
            {
                _blockedUntil[provider] = now.AddMinutes(2);
                samples.Clear();
            }
        }
    }

    public string? UnavailableReason(string provider)
    {
        lock (_sync)
            return _blockedUntil.TryGetValue(provider, out var until) && until > _time.GetUtcNow()
                ? $"provider error rate circuit open until {until:O}" : null;
    }
}
