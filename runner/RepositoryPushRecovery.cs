namespace AgentRunner;

/// <summary>Classifies remote push failures before a bounded retry of the same refspec.</summary>
internal static class RepositoryPushRecoveryPolicy
{
    public static readonly IReadOnlyList<TimeSpan> Delays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
    ];

    private static readonly string[] FinalMarkers =
    [
        "returned error: 401", "returned error: 403", "authentication failed",
        "invalid username or password", "could not read username", "permission denied",
        "write access to repository not granted", "non-fast-forward", "fetch first",
        "protected branch", "repository rule violations", "push protection",
    ];

    public static TimeSpan? NextDelay(string failure, int retriesSoFar)
    {
        if (!IsTransient(failure) || retriesSoFar < 0 || retriesSoFar >= Delays.Count) return null;
        return Delays[retriesSoFar];
    }

    public static bool IsTransient(string failure)
    {
        if (string.IsNullOrWhiteSpace(failure)
            || PushProtection.ParseGitHubRejection(failure) is not null
            || GitPushProbe.IsWorkflowScopeFailure(failure)
            || FinalMarkers.Any(marker => failure.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (failure.Contains("Internal Server Error", StringComparison.OrdinalIgnoreCase)
            || failure.Contains("fatal error in commit_refs", StringComparison.OrdinalIgnoreCase)
            || failure.Contains("connection reset", StringComparison.OrdinalIgnoreCase))
            return true;
        for (var code = 500; code <= 599; code++)
        {
            if (failure.Contains($"returned error: {code}", StringComparison.OrdinalIgnoreCase)
                || failure.Contains($"HTTP {code}", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

/// <summary>The wait between pushes, replaceable by tests.</summary>
internal sealed record RepositoryPushRecovery(Func<TimeSpan, CancellationToken, Task> DelayAsync)
{
    public static RepositoryPushRecovery Host { get; } = new((delay, ct) => Task.Delay(delay, ct));
}

internal sealed class RepositoryPushFailedException(string message) : InvalidOperationException(message)
{
}
