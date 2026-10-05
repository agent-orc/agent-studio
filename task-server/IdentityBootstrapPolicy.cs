using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Pure I05 decisions (docs/operations/deployment-story/index.html, D5 option A). The
/// store reads current state, asks one of these functions, and then performs
/// the bounded write. No I/O happens here.
/// </summary>
public static class OwnerBootstrapPolicy
{
    public enum ArmDecision
    {
        /// <summary>No owner and no armed code: adopt the installer file hash.</summary>
        Arm,
        /// <summary>The armed hash matches the installer file: nothing to do.</summary>
        AlreadyArmed,
        /// <summary>
        /// The installer file differs from the armed hash. Re-running the
        /// installer must not rotate the code implicitly, so the armed hash is
        /// kept and the mismatch is reported.
        /// </summary>
        KeepArmedIgnoreDifferentFile,
        /// <summary>An owner exists; bootstrap is closed and never re-armed.</summary>
        Closed,
        /// <summary>No installer file was configured.</summary>
        NoCode,
    }

    public static ArmDecision DecideArm(bool ownerExists, string? armedHash, string? fileHash)
    {
        if (ownerExists) return ArmDecision.Closed;
        if (fileHash is null) return ArmDecision.NoCode;
        if (armedHash is null) return ArmDecision.Arm;
        return string.Equals(armedHash, fileHash, StringComparison.Ordinal)
            ? ArmDecision.AlreadyArmed
            : ArmDecision.KeepArmedIgnoreDifferentFile;
    }

    public enum AdmitDecision
    {
        Admit,
        AlreadyBootstrapped,
        CodeRequired,
        CodeNotArmed,
        CodeInvalid,
    }

    /// <summary>
    /// <paramref name="requiresCode"/> is true for every authenticated
    /// (networked) installation; only a loopback AUTH=none development store
    /// keeps first-caller bootstrap.
    /// </summary>
    public static AdmitDecision DecideAdmit(
        bool ownerExists,
        bool requiresCode,
        string? armedHash,
        bool presentedCodeMatches,
        bool codePresented)
    {
        if (ownerExists) return AdmitDecision.AlreadyBootstrapped;
        if (!requiresCode && !codePresented && armedHash is null) return AdmitDecision.Admit;
        if (!codePresented) return AdmitDecision.CodeRequired;
        if (armedHash is null) return AdmitDecision.CodeNotArmed;
        return presentedCodeMatches ? AdmitDecision.Admit : AdmitDecision.CodeInvalid;
    }
}

public static class EnrolmentExchangePolicy
{
    public enum Decision
    {
        Issue,
        Unknown,
        AlreadyConsumed,
        Revoked,
        Expired,
        InstallationMismatch,
        PrincipalExists,
    }

    /// <summary>
    /// Evaluation order matters: a consumed code is reported as consumed even
    /// after expiry, so a stolen replay is visible as a replay.
    /// </summary>
    public static Decision Decide(
        bool found,
        bool consumed,
        bool revoked,
        DateTime expiresAt,
        DateTime now,
        bool installationMatches,
        bool principalExists)
    {
        if (!found) return Decision.Unknown;
        if (consumed) return Decision.AlreadyConsumed;
        if (revoked) return Decision.Revoked;
        if (expiresAt <= now) return Decision.Expired;
        if (!installationMatches) return Decision.InstallationMismatch;
        if (principalExists) return Decision.PrincipalExists;
        return Decision.Issue;
    }
}

public static class StudioRolePolicy
{
    /// <summary>Only an owner administers identity, enrolment and project registration.</summary>
    public static bool MayAdministerIdentity(string? role)
        => string.Equals(role, StudioUserRoles.Owner, StringComparison.Ordinal);

    public static bool IsAssignableRole(string role)
        => role is StudioUserRoles.Operator or StudioUserRoles.Viewer;
}

