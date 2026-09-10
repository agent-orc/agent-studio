using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The 27-route P0 "core-attach" bundle: human login/session bootstrap, the
/// board projection, task lifecycle mutation, orchestrator chat and context
/// digests, runner status, and workspace/project listing. Existing
/// <c>/api/v1/workspaces</c> and <c>/api/v1/projects</c> already satisfy the
/// remaining four routes in that bundle and are not duplicated here.
/// </summary>
public static class StudioEndpoints
{
    public static void MapStudioEndpoints(this WebApplication app)
    {
        var studio = app.MapGroup("/api/v1/studio")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        MapAuthEndpoints(studio);
        MapOrchestratorEndpoints(studio);
        MapRunnerEndpoints(studio);
        studio.MapGet("/board", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetStudioBoardAsync(ct)));

        MapTaskLifecycleEndpoints(app);
    }

    private static void MapAuthEndpoints(RouteGroupBuilder studio)
    {
        var auth = studio.MapGroup("/auth");
        auth.MapGet("/status", async (HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetStudioAuthStatusAsync(StudioSessionToken(context), ct)));

        auth.MapPost("/bootstrap", async (HttpContext context, StudioBootstrapRequest request, TaskServerStore store, CancellationToken ct) =>
        {
            try
            {
                var session = await store.BootstrapStudioAuthAsync(request, ct);
                SetStudioSessionCookies(context, session.SessionToken, session.CsrfToken);
                return Results.Json(session, statusCode: StatusCodes.Status201Created);
            }
            catch (Exception exception)
            {
                return TaskServerEndpoints.MapError(exception);
            }
        }).RequireTaskServerScope(TaskServerScopes.TasksWrite);

        auth.MapPost("/login", async (HttpContext context, StudioLoginRequest request, TaskServerStore store, CancellationToken ct) =>
        {
            try
            {
                var session = await store.LoginStudioAsync(request, ct);
                SetStudioSessionCookies(context, session.SessionToken, session.CsrfToken);
                return Results.Ok(session);
            }
            catch (Exception exception)
            {
                return TaskServerEndpoints.MapError(exception);
            }
        }).RequireTaskServerScope(TaskServerScopes.TasksWrite);

        auth.MapPost("/logout", async (HttpContext context, TaskServerStore store, CancellationToken ct) =>
        {
            try
            {
                await store.LogoutStudioAsync(StudioSessionToken(context), ct);
                ClearStudioSessionCookies(context);
                return Results.NoContent();
            }
            catch (Exception exception)
            {
                return TaskServerEndpoints.MapError(exception);
            }
        }).RequireTaskServerScope(TaskServerScopes.TasksWrite);

        auth.MapPost("/change-password", async (
            HttpContext context, StudioChangePasswordRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.ChangeStudioPasswordAsync(StudioSessionToken(context), request, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    private static void MapOrchestratorEndpoints(RouteGroupBuilder studio)
    {
        var orchestrator = studio.MapGroup("/orchestrator");
        orchestrator.MapGet("/context/global", (HttpContext context, TaskServerStore store, CancellationToken ct)
            => BuildOrchestratorDigestAsync("global", context, store, ct));
        orchestrator.MapGet("/context/project:{projectIdentity}", (
            string projectIdentity, HttpContext context, TaskServerStore store, CancellationToken ct)
            => BuildOrchestratorDigestAsync($"project:{projectIdentity}", context, store, ct));
        orchestrator.MapGet("/context/task:{projectIdentity}/{taskIdentity}", (
            string projectIdentity, string taskIdentity, HttpContext context, TaskServerStore store, CancellationToken ct)
            => BuildOrchestratorDigestAsync($"task:{projectIdentity}/{taskIdentity}", context, store, ct));
        orchestrator.MapPost("/context/global/refresh", (HttpContext context, TaskServerStore store, CancellationToken ct)
            => BuildOrchestratorDigestAsync("global", context, store, ct))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        orchestrator.MapPost("/context/project:{projectIdentity}/refresh", (
            string projectIdentity, HttpContext context, TaskServerStore store, CancellationToken ct)
            => BuildOrchestratorDigestAsync($"project:{projectIdentity}", context, store, ct))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        orchestrator.MapPost("/context/task:{projectIdentity}/{taskIdentity}/refresh", (
            string projectIdentity, string taskIdentity, HttpContext context, TaskServerStore store, CancellationToken ct)
            => BuildOrchestratorDigestAsync($"task:{projectIdentity}/{taskIdentity}", context, store, ct))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        orchestrator.MapGet("/sessions", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListStudioOrchestratorSessionsAsync(ct)));
    }

    private static Task<IResult> BuildOrchestratorDigestAsync(
        string rawContextKey, HttpContext context, TaskServerStore store, CancellationToken ct)
        => TaskServerEndpoints.InvokeAsync(
            () => store.BuildStudioOrchestratorDigestAsync(rawContextKey, TaskServerEndpoints.Actor(context), ct));

    private static void MapRunnerEndpoints(RouteGroupBuilder studio)
    {
        var runner = studio.MapGroup("/runner");
        runner.MapGet("/status", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetStudioRunnerStatusAsync(ct)));

        runner.MapGet("/{project}/orchestrator-chat", async (
            string project, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.GetOrchestratorChatAsync(project, TaskServerEndpoints.Actor(context), ct)));

        runner.MapPost("/{project}/orchestrator-chat", async (
            string project,
            HttpContext context,
            StudioOrchestratorChatMessageRequest request,
            StudioLifecycleCoordinator coordinator,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => coordinator.SendOrchestratorChatMessageAsync(project, request, TaskServerEndpoints.Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Chat)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        runner.MapPost("/{project}/orchestrator-chat/attachments", async (
            string project, HttpContext context, StudioChatAttachmentStore attachments, TaskServerStore store, CancellationToken ct) =>
        {
            if (!context.Request.HasFormContentType)
                return Results.BadRequest(new ApiError("invalid-request", "multipart/form-data expected"));
            try
            {
                await store.RequireProjectAsync(project, ct);
                var form = await context.Request.ReadFormAsync(ct);
                var file = form.Files["file"] ?? form.Files.FirstOrDefault();
                if (file is null || file.Length == 0)
                    return Results.BadRequest(new ApiError("invalid-request", "No file uploaded"));
                await using var stream = file.OpenReadStream();
                var response = await attachments.SaveAsync(project, file.FileName, stream, ct);
                return Results.Ok(response);
            }
            catch (Exception exception)
            {
                return TaskServerEndpoints.MapError(exception);
            }
        }).RequireTaskServerScope(TaskServerScopes.TasksWrite)
          .DisableAntiforgery();

        runner.MapGet("/{project}/orchestrator-chat/attachments/{fileName}", async (
            string project, string fileName, StudioChatAttachmentStore attachments, TaskServerStore store, CancellationToken ct) =>
        {
            try
            {
                await store.RequireProjectAsync(project, ct);
            }
            catch (Exception exception)
            {
                return TaskServerEndpoints.MapError(exception);
            }
            var resolved = attachments.Resolve(project, fileName);
            return resolved is null ? Results.NotFound() : Results.File(resolved.Value.Path, resolved.Value.ContentType);
        });
    }

    private static void MapTaskLifecycleEndpoints(WebApplication app)
    {
        var tasks = app.MapGroup("/api/v1/projects/{projectId}/tasks/{taskIdentity}")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        tasks.MapDelete("", async (
            string projectId, string taskIdentity, HttpContext context, StudioLifecycleCoordinator coordinator, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await coordinator.DeleteTaskAsync(projectId, taskIdentity, TaskServerEndpoints.Actor(context), ct);
                return new { deleted = true, taskId = taskIdentity };
            })).RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPost("/continue", async (
            string projectId, string taskIdentity, HttpContext context, ContinueTaskRequest request,
            StudioLifecycleCoordinator coordinator, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => coordinator.ContinueTaskAsync(projectId, taskIdentity, request, TaskServerEndpoints.Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPost("/move", async (
            string projectId, string taskIdentity, HttpContext context, MoveTaskRequest request,
            StudioLifecycleCoordinator coordinator, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => coordinator.MoveTaskAsync(projectId, taskIdentity, request, TaskServerEndpoints.Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPost("/move-to-top", async (
            string projectId, string taskIdentity, HttpContext context, StudioLifecycleCoordinator coordinator, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => coordinator.MoveTaskToTopAsync(projectId, taskIdentity, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPost("/start", async (
            string projectId, string taskIdentity, HttpContext context, StartTaskRequest? request,
            StudioLifecycleCoordinator coordinator, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => coordinator.StartTaskAsync(
                    projectId, taskIdentity, request ?? new StartTaskRequest(), TaskServerEndpoints.Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPut("/state", async (
            string projectId, string taskIdentity, HttpContext context, MoveTaskRequest request,
            StudioLifecycleCoordinator coordinator, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => coordinator.MoveTaskAsync(projectId, taskIdentity, request, TaskServerEndpoints.Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        tasks.MapPost("/stop", async (
            string projectId, string taskIdentity, HttpContext context, StopTaskRequest? request,
            StudioLifecycleCoordinator coordinator, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => coordinator.StopTaskAsync(
                    projectId, taskIdentity, request ?? new StopTaskRequest(), TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    private static string? StudioSessionToken(HttpContext context)
        => context.Request.Headers["X-Studio-Session-Token"].FirstOrDefault()
           ?? context.Request.Cookies["ts-studio-session"];

    private static void SetStudioSessionCookies(HttpContext context, string sessionToken, string csrfToken)
    {
        var secure = context.Request.IsHttps;
        context.Response.Cookies.Append("ts-studio-session", sessionToken, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Secure = secure,
        });
        context.Response.Cookies.Append("ts-studio-csrf", csrfToken, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Secure = secure,
        });
    }

    private static void ClearStudioSessionCookies(HttpContext context)
    {
        context.Response.Cookies.Delete("ts-studio-session");
        context.Response.Cookies.Delete("ts-studio-csrf");
    }
}
