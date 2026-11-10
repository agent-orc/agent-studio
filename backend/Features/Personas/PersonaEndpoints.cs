using AgentStudio.Security;

namespace AgentStudio.Personas;

/// <summary>
/// Workspace persona catalogue and activation (persona team PT-1). The list
/// shows every catalogue entry and custom persona with its activation state.
/// Writes need a human Owner session; the store asks for a fresh step-up on
/// activation and on adopting into a live activation, and for nothing more on
/// pause, resume, deactivate and fork. Refusals answer
/// <c>{ error, message }</c> with the store's status, for example
/// <c>403 step-up-required</c>.
/// </summary>
public static class PersonaEndpoints
{
    public static void MapPersonaEndpoints(this WebApplication app)
    {
        var personas = app.MapGroup("/api/workspaces/{id}/personas");

        personas.MapGet("", (string id, PersonaActivationStore store) =>
            AccessSecurityEndpoints.Execute(() => Results.Ok(store.List(id))));

        personas.MapPost("/{personaId}/activate", (string id, string personaId, PersonaActivateRequest? request,
            HttpContext context, AccessSecurityStore security, PersonaActivationStore store) =>
            AccessSecurityEndpoints.Execute(() => Results.Ok(store.Activate(id, personaId, request ?? new PersonaActivateRequest(),
                AccessSecurityEndpoints.RequireHuman(context, security)))))
            .RequirePermission(IamPermissions.WorkspaceAdminister);

        MapChange(personas, "pause", (store, id, personaId, request, owner) => store.Pause(id, personaId, request, owner));
        MapChange(personas, "resume", (store, id, personaId, request, owner) => store.Resume(id, personaId, request, owner));
        MapChange(personas, "deactivate", (store, id, personaId, request, owner) => store.Deactivate(id, personaId, request, owner));
        MapChange(personas, "adopt", (store, id, personaId, request, owner) => store.Adopt(id, personaId, request, owner));

        personas.MapPost("/{personaId}/fork", (string id, string personaId, PersonaForkRequest request,
            HttpContext context, AccessSecurityStore security, PersonaActivationStore store) =>
            AccessSecurityEndpoints.Execute(() => Results.Ok(store.Fork(id, personaId, request,
                AccessSecurityEndpoints.RequireHuman(context, security)))))
            .RequirePermission(IamPermissions.WorkspaceAdminister);
    }

    private static void MapChange(RouteGroupBuilder personas, string action,
        Func<PersonaActivationStore, string, string, PersonaChangeRequest, HumanPrincipal, object> change)
        => personas.MapPost("/{personaId}/" + action, (string id, string personaId, PersonaChangeRequest? request,
                HttpContext context, AccessSecurityStore security, PersonaActivationStore store) =>
                AccessSecurityEndpoints.Execute(() => Results.Ok(change(store, id, personaId, request ?? new PersonaChangeRequest(),
                    AccessSecurityEndpoints.RequireHuman(context, security)))))
            .RequirePermission(IamPermissions.WorkspaceAdminister);
}