/// <summary>
/// Canonical, credential-free repository origin. The registered project
/// origin is the only owner of a project's repository; a runner fallback
/// remote is diagnostic and never admits a project.
/// </summary>
public static class ProjectRepositoryPolicy
{
    public const string ProbeRequired = "repository-probe-required";
    public const string ProbeDenied = "repository-probe-denied";

    /// <summary>Registered projects require this runner's latest successful origin probe before a claim.</summary>
    public static string? ClaimRefusal(bool registered, string? latestProbeVerdict)
    {
        if (!registered) return null;
        if (string.IsNullOrEmpty(latestProbeVerdict)) return ProbeRequired;
        return latestProbeVerdict == ProjectRepositoryProbeVerdicts.Admitted ? null : ProbeDenied;
    }

    public static string Canonicalize(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
            throw new ArgumentException("Repository URL is required.");
        var value = repositoryUrl.Trim();
        if (value.Any(char.IsWhiteSpace))
            throw new ArgumentException("Repository URL must not contain whitespace.");

        if (!value.Contains("://", StringComparison.Ordinal))
            return CanonicalizeScpLike(value);

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new ArgumentException("Repository URL is not a valid absolute URL.");
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("https" or "ssh"))
            throw new ArgumentException("Repository URL must use https or ssh; local and cleartext origins are not canonical.");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Repository URL must not carry a query or fragment.");
        var userInfo = uri.UserInfo;
        if (scheme == "https" && userInfo.Length > 0)
            throw new ArgumentException("Repository URL must not embed credentials.");
        if (scheme == "ssh" && (userInfo.Contains(':') || userInfo.Length > 0 && userInfo != "git"))
            throw new ArgumentException("SSH repository URL may name only the 'git' user and no password.");
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length <= 1)
            throw new ArgumentException("Repository URL must name a repository path.");
        var host = uri.IdnHost.ToLowerInvariant();
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        var user = scheme == "ssh" ? "git@" : string.Empty;
        return $"{scheme}://{user}{host}{port}{path}";
    }

    /// <summary>Compare two origins after canonicalization; invalid input never matches.</summary>
    public static bool SameOrigin(string registered, string? observed)
    {
        try
        {
            return string.Equals(Canonicalize(registered), Canonicalize(observed), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string DecideProbe(string registeredUrl, ProjectRepositoryProbeRequest probe)
    {
        if (probe.UsedFallbackRemote) return ProjectRepositoryProbeVerdicts.FallbackRemoteOnly;
        if (!SameOrigin(registeredUrl, probe.ObservedFetchUrl)
            || !SameOrigin(registeredUrl, probe.ObservedPushUrl))
            return ProjectRepositoryProbeVerdicts.OriginMismatch;
        if (!probe.FetchSucceeded) return ProjectRepositoryProbeVerdicts.FetchFailed;
        if (!probe.PushSucceeded) return ProjectRepositoryProbeVerdicts.PushFailed;
        return ProjectRepositoryProbeVerdicts.Admitted;
    }

    public static string RequireRef(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{label} is required.");
        var trimmed = value.Trim();
        if (trimmed.Length > 200
            || trimmed.StartsWith('-')
            || trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.Any(ch => char.IsWhiteSpace(ch) || ch is '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
            throw new ArgumentException($"{label} is not a valid Git ref name.");
        return trimmed;
    }

    private static string CanonicalizeScpLike(string value)
    {
        // git@host:owner/repo.git
        var at = value.IndexOf('@');
        var colon = value.IndexOf(':');
        if (at <= 0 || colon <= at + 1 || colon == value.Length - 1)
            throw new ArgumentException("Repository URL must be https://, ssh://, or git@host:path.");
        if (value[..at] != "git")
            throw new ArgumentException("SSH repository URL may name only the 'git' user and no password.");
        var host = value[(at + 1)..colon].ToLowerInvariant();
        var path = value[(colon + 1)..].TrimEnd('/');
        if (host.Length == 0 || path.Length == 0 || path.StartsWith('/') && path.Length == 1)
            throw new ArgumentException("Repository URL must name a host and repository path.");
        return $"ssh://git@{host}/{path.TrimStart('/')}";
    }
}
