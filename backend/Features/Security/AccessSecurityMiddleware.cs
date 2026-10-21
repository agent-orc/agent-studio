using Microsoft.Extensions.Primitives;

namespace AgentStudio.Security;

/// <summary>Fail-closed authentication and project-scoped human authorization.</summary>
public sealed class AccessSecurityMiddleware
{
    public const string HumanPrincipalItem = "AccessSecurity.HumanPrincipal";
    public const string RunnerPrincipalItem = "AccessSecurity.RunnerPrincipal";
    public const string ApiTokenPrincipalItem = "AccessSecurity.ApiTokenPrincipal";
    public const string AttributionClientIdItem = "AccessSecurity.AttributionClientId";

    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly AccessSecurityStore _store;
    private readonly TaskScannerService? _scanner;
    private readonly RunLeaseService? _leases;
    private readonly AgentStudio.Registry.ProjectRegistry? _projects;
    private readonly TimelineLog? _timeline;
    private readonly TimeProvider _clock;
    private readonly AgentStudio.Registry.ProjectUrlPreviewAccess _previewAccess;
    private readonly ILogger<AccessSecurityMiddleware>? _logger;

    public AccessSecurityMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        AccessSecurityStore store,
        TaskScannerService? scanner = null,
        RunLeaseService? leases = null,
        AgentStudio.Registry.ProjectRegistry? projects = null,
        TimelineLog? timeline = null,
        TimeProvider? clock = null,
        AgentStudio.Registry.ProjectUrlPreviewAccess? previewAccess = null,
        ILogger<AccessSecurityMiddleware>? logger = null)
    {
        _next = next;
        _configuration = configuration;
        _store = store;
        _scanner = scanner;
        _leases = leases;
        _projects = projects;
        _timeline = timeline;
        _clock = clock ?? TimeProvider.System;
        _previewAccess = previewAccess ?? new AgentStudio.Registry.ProjectUrlPreviewAccess(TimeProvider.System);
        _logger = logger;
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

        // Module fetches from a CSP-sandboxed document have an opaque origin
        // and omit Studio's SameSite session cookie. The URL capability grants
        // only a GET under the registered preview asset route.
        if (_previewAccess.AllowsAssetRequest(context.Request))
        {
            await _next(context);
            return;
        }
        if (context.Request.Headers.Origin == "null"
            && (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                || BrowserOriginPolicy.IsEventStream(path, context.Request.Headers)))
        {
            await Reject(context, 403, "opaque-origin-denied", "An opaque origin cannot access Studio APIs.");
            return;
        }

        // A service bearer presented to the proxied versioned plane belongs to
        // the standalone Task Server. Do not interpret it against the monolith's
        // credential store; the upstream remains fail-closed and authoritative.
        // The skip holds only when routing selected the proxy endpoint and the
        // Authorization header is one well-formed bearer; any other shape falls
        // through to local authentication.
        var presentedBearer = ReadBearer(context.Request.Headers.Authorization.FirstOrDefault());
        if (IsProxiedTaskServerPlane(context, path)
            && IsWellFormedBearer(context.Request.Headers.Authorization)
            && presentedBearer?.StartsWith("api.", StringComparison.Ordinal) != true)
        {
            await _next(context);
            return;
        }

        // A request that carries a Studio session cookie, or asks for one, must
        // prove it came from a Studio page. SameSite does not separate sibling
        // hosts of the same site, and a WebSocket upgrade to an event stream
        // is a GET that carries the cookie too (AGT-3230).
        var browserCredential = context.Request.Cookies.ContainsKey(AccessSecurityStore.SessionCookieName)
                                || (!SecurityProfiles.IsNetworked(_configuration)
                                    && context.Request.Cookies.ContainsKey(AccessSecurityStore.InsecureSessionCookieName))
                                || BrowserOriginPolicy.IsBrowserSignIn(path);
        var origin = BrowserOriginPolicy.Decide(
            context.Request.Method,
            browserCredential,
            BrowserOriginPolicy.IsEventStream(path, context.Request.Headers),
            context.Request.Headers.Origin.FirstOrDefault(),
            context.Request.Headers["Sec-Fetch-Site"].FirstOrDefault(),
            context.Request.Scheme,
            context.Request.Host);
        if (!origin.Allowed)
        {
            _logger?.LogWarning(
                "Rejected {Method} {Path} from client {ClientAddress}: {Code}",
                context.Request.Method, path, context.Connection.RemoteIpAddress, origin.Code);
            await Reject(context, 403, origin.Code!, origin.Message!);
            return;
        }

        context.Items[AttributionClientIdItem] = context.Request.Headers["X-Client-Id"].FirstOrDefault();
        if (path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase))
            context.Response.Headers.CacheControl = "no-store";
        var sessionToken = context.Request.Cookies[AccessSecurityStore.SessionCookieName]
                           ?? (SecurityProfiles.IsNetworked(_configuration)
                               ? null
                               : context.Request.Cookies[AccessSecurityStore.InsecureSessionCookieName]);
        var human = _store.AuthenticateSession(sessionToken);
        var bearer = presentedBearer;
        var runner = _store.AuthenticateRunner(bearer);
        var apiToken = human is null ? _store.AuthenticateApiToken(bearer) : null;
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
            if (apiToken is not null)
                _store.AppendApiTokenAudit(apiToken.Token, context.Request.Method, path, context.Response.StatusCode);
            return;
        }

        // The OTLP intake authenticates its own per-environment ingest token
        // (TelemetryIntakeService). A Studio session or Runner credential
        // grants nothing there, and the intake needs neither.
        if (IsTelemetryIntake(context.Request.Method, path))
        {
            await _next(context);
            return;
        }

        if (path.Equals("/api/clients/register", StringComparison.OrdinalIgnoreCase))
        {
            await Reject(context, 404, "registration-disabled", "Open client registration is disabled in the networked profile.");
            if (apiToken is not null)
                _store.AppendApiTokenAudit(apiToken.Token, context.Request.Method, path, context.Response.StatusCode);
            return;
        }

        if (path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase))
        {
            if (human is null)
            {
                await Reject(context, 401, "authentication-required", "An authenticated Studio session is required for event streams.");
                if (apiToken is not null)
                    _store.AppendApiTokenAudit(apiToken.Token, context.Request.Method, path, context.Response.StatusCode);
                return;
            }
            if (human.User.MustChangePassword) { await Reject(context, 403, "password-change-required", "Change the temporary password before continuing."); return; }
            await _next(context);
            return;
        }

        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (path is "/api/v1/protocol" or "/api/v1/protocol/compatibility")
        {
            if (human is not null && !human.User.MustChangePassword)
            {
                await _next(context);
                return;
            }
            if (runner is null) { await Reject(context, 401, "runner-authentication-required", "A Runner service credential is required."); return; }
            if (!runner.Scopes.Contains(RunnerScopes.Claim) && !runner.Scopes.Contains(RunnerScopes.Orchestration))
            {
                await Reject(context, 403, "runner-scope-denied", "A Runner execution or engine scope is required.");
                return;
            }
            await _next(context);
            return;
        }

        // Secret values go only to a service credential; the endpoint binds it
        // to a live deploy-job or run lease and checks the matching scope.
        if (path.Equals(AgentStudio.Secrets.SecretEndpoints.ResolvePath, StringComparison.OrdinalIgnoreCase))
        {
            if (runner is null) { await Reject(context, 401, "runner-authentication-required", "A Runner service credential is required."); return; }
            await _next(context);
            return;
        }

        // Core-attach aliases are applied before this middleware. Check the
        // original versioned route as a fallback so only those client reads
        // gain a Runner scope, not other legacy /api/tasks/{id} reads.
        var originalPath = context.Items[AgentStudio.Host.StudioV1LegacyRouteAlias.OriginalPathItem] as string;
        var requiredRunnerScope = RequiredRunnerScope(context.Request.Method, path)
            ?? (originalPath is null ? null : RequiredRunnerScope(context.Request.Method, NormalizePath(originalPath)));
        if (requiredRunnerScope is not null
            && (human is null || !(IsHumanSessionRead(context.Request.Method, path)
                || originalPath is not null && IsHumanSessionRead(context.Request.Method, NormalizePath(originalPath)))))
        {
            if (runner is null)
            {
                await Reject(context, 401, "runner-authentication-required", "A Runner service credential is required.");
                if (apiToken is not null)
                    _store.AppendApiTokenAudit(apiToken.Token, context.Request.Method, path, context.Response.StatusCode);
                return;
            }
            if (!runner.Scopes.Contains(requiredRunnerScope)) { await Reject(context, 403, "runner-scope-denied", $"Runner credential lacks '{requiredRunnerScope}'."); return; }
            if (path.StartsWith("/api/v1/runners/", StringComparison.OrdinalIgnoreCase)
                && !path["/api/v1/runners/".Length..].Split('/')[0].Equals(runner.RunnerId, StringComparison.Ordinal))
            {
                await Reject(context, 403, "runner-identity-denied", "A Runner may access only its enrolled identity.");
                return;
            }
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

        if (apiToken is not null)
        {
            context.Items[ApiTokenPrincipalItem] = apiToken;
            context.Items["ClientId"] = $"api:{apiToken.Token.Id}";
            var faulted = false;
            try
            {
                if (path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase))
                {
                    await Reject(context, 403, "token-route-denied", "API tokens cannot call access administration or Task Server routes.");
                    return;
                }
                if (apiToken.User.Role == StudioRoles.Viewer
                    && !HttpMethods.IsGet(context.Request.Method)
                    && !HttpMethods.IsHead(context.Request.Method)
                    && !HttpMethods.IsOptions(context.Request.Method)
                    && !IsReadOnlyPost(context.Request.Method, path))
                {
                    await Reject(context, 403, "role-denied", "Viewer role is read-only.");
                    return;
                }
                if (await RejectedByRoleRequirement(context, apiToken.User.Role)) return;
                if (!TokenWriteRouteAllowed(context.Request, path))
                {
                    await Reject(context, 403, "project-scope-denied", "A project-scoped token cannot mutate installation-wide settings.");
                    return;
                }
                var declaredScope = context.GetEndpoint()?.Metadata.GetMetadata<ApiTokenRouteScope>()?.Scope
                    ?? ProjectRouteScope.WorkspaceWide;
                if (declaredScope is not (ProjectRouteScope.Addressed or ProjectRouteScope.DeferredToHandler)
                    && !apiToken.Token.Projects.Contains(ProjectAccessAuthorization.AllProjectsGrant, StringComparer.Ordinal))
                {
                    await Reject(context, 403, "project-scope-denied", "This token requires a workspace scope for this route.");
                    return;
                }
                if (!ProjectAllowed(apiToken.User, context.Request, path))
                {
                    await Reject(context, 403, "project-scope-denied", "This token is not scoped to the requested project.");
                    return;
                }
                // Collection handlers already filter by HumanPrincipalItem. A
                // synthetic session lets the same project checks use the token's
                // fixed role and grants, without granting a browser session.
                var synthetic = new StudioSession
                {
                    Id = $"api:{apiToken.Token.Id}", UserId = apiToken.User.Id,
                    TokenHash = string.Empty, CsrfHash = string.Empty,
                    CreatedAt = apiToken.Token.CreatedAt, LastSeenAt = apiToken.Token.LastUsedAt ?? apiToken.Token.CreatedAt,
                    ExpiresAt = apiToken.Token.ExpiresAt, AbsoluteExpiresAt = apiToken.Token.ExpiresAt
                };
                context.Items[HumanPrincipalItem] = new HumanPrincipal(apiToken.User, synthetic);
                await _next(context);
            }
            catch
            {
                faulted = true;
                throw;
            }
            finally
            {
                _store.AppendApiTokenAudit(apiToken.Token, context.Request.Method, path,
                    faulted ? StatusCodes.Status500InternalServerError : context.Response.StatusCode);
            }
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
            || path.StartsWith("/api/auth/invitations", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/auth/runners", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/auth/runner-enrollments", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/auth/api-tokens", StringComparison.OrdinalIgnoreCase))
        {
            if (human.User.Role != StudioRoles.Owner) { await Reject(context, 403, "owner-required", "Owner role is required."); return; }
        }
        if (await RejectedByRoleRequirement(context, human.User.Role)) return;

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

        if (human.User.Role == StudioRoles.Contributor)
        {
            var card = path.StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase)
                ? _scanner?.FindJob(Uri.UnescapeDataString(path["/api/tasks/".Length..].Split('/', 2)[0]))
                : null;
            var productionApproval = card is not null
                && human.User.ProductionApprovalProjects.Any(project =>
                    string.Equals(project, card.ProjectName, StringComparison.OrdinalIgnoreCase)
                    || (_projects?.FindByIdOrDisplayName(project)?.StorageLocation is { } location
                        && AgentStudio.Shared.WatchPathComparison.PathsEqual(location, card.WatchPath)));
            if (!ContributorAccessPolicy.Allows(context.Request.Method, path, card?.State, productionApproval))
            {
                await Reject(context, 403, "role-denied", "The Product role cannot perform this action.");
                return;
            }
        }

        if (!ProjectAllowed(human.User, context.Request, path))
        {
            await Reject(context, 403, "project-scope-denied", "This account is not a member of the requested project.");
            return;
        }

        await _next(context);
        if (human.User.Role == StudioRoles.Contributor
            && !safeMethod && context.Response.StatusCode is >= 200 and < 400
            && path.StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase)
            && _timeline is not null)
        {
            var taskId = Uri.UnescapeDataString(path["/api/tasks/".Length..].Split('/', 2)[0]);
            var updated = _scanner?.FindJob(taskId);
            if (updated is not null)
                _timeline.Append(updated.FolderPath, new TimelineEvent
                {
                    Ts = _clock.GetUtcNow().UtcDateTime,
                    Kind = "contributor-action",
                    Actor = TimelineActors.Human(human.User.Id),
                    Summary = $"{context.Request.Method} {path}"
                });
        }
    }

    private static string? RequiredRunnerScope(string method, string path)
    {
        if (HttpMethods.IsPost(method) && path.Equals("/api/deploy/gate-releases", StringComparison.OrdinalIgnoreCase))
            return RunnerScopes.Release;
        if (HttpMethods.IsPost(method) && path.StartsWith("/api/deploy/jobs/", StringComparison.OrdinalIgnoreCase))
            return RunnerScopes.Deploy;
        if (path.StartsWith("/api/pull-gates/", StringComparison.OrdinalIgnoreCase))
            return RunnerScopes.Gate;
        if (HttpMethods.IsGet(method)
            && path.StartsWith("/api/gate-artifacts/", StringComparison.OrdinalIgnoreCase))
            return RunnerScopes.Artifacts;
        if (path.StartsWith("/api/v1/orchestration/", StringComparison.OrdinalIgnoreCase))
            return RunnerScopes.Orchestration;
        // Keep the versioned client surface explicit. A service credential must
        // never fall through to browser-session authorization on a new route.
        if (path.StartsWith("/api/v1/runners/", StringComparison.OrdinalIgnoreCase))
        {
            if (HttpMethods.IsPut(method) && path.EndsWith("/outbox-status", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Events;
            if (HttpMethods.IsPut(method) && path.EndsWith("/capabilities", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Events;
            if (HttpMethods.IsPut(method) && path.Split('/').Length == 5) return RunnerScopes.Claim;
            if (HttpMethods.IsPost(method) && (path.EndsWith("/claims", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/review-claims", StringComparison.OrdinalIgnoreCase))) return RunnerScopes.Claim;
            if (HttpMethods.IsPost(method) && path.EndsWith("/reports", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Events;
            if (HttpMethods.IsPost(method) && path.EndsWith("/capability-failures", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Events;
            if (HttpMethods.IsPost(method) && path.EndsWith("/cli-update/result", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Events;
            if (HttpMethods.IsPost(method) && path.Contains("/provider-canary/", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
            if (HttpMethods.IsGet(method) && (path.EndsWith("/cli-update", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/provider-comparison", StringComparison.OrdinalIgnoreCase))) return RunnerScopes.Claim;
        }
        if (HttpMethods.IsGet(method) && (path is "/api/v1/projects" or "/api/v1/artifact-limits"
            || path.StartsWith("/api/v1/failure-fingerprints", StringComparison.OrdinalIgnoreCase))) return RunnerScopes.Claim;
        if (HttpMethods.IsGet(method) && path.StartsWith("/api/v1/projects/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = path.Split('/');
            if (parts.Length == 7 && parts[5].Equals("tasks", StringComparison.OrdinalIgnoreCase))
                return RunnerScopes.Claim;
        }
        if (HttpMethods.IsPost(method) && path.Equals("/api/v1/failure-fingerprints", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Events;
        if (path.StartsWith("/api/v1/reviews/attempts/", StringComparison.OrdinalIgnoreCase))
        {
            if (HttpMethods.IsGet(method)) return RunnerScopes.Claim;
            if (HttpMethods.IsPost(method) && path.EndsWith("/lease/renew", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Lease;
            if (HttpMethods.IsPost(method) && path.EndsWith("/reclaim", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
            if (HttpMethods.IsPost(method) && path.EndsWith("/report", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Completion;
            if (HttpMethods.IsPost(method) && path.EndsWith("/cleanup", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Completion;
        }
        if (path.StartsWith("/api/v1/integrations/", StringComparison.OrdinalIgnoreCase))
        {
            if (HttpMethods.IsPost(method) && path.EndsWith("/claims", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
            if (HttpMethods.IsPost(method) && path.EndsWith("/lease/renew", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Lease;
            if (HttpMethods.IsPost(method) && path.EndsWith("/report", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Completion;
        }
        if (path.StartsWith("/api/v1/work-permits/", StringComparison.OrdinalIgnoreCase)
            && HttpMethods.IsPost(method) && path.EndsWith("/accept", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
        if (path.StartsWith("/api/v1/runs/", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("/lease/", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Lease;
            if (path.EndsWith("/artifacts", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/artifacts/", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Artifacts;
            if (path.EndsWith("/events", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Events;
            if (path.EndsWith("/completion", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/result-handoff", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/result-finalization", StringComparison.OrdinalIgnoreCase)
                || (path.Contains("/post-steps/", StringComparison.OrdinalIgnoreCase)
                    && path.EndsWith("/complete", StringComparison.OrdinalIgnoreCase))
                || path.EndsWith("/revoked-reference", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Completion;
            if (path.EndsWith("/reconcile", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/claim", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
        }
        // The replay ingest sits above the general runner routes because its
        // credential must never satisfy any of them.
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/replay/events", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.DemoReplay;
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/claim", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
        if (path.StartsWith("/api/runner/lease", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Lease;
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/orchestrator-decisions/claim", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/orchestrator-decisions/complete", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Completion;
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/logs", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Logs;
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/events", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Events;
        if ((HttpMethods.IsPost(method) || HttpMethods.IsGet(method))
            && path.StartsWith("/api/runner/artifacts", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Artifacts;
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/completion", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Completion;
        if (HttpMethods.IsGet(method) && path.StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase) && path.Contains("/files/", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
        if (HttpMethods.IsGet(method) && path.Equals("/api/system/about", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/project-chat/claim", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/project-chat/renew", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Lease;
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/project-chat/complete", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Completion;
        if (HttpMethods.IsPost(method) && path.StartsWith("/api/clients/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/runner-git-capability", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Events;
        return GateRunnerScope(method, path);
    }

    private static bool IsHumanSessionRead(string method, string path)
    {
        if (!HttpMethods.IsGet(method)) return false;
        if (path is "/api/system/about" or "/api/v1/projects") return true;
        if (path.StartsWith("/api/v1/failure-fingerprints", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v1/reviews/attempts/", StringComparison.OrdinalIgnoreCase)) return true;
        if (!path.StartsWith("/api/v1/projects/", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = path.Split('/');
        return parts.Length == 7 && parts[5].Equals("tasks", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runner routes of claimable gate work on the monolith authority (AGT-3055):
    /// a gate host claims, renews, advances, and reports a gate attempt with the
    /// same scopes as a coding attempt. Subject creation and cancellation stay
    /// authority-internal and have no route here.
    /// </summary>
    private static string? GateRunnerScope(string method, string path)
    {
        if (!path.StartsWith("/api/v1/gates/", StringComparison.OrdinalIgnoreCase)) return null;
        if (HttpMethods.IsGet(method) && path.StartsWith("/api/v1/gates/subjects/", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
        if (!HttpMethods.IsPost(method)) return null;
        if (path.Equals("/api/v1/gates/claims", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
        if (!path.StartsWith("/api/v1/gates/attempts/", StringComparison.OrdinalIgnoreCase)) return null;
        if (path.EndsWith("/renew", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Lease;
        if (path.EndsWith("/phase", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Events;
        if (path.EndsWith("/report", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/containment", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Completion;
        return null;
    }

    private static bool IsRunnerSelfRead(string method, string path, string runnerId)
        => HttpMethods.IsGet(method)
           && (path.Equals("/api/auth/runner", StringComparison.OrdinalIgnoreCase)
               || path.Equals($"/api/clients/{runnerId}", StringComparison.OrdinalIgnoreCase));

    private static bool IsHealth(string path)
        => path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/healthz/drain", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/healthz/orchestration-engine", StringComparison.OrdinalIgnoreCase);
    private static bool IsTelemetryIntake(string method, string path)
        => HttpMethods.IsPost(method)
           && (path.Equals("/api/telemetry/otlp/v1/metrics", StringComparison.OrdinalIgnoreCase)
               || path.Equals("/api/telemetry/otlp/v1/logs", StringComparison.OrdinalIgnoreCase));
    private static bool IsOpenAuth(string path)
        => path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/auth/bootstrap", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/auth/invite/accept", StringComparison.OrdinalIgnoreCase)
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

    private bool IsProxiedTaskServerPlane(HttpContext context, string path)
        => TaskServerPlaneProxy.IsConfigured(_configuration)
           && path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
           && context.GetEndpoint()?.Metadata.GetMetadata<TaskServerPlaneProxyEndpoint>() is not null;

    private const int MaxBearerLength = 4096;

    /// <summary>
    /// Exactly one <c>Authorization: Bearer &lt;b64token&gt;</c> value (RFC 6750
    /// section 2.1), bounded in length. Empty, multi-valued, non-bearer, or
    /// whitespace-bearing shapes are not well formed.
    /// </summary>
    internal static bool IsWellFormedBearer(StringValues authorization)
    {
        if (authorization.Count != 1) return false;
        var value = authorization[0];
        if (value is null || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var token = value.AsSpan("Bearer ".Length);
        if (token.Length is 0 or > MaxBearerLength) return false;
        var index = 0;
        while (index < token.Length && IsB64TokenChar(token[index])) index++;
        if (index == 0) return false;
        while (index < token.Length && token[index] == '=') index++;
        return index == token.Length;
    }

    private static bool IsB64TokenChar(char ch)
        => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
            or '-' or '.' or '_' or '~' or '+' or '/';

    private static string? ReadBearer(string? authorization)
        => authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? authorization[7..].Trim() : null;

    private bool ProjectAllowed(StudioUser user, HttpRequest request, string path)
    {
        if (ProjectAccessAuthorization.HasUnrestrictedProjectAccess(user)) return true;
        return ProjectScopePolicy.Allows(
            ResolveRouteAddress(request, path),
            request.Query["project"].FirstOrDefault(),
            project => ProjectAccessAuthorization.Allows(user, project, _projects));
    }

    private bool TokenWriteRouteAllowed(HttpRequest request, string path)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
            || HttpMethods.IsOptions(request.Method) || IsReadOnlyPost(request.Method, path))
            return true;
        var route = ResolveRouteAddress(request, path);
        // The task creation handler validates its body project before writing.
        // Deferred task handlers validate every affected task. An unaddressed
        // installation mutation has no project to authorize against.
        return route.Scope is ProjectRouteScope.Addressed or ProjectRouteScope.DeferredToHandler
               || (HttpMethods.IsPost(request.Method) && path.Equals("/api/tasks", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Derives the project(s) a request addresses from its route or addressed
    /// task. <c>?project=</c> is read here only where the query is itself the
    /// task address (<c>/core</c>); everywhere else it can only narrow.
    /// </summary>
    private ProjectRouteAddress ResolveRouteAddress(HttpRequest request, string path)
    {
        if (path.Equals("/api/workspace/tokens/timeline", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/workspace/tokens/timeline/cached", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/workspace/tokens/expensive-jobs", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/workspace/tokens/expensive-jobs/cached", StringComparison.OrdinalIgnoreCase))
            return ProjectRouteAddress.WorkspaceWide;
        if (path.StartsWith("/api/runner/global", StringComparison.OrdinalIgnoreCase))
            return ProjectRouteAddress.WorkspaceWide;
        if (path.StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/core", StringComparison.OrdinalIgnoreCase))
        {
            // The core read looks the task up only inside the named project,
            // so the query is part of the task address and is mandatory.
            return ProjectRouteAddress.Addressed(request.Query["project"].FirstOrDefault());
        }
        if (path.StartsWith("/api/projects/", StringComparison.OrdinalIgnoreCase))
        {
            var segment = path["/api/projects/".Length..].Split('/', 2)[0];
            return segment is "settings" or "pipeline-catalogue"
                ? ProjectRouteAddress.Unaddressed
                : ProjectRouteAddress.Addressed(Uri.UnescapeDataString(segment));
        }
        if (path.StartsWith("/api/runner/", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = path["/api/runner/".Length..].Split('/', 2)[0];
            return candidate is "status" or "orchestrator-feed" or "token-summary-aggregate"
                or "claim" or "lease" or "logs" or "events" or "artifacts" or "completion"
                ? ProjectRouteAddress.Unaddressed
                : ProjectRouteAddress.Addressed(Uri.UnescapeDataString(ContextRouteProject(path, candidate)));
        }
        if (path.StartsWith("/api/orchestrator/", StringComparison.OrdinalIgnoreCase))
        {
            // Context routes are task:{projectId}/{taskKey} and
            // workbench:{projectId}/{workbenchKey}. The first segment after
            // the colon is the project, never the task or workbench key. Read
            // the prefix only at the start of the context key: a task key can
            // itself contain text such as "project:mine".
            var context = path.StartsWith("/api/orchestrator/context/", StringComparison.OrdinalIgnoreCase)
                ? path["/api/orchestrator/context/".Length..]
                : path.StartsWith("/api/orchestrator/sessions/", StringComparison.OrdinalIgnoreCase)
                    ? path["/api/orchestrator/sessions/".Length..]
                    : null;
            if (context is null) return ProjectRouteAddress.WorkspaceWide;
            foreach (var prefix in new[] { "project:", "task:", "workbench:" })
                if (context.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return ProjectRouteAddress.Addressed(Uri.UnescapeDataString(context[prefix.Length..].Split('/', 2)[0]));
            return ProjectRouteAddress.WorkspaceWide;
        }
        if (path.StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase))
        {
            var taskId = path["/api/tasks/".Length..].Split('/', 2)[0];
            // Body-addressed and workspace-collection task routes carry their task
            // set in the request body (reorder, batch-move) or filter their payload
            // per task (reference-status, archive). Those handlers enforce membership
            // on every affected task via ProjectAccessAuthorization.AllowsTasks /
            // FilterTasks, so the middleware defers instead of inferring a single
            // project from the literal path segment.
            if (taskId is "reorder" or "batch-move" or "reference-status" or "archive")
                return ProjectRouteAddress.DeferredToHandler;
            // Every other /api/tasks/{id}/... route is single-task addressed. A
            // scoped account may act only on a task whose project it belongs to. If
            // that project cannot be resolved (unknown id or the scanner is
            // unavailable), fail closed rather than allow the request through.
            return ProjectRouteAddress.Addressed(ResolveTaskProjects(Uri.UnescapeDataString(taskId), request));
        }
        return ProjectRouteAddress.Unaddressed;
    }

    private static string ContextRouteProject(string path, string candidate)
    {
        // Only the concrete context-chat routes interpret these prefixes.
        // Other runner routes may address a project whose literal name starts
        // with the same text and must keep checking that full name.
        var segments = path["/api/runner/".Length..].Split('/');
        if (segments.Length == 2
            && segments[1].Equals("orchestrator-chat", StringComparison.OrdinalIgnoreCase)
            && candidate.StartsWith("project:", StringComparison.OrdinalIgnoreCase))
            return candidate["project:".Length..];
        if (segments.Length == 3
            && segments[2].Equals("orchestrator-chat", StringComparison.OrdinalIgnoreCase))
        {
            if (candidate.StartsWith("task:", StringComparison.OrdinalIgnoreCase))
                return candidate["task:".Length..];
            if (candidate.StartsWith("workbench:", StringComparison.OrdinalIgnoreCase))
                return candidate["workbench:".Length..];
        }
        return candidate;
    }

    /// <summary>
    /// Every project a single-task handler could act on: the unscoped lookup
    /// plus the lookups scoped by <c>?project=</c> and <c>?watchPath=</c>, the
    /// way task handlers resolve their scope. A query value therefore cannot
    /// steer authorization towards a member project while the handler acts on
    /// a foreign task.
    /// </summary>
    private string?[] ResolveTaskProjects(string taskId, HttpRequest request)
    {
        var task = _scanner?.FindJob(taskId);
        if (task is null) return [null];
        var projects = new List<string?> { task.ProjectName };
        var watchPath = request.Query["watchPath"].FirstOrDefault();
        var scopes = new[]
        {
            watchPath,
            _projects is null ? null : AgentStudio.Tasks.TaskEndpointHelpers.ResolveWatchPath(_projects, request.Query["project"].FirstOrDefault(), watchPath),
        };
        foreach (var scope in scopes.Where(scope => !string.IsNullOrWhiteSpace(scope)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var scoped = _scanner!.FindJob(taskId, scope);
            if (scoped is not null) projects.Add(scoped.ProjectName);
        }
        return [.. projects];
    }

    /// <summary>Applies the route's declared <see cref="StudioRoleRequirement"/>, if any.</summary>
    private static async Task<bool> RejectedByRoleRequirement(HttpContext context, string role)
    {
        var requirement = context.GetEndpoint()?.Metadata.GetMetadata<StudioRoleRequirement>();
        if (requirement is null || requirement.IsSatisfiedBy(role)) return false;
        if (requirement.MinimumRole == StudioRoles.Owner)
            await Reject(context, 403, "owner-required", "Owner role is required.");
        else
            await Reject(context, 403, "role-denied", $"The {requirement.MinimumRole} role or higher is required.");
        return true;
    }

    private static async Task Reject(
        HttpContext context,
        int status,
        string code,
        string message,
        string? loginUrl = null)
    {
        // A refused write on a secret route never reaches the secret provider; audit it with the person behind it.
        if (status == StatusCodes.Status403Forbidden && context.Items[HumanPrincipalItem] is HumanPrincipal human)
            AgentStudio.Secrets.SecretEndpoints.AuditRefusedWrite(context, AgentStudio.Secrets.SecretActor.Human(human.User.Username), code);
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
