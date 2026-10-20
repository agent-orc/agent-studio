namespace AgentStudio.Pipeline;

/// <summary>Stops in-flight gates when their card is accepted or its delivery is integrated.</summary>
public sealed class TaskGateCancellationRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, HashSet<CancellationTokenSource>> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completed = new(StringComparer.OrdinalIgnoreCase);

    public IDisposable Register(string? project, string? jobId, CancellationTokenSource source)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(jobId))
            return new Registration(null);
        var key = Key(project, jobId);
        lock (_sync)
        {
            // Acceptance may finish before the remote gate reaches Register.
            // The completion tombstone cancels every gate that registers while
            // the card stays Completed; a reopen clears it.
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

    /// <summary>Cancels the gates registered now; later registrations run normally.</summary>
    public void Cancel(string? project, string? jobId) => Cancel(project, jobId, completed: false);

    /// <summary>Cancels registered gates and every gate that registers until <see cref="Reopen"/>.</summary>
    public void CancelOnCompletion(string? project, string? jobId) => Cancel(project, jobId, completed: true);

    /// <summary>Clears the completion tombstone once the card leaves Completed.</summary>
    public void Reopen(string? project, string? jobId)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(jobId)) return;
        lock (_sync) _completed.Remove(Key(project, jobId));
    }

    internal bool HasCompletionTombstone(string project, string jobId)
    {
        lock (_sync) return _completed.Contains(Key(project, jobId));
    }

    private void Cancel(string? project, string? jobId, bool completed)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(jobId)) return;
        lock (_sync)
        {
            var key = Key(project, jobId);
            if (completed) _completed.Add(key);
            if (!_active.TryGetValue(key, out var sources)) return;
            foreach (var source in sources) source.Cancel();
        }
    }

    private static string Key(string project, string jobId) => project + "\n" + jobId;

    private sealed class Registration : IDisposable
    {
        private Action? _dispose;

        public Registration(Action? dispose) => _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
