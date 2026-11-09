namespace AgentStudio.Security;

/// <summary>
/// Settings &gt; Access: the Owner's kill switch (security model SM-2). The
/// routes sit under <c>/api/auth/iam</c>, so only a human with
/// <c>access.manage</c> reaches them and the store checks the Owner role; no
/// service account, API token, or operator session can call them.
/// </summary>
public static class OperatorKillSwitchEndpoints
{
    public static void MapOperatorKillSwitchEndpoints(this RouteGroupBuilder iam)
    {
        var kill = iam.MapGroup("/operator-kill-switch");

        kill.MapGet("", (HttpContext context, AccessSecurityStore store, OperatorKillSwitch killSwitch, OperatorKillSwitchOptions options) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                IamEndpoints.Require(context, store, IamPermissions.AccessRead);
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(killSwitch.Status(options.DrillIntervalHours));
            })).RequirePermission(IamPermissions.AccessRead);

        kill.MapPost("/end-all", (EndOperatorAccessRequest? request, HttpContext context, AccessSecurityStore store,
            OperatorKillSwitch killSwitch) => ExecuteAsync(async () =>
            {
                var owner = IamEndpoints.Require(context, store, IamPermissions.AccessManage);
                return Results.Ok(await killSwitch.EndAllAsync(OperatorKillActor.Owner(owner), OperatorKillTriggers.Studio,
                    request?.Reason, null, context.RequestAborted));
            })).RequirePermission(IamPermissions.AccessManage);

        kill.MapPost("/drills", (HttpContext context, AccessSecurityStore store, OperatorKillSwitch killSwitch) => ExecuteAsync(async () =>
        {
            var owner = IamEndpoints.Require(context, store, IamPermissions.AccessManage);
            return Results.Ok(await killSwitch.RunDrillAsync(OperatorKillTriggers.ManualDrill, owner, context.RequestAborted));
        })).RequirePermission(IamPermissions.AccessManage);

        iam.MapPost("/operator-agents/{id}/sessions/{sessionId}/kill", (string id, string sessionId, KillOperatorSessionRequest? request,
            HttpContext context, AccessSecurityStore store, OperatorKillSwitch killSwitch) => ExecuteAsync(async () =>
            {
                var owner = IamEndpoints.Require(context, store, IamPermissions.AccessManage);
                return Results.Ok(await killSwitch.KillSessionAsync(id, sessionId, owner, request?.Reason, context.RequestAborted));
            })).RequirePermission(IamPermissions.AccessManage);
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (SecurityOperationException exception)
        {
            return Results.Json(new { error = exception.Code, message = exception.Message }, statusCode: exception.Status);
        }
    }
}
