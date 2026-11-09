using AgentStudio.Operator;
using AgentStudio.Registry;
using AgentStudio.Security;

namespace AgentStudio.Tasks;

public static class TaskAttentionEndpoints
{
    public static void MapTaskAttentionEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{jobId}/attention", async (string jobId, string? project,
            string? watchPath, HttpContext context, TaskScannerService scanner,
            ProjectRegistry projects, OperatorCoordinationStore store, CancellationToken ct) =>
        {
            await LeaseEndpoints.ClaimGate.WaitAsync(ct);
            try
            {
                var card = scanner.FindJob(jobId, ResolveWatchPath(projects, project, watchPath));
                if (card is null) return Results.NotFound();
                if (!ProjectAccessAuthorization.AllowsTasks(context, [card.ProjectName],
                        IamPermissions.ProjectCardsWrite, projects))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                var owner = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                    && human.User.Role == StudioRoles.Owner;
                var actor = OperatorEndpoints.Actor(context);
                if (owner) await store.OwnerActionAsync(card.TaskKey, card.ProjectName, actor, ct);
                var lease = store.Acquire(card.TaskKey, actor, owner);
                return lease is null
                    ? Results.Conflict(new { error = "attention-held", holder = store.Current(card.TaskKey)?.Holder })
                    : Results.Ok(new { lease, revision = OperatorCoordinationStore.Revision(card.FolderPath) });
            }
            finally { LeaseEndpoints.ClaimGate.Release(); }
        }).RequirePermission(IamPermissions.ProjectCardsWrite);
    }

    private static string? ResolveWatchPath(ProjectRegistry projects, string? project, string? watchPath)
        => string.IsNullOrWhiteSpace(project) ? watchPath
            : projects.FindByIdOrDisplayName(project)?.StorageLocation ?? watchPath;
}
