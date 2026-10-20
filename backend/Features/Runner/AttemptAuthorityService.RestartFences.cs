using System.Text.Json;

namespace AgentStudio.Runner;

/// <summary>A claim drain that remains in force when the remote outcome is unknown.</summary>
public interface IRestartClaimFence : IDisposable
{
    void Retain();
    void ConfirmTerminal();
}

public sealed partial class AttemptAuthorityService
{
    private readonly HashSet<string> _reviewRestartFences = new(StringComparer.Ordinal);

    /// <summary>Blocks new review leases before a restart can be submitted.</summary>
    public IDisposable? TryFenceReviewHost(string hostId)
    {
        lock (_gate)
        {
            var now = _utcNow();
            if (_reviewRestartFences.Contains("*") || _reviewRestartFences.Contains(hostId)
                || _state.ReviewAttempts.Any(review => review.State == AttemptLifecycleState.Leased
                    && review.Lease is { } lease && lease.ExpiresAt > now && Same(lease.HostId, hostId)))
                return null;
            _reviewRestartFences.Add(hostId);
            try { SaveReviewRestartFences(); }
            catch (IOException exception)
            {
                _reviewRestartFences.Remove(hostId);
                _logger.LogWarning(exception, "review-restart-fence-save-failed host={Host}", hostId);
                return null;
            }
            return new ReviewRestartFence(this, hostId);
        }
    }

    public bool HasReviewRestartFence(string hostId)
    {
        lock (_gate) return _reviewRestartFences.Contains(hostId);
    }

    public void ReleaseReviewRestartFence(string hostId)
    {
        lock (_gate)
        {
            if (!_reviewRestartFences.Remove(hostId)) return;
            try { SaveReviewRestartFences(); }
            catch (IOException exception)
            {
                _reviewRestartFences.Add(hostId);
                _logger.LogWarning(exception, "review-restart-fence-release-failed host={Host}", hostId);
            }
        }
    }

    private sealed class ReviewRestartFence(AttemptAuthorityService owner, string hostId) : IRestartClaimFence
    {
        private bool _retained;
        public void Retain() => _retained = true;
        public void ConfirmTerminal() => _retained = false;
        public void Dispose()
        {
            if (_retained) return;
            lock (owner._gate)
            {
                owner._reviewRestartFences.Remove(hostId);
                try { owner.SaveReviewRestartFences(); }
                catch (IOException exception)
                {
                    owner._reviewRestartFences.Add(hostId);
                    owner._logger.LogWarning(exception, "review-restart-fence-release-failed host={Host}", hostId);
                }
            }
        }
    }

    private string? ReviewRestartFencePath => _path is null ? null
        : Path.Combine(Path.GetDirectoryName(_path)!, "review-restart-fences.json");

    private void LoadReviewRestartFences()
    {
        var path = ReviewRestartFencePath;
        if (path is null || !File.Exists(path)) return;
        try
        {
            foreach (var host in JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [])
                if (!string.IsNullOrWhiteSpace(host)) _reviewRestartFences.Add(host);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            _reviewRestartFences.Add("*");
            _logger.LogError(exception, "review-restart-fence-load-failed; all review claims remain fenced");
        }
    }

    private void SaveReviewRestartFences()
    {
        var path = ReviewRestartFencePath;
        if (path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, _reviewRestartFences.OrderBy(host => host).ToArray());
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
