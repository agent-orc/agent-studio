namespace AgentStudio.Deploy;

/// <summary>
/// Maps today's Studio accounts onto the P1 approval roles. Owner is the
/// Studio owner role. Maintainer is an account holding the per-project
/// production approval grant (<see cref="StudioUser.ProductionApprovalProjects"/>);
/// the Studio has no separate Maintainer role yet. Approval needs a signed-in
/// person in every profile: a caller without a Studio session, such as a
/// coding agent on the local API, is the local operator and never approves.
/// </summary>
public static class DeployApprovalAuthority
{
    public const string LocalOperator = "local-operator";

    public static DeployActor Resolve(StudioUser? user, string project)
    {
        if (user is null) return new(LocalOperator, false);
        var approver = !user.Disabled && (user.Role == StudioRoles.Owner
            || user.ProductionApprovalProjects.Any(grant => string.Equals(grant, project, StringComparison.OrdinalIgnoreCase)));
        return new(user.Id, approver);
    }

    /// <summary>
    /// The networked profile authenticates the session (and its CSRF token on
    /// mutations) in the access middleware. The local profile skips that
    /// middleware, so read the session cookie here and accept it on a
    /// mutation only with its CSRF token, as an interactive Studio tab sends it.
    /// </summary>
    public static StudioUser? SignedInUser(HttpContext context, AccessSecurityStore store)
    {
        if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human) return human.User;
        var session = store.AuthenticateSession(
            context.Request.Cookies[AccessSecurityStore.SessionCookieName]
            ?? context.Request.Cookies[AccessSecurityStore.InsecureSessionCookieName], touch: false);
        if (session is null) return null;
        var safe = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        return safe || store.ValidateCsrf(session.Session, context.Request.Headers["X-CSRF-Token"].FirstOrDefault())
            ? session.User : null;
    }
}
