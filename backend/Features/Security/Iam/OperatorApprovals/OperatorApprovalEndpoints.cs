namespace AgentStudio.Security;

/// <summary>
/// Settings &gt; Access: the Owner's approval requests (security model SM-5,
/// decision SM-D8 option A). The routes sit under <c>/api/auth/iam</c>, so no
/// service account, API token, or operator session reaches them. Approving
/// and rejecting take the actor from the authenticated Studio session and a
/// fresh step-up; no header or body field names the deciding actor.
/// </summary>
public static class OperatorApprovalEndpoints
{
    public static void MapOperatorApprovalEndpoints(this RouteGroupBuilder iam)
    {
        var approvals = iam.MapGroup("/operator-approvals");

        approvals.MapGet("", (HttpContext context, AccessSecurityStore store, string? principalId) => AccessSecurityEndpoints.Execute(() =>
        {
            IamEndpoints.Require(context, store, IamPermissions.AccessRead);
            return Results.Ok(store.ListOperatorApprovals(principalId).Select(store.ToOperatorApprovalResponse).ToList());
        })).RequirePermission(IamPermissions.AccessRead);

        approvals.MapPost("/{id}/approve", (string id, DecideOperatorApprovalRequest? request, HttpContext context, AccessSecurityStore store) =>
            Decide(id, true, request, context, store)).RequirePermission(IamPermissions.AccessManage);

        approvals.MapPost("/{id}/reject", (string id, DecideOperatorApprovalRequest? request, HttpContext context, AccessSecurityStore store) =>
            Decide(id, false, request, context, store)).RequirePermission(IamPermissions.AccessManage);
    }

    private static IResult Decide(string id, bool approve, DecideOperatorApprovalRequest? request, HttpContext context, AccessSecurityStore store)
        => AccessSecurityEndpoints.Execute(() =>
        {
            // A bearer principal (service account, API token, operator session) never decides,
            // even if a route floor in front of this one were lifted.
            if (context.Items[AccessSecurityMiddleware.ServiceAccountPrincipalItem] is not null)
                throw new SecurityOperationException(403, "human-session-required", "Only the Owner's Studio session decides approval requests.");
            IamEndpoints.Require(context, store, IamPermissions.AccessManage);
            var owner = AccessSecurityEndpoints.RequireHuman(context, store);
            return Results.Ok(store.ToOperatorApprovalResponse(
                store.DecideOperatorApproval(id, approve, request ?? new DecideOperatorApprovalRequest(null), owner)));
        });
}
