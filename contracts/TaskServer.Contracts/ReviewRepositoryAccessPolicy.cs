namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Read access to a private repository is a per-repository review capability
/// (AGT-3077). Review executors clone without the coding host's credentials on
/// purpose, so a private repository can only be materialized by an executor
/// whose profile holds a read-only credential scoped to that repository. Both
/// Task Server implementations refuse such a plan at claim time with the
/// missing <c>review:repository-read:&lt;host/path&gt;</c> key named, instead of
/// letting the clone fail and burn the infrastructure retry budget.
/// </summary>
public static class ReviewRepositoryAccessPolicy
{
    public const string ReadCapabilityPrefix = "review:repository-read:";
    public const string PublicVisibility = "public";
    public const string PrivateVisibility = "private";

    /// <summary>Null when <paramref name="value"/> is neither visibility.</summary>
    public static string? NormalizeVisibility(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            PublicVisibility => PublicVisibility,
            PrivateVisibility => PrivateVisibility,
            _ => null,
        };

    public static bool IsPrivate(string? visibility)
        => NormalizeVisibility(visibility) == PrivateVisibility;

    /// <summary>
    /// Credential-free, case-insensitive <c>host[:port]/owner/name</c> form of a
    /// Git remote, so <c>https://github.com/Owner/Repo.git</c>,
    /// <c>https://github.com/owner/repo</c> and <c>git@github.com:owner/repo.git</c>
    /// name the same repository. Null for a local path or an unparsable URL.
    /// </summary>
    public static string? CanonicalRepository(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl)) return null;
        var trimmed = repositoryUrl.Trim();
        string authority;
        string path;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme is "https" or "http" or "ssh" or "git")
        {
            authority = uri.Authority;
            path = uri.AbsolutePath;
        }
        else
        {
            // scp-like syntax: [user@]host:owner/name(.git)
            var colon = trimmed.IndexOf(':');
            if (colon <= 0 || trimmed.Contains("://", StringComparison.Ordinal)) return null;
            var host = trimmed[..colon];
            var at = host.LastIndexOf('@');
            authority = at >= 0 ? host[(at + 1)..] : host;
            path = trimmed[(colon + 1)..];
            // A one-letter "host" is a Windows drive, not a remote.
            if (authority.Length <= 1 || authority.Contains('/') || authority.Contains('\\')
                || path.Contains('\\')) return null;
        }
        path = Uri.UnescapeDataString(path).Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        path = path.TrimEnd('/');
        if (path.Length == 0 || path.Any(char.IsWhiteSpace)) return null;
        return $"{authority}/{path}".ToLowerInvariant();
    }

    public static string? ReadCapability(string? repositoryUrl)
        => CanonicalRepository(repositoryUrl) is { } canonical ? ReadCapabilityPrefix + canonical : null;

    /// <summary>
    /// The repository-read key <paramref name="plan"/> requires for
    /// <paramref name="repositoryUrl"/>; null for a public plan or when the
    /// repository is not yet known (the caller then rechecks once the subject
    /// URL is resolved).
    /// </summary>
    public static string? RequiredCapability(ReviewPlanDto plan, string? repositoryUrl)
        => plan.PrivateRepository ? ReadCapability(repositoryUrl) : null;

    public static IReadOnlyList<string> MissingCapabilities(
        ReviewPlanDto plan,
        string? repositoryUrl,
        IReadOnlySet<string> capabilities)
        => RequiredCapability(plan, repositoryUrl) is { } key && !capabilities.Contains(key) ? [key] : [];

    public static bool Supports(ReviewPlanDto plan, string? repositoryUrl, IReadOnlySet<string> capabilities)
        => MissingCapabilities(plan, repositoryUrl, capabilities).Count == 0;

    /// <summary>The claim-refusal sentence an operator reads on the empty claim.</summary>
    public static string RefusalMessage(string capability)
        => "The review executor holds no read credential for private repository "
           + $"'{capability[ReadCapabilityPrefix.Length..]}'. Add a read-only credential scoped to it "
           + "to RUNNER_REVIEW_GIT_CREDENTIALS_FILE on a review host (see the Remote Review "
           + "Executor section of docs/operations/setup/linux-runner-host.md).";
}
