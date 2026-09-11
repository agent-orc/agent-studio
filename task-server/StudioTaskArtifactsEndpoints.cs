using System.Globalization;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// P1 "task-owned files, attachments, artifacts, screenshots, and results"
/// Studio bundle (group G6): the legacy
/// <c>/api/tasks/{taskId}/artifacts|attachments|files|output|results|screenshot(s)</c>
/// routes, moved onto the versioned, unscoped-project-aware
/// <c>/api/v1/projects/{projectId}/tasks/{taskIdentity}</c> group exactly like
/// <c>StudioEndpoints.MapTaskLifecycleEndpoints</c>.
/// </summary>
internal static class StudioTaskArtifactsEndpoints
{
    /// <summary>
    /// Production entry point - this is the exact method Program.cs wires in
    /// alongside the other <c>Map*Endpoints</c> calls.
    /// </summary>
    internal static void MapStudioTaskArtifactsEndpoints(this WebApplication app)
        => MapStudioTaskArtifactsEndpoints((IEndpointRouteBuilder)app);

    /// <summary>
    /// Route-building logic factored onto <see cref="IEndpointRouteBuilder"/> so
    /// this group's own tests can map the same routes onto a test host through
    /// an <c>IStartupFilter</c>, without needing Program.cs to already call the
    /// <see cref="WebApplication"/> overload above.
    /// </summary>
    internal static void MapStudioTaskArtifactsEndpoints(this IEndpointRouteBuilder app)
    {
        var tasks = app.MapGroup("/api/v1/projects/{projectId}/tasks/{taskIdentity}")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        tasks.MapGet("/artifacts", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListTaskArtifactsAsync(projectId, taskIdentity, ct)));

        tasks.MapGet("/screenshots", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListTaskScreenshotArtifactsAsync(projectId, taskIdentity, ct)));

        tasks.MapGet("/screenshot", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct) =>
        {
            try
            {
                var content = await store.GetLatestTaskScreenshotContentAsync(projectId, taskIdentity, ct);
                return content is null
                    ? Results.NotFound(new ApiError("not-found", "No screenshot artifact was found for this task."))
                    : Results.Bytes(Convert.FromBase64String(content.ContentBase64), content.MediaType);
            }
            catch (Exception exception)
            {
                return TaskServerEndpoints.MapError(exception);
            }
        });

        tasks.MapGet("/output", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetTaskOutputAsync(projectId, taskIdentity, ct)));

        tasks.MapGet("/results/{**path}", async (
            string projectId, string taskIdentity, string path, TaskServerStore store, CancellationToken ct) =>
        {
            try
            {
                // Runner-emitted results artifacts are named with the "results/"
                // prefix included (see RemoteTaskRunner/DurableHandoffRecovery),
                // but the route template already consumes that literal segment, so
                // it has to be added back before matching against artifact names.
                var content = await store.GetTaskResultContentAsync(projectId, taskIdentity, "results/" + path, ct);
                return content is null
                    ? Results.NotFound(new ApiError("not-found", "Result artifact was not found."))
                    : Results.Bytes(Convert.FromBase64String(content.ContentBase64), content.MediaType);
            }
            catch (Exception exception)
            {
                return TaskServerEndpoints.MapError(exception);
            }
        });

        tasks.MapPost("/attachments", async (
            string projectId, string taskIdentity, HttpContext context, TaskServerStore store, CancellationToken ct) =>
        {
            if (!context.Request.HasFormContentType)
                return Results.BadRequest(new ApiError("invalid-request", "multipart/form-data expected"));
            try
            {
                var form = await context.Request.ReadFormAsync(ct);
                var file = form.Files["file"] ?? form.Files.FirstOrDefault();
                if (file is null || file.Length == 0)
                    return Results.BadRequest(new ApiError("invalid-request", "No file uploaded"));
                await using var stream = file.OpenReadStream();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                var mediaType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
                var response = await store.SaveTaskAttachmentAsync(
                    projectId, taskIdentity, file.FileName, buffer.ToArray(), mediaType, TaskServerEndpoints.Actor(context), ct);
                return Results.Json(response, statusCode: StatusCodes.Status201Created);
            }
            catch (Exception exception)
            {
                return TaskServerEndpoints.MapError(exception);
            }
        }).RequireTaskServerScope(TaskServerScopes.TasksWrite)
          .DisableAntiforgery();

        tasks.MapGet("/attachments/{fileName}", async (
            string projectId, string taskIdentity, string fileName, TaskServerStore store, CancellationToken ct) =>
        {
            try
            {
                var result = await store.GetTaskAttachmentContentAsync(projectId, taskIdentity, fileName, ct);
                return result is null
                    ? Results.NotFound(new ApiError("not-found", "Attachment was not found."))
                    : Results.Bytes(result.Value.Content, result.Value.Meta.MediaType);
            }
            catch (Exception exception)
            {
                return TaskServerEndpoints.MapError(exception);
            }
        });

        tasks.MapPut("/files/{fileName}", async (
            string projectId,
            string taskIdentity,
            string fileName,
            HttpContext context,
            UpdateTaskFileRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.PutTaskFileAsync(projectId, taskIdentity, fileName, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        // A single catch-all handler backs both the file-content read and its
        // /history sibling: ASP.NET Core route templates cannot place a literal
        // segment after a catch-all parameter, so - exactly like the legacy
        // monolith's TaskFilesEndpoints.MapTaskFilesEndpoints - a trailing
        // "/history" suffix is stripped from the captured path to dispatch.
        tasks.MapGet("/files/{**path}", async (
            string projectId, string taskIdentity, string path, string? scope,
            HttpContext context, TaskServerStore store, CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(scope))
                return Results.BadRequest(new ApiError(
                    "dev-seat-scope-required",
                    "scope=code file reads are served by the local dev-seat connector, not the Task Server."));

            try
            {
                if (TryStripHistorySuffix(path, out var historyPath))
                {
                    var history = await store.GetTaskFileHistoryAsync(projectId, taskIdentity, historyPath, ct);
                    return Results.Ok(history);
                }

                var file = await store.GetTaskFileContentAsync(projectId, taskIdentity, path, ct);
                if (file is null) return Results.NotFound(new ApiError("not-found", "Task file was not found."));
                context.Response.Headers["X-Task-File-Version"] = file.Value.Meta.Version.ToString(CultureInfo.InvariantCulture);
                return Results.Bytes(file.Value.Content, file.Value.Meta.MediaType);
            }
            catch (Exception exception)
            {
                return TaskServerEndpoints.MapError(exception);
            }
        });
    }

    private static bool TryStripHistorySuffix(string path, out string filePath)
    {
        filePath = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var normalized = path.Replace('\\', '/').TrimEnd('/');
        const string marker = "/history";
        if (!normalized.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
            return false;

        filePath = normalized[..^marker.Length];
        return !string.IsNullOrWhiteSpace(filePath);
    }
}
