using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.TaskServer.Contracts.GitHubApp;

/// <summary>
/// Connects one GitHub App to this installation and turns it into short-lived,
/// single-repository tokens. The browser flow runs in three redirects, each
/// resumed by the page through <see cref="AdvanceAsync"/>:
/// <list type="number">
///   <item>manifest registration: GitHub creates the App and returns a code that
///   the server exchanges for the private key (skipped for an App made by hand);</item>
///   <item>installation: the person picks the accounts and repositories;</item>
///   <item>OAuth authorization with PKCE: proves the person really reaches the
///   installation. The setup redirect's <c>installation_id</c> can be forged, so
///   only an installation GitHub lists for that person is ever adopted.</item>
/// </list>
/// The user token ends with the flow. Every later token is an installation token
/// that the private key mints for one repository, for one hour.
/// No lock is held across a GitHub call: a hung GitHub costs the repositories that
/// wait for it, never a token that is already cached or the status page.
/// </summary>
public sealed class GitHubAppBroker
{
    /// <summary>Where GitHub sends the browser back. The single-page shell picks the parameters up there.</summary>
    public const string ReturnPath = "/github-app/callback";

    /// <summary>How often a host asks again. A token has at least <see cref="MinimumRemaining"/> left when it is handed out.</summary>
    public const int HostRefreshSeconds = 300;

    public const string CredentialUsername = "x-access-token";

    private const string ProductUrl = "https://github.com/agent-orc/agent-studio";
    private const int GitHubParallelism = 4;
    private static readonly TimeSpan FlowLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinimumRemaining = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan CoverageLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SecretsLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RevokeBudget = TimeSpan.FromSeconds(10);

    private readonly GitHubAppClient _github;
    private readonly IGitHubAppVault _vault;
    private readonly IRegisteredRepositoryCatalog _catalog;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _connectionWrites = new(1, 1);
    private readonly SemaphoreSlim _secretsRead = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _flights = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Flow> _flows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GitHubAppHostSync> _hosts = new(StringComparer.Ordinal);

