namespace AgentStudio.Security;

public static class OperatorSessionEndpoints
{
    public const string RenewPath = "/api/auth/operator-session/renew";
    public const string StatusPath = "/api/auth/operator-session/status";

    public static void MapOperatorSessionEndpoints(this WebApplication app)
    {
        app.MapPost(RenewPath, async (HttpContext context, AccessSecurityStore store) =>
        {
            var (request, refusal) = await OperatorSignInEndpoints.ReadBoundedAsync<OperatorSessionRenewalRequest>(context);
            if (refusal is not null) return refusal;
            return AccessSecurityEndpoints.Execute(() => Results.Ok(store.RenewBoundOperatorSession(
                Bearer(context), request!, context.Request, context.Connection.RemoteIpAddress?.ToString())));
        });
        app.MapGet(StatusPath, (HttpContext context, AccessSecurityStore store) => AccessSecurityEndpoints.Execute(() =>
        {
            var bearer = Bearer(context);
            var principal = store.AuthenticateOperatorSession(bearer, recordUse: false)
                ?? throw new SecurityOperationException(401, "session-ended", "The session ended. Run studio-op login.");
            if (principal.BoundKeyThumbprint is { } thumb
                && context.Items["OperatorSession.ProofThumbprint"] as string != thumb && store.ValidateOperatorProof(context.Request, bearer!, thumb) is { } refusal)
                throw new SecurityOperationException(401, refusal, "A session proof is required. Run studio-op login.");
            var agent = store.ListOperatorAgents().First(account => account.Id == principal.AccountId);
            return Results.Ok(store.ToOperatorSessionResponse(agent.Id, agent.Sessions!.First(session => session.Id == principal.TokenId)));
        }));
    }

    private static string? Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : null;
    }

    public static void MapOperatorSessionOwnerEndpoints(this RouteGroupBuilder iam)
    {
        iam.MapGet("/session-limits", (HttpContext context, AccessSecurityStore store) => AccessSecurityEndpoints.Execute(() =>
        {
            IamEndpoints.Require(context, store, IamPermissions.AccessManage);
            return Results.Ok(store.GetOperatorSessionLimits());
        })).RequirePermission(IamPermissions.AccessManage);
        iam.MapPut("/session-limits", (OperatorSessionLimits request, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() => Results.Ok(store.SetOperatorSessionLimits(request,
                AccessSecurityEndpoints.RequireHuman(context, store))))).RequirePermission(IamPermissions.AccessManage);
        iam.MapPut("/operator-agents/{id}/lifetime-limit", (string id, OperatorAgentLifetimeLimitRequest request,
            HttpContext context, AccessSecurityStore store) => AccessSecurityEndpoints.Execute(() =>
        {
            store.SetOperatorAgentLifetimeLimit(id, request.LifetimeHours, AccessSecurityEndpoints.RequireHuman(context, store));
            return Results.NoContent();
        })).RequirePermission(IamPermissions.AccessManage);
        iam.MapPost("/elevated-sessions/end-all", (HttpContext context, AccessSecurityStore store) => AccessSecurityEndpoints.Execute(() =>
        {
            store.EndAllElevatedSessions(IamEndpoints.Require(context, store, IamPermissions.AccessManage));
            return Results.NoContent();
        })).RequirePermission(IamPermissions.AccessManage);
        iam.MapPost("/operator-agents/{id}/sessions/{sessionId}/actions/{action}", (string id, string sessionId, string action,
            HttpContext context, AccessSecurityStore store) => AccessSecurityEndpoints.Execute(() =>
        {
            store.ActOnElevatedSession(id, sessionId, action, IamEndpoints.Require(context, store, IamPermissions.AccessManage));
            return Results.NoContent();
        })).RequirePermission(IamPermissions.AccessManage);
    }
}
