namespace AgentRunner;

/// <summary>
/// A push the remote refused for authorization is often a credential that has
/// just been replaced, as on 1 November 2026, when a review role's read token
/// replaced the coding role's write token. Before the run escalates a retained
/// worktree, the host asks for fresh repository tokens and pushes again, for a
/// bounded time.
/// </summary>
internal static class RepositoryPushRecoveryPolicy
{
    /// <summary>The waits before each retry. Their sum bounds the recovery of one workspace.</summary>
    public static readonly IReadOnlyList<TimeSpan> Delays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    private static readonly string[] RefusalMarkers =
    [
        "The requested URL returned error: 403",
        "The requested URL returned error: 401",
        "Write access to repository not granted",
        "Authentication failed for",
        "Invalid username or password",
        "could not read Username",
    ];

    /// <summary>
    /// The wait before the next push, or null when the failure is no refusal or
    /// this workspace has used up its retries. A push-protection or workflow-scope
    /// rejection is final: fresh tokens cannot change the pushed content.
    /// </summary>
    public static TimeSpan? NextDelay(string failure, int retriesSoFar)
    {
        if (!IsRefusal(failure) || retriesSoFar < 0 || retriesSoFar >= Delays.Count) return null;
        return Delays[retriesSoFar];
    }

    public static bool IsRefusal(string failure)
    {
        if (string.IsNullOrWhiteSpace(failure)
            || PushProtection.ParseGitHubRejection(failure) is not null
            || GitPushProbe.IsWorkflowScopeFailure(failure))
            return false;
        if (RefusalMarkers.Any(marker => failure.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return true;
        var permission = failure.IndexOf("Permission to ", StringComparison.OrdinalIgnoreCase);
        return permission >= 0 && failure.IndexOf(" denied", permission, StringComparison.OrdinalIgnoreCase) > 0;
    }
}

/// <summary>The side effects of one recovery: fresh tokens and a wait. Tests replace both.</summary>
internal sealed record RepositoryPushRecovery(
    Func<CancellationToken, Task<bool>> RefreshAsync,
    Func<TimeSpan, CancellationToken, Task> DelayAsync)
{
    public static RepositoryPushRecovery Host { get; } = new(
        RepositoryCredentialRouting.RefreshAsync,
        (delay, ct) => Task.Delay(delay, ct));
}
