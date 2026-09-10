using System.Net;

namespace AgentStudio.Connector;

public sealed record ConnectorOptions(
    string Mode,
    string Authority,
    string StudioOrigin,
    string UpstreamMode,
    Uri UpstreamBaseUri,
    string? TlsCertificateSha256,
    long Generation,
    string MaskedUpstreamName)
{
    public const string SectionName = "Connector";
    public const string NativeMode = "native";
    public const string DockerMode = "docker";
    public const string DefaultAuthority = "[::1]:5031";
    public const string DefaultStudioOrigin = "http://[::1]:4011";

    public static ConnectorOptions Load(IConfiguration configuration)
    {
        var mode = configuration[$"{SectionName}:Mode"]?.Trim().ToLowerInvariant() ?? NativeMode;
        if (mode is not (NativeMode or DockerMode))
            throw new InvalidOperationException("Connector:Mode must be 'native' or 'docker'.");

        var authority = configuration[$"{SectionName}:Authority"]?.Trim() ?? DefaultAuthority;
        if (!string.Equals(authority, DefaultAuthority, StringComparison.Ordinal))
            throw new InvalidOperationException($"The connector authority must be exactly '{DefaultAuthority}'.");

        var origin = configuration[$"{SectionName}:StudioOrigin"]?.Trim() ?? DefaultStudioOrigin;
        if (!string.Equals(origin, DefaultStudioOrigin, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Connector:StudioOrigin must be the bracketed loopback origin http://[::1]:4011.");
        }

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
            && !IPAddress.TryParse(baseUri.Host, out var address))
            throw new InvalidOperationException("A clear-text local upstream must use a literal loopback address.");
        if (upstreamMode == "local"
            && baseUri.Scheme == Uri.UriSchemeHttp
            && (!IPAddress.TryParse(baseUri.Host, out var localAddress) || !IPAddress.IsLoopback(localAddress)))
            throw new InvalidOperationException("A clear-text local upstream must use a loopback address.");

        var tlsPin = NormalizeTlsPin(configuration[$"{SectionName}:Upstream:TlsCertificateSha256"]);
        var generation = configuration.GetValue<long?>($"{SectionName}:Upstream:Generation") ?? 1;
        if (generation < 1)
            throw new InvalidOperationException("Connector:Upstream:Generation must be positive.");
        var maskedName = configuration[$"{SectionName}:Upstream:MaskedName"]?.Trim();
        if (string.IsNullOrWhiteSpace(maskedName))
            maskedName = upstreamMode == "remote" ? "remote-task-server" : "local-task-server";

        return new ConnectorOptions(
            mode,
            authority,
            origin,
            upstreamMode,
            new Uri(baseUri.ToString().TrimEnd('/') + "/", UriKind.Absolute),
            tlsPin,
            generation,
            maskedName);
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
