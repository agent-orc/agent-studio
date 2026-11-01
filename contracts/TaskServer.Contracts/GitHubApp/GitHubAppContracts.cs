namespace AgentStudio.TaskServer.Contracts.GitHubApp;

/// <summary>What a repository credential may do. Review hosts read; coding and integration hosts write.</summary>
public static class RepositoryAccess
{
    public const string Read = "read";
    public const string Write = "write";

    /// <summary>Null when <paramref name="value"/> is neither level.</summary>
    public static string? Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            Read => Read,
            Write => Write,
            _ => null,
        };
}

/// <summary>How one registered repository relates to the connected GitHub App.</summary>
public static class GitHubAppCoverageStates
{
    /// <summary>An adopted installation reaches the repository; tokens are issued for it.</summary>
    public const string Covered = "covered";
    /// <summary>The App is not installed on this repository.</summary>
    public const string NotInstalled = "not-installed";
    /// <summary>An installation reaches the repository, but nobody proved it belongs to this Studio.</summary>
    public const string NotAdopted = "not-adopted";
    /// <summary>The project registers no repository URL.</summary>
    public const string NoRepositoryUrl = "no-repository-url";
    /// <summary>The registered URL is not a github.com repository.</summary>
    public const string NotGitHub = "not-github";
    /// <summary>GitHub could not be asked.</summary>
    public const string Unknown = "unknown";
}

public sealed record StartGitHubAppRegistrationRequest(string? Origin, string? Organization, bool AnyAccount = true);

/// <summary>The form the browser posts to GitHub. It holds no secret; GitHub shows it to the person for consent.</summary>
public sealed record GitHubAppRegistrationForm(string ActionUrl, string Manifest, string State);

public sealed record StartGitHubAppStepRequest(string? Origin);

/// <summary>What GitHub handed back in the browser's return URL, forwarded by the page that received it.</summary>
public sealed record AdvanceGitHubAppFlowRequest(
    string? Origin,
    string? State,
    string? Code,
    long? InstallationId,
    string? SetupAction);

/// <summary>Connects an App created by hand. The values are write-only and never returned.</summary>
public sealed record ConnectExistingGitHubAppRequest(
    long AppId,
    string? ClientId,
    string? ClientSecret,
    string? PrivateKeyPem)
{
    public override string ToString() => $"ConnectExistingGitHubAppRequest {{ AppId = {AppId} }}";
}

public sealed record GitHubAppInstallation(
    long Id,
    string Account,
    string AccountType,
    DateTime AdoptedAtUtc,
    string AdoptedBy);

public sealed record GitHubAppRepositoryCoverage(
    string Project,
    string? RepositoryUrl,
    string State,
    long? InstallationId,
    string? Detail);

public sealed record GitHubAppHostSync(
    string RunnerId,
    string Access,
    int Repositories,
    DateTime LastSyncUtc);

/// <summary>Everything the settings page shows. Public identifiers only, never a key or a token.</summary>
public sealed record GitHubAppStatus(
    bool Connected,
    long? AppId,
    string? Slug,
    string? Name,
    string? OwnerLogin,
    string? HtmlUrl,
    string? InstallUrl,
    DateTime? ConnectedAtUtc,
    IReadOnlyList<GitHubAppInstallation> Installations,
    IReadOnlyList<GitHubAppRepositoryCoverage> Repositories,
    IReadOnlyList<GitHubAppHostSync> Hosts,
    string? Problem);

/// <summary>One step of the browser flow. <see cref="NextUrl"/> is where the page navigates next.</summary>
public sealed record GitHubAppFlowStep(string? NextUrl, string? Message, GitHubAppStatus Status);

public sealed record RepositoryCredentialsRequest(string? Access);

/// <summary>One short-lived token scoped to exactly one repository.</summary>
public sealed record RepositoryCredential(
    string RepositoryUrl,
    string Username,
    string Token,
    DateTime ExpiresAtUtc,
    string Access)
{
    // The token never reaches a log line or an exception message.
    public override string ToString()
        => $"RepositoryCredential {{ {RepositoryUrl}, {Access}, expires {ExpiresAtUtc:O} }}";
}

public sealed record RepositoryCredentialsResponse(
    IReadOnlyList<RepositoryCredential> Credentials,
    int RefreshAfterSeconds);

/// <summary>The long-lived App material. Stored only through <see cref="IGitHubAppVault"/>.</summary>
public sealed record GitHubAppSecrets(string PrivateKeyPem, string ClientSecret)
{
    public override string ToString() => "GitHubAppSecrets { }";
}

/// <summary>The connection's public metadata. It holds no secret and may be read by the settings page.</summary>
public sealed record GitHubAppConnection(
    long AppId,
    string Slug,
    string Name,
    string ClientId,
    string OwnerLogin,
    string HtmlUrl,
    DateTime ConnectedAtUtc,
    string ConnectedBy,
    IReadOnlyList<string> WritePermissions,
    IReadOnlyList<GitHubAppInstallation> Installations);

/// <summary>A failure the page can name. The message never quotes a secret.</summary>
public sealed class GitHubAppException(string code, string message, int statusCode = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

/// <summary>Where a host keeps the App connection. The Studio uses its secret provider, a Task Server its root store.</summary>
public interface IGitHubAppVault
{
    Task<GitHubAppConnection?> LoadConnectionAsync(CancellationToken ct);
    Task SaveConnectionAsync(GitHubAppConnection connection, CancellationToken ct);
    Task DeleteConnectionAsync(CancellationToken ct);
    Task<GitHubAppSecrets?> ReadSecretsAsync(CancellationToken ct);
    Task WriteSecretsAsync(GitHubAppSecrets secrets, string actor, CancellationToken ct);
    Task DeleteSecretsAsync(string actor, CancellationToken ct);

    /// <summary>Throws when <see cref="WriteSecretsAsync"/> could not store the App's secrets now, for example without a key.</summary>
    Task EnsureWritableAsync(CancellationToken ct);
}

/// <summary>
/// One repository of this installation. <paramref name="StudioOnly"/> marks one
/// the Studio host itself publishes, such as the workspace evidence repository:
/// it is listed and served to that host, but never handed to a runner host.
/// </summary>
public sealed record RegisteredRepository(string Project, string? RepositoryUrl, bool StudioOnly = false);

/// <summary>The repositories this installation's projects register.</summary>
public interface IRegisteredRepositoryCatalog
{
    Task<IReadOnlyList<RegisteredRepository>> ListAsync(CancellationToken ct);
}
