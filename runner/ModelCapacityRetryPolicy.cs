namespace AgentRunner;

/// <summary>Bounded delays for retrying a failed Codex turn in its existing session.</summary>
internal static class ModelCapacityRetryPolicy
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
    ];

    internal static TimeSpan DelayForRetry(int retryNumber)
        => retryNumber >= 1 && retryNumber <= Delays.Length
            ? Delays[retryNumber - 1]
            : throw new ArgumentOutOfRangeException(nameof(retryNumber));
}
