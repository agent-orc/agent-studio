namespace AgentStudio.Security;

/// <summary>Fail-closed authentication and the intentionally small owner/operator/viewer authorization model.</summary>
public sealed class AccessSecurityMiddleware
{
    public const string HumanPrincipalItem = "AccessSecurity.HumanPrincipal";
    public const string RunnerPrincipalItem = "AccessSecurity.RunnerPrincipal";
    public const string AttributionClientIdItem = "AccessSecurity.AttributionClientId";

    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly AccessSecurityStore _store;
    private readonly TaskScannerService? _scanner;
    private readonly RunLeaseService? _leases;
    private readonly AgentStudio.Registry.ProjectRegistry? _projects;

    public AccessSecurityMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        AccessSecurityStore store,
        TaskScannerService? scanner = null,
        RunLeaseService? leases = null,
        AgentStudio.Registry.ProjectRegistry? projects = null)
    {
        _next = next;
        _configuration = configuration;
        _store = store;
        _scanner = scanner;
        _leases = leases;
        _projects = projects;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (SecurityProfiles.IsPublicDemo(_configuration))
        {
            // The browser edge admits no unsafe visitor method, but S3's one
            // private service mutation must remain usable in the integrated
            // public-demo profile. Authenticate that exclusive scope here;
            // every other public-demo request continues to the ephemeral
            // viewer boundary without acquiring a human session.
            var publicDemoPath = NormalizePath(context.Request.Path.Value);
            if (HttpMethods.IsPost(context.Request.Method)
                && publicDemoPath.Equals("/api/runner/replay/events", StringComparison.OrdinalIgnoreCase))
            {
                var replayBearer = ReadBearer(context.Request.Headers.Authorization.FirstOrDefault());
                var replayRunner = _store.AuthenticateRunner(replayBearer);
                if (replayRunner is null)
                {
                    await Reject(context, 401, "runner-authentication-required", "A Runner service credential is required.");
                    return;
                }
                if (!replayRunner.Scopes.Contains(RunnerScopes.DemoReplay))
                {
                    await Reject(context, 403, "runner-scope-denied", $"Runner credential lacks '{RunnerScopes.DemoReplay}'.");
                    return;
                }
                context.Items[RunnerPrincipalItem] = replayRunner;
                context.Items["ClientId"] = replayRunner.RunnerId;
            }
            await _next(context);
            return;
        }

        if (!SecurityProfiles.IsNetworked(_configuration))
        {
            await _next(context);
            return;
        }

        var path = NormalizePath(context.Request.Path.Value);
        if (IsHealth(path)) { await _next(context); return; }
        if (!context.Request.IsHttps)
        {
            await Reject(context, 426, "https-required", "HTTPS is required for the networked profile.");
            return;
        }

        // A service bearer presented to the proxied versioned plane belongs to
        // the standalone Task Server. Do not interpret it against the monolith's
        // credential store; the upstream remains fail-closed and authoritative.
        if (TaskServerPlaneProxy.IsConfigured(_configuration)
            && path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
            && context.Request.Headers.ContainsKey("Authorization"))
        {
            await _next(context);
            return;
        }

        context.Items[AttributionClientIdItem] = context.Request.Headers["X-Client-Id"].FirstOrDefault();
        if (path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase))
            context.Response.Headers.CacheControl = "no-store";
        var human = _store.AuthenticateSession(
            context.Request.Cookies[AccessSecurityStore.SessionCookieName]
            ?? context.Request.Cookies[AccessSecurityStore.InsecureSessionCookieName]);
        var bearer = ReadBearer(context.Request.Headers.Authorization.FirstOrDefault());
        var runner = _store.AuthenticateRunner(bearer);
        if (human is not null)
        {
            context.Items[HumanPrincipalItem] = human;
            context.Items["ClientId"] = human.User.Id;
        }
        if (runner is not null)
        {
            context.Items[RunnerPrincipalItem] = runner;
            context.Items["ClientId"] = runner.RunnerId;
        }

        if (IsOpenAuth(path))
        {
            await _next(context);
            return;
        }

        if (path.Equals("/api/clients/register", StringComparison.OrdinalIgnoreCase))
        {
            await Reject(context, 404, "registration-disabled", "Open client registration is disabled in the networked profile.");
            return;
        }

        if (path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase))
        {
            if (human is null) { await Reject(context, 401, "authentication-required", "An authenticated Studio session is required for event streams."); return; }
            await _next(context);
            return;
        }

        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var requiredRunnerScope = RequiredRunnerScope(context.Request.Method, path);
        if (requiredRunnerScope is not null)
        {
            if (runner is null) { await Reject(context, 401, "runner-authentication-required", "A Runner service credential is required."); return; }
            if (!runner.Scopes.Contains(requiredRunnerScope)) { await Reject(context, 403, "runner-scope-denied", $"Runner credential lacks '{requiredRunnerScope}'."); return; }
            if (IsRunnerTaskFileRead(context.Request.Method, path)
                && !RunnerOwnsTaskInput(path, runner.RunnerId))
            {
                await Reject(context, 403, "runner-task-denied", "A Runner may read prompt.md only while it holds that task's live lease.");
                return;
            }
            await _next(context);
            return;
        }

        if (runner is not null && IsRunnerSelfRead(context.Request.Method, path, runner.RunnerId))
        {
            await _next(context);
            return;
        }

        if (human is null)
        {
            var managementRequest = path.StartsWith("/api/v1/management", StringComparison.OrdinalIgnoreCase);
            await Reject(
                context,
                401,
                "authentication-required",
                managementRequest
                    ? "Sign in with an owner or operator account to manage the Task Server."
                    : "An authenticated Studio session is required.",
                loginUrl: managementRequest ? "/api/auth/login" : null);
            return;
        }
        if (human.User.MustChangePassword && path is not "/api/auth/change-password" and not "/api/auth/logout" and not "/api/auth/session")
        {
            await Reject(context, 403, "password-change-required", "Change the temporary password before continuing.");
            return;
        }

        if (path.StartsWith("/api/auth/users", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/auth/runners", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/auth/runner-enrollments", StringComparison.OrdinalIgnoreCase))
        {
            if (human.User.Role != StudioRoles.Owner) { await Reject(context, 403, "owner-required", "Owner role is required."); return; }
        }

        var safeMethod = HttpMethods.IsGet(context.Request.Method)
                         || HttpMethods.IsHead(context.Request.Method)
                         || HttpMethods.IsOptions(context.Request.Method)
                         || IsReadOnlyPost(context.Request.Method, path);
        if (!safeMethod)
        {
            var selfServiceAuthMutation = path is "/api/auth/logout" or "/api/auth/change-password";
            if (human.User.Role == StudioRoles.Viewer && !selfServiceAuthMutation) { await Reject(context, 403, "role-denied", "Viewer role is read-only."); return; }
            if (!_store.ValidateCsrf(human.Session, context.Request.Headers["X-CSRF-Token"].FirstOrDefault()))
            {
                await Reject(context, 403, "csrf-invalid", "A valid CSRF token is required for browser mutations.");
                return;
            }
        }

        if (!ProjectAllowed(human.User, context.Request, path))
        {
            await Reject(context, 403, "project-scope-denied", "This account is not a member of the requested project.");
            return;
        }

        await _next(context);
    }

    private static string? RequiredRunnerScope(string method, string path)
    {
        // The replay ingest sits above the general runner routes because its
        // credential must never satisfy any of them.
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/replay/events", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.DemoReplay;
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/claim", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
        if (path.StartsWith("/api/runner/lease", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Lease;
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/logs", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Logs;
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/events", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Events;
        if ((HttpMethods.IsPost(method) || HttpMethods.IsGet(method))
            && path.StartsWith("/api/runner/artifacts", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Artifacts;
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/completion", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Completion;
        if (HttpMethods.IsGet(method) && path.StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase) && path.Contains("/files/", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
        return null;
    }

    private static bool IsRunnerSelfRead(string method, string path, string runnerId)
        => HttpMethods.IsGet(method)
           && (path.Equals("/api/auth/runner", StringComparison.OrdinalIgnoreCase)
               || path.Equals($"/api/clients/{runnerId}", StringComparison.OrdinalIgnoreCase));

    private static bool IsHealth(string path)
        => path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/healthz/drain", StringComparison.OrdinalIgnoreCase);
    private static bool IsOpenAuth(string path)
        => path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/auth/bootstrap", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/auth/runner-enroll", StringComparison.OrdinalIgnoreCase);

    private static bool IsRunnerTaskFileRead(string method, string path)
        => HttpMethods.IsGet(method)
           && path.StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase)
           && path.Contains("/files/", StringComparison.OrdinalIgnoreCase);

    private static bool IsReadOnlyPost(string method, string path)
        => HttpMethods.IsPost(method)
           && path.Equals("/api/tasks/reference-status", StringComparison.OrdinalIgnoreCase);

    private bool RunnerOwnsTaskInput(string path, string runnerId)
    {
        if (_leases is null) return false;
        var rest = path["/api/tasks/".Length..];
        var fileMarker = rest.IndexOf("/files/", StringComparison.OrdinalIgnoreCase);
        if (fileMarker <= 0) return false;
        var taskKey = Uri.UnescapeDataString(rest[..fileMarker]);
        var relativePath = Uri.UnescapeDataString(rest[(fileMarker + "/files/".Length)..]);
        if (!relativePath.Equals("prompt.md", StringComparison.OrdinalIgnoreCase)) return false;
        var lease = _leases.Peek(taskKey).Lease;
        return lease is not null && string.Equals(lease.RunnerId, runnerId, StringComparison.Ordinal);
    }

    private static string NormalizePath(string? value)
    {
        var path = string.IsNullOrWhiteSpace(value) ? "/" : value;
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static string? ReadBearer(string? authorization)
        => authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? authorization[7..].Trim() : null;

    private bool ProjectAllowed(StudioUser user, HttpRequest request, string path)
    {
        if (user.Role == StudioRoles.Owner || user.Projects.Count == 0) return true;
        if (path.StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/core", StringComparison.OrdinalIgnoreCase))
            return !string.IsNullOrWhiteSpace(request.Query["project"])
                && ProjectAccessAuthorization.Allows(user, request.Query["project"].FirstOrDefault(), _projects);
        if (path.StartsWith("/api/runner/global", StringComparison.OrdinalIgnoreCase)) return false;
        string? requested = request.Query["project"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(requested) && path.StartsWith("/api/projects/", StringComparison.OrdinalIgnoreCase))
            requested = path["/api/projects/".Length..].Split('/', 2)[0];
        if (requested is "settings" or "pipeline-catalogue") requested = null;
        if (string.IsNullOrWhiteSpace(requested) && path.StartsWith("/api/runner/", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = path["/api/runner/".Length..].Split('/', 2)[0];
            if (candidate is not ("status" or "global" or "orchestrator-feed" or "token-summary-aggregate"
                or "claim" or "lease" or "logs" or "events" or "artifacts" or "completion"))
                requested = candidate;
        }
        if (string.IsNullOrWhiteSpace(requested) && path.StartsWith("/api/orchestrator/", StringComparison.OrdinalIgnoreCase))
        {
            var marker = path.Contains("/project:", StringComparison.OrdinalIgnoreCase)
                ? "/project:"
                : path.Contains("/task:", StringComparison.OrdinalIgnoreCase) ? "/task:" : null;
            if (marker is null) return false;
            var start = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length;
            requested = path[start..].Split('/', 2)[0];
        }
        if (string.IsNullOrWhiteSpace(requested) && path.StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase))
        {
            var taskId = path["/api/tasks/".Length..].Split('/', 2)[0];
            // Body-addressed and workspace-collection task routes carry their task
            // set in the request body (reorder, batch-move) or filter their payload
            // per task (reference-status, archive). Those handlers enforce membership
            // on every affected task via ProjectAccessAuthorization.AllowsTasks /
            // FilterTasks, so the middleware defers instead of inferring a single
            // project from the literal path segment.
            if (taskId is "reorder" or "batch-move" or "reference-status" or "archive")
                return true;
            // Every other /api/tasks/{id}/... route is single-task addressed. A
            // scoped account may act only on a task whose project it belongs to. If
            // that project cannot be resolved (unknown id or the scanner is
            // unavailable), fail closed rather than allow the request through — this
            // closes the path-/body-addressed mutation gap (move-to-top,
            // change-project, orphan-folder, …) the review flagged.
            var project = _scanner?.FindJob(Uri.UnescapeDataString(taskId), request.Query["watchPath"].FirstOrDefault())?.ProjectName;
            return !string.IsNullOrWhiteSpace(project)
                   && ProjectAccessAuthorization.Allows(user, project, _projects);
        }
        return string.IsNullOrWhiteSpace(requested)
               || ProjectAccessAuthorization.Allows(user, Uri.UnescapeDataString(requested), _projects);
    }

    private static async Task Reject(
        HttpContext context,
        int status,
        string code,
        string message,
        string? loginUrl = null)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        if (loginUrl is null)
            await context.Response.WriteAsJsonAsync(new { error = code, message });
        else
            await context.Response.WriteAsJsonAsync(new { error = code, message, loginUrl });
    }
}

public static class AccessSecurityMiddlewareExtensions
{
    public static IApplicationBuilder UseAccessSecurity(this IApplicationBuilder app) => app.UseMiddleware<AccessSecurityMiddleware>();
}
