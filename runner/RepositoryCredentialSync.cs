using System.Net;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentRunner;

internal static class RepositoryCredentialPaths
{
    // Absolute, because Git refuses a relative include in process-level configuration.
    private static string Folder(string stateDir) => Path.GetFullPath(Path.Combine(stateDir, "github-app"));
    public static string GitStore(string stateDir) => Path.Combine(Folder(stateDir), "git-credentials");
    public static string GitConfig(string stateDir) => GitStore(stateDir) + ".config";
    public static string ReviewStore(string stateDir) => Path.Combine(Folder(stateDir), "review-credentials");
    public static string EarliestExpiry(string stateDir) => Path.Combine(Folder(stateDir), "earliest-expiry");
}

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
    private string? _lastState;

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

    /// <summary>One round, never throwing for a server or file problem. Returns the wait before the next round.</summary>
    public async Task<TimeSpan> SyncOnceAsync(CancellationToken ct)
    {
        try
        {
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
        var store = RepositoryCredentialPaths.GitStore(state);
        RepositoryGitCredentialFiles.WriteProtected(store, RepositoryGitCredentialFiles.RenderGitStore(credentials));
        RepositoryGitCredentialFiles.WriteProtected(RepositoryCredentialPaths.GitConfig(state),
            RepositoryGitCredentialFiles.RenderGitConfig(store, credentials));
        if (options.Role == "review")
            RepositoryGitCredentialFiles.WriteProtected(RepositoryCredentialPaths.ReviewStore(state),
                RepositoryGitCredentialFiles.RenderReviewStore(credentials));
        if (credentials.Count > 0)
            RepositoryGitCredentialFiles.WriteProtected(RepositoryCredentialPaths.EarliestExpiry(state),
                credentials.Min(item => item.ExpiresAtUtc).ToString("O"));
    }

    private void Clear()
    {
        var state = options.StateDir;
        // The config first: nothing may point at a store that is gone.
        foreach (var path in new[]
                 {
                     RepositoryCredentialPaths.GitConfig(state),
                     RepositoryCredentialPaths.GitStore(state),
                     RepositoryCredentialPaths.ReviewStore(state),
                     RepositoryCredentialPaths.EarliestExpiry(state),
                 })
            if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>While the server is away, an expired token must not shadow the host's older credential.</summary>
    private void PruneExpired()
    {
        var path = RepositoryCredentialPaths.EarliestExpiry(options.StateDir);
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

    public static void Configure(string stateDir) => _config = RepositoryCredentialPaths.GitConfig(stateDir);

    public static void Apply(IDictionary<string, string?> environment)
    {
        if (_config is { } config && File.Exists(config))
            RepositoryGitCredentialFiles.Include(environment, config);
    }
}
