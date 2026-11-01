using AgentStudio.Git;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.GitHubApp;

/// <summary>
/// Keeps this host's own Git supplied with App tokens: integration pushes, fetches
/// and checkouts of every registered repository, and the workspace evidence
/// repository. The tokens live in a protected credential-store file outside the
/// workspace repository; every backend Git process consults it through
/// <see cref="GitHubAppGitRouting"/>. Without a connected App the files are gone and
/// Git behaves as if this service did not exist.
/// </summary>
public sealed class GitHubAppHostCredentialService : BackgroundService
{
    private const string HostName = "studio-host";

    private readonly GitHubAppBroker _broker;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GitHubAppHostCredentialService> _logger;
    private readonly TimeProvider _clock;
    private readonly string _storePath;
    private readonly string _configPath;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private DateTime? _earliestExpiryUtc;
    private int _lastCount = -1;

    public GitHubAppHostCredentialService(
        GitHubAppBroker broker, IConfiguration configuration, TimeProvider clock, ILogger<GitHubAppHostCredentialService> logger)
        : this(broker, configuration, clock, logger, DefaultStorePath()) { }

    internal GitHubAppHostCredentialService(
        GitHubAppBroker broker, IConfiguration configuration, TimeProvider clock,
        ILogger<GitHubAppHostCredentialService> logger, string storePath)
    {
        _broker = broker;
        _configuration = configuration;
        _clock = clock;
        _logger = logger;
        _storePath = storePath;
        _configPath = storePath + ".config";
        _broker.ConnectionChanged += () =>
        {
            if (_wake.CurrentCount != 0) return;
            try { _wake.Release(); }
            catch (SemaphoreFullException exception)
            {
                // Two changes at once: the pending wake-up covers both.
                SilentCatch.Note(exception, "GitHubAppHostCredentialService: wake-up already pending");
            }
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (SecurityProfiles.IsPublicDemo(_configuration) || TaskServerPlaneProxy.IsConfigured(_configuration)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                // Tokens already written stay valid for their hour; retry soon.
                _logger.LogWarning("github-app-host-credentials refresh failed: {Reason}", exception.Message);
                DropIfExpired();
            }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(GitHubAppBroker.HostRefreshSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    internal async Task RefreshAsync(CancellationToken ct)
    {
        var credentials = await _broker.IssueAllAsync(RepositoryAccess.Write, HostName, ct, includeStudioOnly: true);
        if (credentials.Count == 0)
        {
            Drop();
        }
        else
        {
            RepositoryGitCredentialFiles.WriteProtected(_storePath, RepositoryGitCredentialFiles.RenderGitStore(credentials));
            RepositoryGitCredentialFiles.WriteProtected(_configPath, RepositoryGitCredentialFiles.RenderGitConfig(_storePath, credentials));
            _earliestExpiryUtc = credentials.Min(item => item.ExpiresAtUtc);
            GitHubAppGitRouting.Enable(_configPath);
        }
        if (credentials.Count != _lastCount)
            _logger.LogInformation("github-app-host-credentials repositories={Count}", credentials.Count);
        _lastCount = credentials.Count;
    }

    /// <summary>When refreshing keeps failing, an expired token must not answer before the host's older credential.</summary>
    internal void DropIfExpired()
    {
        if (_earliestExpiryUtc is { } expiry && expiry <= _clock.GetUtcNow().UtcDateTime) Drop();
    }

    private void Drop()
    {
        GitHubAppGitRouting.Disable();
        foreach (var path in new[] { _configPath, _storePath })
            if (File.Exists(path)) File.Delete(path);
        _earliestExpiryUtc = null;
    }

    private static string DefaultStorePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!Path.IsPathFullyQualified(appData))
            appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(appData, "AgentStudio", "github-app", "git-credentials");
    }
}
