using System.Net;

namespace AgentStudio.Security;

/// <summary>
/// The TCP peer of a request, captured before <c>UseForwardedHeaders</c>
/// replaces <c>RemoteIpAddress</c> with the forwarded client (AGT-3230).
/// Loopback trust belongs to the peer only: a trusted proxy controls the
/// forwarded address, and in the Cloudflare edge mode so does every Cloudflare
/// peer through <c>CF-Connecting-IP</c>, so a forwarded 127.0.0.1 proves nothing.
/// </summary>
public static class ConnectionPeer
{
    public const string Item = "AgentStudio.Security.ConnectionPeer";

    public static IApplicationBuilder UseConnectionPeerCapture(this IApplicationBuilder app)
        => app.Use((context, next) =>
        {
            context.Items[Item] = context.Connection.RemoteIpAddress;
            return next(context);
        });

    /// <summary>
    /// True only when the connection itself comes from loopback and no proxy
    /// forwarded the request. Without a captured peer, the current address is
    /// the peer; an <c>X-Original-For</c> header means forwarding happened.
    /// </summary>
    public static bool IsDirectLoopback(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey("X-Original-For")) return false;
        var peer = context.Items.TryGetValue(Item, out var captured)
            ? captured as IPAddress
            : context.Connection.RemoteIpAddress;
        return peer is not null && IPAddress.IsLoopback(peer);
    }
}
