namespace AgentStudio.Security;

/// <summary>
/// Settings &gt; Access: the operator operation catalogue and the Owner's
/// standing grants (security model SM-4). The routes sit under
/// <c>/api/auth/iam</c>, so no service account, API token, or operator
/// session reaches them; the store admits only an active Owner to change a
/// grant and asks for a fresh step-up before it issues or renews one.
/// </summary>
public static class StandingGrantEndpoints
{
    public static void MapStandingGrantEndpoints(this RouteGroupBuilder iam)
    {
        iam.MapGet("/operator-catalogue", (HttpContext context, AccessSecurityStore store) => AccessSecurityEndpoints.Execute(() =>
        {
            IamEndpoints.Require(context, store, IamPermissions.AccessRead);
            return Results.Ok(new OperatorCatalogueResponse(OperatorOperationCatalog.Operations,
                AccessSecurityStore.DefaultStandingGrantDays, AccessSecurityStore.MaxStandingGrantDays,
                AccessSecurityStore.DefaultStandingGrantRatePerHour, AccessSecurityStore.MaxStandingGrantRatePerHour));
        })).RequirePermission(IamPermissions.AccessRead);

        var grants = iam.MapGroup("/standing-grants");

        grants.MapGet("", (HttpContext context, AccessSecurityStore store, string? principalId) => AccessSecurityEndpoints.Execute(() =>
        {
            IamEndpoints.Require(context, store, IamPermissions.AccessRead);
            return Results.Ok(store.ListStandingGrants(principalId).Select(store.ToStandingGrantResponse).ToList());
        })).RequirePermission(IamPermissions.AccessRead);

        grants.MapPost("", (CreateStandingGrantRequest request, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                IamEndpoints.Require(context, store, IamPermissions.AccessManage);
                var owner = AccessSecurityEndpoints.RequireHuman(context, store);
                return Results.Ok(store.ToStandingGrantResponse(store.CreateStandingGrant(request, owner)));
            })).RequirePermission(IamPermissions.AccessManage);

        grants.MapPost("/{id}/renew", (string id, RenewStandingGrantRequest? request, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                IamEndpoints.Require(context, store, IamPermissions.AccessManage);
                var owner = AccessSecurityEndpoints.RequireHuman(context, store);
                return Results.Ok(store.ToStandingGrantResponse(
                    store.RenewStandingGrant(id, request ?? new RenewStandingGrantRequest(null), owner)));
            })).RequirePermission(IamPermissions.AccessManage);

        grants.MapPost("/{id}/revoke", (string id, RevokeStandingGrantRequest? request, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() => Results.Ok(store.ToStandingGrantResponse(store.RevokeStandingGrant(id,
                request ?? new RevokeStandingGrantRequest(null), IamEndpoints.Require(context, store, IamPermissions.AccessManage))))))
            .RequirePermission(IamPermissions.AccessManage);
    }
}
