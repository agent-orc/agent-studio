using System.Text.Json;
using AgentStudio.Magazine;
using AgentStudio.Operator;
using AgentStudio.Personas;
using AgentStudio.Registry;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tasks;

internal static class ResearchMagazineDeliverable
{
    internal const string Field = "magazineDeliverable";

    internal static MagazineDeliverableDeclaration? Read(JsonElement task)
    {
        if (!task.TryGetProperty(Field, out var value) || value.ValueKind != JsonValueKind.Object)
            return null;
        try { return value.Deserialize<MagazineDeliverableDeclaration>(TaskJsonFile.ReadOpts); }
        catch (JsonException ex)
        {
            SilentCatch.Note(ex, "Invalid Magazine declaration in task metadata.");
            return null;
        }
    }

    internal static MagazineAccess.Resolution Authorize(HttpContext context, MagazineDeliverableRequest request,
        ProjectRegistry projects, WorkspaceRegistry workspaces, PersonaCatalogue catalogue)
    {
        var error = MagazineDeliverableContract.Validate(request, id => catalogue.Find(id) is not null);
        return error is null
            ? MagazineAccess.Resolve(context, request.Scope, IamPermissions.MagazineWrite, projects, workspaces)
            : new(null, Results.BadRequest(new { error }));
    }

    internal static string Actor(HttpContext context)
        => context.Items[AccessSecurityMiddleware.RequestAccessItem] is RequestAccess
            ? OperatorEndpoints.Actor(context)
            : "local-operator";

    internal static void MapAttachment(RouteGroupBuilder group)
    {
        group.MapPost("/{jobId}/magazine-deliverable/preview", (
            string jobId, string? project, string? watchPath, AttachMagazineDeliverableRequest request,
            HttpContext context, ProjectRegistry projects, WorkspaceRegistry workspaces,
            PersonaCatalogue catalogue, TaskScannerService scanner, TaskMutationService mutations,
            TaskFileHistoryService history) => Attach(jobId, project, watchPath, request, context,
                projects, workspaces, catalogue, scanner, mutations, history, previewOnly: true));

        group.MapPut("/{jobId}/magazine-deliverable", (
            string jobId, string? project, string? watchPath, AttachMagazineDeliverableRequest request,
            HttpContext context, ProjectRegistry projects, WorkspaceRegistry workspaces,
            PersonaCatalogue catalogue, TaskScannerService scanner, TaskMutationService mutations,
            TaskFileHistoryService history) => Attach(jobId, project, watchPath, request, context,
                projects, workspaces, catalogue, scanner, mutations, history, previewOnly: false));
    }

    private static IResult Attach(string jobId, string? project, string? watchPath,
        AttachMagazineDeliverableRequest request, HttpContext context, ProjectRegistry projects,
        WorkspaceRegistry workspaces, PersonaCatalogue catalogue, TaskScannerService scanner,
        TaskMutationService mutations, TaskFileHistoryService history, bool previewOnly)
    {
        var task = scanner.FindJob(jobId, TaskEndpointHelpers.ResolveWatchPath(projects, project, watchPath));
        if (task is null) return Results.NotFound();
        if (!ProjectAccessAuthorization.AllowsTasks(context, [task.ProjectName], IamPermissions.ProjectCardsWrite, projects))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (request.MagazineDeliverable is null)
            return Results.BadRequest(new { error = "A Magazine deliverable is required." });

        var resolved = Authorize(context, request.MagazineDeliverable, projects, workspaces, catalogue);
        if (resolved.Refusal is { } refusal) return refusal;
        return mutations.AttachMagazineDeliverable(task, request, resolved.Scope!.ToString(),
            Actor(context), history, previewOnly);
    }
}
