using System.Net;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentRunner;

internal static class RepositoryCredentialPaths
{
    /// <summary>
    /// One folder per role. A coding and a review role on one state folder once
    /// wrote the same store, and the review role's read tokens replaced the coding
    /// role's write tokens a second later (1 November 2026).
    /// </summary>
    // Absolute, because Git refuses a relative include in process-level configuration.
    public static string Folder(string stateDir, string role) => Path.GetFullPath(Path.Combine(Root(stateDir), RoleSegment(role)));
    public static string GitStore(string stateDir, string role) => Path.Combine(Folder(stateDir, role), "git-credentials");
    public static string GitConfig(string stateDir, string role) => GitStore(stateDir, role) + ".config";
    /// <summary>Only a review role writes it; its detached workers read it from the state folder in their spec.</summary>
    public static string ReviewStore(string stateDir) => Path.Combine(Folder(stateDir, "review"), "review-credentials");
    public static string EarliestExpiry(string stateDir, string role) => Path.Combine(Folder(stateDir, role), "earliest-expiry");
    public static string OwnerLock(string stateDir, string role) => Path.Combine(Folder(stateDir, role), "owner.lock");
    public static string Owner(string stateDir, string role) => Path.Combine(Folder(stateDir, role), "owner");

    /// <summary>The shared files every role wrote before each role had its own folder.</summary>
    public static IReadOnlyList<string> Legacy(string stateDir) =>
    [
        Path.Combine(Root(stateDir), "git-credentials.config"),
        Path.Combine(Root(stateDir), "git-credentials"),
        Path.Combine(Root(stateDir), "review-credentials"),
        Path.Combine(Root(stateDir), "earliest-expiry"),
    ];

    private static string Root(string stateDir) => Path.GetFullPath(Path.Combine(stateDir, "github-app"));

    private static string RoleSegment(string role)
    {
        var segment = role.Trim().ToLowerInvariant();
        if (segment.Length == 0 || !segment.All(character => character is (>= 'a' and <= 'z') or '-'))
            throw new ArgumentException($"Role '{role}' cannot name a credential folder.", nameof(role));
        return segment;
    }
}

/// <summary>
/// The credential folder of one role on one state folder belongs to one process.
/// A second process that would write the same files refuses to start instead of
/// silently replacing the first one's tokens. The lock is the operating system's,
/// so a crashed owner never blocks its replacement.
/// </summary>
public sealed class RepositoryCredentialOwnership : IDisposable
{
    private readonly FileStream _lock;

    private RepositoryCredentialOwnership(FileStream fileLock) => _lock = fileLock;

