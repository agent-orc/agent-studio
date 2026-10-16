using AgentStudio.TaskServer.Contracts;
using System.Text.Json;

namespace AgentStudio.TaskServer;

/// <summary>Restricts Product sessions on the versioned Studio plane.</summary>
internal sealed class StudioContributorMiddleware(RequestDelegate next, TaskServerStore store)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var authPath = context.Request.Path.Value?.TrimEnd('/') ?? string.Empty;
        if (authPath is "/api/v1/studio/auth/login" or "/api/v1/studio/auth/bootstrap"
            or "/api/v1/studio/auth/invite/accept" or "/api/v1/studio/auth/recover")
        {
            await next(context);
            return;
        }
        var token = StudioEndpoints.StudioSessionToken(context);
        if (token is null)
        {
            await next(context);
            return;
        }
        var status = await store.GetStudioAuthStatusAsync(token, context.RequestAborted);
        if (!status.Authenticated || status.User is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var user = status.User;
        context.Items["StudioActorId"] = user.UserId;
        if (user.Role != StudioUserRoles.Contributor)
        {
            await next(context);
            return;
        }
        context.Items["StudioContributor"] = true;

        var path = context.Request.Path.Value?.TrimEnd('/') ?? string.Empty;
        var method = context.Request.Method;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        var selfAuth = path is "/api/v1/studio/auth/status" or "/api/v1/studio/auth/logout"
            or "/api/v1/studio/auth/change-password";
        if (selfAuth)
        {
            await next(context);
            return;
        }
        if (user.MustChangePassword)
        {
            await Deny(context);
            return;
        }
        if (parts.Any(part => part is "credentials" or "remote-hosts" or "settings" or "quotas"
            or "security" or "tokens" or "usage" or "logs" or "raw-logs"
            or "merge" or "promotions" or "production" or "management"
            or "secrets" or "output" or "trace" or "files"))
        {
            await Deny(context);
            return;
        }

        if (safe && (path is "/api/v1/projects" or "/api/v1/workspaces" or "/api/v1/studio/board"))
        {
            await next(context);
            return;
        }
        if (parts.Length >= 4 && parts[0] == "api" && parts[1] == "v1"
            && parts[2] == "projects")
        {
            var project = await ResolveProject(parts[3], context.RequestAborted);
            TaskDto? task = null;
            if (parts.Length >= 6 && parts[4] == "tasks")
            {
                task = await store.GetTaskAsync(parts[3], parts[5], context.RequestAborted);
                if (task is not null) project = task.ProjectId;
            }
            if (project is null || !user.ProjectIds.Contains(project, StringComparer.Ordinal))
            {
                await Deny(context);
                return;
            }
            var taskRoute = parts.Length >= 5 && parts[4] == "tasks";
            var allowed = safe && taskRoute;
            if (HttpMethods.IsPost(method) && parts.Length == 5 && taskRoute)
                allowed = true;
            if (task is not null && task.State is (StudioTaskLanes.Backlog or StudioTaskLanes.Ready))
                allowed |= (HttpMethods.IsPut(method) && parts.Length == 6)
                    || (HttpMethods.IsPut(method) && parts.Length == 7
                        && parts[6] is "title" or "tags" or "references");
            if (task is not null && task.State is (StudioTaskLanes.Backlog or StudioTaskLanes.Ready or StudioTaskLanes.HumanReview))
                allowed |= (HttpMethods.IsPost(method) && parts.Length == 7 && parts[6] == "move")
                    || (HttpMethods.IsPut(method) && parts.Length == 7 && parts[6] == "state");
            if (task is not null && HttpMethods.IsPost(method) && parts.Length == 7 && parts[6] == "preview-comments")
                allowed = true;
            if (task is not null && HttpMethods.IsPut(method) && parts.Length == 7 && parts[6] == "release")
                allowed = await store.HasStudioProductionApprovalAsync(user.UserId, project, context.RequestAborted);
            if (allowed)
            {
                if (!await AllowsPayloadAsync(context.Request, parts, task?.State, context.RequestAborted))
                {
                    await Deny(context);
                    return;
                }
                await next(context);
                return;
            }
        }
        if (parts.Length >= 6 && parts[0] == "api" && parts[1] == "v1"
            && parts[2] == "studio" && parts[3] == "runner"
            && parts[^1] == "orchestrator-chat"
            && (safe || HttpMethods.IsPost(method)))
        {
            var identity = parts[4].StartsWith("project:", StringComparison.Ordinal)
                ? parts[4]["project:".Length..]
                : parts[4].StartsWith("task:", StringComparison.Ordinal)
                    ? parts[4]["task:".Length..] : parts[4];
            var project = await ResolveProject(identity, context.RequestAborted);
            if (project is not null && user.ProjectIds.Contains(project, StringComparer.Ordinal))
            {
                await next(context);
                return;
            }
        }
        if (safe && parts.Length >= 6 && parts[0] == "api" && parts[1] == "v1"
            && parts[2] == "studio" && parts[3] == "orchestrator"
            && parts[4] == "context" && parts[5].StartsWith("project:", StringComparison.Ordinal))
        {
            var project = await ResolveProject(parts[5]["project:".Length..], context.RequestAborted);
            if (project is not null && user.ProjectIds.Contains(project, StringComparer.Ordinal))
            {
                await next(context);
                return;
            }
        }
        if (safe && parts.Length >= 6 && parts[0] == "api" && parts[1] == "v1"
            && parts[2] == "studio" && parts[3] == "projects"
            && parts[5] == "pipeline-health")
        {
            var project = await ResolveProject(parts[4], context.RequestAborted);
            if (project is not null && user.ProjectIds.Contains(project, StringComparer.Ordinal))
            {
                await next(context);
                return;
            }
        }
        await Deny(context);
    }

    private static async Task<bool> AllowsPayloadAsync(HttpRequest request, string[] parts, string? currentState, CancellationToken ct)
    {
        var isCreate = parts.Length == 5 && HttpMethods.IsPost(request.Method);
        var isUpdate = parts.Length == 6 && HttpMethods.IsPut(request.Method);
        var isMove = parts.Length == 7 && (parts[6] is "move" or "state");
        if (!isCreate && !isUpdate && !isMove) return true;
        request.EnableBuffering();
        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            var field = isMove ? "targetState" : "state";
            var value = doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().FirstOrDefault(property =>
                    property.Name.Equals(field, StringComparison.OrdinalIgnoreCase)).Value
                : default;
            var target = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (isCreate) return target is null or StudioTaskLanes.Backlog or StudioTaskLanes.Ready;
            if (isUpdate)
            {
                var mechanicalDelta = doc.RootElement.EnumerateObject().FirstOrDefault(property =>
                    property.Name.Equals("mechanicalDelta", StringComparison.OrdinalIgnoreCase)).Value;
                return mechanicalDelta.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    && target is (null or StudioTaskLanes.Backlog or StudioTaskLanes.Ready);
            }
            return (currentState is (StudioTaskLanes.Backlog or StudioTaskLanes.Ready)
                    && target is (StudioTaskLanes.Backlog or StudioTaskLanes.Ready))
                || (currentState == StudioTaskLanes.HumanReview && target == StudioTaskLanes.Completed);
        }
        catch (JsonException)
        {
            return true;
        }
        finally
        {
            if (request.Body.CanSeek) request.Body.Position = 0;
        }
    }

    private async Task<string?> ResolveProject(string identity, CancellationToken ct)
    {
        if (identity == TaskServerStore.UnscopedProjectToken) return null;
        try { return (await store.RequireProjectAsync(Uri.UnescapeDataString(identity), ct)).ProjectId; }
        catch (KeyNotFoundException) { return null; }
    }

    private static Task Deny(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsJsonAsync(new { error = "contributor-role-denied" });
    }
}
