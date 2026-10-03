using System.Net;

namespace AgentStudio.Connector;

public sealed record ConnectorOptions(
    string Mode,
    string Authority,
    IReadOnlyList<string> StudioOrigins,
    string UpstreamMode,
    Uri UpstreamBaseUri,
    string? TlsCertificateSha256,
    long Generation,
    string MaskedUpstreamName,
    string CredentialTarget,
    string? CredentialFile,
    TimeSpan CredentialRefreshInterval,
    TimeSpan SessionLifetime)
{
    public const string SectionName = "Connector";
    public const string NativeMode = "native";
    public const string DockerMode = "docker";
    public const string DefaultAuthority = "[::1]:5031";
    public const string DefaultStudioOrigin = "http://localhost:4011";
    public const string BracketedLoopbackStudioOrigin = "http://[::1]:4011";
    public const string DefaultCredentialTarget = "AgentStudio/TaskServer/studio-robert-windows";
    public const string CredentialTargetPrefix = "AgentStudio/TaskServer/";
    public static readonly TimeSpan DefaultCredentialRefreshInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromHours(12);

    /// <summary>
    /// Configuration keys that would carry the Studio credential value itself.
    /// The connector reads the credential only from Windows Credential Manager
    /// or an owner-only file, so any of these (typically set through an
    /// environment variable such as <c>Connector__BearerToken</c>) stops the
    /// boot instead of being silently ignored.
    /// </summary>
    internal static readonly string[] ForbiddenCredentialKeys =
    [
        $"{SectionName}:Credential",
        $"{SectionName}:Bearer",
        $"{SectionName}:BearerToken",
        $"{SectionName}:Token",
        $"{SectionName}:AuthToken",
        $"{SectionName}:Upstream:Credential",
        $"{SectionName}:Upstream:BearerToken",
        $"{SectionName}:Upstream:Token",
        $"{SectionName}:Upstream:AuthToken",
        "TaskServer:AuthToken",
        "TaskServer:AuthTokenFile",
        "TaskServer:BearerToken",
        "TaskServer:StudioBearerToken",
        "STUDIO_AUTH_TOKEN",
        "STUDIO_AUTH_TOKEN_FILE",
    ];

    public static IReadOnlyList<string> DefaultStudioOrigins { get; } =
        [DefaultStudioOrigin, BracketedLoopbackStudioOrigin];

    /// <summary>The primary Studio origin, used where one origin must be named.</summary>
    public string StudioOrigin => StudioOrigins[0];

    public bool IsStudioOrigin(string? origin)
        => origin is not null && StudioOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);

    public static ConnectorOptions Load(IConfiguration configuration)
    {
        RejectConfiguredCredential(configuration);

        var mode = configuration[$"{SectionName}:Mode"]?.Trim().ToLowerInvariant() ?? NativeMode;
        if (mode is not (NativeMode or DockerMode))
            throw new InvalidOperationException("Connector:Mode must be 'native' or 'docker'.");

        var authority = configuration[$"{SectionName}:Authority"]?.Trim() ?? DefaultAuthority;
        if (!string.Equals(authority, DefaultAuthority, StringComparison.Ordinal))
            throw new InvalidOperationException($"The connector authority must be exactly '{DefaultAuthority}'.");

        var origins = LoadStudioOrigins(configuration);

        var upstreamMode = configuration[$"{SectionName}:Upstream:Mode"]?.Trim().ToLowerInvariant() ?? "remote";
        if (upstreamMode is not ("remote" or "local"))
            throw new InvalidOperationException("Connector:Upstream:Mode must be 'remote' or 'local'.");

        var baseUrl = configuration[$"{SectionName}:Upstream:BaseUrl"]?.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Connector:Upstream:BaseUrl must be an absolute HTTP or HTTPS URI.");
        if (upstreamMode == "remote" && baseUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("A remote connector upstream must use HTTPS.");
        if (upstreamMode == "local"
            && baseUri.Scheme == Uri.UriSchemeHttp
            && (!IPAddress.TryParse(baseUri.Host, out var localAddress) || !IPAddress.IsLoopback(localAddress)))
            throw new InvalidOperationException("A clear-text local upstream must use a literal loopback address.");

        var tlsPin = NormalizeTlsPin(configuration[$"{SectionName}:Upstream:TlsCertificateSha256"]);
        var generation = configuration.GetValue<long?>($"{SectionName}:Upstream:Generation") ?? 1;
        if (generation < 1)
            throw new InvalidOperationException("Connector:Upstream:Generation must be positive.");
        var maskedName = configuration[$"{SectionName}:Upstream:MaskedName"]?.Trim();
        if (string.IsNullOrWhiteSpace(maskedName))
            maskedName = upstreamMode == "remote" ? "remote-task-server" : "local-task-server";

        var credentialTarget = configuration[$"{SectionName}:CredentialTarget"]?.Trim();
        if (string.IsNullOrEmpty(credentialTarget)) credentialTarget = DefaultCredentialTarget;
        if (!credentialTarget.StartsWith(CredentialTargetPrefix, StringComparison.Ordinal)
            || credentialTarget.Length == CredentialTargetPrefix.Length
            || credentialTarget.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException(
                $"Connector:CredentialTarget must be a Windows Credential Manager target under '{CredentialTargetPrefix}'.");
        }

        var credentialFile = configuration[$"{SectionName}:CredentialFile"]?.Trim();
        if (string.IsNullOrEmpty(credentialFile)) credentialFile = DefaultCredentialFile();
        if (credentialFile is not null && !Path.IsPathFullyQualified(credentialFile))
            throw new InvalidOperationException("Connector:CredentialFile must be an absolute path.");

        var refreshSeconds = configuration.GetValue<int?>($"{SectionName}:CredentialRefreshSeconds")
            ?? (int)DefaultCredentialRefreshInterval.TotalSeconds;
        if (refreshSeconds is < 0 or > 300)
            throw new InvalidOperationException("Connector:CredentialRefreshSeconds must be between 0 and 300.");

        var sessionMinutes = configuration.GetValue<int?>($"{SectionName}:SessionLifetimeMinutes")
            ?? (int)DefaultSessionLifetime.TotalMinutes;
        if (sessionMinutes is < 5 or > 10080)
            throw new InvalidOperationException("Connector:SessionLifetimeMinutes must be between 5 and 10080.");

        return new ConnectorOptions(
            mode,
            authority,
            origins,
            upstreamMode,
            new Uri(baseUri.ToString().TrimEnd('/') + "/", UriKind.Absolute),
            tlsPin,
            generation,
            maskedName,
            credentialTarget,
            credentialFile,
            TimeSpan.FromSeconds(refreshSeconds),
            TimeSpan.FromMinutes(sessionMinutes));
    }

    /// <summary>
    /// Normalizes one configured Studio origin to its browser serialization
    /// (<c>scheme://host[:port]</c>, lower case) and rejects anything that is
    /// not a loopback origin: the connector serves exactly one local browser.
    /// </summary>
    public static string NormalizeLoopbackOrigin(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"Connector Studio origin '{value}' must be a bare http or https origin such as {DefaultStudioOrigin}.");
        }

        var loopback = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address));
        if (!loopback)
        {
            throw new InvalidOperationException(
                $"Connector Studio origin '{value}' is not a loopback origin. Only localhost, 127.0.0.1, and [::1] are accepted.");
        }

        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.Host}{port}".ToLowerInvariant();
    }

    private static IReadOnlyList<string> LoadStudioOrigins(IConfiguration configuration)
    {
        var configured = configuration.GetSection($"{SectionName}:StudioOrigins").GetChildren()
            .Select(child => child.Value)
            .Append(configuration[$"{SectionName}:StudioOrigin"])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => NormalizeLoopbackOrigin(value!))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return configured.Length == 0 ? DefaultStudioOrigins : configured;
    }

    private static void RejectConfiguredCredential(IConfiguration configuration)
    {
        var configured = ForbiddenCredentialKeys
            .Where(key => !string.IsNullOrWhiteSpace(configuration[key]))
            .ToArray();
        if (configured.Length == 0) return;
        throw new InvalidOperationException(
            $"The connector never reads the Studio credential from configuration or environment variables, but {string.Join(", ", configured)} is set. " +
            "Remove it and store the credential in Windows Credential Manager (set-studio-credential.ps1) or in the owner-only Connector:CredentialFile on Linux.");
    }

    private static string? DefaultCredentialFile()
    {
        if (OperatingSystem.IsWindows()) return null;
        var configHome = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(configHome)
            ? null
            : Path.Combine(configHome, "agent-studio", "studio-connector.credential");
    }

    private static string? NormalizeTlsPin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("Connector:Upstream:TlsCertificateSha256 must be a SHA-256 fingerprint.");
        return normalized;
    }
}

public static class ConnectorProfile
{
    public const string ConfigurationKey = "OrchestratorApi:Profile";

    public static bool IsEnabled(IConfiguration configuration)
        => string.Equals(configuration[ConfigurationKey]?.Trim(), "connector", StringComparison.OrdinalIgnoreCase);
}
