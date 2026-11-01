using System.Collections.Concurrent;

namespace AgentStudio.Security;

/// <summary>
/// The local profile has no sign-in, and a host's SSH tunnel makes its API
/// reachable from that host, including from the agents running there. Setting
/// <c>Security:OperatorListenerPort</c> adds a second loopback port that only the
/// browser path on this machine uses. On every other port a write passes only
/// when it is what a host does anyway: the Runner service routes and the card
/// operations of the Task API. Everything else answers only on the operator port,
/// such as this installation's access, its secrets, project settings and the
/// project URL commands that run on this machine. An allow list, because a new
/// route must not open a path from a tunnel by default.
/// Without the setting, as on a machine without tunnels, every port is the
/// operator's. The networked profile has real sessions and ignores all of this.
/// </summary>
public static class OperatorListener
{
    public const string PortSetting = "Security:OperatorListenerPort";

    /// <summary>Runs after routing, so the request's permission is the one its route really declares.</summary>
    public static IApplicationBuilder UseOperatorListener(this IApplicationBuilder app)
    {
        var configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();
        if (!SecurityProfiles.IsLocal(configuration)) return app;
        var operatorPort = configuration.GetValue<int?>(PortSetting);
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(OperatorListener));
        var reported = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        return app.Use(async (context, next) =>
        {
            var endpoint = context.GetEndpoint();
            var known = endpoint is not null && !RoutePermissionCatalog.IsMethodRejection(endpoint);
            var refusal = Decide(
                context.Request.Method,
                context.Request.Path.Value,
                known,
                known ? RoutePermissionCatalog.Resolve(endpoint!, context.Request.Method)?.Permission : null,
                context.Connection.LocalPort,
                operatorPort,
                context.Request.Headers.Origin.FirstOrDefault());
            if (refusal is null)
            {
                await next();
                return;
            }
            // Once per route: a host that needs one of these would otherwise fail in silence.
            var route = context.Request.Method + " " + ((endpoint as RouteEndpoint)?.RoutePattern.RawText ?? context.Request.Path.Value);
            if (reported.TryAdd(route, 0))
                logger.LogWarning("operator-listener refused {Route}: {Code}", route, refusal.Value.Code);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = refusal.Value.Code, message = refusal.Value.Message });
        });
    }

    /// <summary>Why this request is refused, or null when it may pass.</summary>
    internal static (string Code, string Message)? Decide(
        string method, string? path, bool knownRoute, string? permission, int localPort, int? operatorPort, string? origin)
    {
        var safe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        var sensitiveRead = safe && permission == IamPermissions.WorkspaceAdminister;
        // DNS rebinding: a website that resolves to this machine still names itself.
        if ((!safe || sensitiveRead) && !string.IsNullOrEmpty(origin) && !IsLoopbackOrigin(origin))
            return ("cross-origin-denied", "This change is only possible from this Studio's own page.");
        if (operatorPort is not { } port || localPort == port) return null;
        // An unknown route answers 404 or 405 by itself; nothing runs.
        if (!knownRoute) return null;
        if (safe ? !sensitiveRead : HostMayWrite(method, path, permission)) return null;
        return ("operator-listener-required",
            $"This change is only possible through the operator port {port}: the browser or a script on the Studio machine.");
    }

    /// <summary>What hosts and the agents on them legitimately change. A read-level POST changes nothing.</summary>
    internal static bool HostMayWrite(string method, string? path, string? permission)
    {
        var segments = (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3 && Is(segments[0], "api"))
        {
            // Both authenticate their caller on their own: a one-time code, a client identity.
            if (HttpMethods.IsPost(method) && Is(segments[1], "auth") && Is(segments[2], "runner-enroll")) return true;
            if (HttpMethods.IsPost(method) && Is(segments[1], "clients") && Is(segments[2], "register")) return true;
        }
        var onCard = segments.Length >= 2 && Is(segments[0], "api") && Is(segments[1], "tasks");
        return permission switch
        {
            IamPermissions.RunnerService or IamPermissions.ProjectRead or IamPermissions.WorkspaceRead => true,
            IamPermissions.ProjectCardsWrite => onCard,
            // The other run operations reach further: git, merges, editors on this machine.
            IamPermissions.ProjectRunsOperate => onCard && segments.Length == 4
                                                 && (Is(segments[3], "start") || Is(segments[3], "stop") || Is(segments[3], "continue")),
            _ => false,
        };
    }

    private static bool Is(string segment, string expected) => segment.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopbackOrigin(string origin)
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback;
}
