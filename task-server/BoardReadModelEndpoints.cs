using AgentStudio.TaskServer.Contracts;
using Microsoft.Net.Http.Headers;

namespace AgentStudio.TaskServer;

/// <summary>
/// Board read model and change feed per project (pipeline delivery target,
/// M1 and M2). The board answers 304 when the client's validator names the
/// current generation, checked before the card rows are read.
/// </summary>
internal static class BoardReadModelEndpoints
{
    public static void MapBoardReadModelEndpoints(this WebApplication app)
    {
        var board = app.MapGroup("/api/v1/projects/{projectId}/board")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        board.MapGet("", async (string projectId, HttpContext context, TaskServerStore store, CancellationToken ct) =>
        {
            try
            {
                var validators = context.Request.Headers.IfNoneMatch.ToString();
                if (validators.Length > 0)
                {
                    var current = BoardReadModelProjection.FormatETag(
                        await store.GetBoardGenerationAsync(projectId, ct));
                    if (MatchesValidator(validators, current))
                    {
                        context.Response.Headers.ETag = current;
                        return Results.StatusCode(StatusCodes.Status304NotModified);
                    }
                }
                var model = await store.GetBoardReadModelAsync(projectId, ct);
                context.Response.Headers.ETag = model.ETag;
                context.Response.Headers.CacheControl = "no-cache";
                return Results.Json(model);
            }
            catch (Exception exception)
            {
                return TaskServerEndpoints.MapError(exception);
            }
        });

        board.MapGet("/changes", async (string projectId, long? after, int? limit, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetBoardChangesAsync(projectId, after ?? 0, limit, ct)));

        board.MapPut("/workspace-import", async (
                HttpContext context, string projectId, BoardWorkspaceImportRequest request,
                TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ImportWorkspaceBoardAsync(
                projectId, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    internal static bool MatchesValidator(string ifNoneMatch, string current)
        => EntityTagHeaderValue.TryParseList([ifNoneMatch], out var tags)
           && tags.Any(tag => tag.Equals(EntityTagHeaderValue.Any)
                              || string.Equals(tag.Tag.ToString(), current, StringComparison.Ordinal));
}
