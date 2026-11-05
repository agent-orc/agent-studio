namespace AgentStudio.Security;

public static class BreakGlassWindowEndpoints
{
    public static void MapBreakGlassWindowEndpoints(this RouteGroupBuilder iam)
    {
        var group = iam.MapGroup("/break-glass");
        group.MapGet("/windows", (HttpContext context, AccessSecurityStore store) => AccessSecurityEndpoints.Execute(() =>
        {
            IamEndpoints.Require(context, store, IamPermissions.AccessRead);
            return Results.Ok(store.ListBreakGlassWindows());
        })).RequirePermission(IamPermissions.AccessRead);
        group.MapPost("/credential", (HttpContext context, AccessSecurityStore store) => AccessSecurityEndpoints.Execute(() =>
        {
            IamEndpoints.Require(context, store, IamPermissions.AccessManage);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { credential = store.IssueBreakGlassCredential(AccessSecurityEndpoints.RequireHuman(context, store)) });
        })).RequirePermission(IamPermissions.AccessManage);
        group.MapPost("/windows", (OpenBreakGlassRequest request, HttpContext context, AccessSecurityStore store,
            BreakGlassWindowService windows) => ExecuteAsync(async () =>
        {
            IamEndpoints.Require(context, store, IamPermissions.AccessManage);
            return Results.Ok(await windows.OpenAsync(AccessSecurityEndpoints.RequireHuman(context, store), request, context.RequestAborted));
        })).RequirePermission(IamPermissions.AccessManage);
        group.MapPost("/windows/{id}/close", (string id, HttpContext context, AccessSecurityStore store,
            BreakGlassWindowService windows) => AccessSecurityEndpoints.Execute(() =>
        {
            var owner = IamEndpoints.Require(context, store, IamPermissions.AccessManage);
            store.RequireBreakGlassOwner(owner);
            return Results.Ok(windows.Close(id, $"human:{owner}"));
        })).RequirePermission(IamPermissions.AccessManage);
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (SecurityOperationException exception)
        { return Results.Json(new { error = exception.Code, message = exception.Message }, statusCode: exception.Status); }
    }
}