    // Guards the caches below. Only ever held for a dictionary access.
    private readonly object _cache = new();
    private readonly Dictionary<string, GitHubInstallationToken> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long? InstallationId, DateTime CheckedAtUtc)> _coverage = new(StringComparer.Ordinal);
    private (GitHubAppSecrets Secrets, DateTime ReadAtUtc)? _secrets;
    // What GitHub reports for each repository an adopted installation lists, and which installation listed it.
    private readonly Dictionary<string, (bool Private, long InstallationId)> _visibility = new(StringComparer.Ordinal);
    private DateTime _visibilityCheckedAtUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _visibilityRefresh = new(1, 1);
    private long _generation;
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    public GitHubAppBroker(
        GitHubAppClient github,
        IGitHubAppVault vault,
        IRegisteredRepositoryCatalog catalog,
        TimeProvider clock)
    {
        _github = github;
        _vault = vault;
        _catalog = catalog;
        _clock = clock;
    }

    /// <summary>Raised after the connection or its installations changed, so a host-side store can refresh at once.</summary>
    public event Action? ConnectionChanged;

    /// <summary>How long one host's request may wait for GitHub before it takes what is ready.</summary>
    internal TimeSpan IssueBudget { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the status page may wait for GitHub before it says so.</summary>
    internal TimeSpan StatusBudget { get; set; } = TimeSpan.FromSeconds(15);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<GitHubAppRegistrationForm> StartRegistrationAsync(
        StartGitHubAppRegistrationRequest request, CancellationToken ct)
    {
        if (await _vault.LoadConnectionAsync(ct) is not null)
            throw AlreadyConnected();
        var origin = ValidOrigin(request.Origin);
        var organization = request.Organization?.Trim();
        if (string.IsNullOrEmpty(organization)) organization = null;
        if (organization is not null && !ValidAccount(organization))
            throw new GitHubAppException("invalid-account", "The organization name is not a valid GitHub account.");
        // GitHub hands out the private key once, after it created the App: the store must be able to keep it first.
        await RequireWritableVaultAsync(ct);
        var state = NewFlow(new Flow(FlowKind.Registration, origin, null, null, Now + FlowLifetime));
        var callback = origin + ReturnPath;
        var suffix = (organization ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(3))).ToLowerInvariant();
        var manifest = JsonSerializer.Serialize(new
        {
            name = Limit("agent-studio-" + suffix, 34),
            url = ProductUrl,
            description = "Short-lived repository access for Agent Studio coding, review and integration hosts.",
            // No webhook: this App reacts to nothing, Studio only asks for tokens.
            hook_attributes = new { url = ProductUrl, active = false },
            redirect_url = callback,
            callback_urls = new[] { callback },
            setup_url = callback,
            setup_on_update = true,
            request_oauth_on_install = false,
            @public = request.AnyAccount,
            default_permissions = new Dictionary<string, string>
            {
                ["contents"] = "write",
                ["metadata"] = "read",
                // Without this, a delivery touching .github/workflows cannot be pushed.
                ["workflows"] = "write",
            },
            default_events = Array.Empty<string>(),
        });
        var target = organization is null
            ? "https://github.com/settings/apps/new"
            : $"https://github.com/organizations/{Uri.EscapeDataString(organization)}/settings/apps/new";
        return new GitHubAppRegistrationForm($"{target}?state={Uri.EscapeDataString(state)}", manifest, state);
    }

    /// <summary>
    /// Connects an App created by hand after proving the key belongs to it. While
    /// connected, this only replaces the same App's key: another App needs a visible
    /// disconnect first, so a connection cannot be swapped silently.
    /// </summary>
    public async Task<GitHubAppStatus> ConnectExistingAsync(
        ConnectExistingGitHubAppRequest request, string actor, CancellationToken ct)
    {
        if (request.AppId <= 0 || string.IsNullOrWhiteSpace(request.ClientId)
            || string.IsNullOrWhiteSpace(request.ClientSecret) || string.IsNullOrWhiteSpace(request.PrivateKeyPem))
            throw new GitHubAppException("invalid-request", "App ID, client ID, client secret and private key are all required.");
        var clientId = request.ClientId.Trim();
        var pem = request.PrivateKeyPem.Trim();
        var info = await _github.GetAppAsync(request.AppId, pem, ct);
        if (!string.Equals(info.ClientId, clientId, StringComparison.Ordinal))
            throw new GitHubAppException("app-mismatch", "The client ID does not belong to this GitHub App.");
        await _connectionWrites.WaitAsync(ct);
        try
        {
            var existing = await _vault.LoadConnectionAsync(ct);
            if (existing is not null && existing.AppId != info.AppId) throw AlreadyConnected();
            await _vault.WriteSecretsAsync(new GitHubAppSecrets(pem, request.ClientSecret.Trim()), actor, ct);
            await _vault.SaveConnectionAsync(new GitHubAppConnection(
                info.AppId, info.Slug, info.Name, clientId, info.OwnerLogin, info.HtmlUrl, Now, actor,
                info.WritePermissions, existing?.Installations ?? []), ct);
            ForgetCaches();
        }
        finally { _connectionWrites.Release(); }
        ConnectionChanged?.Invoke();
        return await StatusAsync(false, ct);
    }

    /// <summary>Where the person adds repositories or another account.</summary>
    public async Task<GitHubAppFlowStep> StartInstallationAsync(StartGitHubAppStepRequest request, CancellationToken ct)
    {
        var connection = await RequireConnectionAsync(ct);
        var state = NewFlow(new Flow(FlowKind.Installation, ValidOrigin(request.Origin), null, null, Now + FlowLifetime));
        return new GitHubAppFlowStep(InstallUrl(connection, state), null, await StatusAsync(false, ct));
    }

    /// <summary>Starts authorization directly, for an App that is already installed.</summary>
    public async Task<GitHubAppFlowStep> StartAuthorizationAsync(StartGitHubAppStepRequest request, CancellationToken ct)
    {
        var connection = await RequireConnectionAsync(ct);
        return new GitHubAppFlowStep(AuthorizeUrl(connection, ValidOrigin(request.Origin), null), null, await StatusAsync(false, ct));
    }

    /// <summary>Resumes the flow with what GitHub put into the return URL.</summary>
    public async Task<GitHubAppFlowStep> AdvanceAsync(AdvanceGitHubAppFlowRequest request, string actor, CancellationToken ct)
    {
        PruneFlows();
        Flow? flow = null;
        // A state is single use: a replayed return URL finds nothing.
        if (!string.IsNullOrEmpty(request.State)) _flows.TryRemove(request.State, out flow);
        if (flow is not null && flow.ExpiresAtUtc <= Now) flow = null;

        if (flow?.Kind == FlowKind.Registration)
            return await FinishRegistrationAsync(flow, request.Code, actor, ct);
        if (flow?.Kind == FlowKind.Authorization)
            return await FinishAuthorizationAsync(flow, request.Code, actor, ct);
        // GitHub sends no state when the person changes a selection on its own
        // settings page, so an installation return without a flow stays valid.
        if (request.InstallationId is { } installationId)
            return await FinishInstallationAsync(flow?.Origin ?? ValidOrigin(request.Origin), installationId, request.SetupAction, ct);
        if (string.Equals(request.SetupAction, "request", StringComparison.Ordinal))
            return new GitHubAppFlowStep(null,
                "GitHub asked an organization owner to approve the installation. Come back here after the approval.",
                await StatusAsync(false, ct));
        throw new GitHubAppException("flow-expired", "This GitHub step expired or was already used. Start it again.", 409);
    }

    public async Task DisconnectAsync(string actor, CancellationToken ct)
    {
        GitHubInstallationToken[] issued;
        await _connectionWrites.WaitAsync(ct);
        try
        {
            await _vault.DeleteSecretsAsync(actor, ct);
            await _vault.DeleteConnectionAsync(ct);
            // In one step: a token minted before this is revoked below, one after it never cached.
            lock (_cache)
            {
                issued = _tokens.Values.ToArray();
                ForgetCaches();
            }
            _hosts.Clear();
        }
        finally { _connectionWrites.Release(); }
        // The tokens expire within the hour; a disconnect must not hang on GitHub.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RevokeBudget);
        await Task.WhenAll(issued.Select(async token =>
        {
            try { await _github.RevokeInstallationTokenAsync(token.Value, budget.Token); }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) { }
        }));
        ConnectionChanged?.Invoke();
    }

    public async Task<GitHubAppStatus> StatusAsync(bool refresh, CancellationToken ct)
    {
        var connection = await _vault.LoadConnectionAsync(ct);
        var hosts = _hosts.Values.OrderBy(host => host.RunnerId, StringComparer.Ordinal).ToArray();
        var catalog = await _catalog.ListAsync(ct);
        if (connection is null)
        {
            return new GitHubAppStatus(false, null, null, null, null, null, null, null, [],
                catalog.Select(item => Uncovered(item, connected: false)).ToArray(), hosts, null);
        }
        // Any caller of this page can ask for a refresh; GitHub's quota is not theirs to spend.
        if (refresh)
        {
            lock (_cache)
            {
                if (Now - _lastRefreshUtc >= RefreshInterval)
                {
                    _coverage.Clear();
                    _visibilityCheckedAtUtc = DateTime.MinValue;
                    _lastRefreshUtc = Now;
                }
            }
        }
        string? problem = null;
        GitHubAppSecrets? secrets = null;
        try { secrets = await ReadSecretsAsync(ct); }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            problem = "The App's private key cannot be read from the secret store.";
        }
        problem ??= secrets is null ? "The App's private key is missing from the secret store." : null;
        var repositories = catalog.Select(item => Uncovered(item, connected: true)).ToArray();
        if (secrets is { } key)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(StatusBudget);
            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, catalog.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = GitHubParallelism, CancellationToken = budget.Token },
                    async (index, token) => repositories[index] = await CoverageAsync(catalog[index], connection, key, token));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                problem ??= "GitHub did not answer in time; some repositories show as unknown.";
            }
            try { await RefreshRepositoryVisibilityAsync(budget.Token); }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Without an answer the page shows what GitHub reported before.
            }
            for (var index = 0; index < repositories.Length; index++)
                if (repositories[index].State == GitHubAppCoverageStates.Covered
                    && ReportedPrivate(repositories[index].RepositoryUrl) is { } reported)
                    repositories[index] = repositories[index] with
                    {
                        Visibility = reported ? ReviewRepositoryAccessPolicy.PrivateVisibility : ReviewRepositoryAccessPolicy.PublicVisibility,
                    };
        }
        return new GitHubAppStatus(true, connection.AppId, connection.Slug, connection.Name, connection.OwnerLogin,
            connection.HtmlUrl, InstallUrl(connection, null), connection.ConnectedAtUtc,
            connection.Installations, repositories, hosts, problem);
    }

    /// <summary>
    /// GitHub's private flag for a repository an adopted installation lists, or
    /// null when no adopted installation lists it or GitHub has not answered yet.
    /// Never waits for GitHub; <see cref="RefreshRepositoryVisibilityAsync"/> fills it.
    /// </summary>
    public bool? ReportedPrivate(string? repositoryUrl)
    {
        if (GitHubRepositoryName.TryParse(repositoryUrl) is not { } repository) return null;
        lock (_cache) return _visibility.TryGetValue(repository.Canonical, out var known) ? known.Private : null;
    }

    /// <summary>
    /// Reads the repository list of every adopted installation again, at most once
    /// per coverage lifetime unless <paramref name="force"/>d. An installation GitHub
    /// does not answer for keeps what it reported before, so an outage can never
    /// turn a private repository public. A repository no adopted installation lists
    /// any more is dropped: its project setting decides again.
    /// </summary>
    public async Task RefreshRepositoryVisibilityAsync(CancellationToken ct, bool force = false)
    {
        await _visibilityRefresh.WaitAsync(ct);
        try
        {
            var generation = CurrentGeneration();
            lock (_cache)
            {
                if (!force && Now - _visibilityCheckedAtUtc < CoverageLifetime) return;
            }
            var connection = await _vault.LoadConnectionAsync(ct);
            if (connection is null) return;
            var secrets = await ReadSecretsAsync(ct);
            if (secrets is null) return;
            var listed = new Dictionary<string, (bool Private, long InstallationId)>(StringComparer.Ordinal);
            var unanswered = new HashSet<long>();
            foreach (var installation in connection.Installations)
            {
                try
                {
                    foreach (var repository in await _github.ListInstallationRepositoriesAsync(
                                 connection.AppId, secrets.PrivateKeyPem, installation.Id, ct))
                        // Two installations listing one repository: private wins.
                        if (!listed.TryGetValue(repository.Canonical, out var seen) || !seen.Private)
                            listed[repository.Canonical] = (repository.Private, installation.Id);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    unanswered.Add(installation.Id);
                }
            }
            lock (_cache)
            {
                // A disconnect while this ran: what it learned belongs to the old connection.
                if (generation != _generation) return;
                foreach (var (key, known) in _visibility)
                    if (unanswered.Contains(known.InstallationId)
                        && (!listed.TryGetValue(key, out var fresh) || (known.Private && !fresh.Private)))
                        listed[key] = known;
                _visibility.Clear();
                foreach (var (key, value) in listed) _visibility[key] = value;
                _visibilityCheckedAtUtc = Now;
            }
        }
        finally { _visibilityRefresh.Release(); }
    }

    /// <summary>
    /// One token per registered repository the App reaches, reused until it nears
    /// expiry. A repository GitHub refuses is left out, so one bad entry cannot cost
    /// a host its other repositories, and cached tokens never wait for GitHub.
    /// <paramref name="admits"/> narrows the set to the repositories a host's
    /// credential is bound to (SM-9); a repository it refuses is never minted.
    /// </summary>
    public async Task<IReadOnlyList<RepositoryCredential>> IssueAllAsync(
        string access, string? runnerId, CancellationToken ct, bool includeStudioOnly = false,
        Func<RegisteredRepository, bool>? admits = null)
        => (await IssueAllWithFailuresAsync(access, runnerId, ct, includeStudioOnly, admits)).Credentials;

    /// <summary>
    /// <see cref="IssueAllAsync"/>, plus the repositories whose token GitHub did not
    /// issue this round (an error or the round's budget). A repository no
    /// installation reaches is not a failure. The host's own Git uses it to know
    /// whether a private repository is still without its token.
    /// </summary>
    public async Task<GitHubAppIssueRound> IssueAllWithFailuresAsync(
        string access, string? runnerId, CancellationToken ct, bool includeStudioOnly = false,
        Func<RegisteredRepository, bool>? admits = null)
    {
        var level = RepositoryAccess.Normalize(access)
            ?? throw new GitHubAppException("invalid-access", "Access must be 'read' or 'write'.");
        // Before anything is read: what this call learns belongs to this connection.
        var generation = CurrentGeneration();
        var connection = await _vault.LoadConnectionAsync(ct);
        if (connection is null) return GitHubAppIssueRound.Empty;
        var repositories = (await _catalog.ListAsync(ct))
            .Where(item => includeStudioOnly || !item.StudioOnly)
            .Where(item => admits is null || admits(item))
            .Select(item => GitHubRepositoryName.TryParse(item.RepositoryUrl))
            .OfType<GitHubRepositoryName>()
            .DistinctBy(name => name.Canonical, StringComparer.Ordinal)
            .ToArray();
        var secrets = await ReadSecretsAsync(ct);
        if (secrets is null) return GitHubAppIssueRound.Empty;

        var slots = new RepositoryCredential?[repositories.Length];
        var answered = new bool[repositories.Length];
        var missing = new List<int>();
        for (var index = 0; index < repositories.Length; index++)
        {
            if (Cached(repositories[index], level) is { } token) slots[index] = Credential(repositories[index], token, level);
            else missing.Add(index);
        }
        if (missing.Count > 0)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(IssueBudget);
            try
            {
                await Parallel.ForEachAsync(missing,
                    new ParallelOptions { MaxDegreeOfParallelism = GitHubParallelism, CancellationToken = budget.Token },
                    async (index, token) =>
                    {
                        try
                        {
                            if (await IssueAsync(connection, secrets, repositories[index], level, generation, token) is { } issued)
                                slots[index] = Credential(repositories[index], issued, level);
                            answered[index] = true;
                        }
                        catch (Exception exception) when (exception is GitHubAppException or HttpRequestException
                                                              or TaskCanceledException && !token.IsCancellationRequested)
                        {
                            // The next round retries; the status page names the repository.
                            lock (_cache) _coverage.Remove(repositories[index].Canonical);
                        }
                    });
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Out of budget: the host takes what is ready and comes back.
            }
        }
        // A disconnect during this round revoked what it minted, and the host list starts over.
        if (CurrentGeneration() != generation) return GitHubAppIssueRound.Empty;
        var credentials = slots.OfType<RepositoryCredential>().ToArray();
        if (runnerId is not null)
            _hosts[runnerId + "|" + level] = new GitHubAppHostSync(runnerId, level, credentials.Length, Now);
        var failed = missing.Where(index => !answered[index]).Select(index => repositories[index].Canonical).ToArray();
        return new GitHubAppIssueRound(credentials, failed);
    }

    /// <summary>
    /// A token of its own for one work attempt, never shared and never cached, so
    /// revoking it at the attempt's end touches nothing else. Null when the App
    /// does not reach the repository.
    /// </summary>
    public async Task<GitHubInstallationToken?> IssueDedicatedAsync(string repositoryUrl, string access, CancellationToken ct)
    {
        var level = RepositoryAccess.Normalize(access)
            ?? throw new GitHubAppException("invalid-access", "Access must be 'read' or 'write'.");
        if (GitHubRepositoryName.TryParse(repositoryUrl) is not { } repository) return null;
        if (await _vault.LoadConnectionAsync(ct) is not { } connection) return null;
        if (await ReadSecretsAsync(ct) is not { } secrets) return null;
        var installationId = await InstallationAsync(connection, secrets, repository, ct);
        if (installationId is not { } id || connection.Installations.All(item => item.Id != id)) return null;
        return await _github.CreateInstallationTokenAsync(connection.AppId, secrets.PrivateKeyPem, id, repository,
            Permissions(connection, level), ct);
    }

    private async Task<GitHubAppFlowStep> FinishRegistrationAsync(Flow flow, string? code, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new GitHubAppException("invalid-request", "GitHub's return is missing its code.");
        // Two registrations can be open at once; the second must not replace the first.
        if (await _vault.LoadConnectionAsync(ct) is not null) throw AlreadyConnected();
        var conversion = await _github.ConvertManifestAsync(code, ct);
        GitHubAppConnection connection;
        await _connectionWrites.WaitAsync(ct);
        try
        {
            if (await _vault.LoadConnectionAsync(ct) is not null) throw AlreadyConnected();
            connection = new GitHubAppConnection(
                conversion.AppId, conversion.Slug, conversion.Name, conversion.ClientId, conversion.OwnerLogin,
                conversion.HtmlUrl, Now, actor, conversion.WritePermissions, []);
            try
            {
                await _vault.WriteSecretsAsync(conversion.Secrets, actor, ct);
                await _vault.SaveConnectionAsync(connection, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The App now exists at GitHub and the key cannot be asked for again, so name what to remove.
                throw new GitHubAppException("registration-not-saved",
                    $"GitHub created the App {conversion.Name}, but Studio could not save it: {exception.Message} "
                    + $"Delete the App at {conversion.HtmlUrl} and connect again.", 500);
            }
            ForgetCaches();
        }
        finally { _connectionWrites.Release(); }
        ConnectionChanged?.Invoke();
        var state = NewFlow(new Flow(FlowKind.Installation, flow.Origin, null, null, Now + FlowLifetime));
        return new GitHubAppFlowStep(InstallUrl(connection, state),
            "The App exists. Next, choose the repositories it may reach.", await StatusAsync(false, ct));
    }

    private async Task<GitHubAppFlowStep> FinishInstallationAsync(
        string origin, long installationId, string? setupAction, CancellationToken ct)
    {
        var connection = await RequireConnectionAsync(ct);
        if (string.Equals(setupAction, "request", StringComparison.Ordinal))
            return new GitHubAppFlowStep(null,
                "GitHub asked an organization owner to approve the installation. Come back here after the approval.",
                await StatusAsync(false, ct));
        // A selection change on a known installation needs no new consent.
        if (connection.Installations.Any(item => item.Id == installationId))
        {
            ForgetCoverage();
            ConnectionChanged?.Invoke();
            return new GitHubAppFlowStep(null, "The repository selection changed.", await StatusAsync(false, ct));
        }
        return new GitHubAppFlowStep(AuthorizeUrl(connection, origin, installationId),
            "Next, confirm the installation with your GitHub account.", await StatusAsync(false, ct));
    }

    private async Task<GitHubAppFlowStep> FinishAuthorizationAsync(Flow flow, string? code, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new GitHubAppException("invalid-request", "GitHub's return is missing its code.");
        var connection = await RequireConnectionAsync(ct);
        var secrets = await ReadSecretsAsync(ct)
                      ?? throw new GitHubAppException("secrets-missing", "The App's client secret is missing from the secret store.", 409);
        var userToken = await _github.ExchangeUserCodeAsync(connection.ClientId, secrets.ClientSecret, code,
            flow.Origin + ReturnPath, flow.CodeVerifier!, ct);
        IReadOnlyList<GitHubUserInstallation> reachable;
        try
        {
            reachable = (await _github.ListUserInstallationsAsync(userToken, ct))
                .Where(item => item.AppId == connection.AppId)
                .ToArray();
        }
        finally
        {
            // GitHub expires it after eight hours; nothing stores it.
            try { await _github.RevokeUserTokenAsync(connection.ClientId, secrets.ClientSecret, userToken, ct); }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
        }
        if (flow.InstallationId is { } claimed && reachable.All(item => item.Id != claimed))
            throw new GitHubAppException("installation-not-reachable",
                "This GitHub account does not reach that installation, so it was not connected.", 403);
        await _connectionWrites.WaitAsync(ct);
        try
        {
            var current = await _vault.LoadConnectionAsync(ct)
                          ?? throw new GitHubAppException("not-connected", "No GitHub App is connected.", 409);
            var adopted = current.Installations.ToDictionary(item => item.Id);
            foreach (var item in reachable)
                adopted.TryAdd(item.Id, new GitHubAppInstallation(item.Id, item.Account, item.AccountType, Now, actor));
            await _vault.SaveConnectionAsync(current with
            {
                Installations = adopted.Values.OrderBy(item => item.Account, StringComparer.OrdinalIgnoreCase).ToArray(),
            }, ct);
            ForgetCoverage();
        }
        finally { _connectionWrites.Release(); }
        ConnectionChanged?.Invoke();
        return new GitHubAppFlowStep(null,
            reachable.Count == 0
                ? "Your GitHub account reaches no installation of this App yet."
                : "GitHub is connected.",
            await StatusAsync(false, ct));
    }

    private long CurrentGeneration()
    {
        lock (_cache) return _generation;
    }

    private GitHubInstallationToken? Cached(GitHubRepositoryName repository, string access)
    {
        lock (_cache)
        {
            return _tokens.TryGetValue(repository.Canonical + "|" + access, out var token)
                   && token.ExpiresAtUtc - Now >= MinimumRemaining
                ? token
                : null;
        }
    }

    /// <summary>One GitHub call per repository and level at a time; the others wait for its result.</summary>
    private async Task<GitHubInstallationToken?> IssueAsync(
        GitHubAppConnection connection, GitHubAppSecrets secrets, GitHubRepositoryName repository, string access,
        long generation, CancellationToken ct)
    {
        var key = repository.Canonical + "|" + access;
        var flight = _flights.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await flight.WaitAsync(ct);
        try
        {
            // The connection may have ended while this call waited for the flight.
            if (CurrentGeneration() != generation) return null;
            if (Cached(repository, access) is { } ready) return ready;
            var installationId = await InstallationAsync(connection, secrets, repository, ct);
            // An installation nobody proved is never used, even when the App reaches the repository.
            if (installationId is not { } id || connection.Installations.All(item => item.Id != id)) return null;
            var token = await _github.CreateInstallationTokenAsync(connection.AppId, secrets.PrivateKeyPem, id, repository,
                Permissions(connection, access), ct);
            bool current;
            lock (_cache)
            {
                current = generation == _generation;
                if (current) _tokens[key] = token;
            }
            if (current) return token;
            // Disconnected while this call ran: the token must not be handed out or outlive its hour.
            try { await _github.RevokeInstallationTokenAsync(token.Value, ct); }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) { }
            return null;
        }
        finally { flight.Release(); }
    }

    private static RepositoryCredential Credential(GitHubRepositoryName repository, GitHubInstallationToken token, string access)
        => new(repository.CloneUrl, CredentialUsername, token.Value, token.ExpiresAtUtc, access);

    /// <summary>What a token may do. Metadata is implied by every token; workflows only if the App holds it.</summary>
    private static IReadOnlyDictionary<string, string> Permissions(GitHubAppConnection connection, string access)
    {
        var permissions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contents"] = access,
            ["metadata"] = RepositoryAccess.Read,
        };
        if (access == RepositoryAccess.Write && connection.WritePermissions.Contains("workflows", StringComparer.Ordinal))
            permissions["workflows"] = RepositoryAccess.Write;
        return permissions;
    }

    private async Task<GitHubAppRepositoryCoverage> CoverageAsync(
        RegisteredRepository item, GitHubAppConnection connection, GitHubAppSecrets secrets, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.RepositoryUrl)) return Uncovered(item, connected: true);
        if (GitHubRepositoryName.TryParse(item.RepositoryUrl) is not { } repository) return Uncovered(item, connected: true);
        var url = WithoutUserInfo(item.RepositoryUrl);
        long? installationId;
        try { installationId = await InstallationAsync(connection, secrets, repository, ct); }
        catch (Exception exception) when (exception is GitHubAppException or HttpRequestException or TaskCanceledException
                                              && !ct.IsCancellationRequested)
        {
            return new GitHubAppRepositoryCoverage(item.Project, url, GitHubAppCoverageStates.Unknown, null,
                exception is GitHubAppException ? exception.Message : "GitHub could not be reached.");
        }
        if (installationId is null)
            return new GitHubAppRepositoryCoverage(item.Project, url, GitHubAppCoverageStates.NotInstalled, null,
                $"The App is not installed on {repository.Owner}/{repository.Name}.");
        return connection.Installations.Any(known => known.Id == installationId)
            ? new GitHubAppRepositoryCoverage(item.Project, url, GitHubAppCoverageStates.Covered, installationId, null)
            : new GitHubAppRepositoryCoverage(item.Project, url, GitHubAppCoverageStates.NotAdopted, installationId,
                "Installed, but not yet confirmed with your GitHub account.");
    }

    private async Task<long?> InstallationAsync(
        GitHubAppConnection connection, GitHubAppSecrets secrets, GitHubRepositoryName repository, CancellationToken ct)
    {
        lock (_cache)
        {
            if (_coverage.TryGetValue(repository.Canonical, out var known) && Now - known.CheckedAtUtc < CoverageLifetime)
                return known.InstallationId;
        }
        var found = await _github.FindRepositoryInstallationAsync(connection.AppId, secrets.PrivateKeyPem, repository, ct);
        lock (_cache) _coverage[repository.Canonical] = (found, Now);
        return found;
    }

    private static GitHubAppRepositoryCoverage Uncovered(RegisteredRepository item, bool connected)
    {
        if (string.IsNullOrWhiteSpace(item.RepositoryUrl))
            return new GitHubAppRepositoryCoverage(item.Project, null, GitHubAppCoverageStates.NoRepositoryUrl, null,
                "The project registers no repository URL.");
        var url = WithoutUserInfo(item.RepositoryUrl);
        if (GitHubRepositoryName.TryParse(item.RepositoryUrl) is null)
            return new GitHubAppRepositoryCoverage(item.Project, url, GitHubAppCoverageStates.NotGitHub, null,
                "Not a github.com repository; the App cannot reach it.");
        return new GitHubAppRepositoryCoverage(item.Project, url,
            connected ? GitHubAppCoverageStates.Unknown : GitHubAppCoverageStates.NotInstalled, null, null);
    }

    /// <summary>A remote can carry a token in its URL. The page shows the repository, never that.</summary>
    private static string WithoutUserInfo(string url)
    {
        var start = url.IndexOf("://", StringComparison.Ordinal);
        if (start < 0) return url;
        start += 3;
        var at = url.IndexOf('@', start);
        var slash = url.IndexOf('/', start);
        return at >= 0 && (slash < 0 || at < slash) ? url[..start] + url[(at + 1)..] : url;
    }

    /// <summary>A rotation in the secret store takes effect within <see cref="SecretsLifetime"/>.</summary>
    private async Task<GitHubAppSecrets?> ReadSecretsAsync(CancellationToken ct)
    {
        lock (_cache)
        {
            if (_secrets is { } held && Now - held.ReadAtUtc < SecretsLifetime) return held.Secrets;
        }
        await _secretsRead.WaitAsync(ct);
        try
        {
            lock (_cache)
            {
                if (_secrets is { } held && Now - held.ReadAtUtc < SecretsLifetime) return held.Secrets;
            }
            var generation = CurrentGeneration();
            var read = await _vault.ReadSecretsAsync(ct);
            lock (_cache)
            {
                if (generation == _generation) _secrets = read is null ? null : (read, Now);
            }
            return read;
        }
        finally { _secretsRead.Release(); }
    }

    private void ForgetCaches()
    {
        lock (_cache)
        {
            _generation++;
            _tokens.Clear();
            _coverage.Clear();
            _visibility.Clear();
            _visibilityCheckedAtUtc = DateTime.MinValue;
            _secrets = null;
        }
    }

    private void ForgetCoverage()
    {
        lock (_cache)
        {
            _coverage.Clear();
            _visibilityCheckedAtUtc = DateTime.MinValue;
        }
    }

    private static GitHubAppException AlreadyConnected()
        => new("already-connected", "A GitHub App is already connected. Disconnect it before connecting another one.", 409);

    private async Task RequireWritableVaultAsync(CancellationToken ct)
    {
        try { await _vault.EnsureWritableAsync(ct); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new GitHubAppException("secret-store-unavailable",
                $"Studio cannot store the App's private key: {exception.Message} "
                + "Fix the secret store, then connect GitHub again. Nothing was created at GitHub.", 503);
        }
    }

    private async Task<GitHubAppConnection> RequireConnectionAsync(CancellationToken ct)
        => await _vault.LoadConnectionAsync(ct)
           ?? throw new GitHubAppException("not-connected", "No GitHub App is connected.", 409);

    private string AuthorizeUrl(GitHubAppConnection connection, string origin, long? installationId)
    {
        var verifier = GitHubAppClient.Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = NewFlow(new Flow(FlowKind.Authorization, origin, installationId, verifier, Now + FlowLifetime));
        var challenge = GitHubAppClient.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return "https://github.com/login/oauth/authorize"
               + "?client_id=" + Uri.EscapeDataString(connection.ClientId)
               + "&redirect_uri=" + Uri.EscapeDataString(origin + ReturnPath)
               + "&state=" + Uri.EscapeDataString(state)
               + "&code_challenge=" + Uri.EscapeDataString(challenge)
               + "&code_challenge_method=S256";
    }

    private static string InstallUrl(GitHubAppConnection connection, string? state)
        => $"https://github.com/apps/{Uri.EscapeDataString(connection.Slug)}/installations/new"
           + (state is null ? string.Empty : "?state=" + Uri.EscapeDataString(state));

    private string NewFlow(Flow flow)
    {
        PruneFlows();
        var state = GitHubAppClient.Base64Url(RandomNumberGenerator.GetBytes(32));
        _flows[state] = flow;
        return state;
    }

    private void PruneFlows()
    {
        foreach (var (state, flow) in _flows)
            if (flow.ExpiresAtUtc <= Now) _flows.TryRemove(state, out _);
    }

    /// <summary>
    /// The address GitHub sends the browser back to. Plain http only for a
    /// loopback Studio; nothing but scheme, host and port is accepted.
    /// </summary>
    private static string ValidOrigin(string? origin)
    {
        if (!Uri.TryCreate(origin?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0
            || uri.AbsolutePath != "/"
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || (uri.Scheme == "http" && !uri.IsLoopback))
            throw new GitHubAppException("invalid-origin",
                "GitHub can only return to this Studio's own https address, or to a loopback address.");
        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        return $"{uri.Scheme}://{uri.Host}{port}";
    }

    private static bool ValidAccount(string account)
        => account.Length is > 0 and <= 39
           && account[0] != '-'
           && account.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');

    private static string Limit(string value, int length)
        => value.Length <= length ? value : value[..length].TrimEnd('-');

    private enum FlowKind { Registration, Installation, Authorization }

    private sealed record Flow(FlowKind Kind, string Origin, long? InstallationId, string? CodeVerifier, DateTime ExpiresAtUtc);
}

/// <summary>One issuing round: the credentials, and the repositories GitHub left without one.</summary>
public sealed record GitHubAppIssueRound(
    IReadOnlyList<RepositoryCredential> Credentials,
    IReadOnlyList<string> Failed)
{
    public static GitHubAppIssueRound Empty { get; } = new([], []);
}