    public static RepositoryCredentialOwnership Claim(string stateDir, string role, string runnerId)
    {
        var folder = RepositoryCredentialPaths.Folder(stateDir, role);
        Directory.CreateDirectory(folder);
        var lockPath = RepositoryCredentialPaths.OwnerLock(stateDir, role);
        var ownerPath = RepositoryCredentialPaths.Owner(stateDir, role);
        FileStream fileLock;
        try
        {
            fileLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            string holder;
            try { holder = File.Exists(ownerPath) ? File.ReadAllText(ownerPath).Trim() : "unknown"; }
            catch (Exception readFailure) when (readFailure is IOException or UnauthorizedAccessException) { holder = "unknown"; }
            throw new RepositoryCredentialCollisionException(
                $"Another agent-host process already keeps the {role} repository credentials in '{folder}' ({holder}). " +
                $"Two processes with role {role} share the state folder '{Path.GetFullPath(stateDir)}'. " +
                "Give each one its own RUNNER_STATE_DIR, or stop the other process.", exception);
        }
        try
        {
            File.WriteAllText(ownerPath, $"runner={runnerId} role={role} pid={Environment.ProcessId}\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The lock is the claim; the owner line only names the holder in a refusal.
        }
        return new RepositoryCredentialOwnership(fileLock);
    }

    public void Dispose() => _lock.Dispose();
}

public sealed class RepositoryCredentialCollisionException(string message, Exception inner)
    : InvalidOperationException(message, inner);

/// <summary>
/// Keeps this host's GitHub App tokens fresh. Tokens live for an hour, so the
/// files hold nothing worth stealing for long; the server decides which
/// repositories and which access level this host's credential may have. Without
/// a connected App the files are absent and Git behaves as the host configured it.
/// </summary>
public sealed class RepositoryCredentialSync(
    RunnerOptions options,
    TaskServerClient client,
    Action<string> log,
    TimeProvider? clock = null)
{
    private static readonly TimeSpan FailureRetry = TimeSpan.FromSeconds(60);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _round = new(1, 1);
    private string? _lastState;
    private string? _writtenFingerprint;
    private bool _legacyRemoved;

    /// <summary>
    /// Changes whenever the files this role's Git reads change. Random, so nothing
    /// about a token leaves the host; the server re-runs a failed delivery
    /// preflight at once when it sees a new value.
    /// </summary>
    public string Generation { get; private set; } = Guid.NewGuid().ToString("N");

    /// <summary>Review hosts only ever read. Everything else that publishes writes.</summary>
    private string Access => options.Role is "coding" or "integration" ? RepositoryAccess.Write : RepositoryAccess.Read;

    public async Task PollAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            TimeSpan wait;
            try { wait = await SyncOnceAsync(shutdown); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                // One bad round must never end the renewal: tokens would then run out silently.
                Report("failed:" + exception.GetType().Name, $"failed type={exception.GetType().Name}");
                wait = FailureRetry;
            }
            try { await Task.Delay(wait, _clock, shutdown); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return; }
        }
    }

    /// <summary>
    /// An extra round outside the schedule, for a push the remote refused. True when
    /// this role holds App tokens afterwards.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        await SyncOnceAsync(ct);
        return File.Exists(RepositoryCredentialPaths.GitStore(options.StateDir, options.Role));
    }

    /// <summary>One round, never throwing for a server or file problem. Returns the wait before the next round.</summary>
    public async Task<TimeSpan> SyncOnceAsync(CancellationToken ct)
    {
        await _round.WaitAsync(ct);
        try { return await SyncRoundAsync(ct); }
        finally { _round.Release(); }
    }

    private async Task<TimeSpan> SyncRoundAsync(CancellationToken ct)
    {
        try
        {
            RemoveLegacyOnce();
            var response = await client.GetRepositoryCredentialsAsync(options.RunnerId, Access, ReadHostKey(), ct);
            if (response is null)
            {
                Clear();
                Report("no-app", "no GitHub App reaches a repository of this host");
                return TimeSpan.FromSeconds(GitHubAppBroker.HostRefreshSeconds);
            }
            Write(response.Credentials);
            Report("ready:" + response.Credentials.Count, $"repositories={response.Credentials.Count} access={Access}");
            return TimeSpan.FromSeconds(Math.Clamp(response.RefreshAfterSeconds, 60, 900));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // A revoked host loses its tokens at once, not at their expiry.
            Clear();
            Report("refused", $"refused status={(int)exception.StatusCode.Value}; check {RunnerOptions.RepositoryKeyFileVariable}");
            return TimeSpan.FromSeconds(GitHubAppBroker.HostRefreshSeconds);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException
                                              or UnauthorizedAccessException or ArgumentException
                                              or JsonException or NotSupportedException)
        {
            PruneExpired();
            Report("deferred:" + exception.GetType().Name, $"deferred type={exception.GetType().Name}");
            return FailureRetry;
        }
    }

    private void Write(IReadOnlyList<RepositoryCredential> credentials)
    {
        var state = options.StateDir;
        var role = options.Role;
        var store = RepositoryCredentialPaths.GitStore(state, role);
        var content = RepositoryGitCredentialFiles.RenderGitStore(credentials);
        RepositoryGitCredentialFiles.WriteProtected(store, content);
        RepositoryGitCredentialFiles.WriteProtected(RepositoryCredentialPaths.GitConfig(state, role),
            RepositoryGitCredentialFiles.RenderGitConfig(store, credentials));
        if (role == "review")
            RepositoryGitCredentialFiles.WriteProtected(RepositoryCredentialPaths.ReviewStore(state),
                RepositoryGitCredentialFiles.RenderReviewStore(credentials));
        if (credentials.Count > 0)
            RepositoryGitCredentialFiles.WriteProtected(RepositoryCredentialPaths.EarliestExpiry(state, role),
                credentials.Min(item => item.ExpiresAtUtc).ToString("O"));
        Changed(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))));
    }

    private void Clear()
    {
        var state = options.StateDir;
        var role = options.Role;
        // The config first: nothing may point at a store that is gone.
        var paths = new List<string>
        {
            RepositoryCredentialPaths.GitConfig(state, role),
            RepositoryCredentialPaths.GitStore(state, role),
            RepositoryCredentialPaths.EarliestExpiry(state, role),
        };
        if (role == "review") paths.Add(RepositoryCredentialPaths.ReviewStore(state));
        foreach (var path in paths)
            if (File.Exists(path)) File.Delete(path);
        Changed("cleared");
    }

    private void Changed(string fingerprint)
    {
        if (fingerprint == _writtenFingerprint) return;
        _writtenFingerprint = fingerprint;
        Generation = Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// The shared files of an older release hold whichever role's tokens were
    /// written last. Nothing reads them any more, so they go.
    /// </summary>
    private void RemoveLegacyOnce()
    {
        if (_legacyRemoved) return;
        foreach (var path in RepositoryCredentialPaths.Legacy(options.StateDir))
            if (File.Exists(path)) File.Delete(path);
        _legacyRemoved = true;
    }

    /// <summary>While the server is away, an expired token must not shadow the host's older credential.</summary>
    private void PruneExpired()
    {
        var path = RepositoryCredentialPaths.EarliestExpiry(options.StateDir, options.Role);
        if (!File.Exists(path)) return;
        if (DateTime.TryParse(File.ReadAllText(path), null, System.Globalization.DateTimeStyles.RoundtripKind, out var expiry)
            && expiry.ToUniversalTime() <= _clock.GetUtcNow().UtcDateTime)
            Clear();
    }

    private string? ReadHostKey()
    {
        if (string.IsNullOrWhiteSpace(options.RepositoryKeyFile)) return null;
        return RunnerOptions.ReadOwnerOnlySecretFile(options.RepositoryKeyFile, RunnerOptions.RepositoryKeyFileVariable);
    }

    private void Report(string state, string detail)
    {
        if (state == _lastState) return;
        _lastState = state;
        log($"repository-credentials {detail}");
    }
}

/// <summary>
/// Points this process's Git at the generated config. Applied to every Git spawn
/// that keeps the host environment; a spawn with a cleared environment selects its
/// own credential, as review workspaces do.
/// </summary>
internal static class RepositoryCredentialRouting
{
    private static string? _config;
    private static RepositoryCredentialSync? _sync;

    /// <summary>Each role reads only its own folder, so a role sharing the state folder cannot change its access level.</summary>
    public static void Configure(string stateDir, string role) => _config = RepositoryCredentialPaths.GitConfig(stateDir, role);

    /// <summary>The daemon's renewal, so a refused push can ask for fresh tokens at once.</summary>
    public static void Attach(RepositoryCredentialSync? sync) => _sync = sync;

    /// <summary>Null in a process that renews no tokens.</summary>
    public static string? Generation => _sync?.Generation;

    /// <summary>False when this process renews no tokens or holds none afterwards.</summary>
    public static Task<bool> RefreshAsync(CancellationToken ct) => _sync?.RefreshAsync(ct) ?? Task.FromResult(false);

    public static void Apply(IDictionary<string, string?> environment)
    {
        if (_config is { } config && File.Exists(config))
            RepositoryGitCredentialFiles.Include(environment, config);
    }
}
