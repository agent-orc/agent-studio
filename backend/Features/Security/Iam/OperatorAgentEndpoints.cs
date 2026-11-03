namespace AgentStudio.Security;

/// <summary>
/// Settings &gt; Access: the Owner creates operator agents and starts, renews,
/// and revokes their sessions (security model SM-1). The routes sit under
/// <c>/api/auth/iam</c>, so only the Owner reaches them and no service
/// account, API token, or operator session can call them.
/// </summary>
public static class OperatorAgentEndpoints
{
    public static void MapOperatorAgentEndpoints(this RouteGroupBuilder iam)
    {
        var agents = iam.MapGroup("/operator-agents");

        agents.MapGet("", (HttpContext context, AccessSecurityStore store) => AccessSecurityEndpoints.Execute(() =>
        {
            IamEndpoints.Require(context, store, IamPermissions.AccessRead);
            return Results.Ok(store.ListOperatorAgents().Select(store.ToOperatorAgentResponse).ToList());
        })).RequirePermission(IamPermissions.AccessRead);

        agents.MapPost("", (CreateOperatorAgentRequest request, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() => Results.Ok(store.ToOperatorAgentResponse(
                store.CreateOperatorAgent(request, IamEndpoints.Require(context, store, IamPermissions.AccessManage))))))
            .RequirePermission(IamPermissions.AccessManage);

        agents.MapDelete("/{id}", (string id, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                store.DisableOperatorAgent(id, IamEndpoints.Require(context, store, IamPermissions.AccessManage));
                return Results.NoContent();
            })).RequirePermission(IamPermissions.AccessManage);

        agents.MapPost("/{id}/sessions", (string id, StartOperatorSessionRequest request, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                var owner = IamEndpoints.Require(context, store, IamPermissions.AccessManage);
                var started = store.StartOperatorSession(id, request, owner);
                var actor = new AgentStudio.Shared.OperatorActor(id, started.Session.Id, started.Session.CreatedBy);
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new OneTimeOperatorSessionResponse(
                    id, started.Session.Id, started.Secret, actor.Actor, actor.DelegatedBy, started.Session.ExpiresAt));
            })).RequirePermission(IamPermissions.AccessManage);

        agents.MapPost("/{id}/sessions/{sessionId}/renew", (string id, string sessionId, RenewOperatorSessionRequest? request,
            HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                var owner = IamEndpoints.Require(context, store, IamPermissions.AccessManage);
                var renewed = store.RenewOperatorSession(id, sessionId, request ?? new RenewOperatorSessionRequest(null), owner);
                return Results.Ok(store.ToOperatorSessionResponse(id, renewed));
            })).RequirePermission(IamPermissions.AccessManage);

        agents.MapDelete("/{id}/sessions/{sessionId}", (string id, string sessionId, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                store.RevokeOperatorSession(id, sessionId, IamEndpoints.Require(context, store, IamPermissions.AccessManage));
                return Results.NoContent();
            })).RequirePermission(IamPermissions.AccessManage);
    }
}
