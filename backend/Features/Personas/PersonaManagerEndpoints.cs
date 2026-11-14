using AgentStudio.Security;

namespace AgentStudio.Personas;

public static class PersonaManagerEndpoints
{
    public static void Map(RouteGroupBuilder personas)
    {
        personas.MapGet("/manager-actions", (string id, PersonaManagerService managers) =>
            Results.Ok(managers.List(id))).RequirePermission(IamPermissions.WorkspaceRead);
        personas.MapPost("/manager-actions/{receiptId}/undo", async (string id, string receiptId,
            HttpContext context, AccessSecurityStore security, PersonaManagerService managers) =>
            {
                try
                {
                    var owner = AccessSecurityEndpoints.RequireHuman(context, security);
                    return Results.Ok(await managers.UndoAsync(id, receiptId, owner, context.RequestAborted));
                }
                catch (SecurityOperationException ex)
                {
                    return Results.Json(new { error = ex.Code, message = ex.Message }, statusCode: ex.Status);
                }
                catch (PersonaManagerRefusal ex)
                {
                    return Results.Conflict(new { error = ex.Code });
                }
            }).RequirePermission(IamPermissions.WorkspaceAdminister);
    }
}
