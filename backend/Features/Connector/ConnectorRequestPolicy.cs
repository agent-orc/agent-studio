namespace AgentStudio.Connector;

public enum ConnectorOriginFact
{
    Absent,
    Studio,
    Foreign,
}

public enum ConnectorSurface
{
    /// <summary>Anything outside the connector's browser API, such as the static fallback.</summary>
    Other,
    /// <summary><c>/healthz</c> and <c>/readyz</c>: no session, no secrets.</summary>
    Liveness,
    /// <summary><c>GET</c> or <c>POST /connector/session</c>: issues a session and its CSRF token.</summary>
    SessionBootstrap,
    /// <summary><c>DELETE /connector/session</c>: ends the caller's own session.</summary>
    SessionControl,
    /// <summary><c>/api</c> and <c>/hubs</c>: every classified Studio route.</summary>
    Protected,
}

/// <summary>Everything the admission decision needs, read from one browser request.</summary>
public sealed record ConnectorRequestFacts(
    bool HostAllowed,
    ConnectorOriginFact Origin,
    bool SameOriginFetch,
    ConnectorSurface Surface,
    bool UnsafeMethod,
    bool WebSocketUpgrade,
    bool HubNegotiate,
    bool SessionValid,
    bool CsrfValid);

public sealed record ConnectorAdmission(int? RejectStatus, string? RejectCode, bool IssueSession)
{
    public static ConnectorAdmission Proceed { get; } = new(null, null, false);
    public static ConnectorAdmission ProceedWithNewSession { get; } = new(null, null, true);
    public bool Rejected => RejectStatus is not null;

    public static ConnectorAdmission Reject(int status, string code) => new(status, code, false);
}

/// <summary>
/// Pure browser-boundary policy of the loopback connector. Order matters: a
/// wrong Host (DNS rebinding) and a foreign Origin are refused before any
/// session state is consulted; every mutation and WebSocket upgrade needs the
/// exact Studio Origin; every mutation on the Studio surface needs a live
/// session and its CSRF token.
/// </summary>
public static class ConnectorRequestPolicy
{
    public const string HostRejected = "connector-host-rejected";
    public const string OriginRejected = "connector-origin-rejected";
    public const string OriginRequired = "connector-origin-required";
    public const string SessionRequired = "connector-session-required";
    public const string CsrfRejected = "connector-csrf-rejected";

    public static ConnectorAdmission Decide(ConnectorRequestFacts facts)
    {
        if (!facts.HostAllowed)
            return ConnectorAdmission.Reject(StatusCodes.Status400BadRequest, HostRejected);
        if (facts.Origin == ConnectorOriginFact.Foreign)
            return ConnectorAdmission.Reject(StatusCodes.Status403Forbidden, OriginRejected);

        if (facts.Surface == ConnectorSurface.SessionBootstrap)
        {
            // A same-origin GET carries no Origin header in browsers; the
            // unforgeable Sec-Fetch-Site metadata proves the caller instead.
            var proven = facts.Origin == ConnectorOriginFact.Studio
                || (!facts.UnsafeMethod && facts.Origin == ConnectorOriginFact.Absent && facts.SameOriginFetch);
            return proven
                ? ConnectorAdmission.Proceed
                : ConnectorAdmission.Reject(StatusCodes.Status403Forbidden, OriginRequired);
        }

        if ((facts.UnsafeMethod || facts.WebSocketUpgrade) && facts.Origin != ConnectorOriginFact.Studio)
            return ConnectorAdmission.Reject(StatusCodes.Status403Forbidden, OriginRequired);

        if (facts.Surface is not (ConnectorSurface.Protected or ConnectorSurface.SessionControl))
            return ConnectorAdmission.Proceed;

        if (!facts.SessionValid)
        {
            // The first same-origin read of a Studio tab starts its session, so
            // Angular needs no connector-specific bootstrap call. Mutations and
            // upgrades never start a session: they must already hold one.
            var ambient = facts.Surface == ConnectorSurface.Protected
                && !facts.UnsafeMethod
                && !facts.WebSocketUpgrade
                && facts.SameOriginFetch;
            return ambient
                ? ConnectorAdmission.ProceedWithNewSession
                : ConnectorAdmission.Reject(StatusCodes.Status401Unauthorized, SessionRequired);
        }

        // SignalR negotiate is a POST that only allocates a connection id; it is
        // not a state change and the SignalR client cannot attach the CSRF
        // header, so it is admitted on the exact Origin plus a live session.
        if (facts.UnsafeMethod && !facts.HubNegotiate && !facts.CsrfValid)
            return ConnectorAdmission.Reject(StatusCodes.Status403Forbidden, CsrfRejected);

        return ConnectorAdmission.Proceed;
    }
}
