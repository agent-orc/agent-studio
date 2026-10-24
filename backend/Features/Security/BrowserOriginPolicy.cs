namespace AgentStudio.Security;

public sealed record BrowserOriginDecision(bool Allowed, string? Code = null, string? Message = null)
{
    public static readonly BrowserOriginDecision Allow = new(true);
}

/// <summary>
/// Same-origin proof for unsafe browser requests in the networked profile
/// (AGT-3230). SameSite cookies do not separate sibling hosts of one site, so a
/// request that carries a Studio browser credential, or that asks for one, must
/// also show that the Studio page itself sent it: a matching <c>Origin</c> or
/// <c>Sec-Fetch-Site: same-origin</c>. Browsers send at least one of them on
/// every unsafe method. Requests without a browser credential (Runner bearer,
/// enrollment code) are service protocol and stay outside this rule.
/// Event streams (<c>/hubs/*</c> and every WebSocket upgrade) are checked on
/// every method, because the upgrade is a GET that a sibling host can open
/// with the session cookie. Non-browser stream clients send neither header
/// and stay allowed.
/// </summary>
public static class BrowserOriginPolicy
{
    /// <summary>Open endpoints that issue a browser session.</summary>
    public static bool IsBrowserSignIn(string path)
        => path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/auth/bootstrap", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/auth/invite/accept", StringComparison.OrdinalIgnoreCase);

    public static bool IsSafeMethod(string method)
        => HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
           || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    /// <summary>SignalR hubs and any WebSocket upgrade, whatever the method.</summary>
    public static bool IsEventStream(string path, IHeaderDictionary headers)
        => path.Equals("/hubs", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase)
           || headers.Upgrade.Any(value => value?.Contains("websocket", StringComparison.OrdinalIgnoreCase) == true);

    public static BrowserOriginDecision Decide(
        string method,
        bool browserCredential,
        bool eventStream,
        string? origin,
        string? secFetchSite,
        string requestScheme,
        HostString requestHost)
    {
        if (!browserCredential || (IsSafeMethod(method) && !eventStream)) return BrowserOriginDecision.Allow;
        if (!string.IsNullOrEmpty(secFetchSite)
            && !string.Equals(secFetchSite, "same-origin", StringComparison.OrdinalIgnoreCase))
            return new(false, "cross-site-request", "Studio accepts browser changes only from its own pages.");
        if (!string.IsNullOrEmpty(origin))
            return OriginMatches(origin, requestScheme, requestHost)
                ? BrowserOriginDecision.Allow
                : new(false, "origin-mismatch", "The request Origin does not match this Studio.");
        return string.IsNullOrEmpty(secFetchSite) && !eventStream
            ? new(false, "origin-required", "A browser change must carry its Origin or Sec-Fetch-Site.")
            : BrowserOriginDecision.Allow;
    }

    /// <summary>Exact scheme, host and effective port match; <c>null</c> and paths never match.</summary>
    public static bool OriginMatches(string origin, string requestScheme, HostString requestHost)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            || !string.IsNullOrEmpty(originUri.UserInfo)
            || originUri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(originUri.Query)
            || !string.IsNullOrEmpty(originUri.Fragment))
            return false;

        var https = string.Equals(requestScheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var requestPort = requestHost.Port ?? (https ? 443 : 80);
        var originPort = originUri.IsDefaultPort
            ? string.Equals(originUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80
            : originUri.Port;
        return string.Equals(originUri.Scheme, requestScheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(originUri.Host, requestHost.Host, StringComparison.OrdinalIgnoreCase)
               && originPort == requestPort;
    }
}
