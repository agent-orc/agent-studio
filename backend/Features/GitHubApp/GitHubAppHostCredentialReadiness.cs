namespace AgentStudio.GitHubApp;

/// <summary>
/// Whether this host's own Git has what <see cref="GitHubAppHostCredentialService"/>
/// owes it: the App tokens written, or the certainty that no App is connected.
/// On 2 November 2026 merge-into-develop ran directly after a start, before the
/// first tokens were written, and every private repository failed its fetch.
/// Integration waits on this signal. A host that runs no credential service
/// (public demo, a test host without hosted services) never waits.
/// </summary>
public sealed class GitHubAppHostCredentialReadiness
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _expected;
    private volatile string? _lastFailure;

    /// <summary>A credential service runs and has not written its first answer yet.</summary>
    public bool Pending => _expected && !_ready.Task.IsCompleted;

    /// <summary>Why the last refresh before readiness failed, or null.</summary>
    public string? LastFailure => _lastFailure;

    internal void Expect() => _expected = true;

    internal void MarkReady()
    {
        _lastFailure = null;
        _ready.TrySetResult();
    }

    internal void MarkFailed(string reason) => _lastFailure = reason;

    /// <summary>True once ready (or when nothing is pending); false when <paramref name="timeout"/> elapsed first.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (!Pending) return true;
        try
        {
            await _ready.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
