namespace AgentRunner;

/// <summary>The last read proof of one <c>review:repository-read</c> key.</summary>
public sealed record ReviewRepositoryReadProof(string Status, string Detail, DateTimeOffset ObservedAt);

/// <summary>
/// AGT-3077: proves each configured private-repository credential with a real,
/// credentialed <c>git ls-remote</c> before the review executor advertises its
/// <c>review:repository-read</c> key as ready. A line in the credentials file
/// only says a token was configured; an expired, revoked, unapproved or
/// under-scoped token used to pass the claim filter and then fail the clone,
/// spending the infrastructure retry budget the claim filter exists to save.
/// Both Task Server implementations admit a registered read key only while the
/// current advertisement reports it ready.
/// </summary>
public sealed class ReviewRepositoryReadProbe
{
    public const string Ready = "ready";
    public const string Unavailable = "unavailable";

    /// <summary>Idle cost is one ls-remote per credential per ten minutes.</summary>
    public static readonly TimeSpan ReadyTtl = TimeSpan.FromMinutes(10);

    /// <summary>A failed proof is retried soon, so an approved token is picked up without a restart.</summary>
    public static readonly TimeSpan UnavailableTtl = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The instance the daemon refreshes and the advertisement reads.</summary>
    public static ReviewRepositoryReadProbe Shared { get; } = new();

    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<ReviewRepositoryCredential, CancellationToken, Task<ProcessResult>> _prove;
    private readonly TimeSpan _timeout;
    private readonly Dictionary<string, ReviewRepositoryReadProof> _proofs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dueNow = new(StringComparer.Ordinal);

    public ReviewRepositoryReadProbe(
        Func<DateTimeOffset>? clock = null,
        Func<ReviewRepositoryCredential, CancellationToken, Task<ProcessResult>>? prove = null,
        TimeSpan? timeout = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _prove = prove ?? LsRemoteAsync;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>Snapshot of every proven or failed key, for the advertisement.</summary>
    public IReadOnlyDictionary<string, ReviewRepositoryReadProof> Proofs
    {
        get
        {
            lock (_sync) return new Dictionary<string, ReviewRepositoryReadProof>(_proofs, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Re-proves every configured credential whose proof is missing, expired or
    /// invalidated. A key removed from the file is forgotten; a file that turns
    /// unreadable or malformed marks every known key unavailable.
    /// </summary>
    public async Task RefreshDueAsync(string? credentialsFile, Action<string> log, CancellationToken ct)
    {
        ReviewRepositoryCredentials credentials;
        try
        {
            credentials = ReviewRepositoryCredentials.Load(credentialsFile);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
                                              or UnauthorizedAccessException)
        {
            var now = _clock();
            lock (_sync)
            {
                foreach (var key in _proofs.Keys.ToArray())
                    _proofs[key] = new ReviewRepositoryReadProof(
                        Unavailable, $"credentials file unreadable: {OneLine(exception.Message)}", now);
            }
            log($"review-repository-read-probe credentials=unreadable error={OneLine(exception.Message)}");
            return;
        }

        var configured = credentials.Entries.Select(entry => entry.CapabilityKey).ToHashSet(StringComparer.Ordinal);
        lock (_sync)
        {
            foreach (var key in _proofs.Keys.Where(key => !configured.Contains(key)).ToArray())
            {
                _proofs.Remove(key);
                _dueNow.Remove(key);
            }
        }

        foreach (var credential in credentials.Entries)
        {
            if (!Due(credential.CapabilityKey)) continue;
            var proof = await ProveAsync(credential, ct);
            ReviewRepositoryReadProof? previous;
            lock (_sync)
            {
                _proofs.TryGetValue(credential.CapabilityKey, out previous);
                _proofs[credential.CapabilityKey] = proof;
                _dueNow.Remove(credential.CapabilityKey);
            }
            if (previous?.Status != proof.Status)
                log($"review-repository-read-probe repository={credential.Canonical} status={proof.Status} "
                    + $"detail={proof.Detail}");
        }
    }

    /// <summary>
    /// A credentialed clone or fetch failed: prove the key again before the
    /// next advertisement instead of draining repository access for every
    /// project this executor serves.
    /// </summary>
    public void Invalidate(string capabilityKey)
    {
        lock (_sync) _dueNow.Add(capabilityKey);
    }

    private bool Due(string key)
    {
        lock (_sync)
        {
            if (_dueNow.Contains(key) || !_proofs.TryGetValue(key, out var proof)) return true;
            var ttl = proof.Status == Ready ? ReadyTtl : UnavailableTtl;
            return _clock() - proof.ObservedAt >= ttl;
        }
    }

    private async Task<ReviewRepositoryReadProof> ProveAsync(ReviewRepositoryCredential credential, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        ProcessResult result;
        try
        {
            result = await _prove(credential, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ReviewRepositoryReadProof(
                Unavailable, $"ls-remote timed out after {_timeout.TotalSeconds:0}s", _clock());
        }
        return result.Success
            ? new ReviewRepositoryReadProof(Ready, "credentialed ls-remote succeeded", _clock())
            : new ReviewRepositoryReadProof(
                Unavailable, $"credentialed ls-remote failed ({result.ExitCode}): {OneLine(result.StdErr)}", _clock());
    }

    /// <summary>
    /// The same credential helper and transport limits as the review clone; the
    /// token reaches only this git child process.
    /// </summary>
    private static async Task<ProcessResult> LsRemoteAsync(ReviewRepositoryCredential credential, CancellationToken ct)
    {
        var home = Path.Combine(Path.GetTempPath(), "agent-review-read-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
                ["LANG"] = Environment.GetEnvironmentVariable("LANG") ?? "C.UTF-8",
                ["LC_ALL"] = Environment.GetEnvironmentVariable("LC_ALL") ?? "C.UTF-8",
                ["SSL_CERT_FILE"] = Environment.GetEnvironmentVariable("SSL_CERT_FILE"),
                ["SSL_CERT_DIR"] = Environment.GetEnvironmentVariable("SSL_CERT_DIR"),
                ["HOME"] = home,
            };
            return await ProcessRunner.RunAsync(
                "git",
                RemoteReviewWorkspace.CredentialedGitArguments(credential, "ls-remote", "--heads", credential.CloneUrl),
                home,
                environment: RemoteReviewWorkspace.CredentialedGitEnvironment(credential, environment),
                clearEnvironment: true,
                ct: ct);
        }
        finally
        {
            ResilientDirectory.TryDelete(home);
        }
    }

    private static string OneLine(string value)
    {
        var line = string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= 300 ? line : line[..300];
    }
}
