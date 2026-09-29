namespace AgentStudio.Host;

/// <summary>
/// AGT-2983: Angular calls the P0 core-attach operations (login, board, task
/// lifecycle, orchestrator context and chat, runner status, workspaces and
/// projects, and the live-update hub) on their versioned Task Server routes.
/// Through the Studio connector those requests reach the Task Server
/// unchanged. OrchestratorApi itself, both as the local monolith and as
/// Stable's transitional <see cref="TaskServerPlaneProxy"/> profile, still owns
/// these operations through its legacy handlers, whose wire shapes the Studio
/// renders. This alias maps exactly those versioned method and path shapes back
/// onto the legacy handler paths before routing, so every guard, security
/// check, and handler sees the request it always did. Nothing else under
/// <c>/api/v1</c> is touched: the local v1 owners or the standalone proxy keep
/// it. The connector profile never runs this middleware.
/// </summary>
public static class StudioV1LegacyRouteAlias
{
    /// <summary>
    /// The Task Server's "resolve this task by id alone" project segment
    /// (<c>TaskServerStore.UnscopedProjectToken</c>). The Studio sends it when
    /// it knows only the task id; the legacy handler then resolves the task
    /// from its <c>watchPath</c> query as before.
    /// </summary>
    public const string UnscopedProjectToken = "-";

    private static readonly string[] AuthPostVerbs = ["bootstrap", "login", "logout", "change-password"];
    private static readonly string[] TaskPostVerbs = ["continue", "move", "move-to-top", "start", "stop"];

    public readonly record struct LegacyTarget(string Path, string? Project);

    public static LegacyTarget? Resolve(string method, string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3 && Is(segments[0], "hubs") && Is(segments[1], "v1") && Is(segments[2], "studio"))
            return new LegacyTarget("/hubs/jobs" + Tail(segments, 3), null);
        if (segments.Length < 3 || !Is(segments[0], "api") || !Is(segments[1], "v1")) return null;

        var get = HttpMethods.IsGet(method);
        var post = HttpMethods.IsPost(method);
        if (segments.Length == 3 && (Is(segments[2], "workspaces") || Is(segments[2], "projects")) && (get || post))
            return new LegacyTarget("/api/" + segments[2], null);
        if (Is(segments[2], "projects")) return ResolveTask(method, segments);
        if (Is(segments[2], "studio")) return ResolveStudio(get, post, segments[3..]);
        return null;
    }

    public static IApplicationBuilder UseStudioV1LegacyRouteAlias(this IApplicationBuilder app)
        => app.Use((context, next) =>
        {
            if (Resolve(context.Request.Method, context.Request.Path.Value ?? string.Empty) is { } target)
            {
                context.Request.Path = new PathString(target.Path);
                if (target.Project is not null && !context.Request.Query.ContainsKey("project"))
                    context.Request.QueryString = context.Request.QueryString.Add("project", target.Project);
            }
            return next(context);
        });

    private static LegacyTarget? ResolveTask(string method, string[] segments)
    {
        // /api/v1/projects/{projectId}/tasks/{taskId}[/{verb}]
        if (segments.Length is not (6 or 7) || !Is(segments[4], "tasks")) return null;
        var project = segments[3] == UnscopedProjectToken ? null : segments[3];
        var legacy = "/api/tasks/" + segments[5];
        if (segments.Length == 6)
            return HttpMethods.IsGet(method) || HttpMethods.IsDelete(method) ? new LegacyTarget(legacy, project) : null;
        var verb = segments[6];
        var allowed = HttpMethods.IsPost(method) && TaskPostVerbs.Any(item => Is(item, verb))
            || HttpMethods.IsPut(method) && Is(verb, "state");
        return allowed ? new LegacyTarget($"{legacy}/{verb}", project) : null;
    }

    private static LegacyTarget? ResolveStudio(bool get, bool post, string[] rest)
    {
        if (rest.Length == 2 && Is(rest[0], "auth"))
        {
            var allowed = get && Is(rest[1], "status") || post && AuthPostVerbs.Any(item => Is(item, rest[1]));
            return allowed ? new LegacyTarget("/api/auth/" + rest[1], null) : null;
        }
        if (rest.Length == 1 && Is(rest[0], "board") && get)
            return new LegacyTarget("/api/tasks/grouped", null);
        if (rest.Length >= 3 && Is(rest[0], "orchestrator") && Is(rest[1], "context")
            && (get || post && Is(rest[^1], "refresh")))
            return new LegacyTarget("/api/orchestrator/context" + Tail(rest, 2), null);
        if (rest.Length == 2 && Is(rest[0], "orchestrator") && Is(rest[1], "sessions") && get)
            return new LegacyTarget("/api/orchestrator/sessions", null);
        if (rest.Length == 5 && Is(rest[0], "orchestrator") && Is(rest[1], "sessions")
            && rest[2].StartsWith("workbench:", StringComparison.OrdinalIgnoreCase) && Is(rest[4], "turns") && post)
            return new LegacyTarget("/api/orchestrator/sessions" + Tail(rest, 2), null);
        if (rest.Length == 2 && Is(rest[0], "runner") && Is(rest[1], "status") && get)
            return new LegacyTarget("/api/runner/status", null);
        if (rest.Length == 3 && Is(rest[0], "runner") && Is(rest[2], "orchestrator-chat") && (get || post))
            return new LegacyTarget("/api/runner" + Tail(rest, 1), null);
        if (rest.Length == 5 && Is(rest[0], "runner") && Is(rest[2], "orchestrator-chat") && Is(rest[3], "attachments") && get)
            return new LegacyTarget("/api/runner" + Tail(rest, 1), null);
        return null;
    }

    private static string Tail(string[] segments, int start)
        => start >= segments.Length ? string.Empty : "/" + string.Join('/', segments[start..]);

    private static bool Is(string value, string expected)
        => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
