namespace AgentStudio.Security;

/// <summary>
/// Access administration: users, groups, service accounts, and role bindings
/// per scope. The middleware enforces the declared permissions in the
/// networked profile; each handler checks them again for the acting human so
/// the local profile cannot reach them without a session either.
/// </summary>
public static class IamEndpoints
{
    public static void MapIamEndpoints(this WebApplication app)
    {
        var iam = app.MapGroup("/api/auth/iam");

        iam.MapGet("", (HttpContext context, AccessSecurityStore store) => AccessSecurityEndpoints.Execute(() =>
        {
            Require(context, store, IamPermissions.AccessRead);
            return Results.Ok(Overview(store.ReadIamSnapshot()));
        })).RequirePermission(IamPermissions.AccessRead);

        iam.MapGet("/check", (HttpContext context, AccessSecurityStore store, AgentStudio.Registry.ProjectRegistry registry,
            string principalKind, string principalId, string permission, string? project, string? environment) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                Require(context, store, IamPermissions.AccessRead);
                if (!IamPermissions.IsKnown(permission))
                    throw new SecurityOperationException(400, "invalid-permission", $"Unknown permission '{permission}'.");
                var target = string.IsNullOrWhiteSpace(project)
                    ? PermissionTarget.WorkspaceWide
                    : PermissionTarget.ForProject(project, environment);
                var kind = principalKind switch
                {
                    "user" => IamPrincipalKind.User,
                    "service-account" => IamPrincipalKind.ServiceAccount,
                    _ => throw new SecurityOperationException(400, "invalid-principal", "Check a user or a service account."),
                };
                var decision = store.CheckPermission(kind, principalId, permission, target,
                    (bound, requested) => ProjectAccessAuthorization.SameProject(bound, requested, registry));
                return Results.Ok(new IamCheckResponse(decision.Allowed, permission,
                    decision.GrantingBindings.Select(binding => binding.Id).ToList(), decision.Reason));
            })).RequirePermission(IamPermissions.AccessRead);

        iam.MapPost("/groups", (CreateGroupRequest request, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
                Results.Ok(store.CreateGroup(request, Require(context, store, IamPermissions.AccessManage)))))
            .RequirePermission(IamPermissions.AccessManage);

        iam.MapPut("/groups/{id}", (string id, UpdateGroupRequest request, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
                Results.Ok(store.UpdateGroup(id, request, Require(context, store, IamPermissions.AccessManage)))))
            .RequirePermission(IamPermissions.AccessManage);

        iam.MapDelete("/groups/{id}", (string id, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                store.DeleteGroup(id, Require(context, store, IamPermissions.AccessManage));
                return Results.NoContent();
            })).RequirePermission(IamPermissions.AccessManage);

        iam.MapPost("/bindings", (IamBindingRequest request, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
                Results.Ok(store.AddBinding(request, Require(context, store, IamPermissions.AccessManage)))))
            .RequirePermission(IamPermissions.AccessManage);

        iam.MapDelete("/bindings/{id}", (string id, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                store.RemoveBinding(id, Require(context, store, IamPermissions.AccessManage));
                return Results.NoContent();
            })).RequirePermission(IamPermissions.AccessManage);

        iam.MapPost("/service-accounts", (CreateServiceAccountRequest request, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
                Results.Ok(ToResponse(store.CreateServiceAccount(request, Require(context, store, IamPermissions.AccessManage))))))
            .RequirePermission(IamPermissions.AccessManage);

        iam.MapPost("/service-accounts/{id}/tokens", (string id, CreateServiceAccountTokenRequest request,
            HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                Require(context, store, IamPermissions.AccessManage);
                var created = store.CreateServiceAccountToken(id, request);
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new OneTimeServiceAccountTokenResponse(
                    created.Account.Id, created.Token.Id, created.Secret, created.Token.ExpiresAt));
            })).RequirePermission(IamPermissions.AccessManage);

        iam.MapDelete("/service-accounts/{id}/tokens/{tokenId}", (string id, string tokenId,
            HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                Require(context, store, IamPermissions.AccessManage);
                store.RevokeServiceAccountToken(id, tokenId);
                return Results.NoContent();
            })).RequirePermission(IamPermissions.AccessManage);

        iam.MapDelete("/service-accounts/{id}", (string id, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                Require(context, store, IamPermissions.AccessManage);
                store.DisableServiceAccount(id);
                return Results.NoContent();
            })).RequirePermission(IamPermissions.AccessManage);
    }

    /// <summary>Returns the acting user's id once the workspace-level permission holds.</summary>
    private static string Require(HttpContext context, AccessSecurityStore store, string permission)
    {
        var human = AccessSecurityEndpoints.RequireHuman(context, store);
        var decision = store.AuthorizeUser(human.User.Id, permission, PermissionTarget.WorkspaceWide);
        if (!decision.Allowed) throw new SecurityOperationException(403, "permission-denied", decision.Reason ?? $"'{permission}' is required.");
        return human.User.Id;
    }

    private static IamOverviewResponse Overview(IamSnapshot snapshot) => new(
        IamRoles.All.Select(role => new IamRoleResponse(
            role.Id, role.DisplayName, role.Description,
            role.Permissions.Order(StringComparer.Ordinal).ToList(),
            role.BindableScopes.Order().ToList(), role.ServiceAccountBindable)).ToList(),
        IamPermissions.All.Where(permission => !IamPermissions.IsUnbound(permission.Name)
                && permission.Name != IamPermissions.RunnerService)
            .Select(permission => new IamPermissionResponse(permission.Name, permission.Level, permission.Description)).ToList(),
        snapshot.Users.Select(AccessSecurityEndpoints.ToResponse).ToList(),
        snapshot.Groups,
        snapshot.ServiceAccounts.Select(ToResponse).ToList(),
        snapshot.Bindings,
        snapshot.Invitations.Select(invitation => new IamInvitationResponse(
            invitation.Id, invitation.UserId, invitation.ExpiresAt, invitation.UsedAt is not null)).ToList(),
        snapshot.IamVersion);

    private static ServiceAccountResponse ToResponse(ServiceAccount account) => new(
        account.Id, account.Name, account.Description, account.CreatedAt, account.DisabledAt,
        account.Tokens.Select(token => new ServiceAccountTokenResponse(
            token.Id, token.Name, token.CreatedAt, token.ExpiresAt, token.LastUsedAt, token.RevokedAt)).ToList());
}
