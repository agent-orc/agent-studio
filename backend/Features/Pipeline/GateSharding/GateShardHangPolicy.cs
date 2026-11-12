namespace AgentStudio.Pipeline;

/// <summary>Bounds silence in one test shard independently of the whole gate budget.</summary>
public static class GateShardHangPolicy
{
    public static readonly TimeSpan MinimumIdle = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan CompletedGrace = TimeSpan.FromMinutes(2);

    /// <summary>Two estimated shard durations, with a ten minute floor.</summary>
    public static TimeSpan IdleBound(double estimatedMs)
        => TimeSpan.FromMilliseconds(Math.Max(MinimumIdle.TotalMilliseconds,
            double.IsFinite(estimatedMs) && estimatedMs > 0
                ? Math.Min(estimatedMs * 2, TimeSpan.FromDays(1).TotalMilliseconds)
                : 0));
}

public sealed record GateShardHangEvidence(
    string Suite, int Shard, int Count, string Kind, long StallMs, IReadOnlyList<string> Tests)
{
    public string Summary()
        => $"infrastructure hang: {Suite} shard {Shard + 1}/{Count}, "
           + $"{Kind} for {TimeSpan.FromMilliseconds(StallMs):g}; "
           + $"tests or fixtures: {string.Join(", ", Tests)}";
}
