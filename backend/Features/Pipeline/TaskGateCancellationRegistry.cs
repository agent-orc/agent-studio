namespace AgentStudio.Pipeline;

/// <summary>Stops in-flight gates when their card is accepted or its delivery is integrated.</summary>
public sealed class TaskGateCancellationRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, HashSet<CancellationTokenSource>> _active = new(StringComparer.OrdinalIgnoreCase);
    // Cards that entered Completed. A gate that registers while its card stays
    // there is superseded; the mark ends when the card leaves Completed.
    private readonly HashSet<string> _completed = new(StringComparer.OrdinalIgnoreCase);

    public IDisposable Register(string? project, string? jobId, CancellationTokenSource source)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(jobId))
            return new Registration(null);
        var key = Key(project, jobId);
        lock (_sync)
        {
            if (_completed.Contains(key)) source.Cancel();
            if (!_active.TryGetValue(key, out var sources))
                _active[key] = sources = [];
            sources.Add(source);
        }
        return new Registration(() =>
        {
            lock (_sync)
            {
                if (!_active.TryGetValue(key, out var sources)) return;
                sources.Remove(source);
                if (sources.Count == 0) _active.Remove(key);
            }
        });
    }

    /// <summary>Cancels the gates registered now; a later registration is not affected.</summary>
    public void Cancel(string? project, string? jobId)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(jobId)) return;
        lock (_sync) CancelActive(Key(project, jobId));
    }

    /// <summary>
    /// The card entered Completed: cancel its registered gates and every gate
    /// that registers before the card leaves Completed again.
    /// </summary>
    public void CancelForCompletion(string? project, string? jobId)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(jobId)) return;
        var key = Key(project, jobId);
        lock (_sync)
        {
            _completed.Add(key);
            CancelActive(key);
        }
    }

    /// <summary>The card left Completed; its next gate registers normally.</summary>
    public void LeaveCompleted(string? project, string? jobId)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(jobId)) return;
        lock (_sync) _completed.Remove(Key(project, jobId));
    }

    private void CancelActive(string key)
    {
        if (!_active.TryGetValue(key, out var sources)) return;
        foreach (var source in sources) source.Cancel();
    }

    private static string Key(string project, string jobId) => project + "\n" + jobId;

    private sealed class Registration : IDisposable
    {
        private Action? _dispose;

        public Registration(Action? dispose) => _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
