using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;

namespace AgentStudio.Security;

/// <summary>
/// Fail-closed authentication and project-scoped authorization. Every human
/// and service-account request must hold the named permission its route
/// declares (<see cref="RoutePermissionCatalog"/>) through a role binding that
/// covers the addressed scope. Handlers then see a per-request projected
/// principal, never the persisted legacy summary.
/// </summary>
public sealed class AccessSecurityMiddleware
{
    public const string HumanPrincipalItem = "AccessSecurity.HumanPrincipal";
    public const string RunnerPrincipalItem = "AccessSecurity.RunnerPrincipal";
    public const string RunnerCredentialExpiresHeader = "X-Principal-Credential-Expires-At";
    public const string ServiceAccountPrincipalItem = "AccessSecurity.ServiceAccountPrincipal";
    /// <summary>The <see cref="RequestAccess"/> captured with the request's permission decision.</summary>
    public const string RequestAccessItem = "AccessSecurity.RequestAccess";
    public const string AttributionClientIdItem = "AccessSecurity.AttributionClientId";
    /// <summary>The operator policy step's <see cref="OperatorPolicyDecision"/> for an operator session request (SM-4).</summary>
    public const string OperatorPolicyItem = "AccessSecurity.OperatorPolicy";
    /// <summary>The id of the Owner approval a T2 operator request runs under (SM-5).</summary>
    public const string OperatorApprovalItem = "AccessSecurity.OperatorApproval";
    private const string ParameterDigestItem = "AccessSecurity.ParameterDigest";
    private const string TaskResolutionItem = "AccessSecurity.TaskResolution";
    private const string AuditTargetItem = "AccessSecurity.AuditTarget";
    /// <summary>Bodies above this size are digested by length only, without buffering them.</summary>
    private const long MaxDigestedBodyBytes = 16L * 1024 * 1024;
    private const string SkipTaskResolutionItem = "AccessSecurity.SkipTaskResolution";

    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly AccessSecurityStore _store;
    private readonly TaskScannerService? _scanner;
    private readonly TaskIndexCache? _taskIndex;
    private readonly RunLeaseService? _leases;
    private readonly AgentStudio.Registry.ProjectRegistry? _projects;
    private readonly TimelineLog? _timeline;
    private readonly TimeProvider _clock;
    private readonly AgentStudio.Registry.ProjectUrlPreviewAccess _previewAccess;
    private readonly AgentStudio.Projects.ProjectEnvironmentService? _environments;
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
        AgentStudio.Projects.ProjectEnvironmentService? environments = null,
        ILogger<AccessSecurityMiddleware>? logger = null,
        TaskIndexCache? taskIndex = null)
    {
        _next = next;
        _configuration = configuration;
        _store = store;
        _scanner = scanner;
        _taskIndex = taskIndex;
        _leases = leases;
        _projects = projects;
        _timeline = timeline;
        _clock = clock ?? TimeProvider.System;
        _previewAccess = previewAccess ?? new AgentStudio.Registry.ProjectUrlPreviewAccess(TimeProvider.System);
        _environments = environments;
        _logger = logger;
    }

    /// <summary>
    /// Who a handler attributes an action to: <c>service-account:&lt;id&gt;</c> for a
    /// service account or API token, the user id for a signed-in human, and
    /// <paramref name="fallback"/> without a principal (local profile).
    /// </summary>
    public static string ActorId(HttpContext context, string fallback)
        => context.Items[ServiceAccountPrincipalItem] is ServiceAccountPrincipal account ? account.ActorId
            : context.Items[HumanPrincipalItem] is HumanPrincipal human ? human.User.Id
            : fallback;

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
        // through to local authentication. Studio's own API-token and
        // service-account bearers (api.*, sat.*) never take the skip.
        var presentedBearer = ReadBearer(context.Request.Headers.Authorization.FirstOrDefault());
        if (IsProxiedTaskServerPlane(context, path)
            && IsWellFormedBearer(context.Request.Headers.Authorization)
            && !AccessSecurityStore.IsServiceAccountBearer(presentedBearer))
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
        var serviceAccount = human is null && runner is null ? _store.AuthenticateServiceAccount(bearer) : null;
        if (HttpMethods.IsGet(context.Request.Method)
            && path.StartsWith("/api/quota/admission/", StringComparison.OrdinalIgnoreCase))
        {
            var serviceToken = _configuration["TaskServer:QuotaAdmissionBearerToken"];
            var serviceAuthenticated = !string.IsNullOrWhiteSpace(serviceToken)
                && bearer is not null
                && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(serviceToken)),
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bearer)));
            if (!serviceAuthenticated && (runner is null || !runner.Scopes.Contains(RunnerScopes.Claim)))
            {
                await Reject(context, 403, "quota-admission-scope-denied",
                    "A Task Server service credential or Runner claim scope is required.");
                return;
            }
            await _next(context);
            return;
        }
        if (human is not null)
        {
            context.Items[HumanPrincipalItem] = human;
            context.Items["ClientId"] = human.User.Id;
        }
        if (runner is not null)
        {
            context.Items[RunnerPrincipalItem] = runner;
            context.Items["ClientId"] = runner.RunnerId;
            // SM-9: every answer names the credential's end so the host warns ahead of it.
            if (runner.CredentialExpiresAt is { } credentialExpiresAt)
                context.Response.Headers[RunnerCredentialExpiresHeader] =
                    credentialExpiresAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }
        if (serviceAccount is null)
        {
            await AuthorizeAsync(context, path, human, runner, null);
            return;
        }

        // Every call a service-account, API-token, or operator session bearer
        // makes is audited, refused calls included. An operator session runs
        // inside its actor scope, so every timeline event and audit entry the
        // request writes names the session (security model SM-1).
        context.Items[ServiceAccountPrincipalItem] = serviceAccount;
        context.Items["ClientId"] = serviceAccount.ActorId;
        var parameterDigest = await ParameterDigestAsync(context.Request, path);
        context.Items[ParameterDigestItem] = parameterDigest;
        using var operatorScope = serviceAccount.Operator is { } operatorActor ? OperatorActorScope.Enter(operatorActor) : null;
        var faulted = false;
        try
        {
            await AuthorizeAsync(context, path, null, null, serviceAccount);
        }
        catch
        {
            faulted = true;
            throw;
        }
        finally
        {
            var status = faulted ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
            _store.AppendServiceAccountAudit(serviceAccount, context.Request.Method, path, status,
                AuditTarget(context, path), parameterDigest.Value, context.Items[OperatorPolicyItem] as OperatorPolicyDecision);
            // SM-5: the result of a request an approval admitted is recorded against that approval.
            if (context.Items[OperatorApprovalItem] is string approvalId) _store.RecordOperatorApprovalResult(approvalId, status);
        }
    }

    private async Task AuthorizeAsync(
        HttpContext context, string path, HumanPrincipal? human, RunnerPrincipal? runner, ServiceAccountPrincipal? serviceAccount)
    {
        if (IsOpenAuth(path))
        {
            await _next(context);
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
            return;
        }

        if (path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase))
        {
            if (human is null) { await Reject(context, 401, "authentication-required", "An authenticated Studio session is required for event streams."); return; }
            if (human.User.MustChangePassword) { await Reject(context, 403, "password-change-required", "Change the temporary password before continuing."); return; }
            if (!await HumanPermissionAllowed(context, human, path)) return;
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
                if (!await HumanPermissionAllowed(context, human, path)) return;
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
            if (serviceAccount is not null)
            {
                await Reject(context, 403, "secret-resolve-denied", "Service accounts and API tokens never read secret values.");
                return;
            }
            if (runner is null) { await Reject(context, 401, "runner-authentication-required", "A Runner service credential is required."); return; }
            await _next(context);
            return;
        }

        // Core-attach aliases are applied before this middleware. Check the
        // original versioned route as a fallback so only those client reads
        // gain a Runner scope, not other legacy /api/tasks/{id} reads.
        var originalPath = context.Items[AgentStudio.Host.StudioV1LegacyRouteAlias.OriginalPathItem] as string;
        var humanTaskFileRead = human is not null && IsRunnerTaskFileRead(context.Request.Method, path);
        var productTaskFileRead = humanTaskFileRead && ContributorAccessPolicy.IsProductSession(human!.User);
        // A signed-in member reads card attachments through the route's own
        // permission; only a service credential takes the runner branch.
        var humanTaskInputRead = humanTaskFileRead
            || human is not null && IsRunnerTaskAttachmentRead(context.Request.Method, path);
        if (productTaskFileRead
            && !ContributorAccessPolicy.Allows(context.Request.Method, path, null, false))
        {
            await Reject(context, 403, "role-denied", "The Product role cannot read this file.");
            return;
        }
        var requiredRunnerScope = RequiredRunnerScope(context.Request.Method, path)
            ?? (originalPath is null ? null : RequiredRunnerScope(context.Request.Method, NormalizePath(originalPath)));
        if (requiredRunnerScope is not null
            && (human is null || !(humanTaskInputRead || IsHumanSessionRead(context.Request.Method, path)
                || originalPath is not null && IsHumanSessionRead(context.Request.Method, NormalizePath(originalPath)))))
        {
            if (runner is null) { await Reject(context, 401, "runner-authentication-required", "A Runner service credential is required."); return; }
            if (!runner.Scopes.Contains(requiredRunnerScope)) { await Reject(context, 403, "runner-scope-denied", $"Runner credential lacks '{requiredRunnerScope}'."); return; }
            if (path.StartsWith("/api/v1/runners/", StringComparison.OrdinalIgnoreCase)
                && !path["/api/v1/runners/".Length..].Split('/')[0].Equals(runner.RunnerId, StringComparison.Ordinal))
            {
                await Reject(context, 403, "runner-identity-denied", "A Runner may access only its enrolled identity.");
                return;
            }
            if ((IsRunnerTaskFileRead(context.Request.Method, path) || IsRunnerTaskAttachmentRead(context.Request.Method, path))
                && !RunnerOwnsTaskInput(path, runner.RunnerId))
            {
                await Reject(context, 403, "runner-task-denied",
                    "A Runner may read prompt.md and the card's attachments only while it holds that task's live lease.");
                return;
            }
            // SM-9: a card is read only by the runner that holds its lease.
            if (RunnerCardReadKey(context.Request.Method, originalPath is null ? path : NormalizePath(originalPath)) is { } cardKey
                && !RunnerHoldsLease(cardKey, runner.RunnerId))
            {
                await Reject(context, 403, "runner-card-not-leased", "A runner may read a card only while it holds that card's lease.");
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

        // A host also calls feature routes that only the standalone Task
        // Server maps. A runner credential learns that the route does not
        // exist here, so the host pauses that loop instead of retrying a
        // refusal. An anonymous caller still gets 401 below. No middleware
        // after this one answers an unrouted request.
        if (runner is not null && human is null && context.GetEndpoint() is null)
        {
            await RejectUnmappedRoute(context);
            return;
        }

        if (serviceAccount is not null)
        {
            await AuthorizeServiceAccount(context, serviceAccount, path);
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
        if (human.Session.SecondFactorEnrollmentOnly
            && path is not "/api/auth/session" and not "/api/auth/logout" and not "/api/auth/change-password"
            && !path.StartsWith("/api/auth/totp/", StringComparison.Ordinal)
            && !path.StartsWith("/api/auth/passkeys/", StringComparison.Ordinal))
        {
            await Reject(context, 403, "second-factor-enrollment-required", "Register a second factor before continuing.");
            return;
        }
        if (human.User.MustChangePassword && path is not "/api/auth/change-password" and not "/api/auth/logout" and not "/api/auth/session")
        {
            await Reject(context, 403, "password-change-required", "Change the temporary password before continuing.");
            return;
        }

        var safeMethod = IsSafe(context.Request.Method, path);
        if (context.GetEndpoint() is { } endpoint)
            await AuthorizeHuman(context, human, endpoint, path, safeMethod);
        else
            await AuthorizeUnroutedHuman(context, human, path, safeMethod);
    }

    /// <summary>
    /// A signed-in human on a routed endpoint. The route's named permission
    /// is decided against the user's role bindings; the request then carries
    /// a projected principal (role and projects for this route only) and the
    /// captured <see cref="RequestAccess"/>, so no handler authorizes through
    /// the persisted legacy summary.
    /// </summary>
    private async Task AuthorizeHuman(HttpContext context, HumanPrincipal human, Endpoint endpoint, string path, bool safeMethod)
    {
        if (RoutePermissionCatalog.IsMethodRejection(endpoint))
        {
            await _next(context);
            return;
        }
        var required = RoutePermissionCatalog.Resolve(endpoint, context.Request.Method);
        if (required is null)
        {
            await Reject(context, 403, "permission-undeclared", "This route declares no permission.");
            return;
        }
        if (IamPermissions.IsUnbound(required.Permission))
        {
            if (!safeMethod && !_store.ValidateCsrf(human.Session, context.Request.Headers["X-CSRF-Token"].FirstOrDefault()))
            {
                await Reject(context, 403, "csrf-invalid", "A valid CSRF token is required for browser mutations.");
                return;
            }
            await _next(context);
            return;
        }

        var (subject, bindings) = _store.PrincipalBindings(IamPrincipalKind.User, human.User.Id);
        context.Items[SkipTaskResolutionItem] = WorkspaceBindingsDecide(subject, bindings);
        var target = await TargetAsync(context, path, required, safeMethod);
        var decision = PermissionEvaluator.Evaluate(required.Permission, subject, bindings, target, SameProject);
        var projected = PermissionEvaluator.Project(required.Permission, subject, bindings, target, SameProject);
        if (await RejectedByRoleRequirement(context, projected.LegacyRole)) return;
        // A caller with no binding at all on what the request addresses is
        // refused as a non-member, not as a read-only or Product role.
        if (decision.ScopeDenied
            && !PermissionEvaluator.Evaluate(IamPermissions.ProjectRead, subject, bindings, target, SameProject).Allowed)
        {
            await RejectPermission(context, decision, required.Permission);
            return;
        }
        if (!safeMethod && projected.LegacyRole == StudioRoles.Viewer
            && required.Permission != IamPermissions.EnvironmentProductionDeployApprove)
        {
            await Reject(context, 403, "role-denied", "Viewer role is read-only.");
            return;
        }
        if (!safeMethod && !_store.ValidateCsrf(human.Session, context.Request.Headers["X-CSRF-Token"].FirstOrDefault()))
        {
            await Reject(context, 403, "csrf-invalid", "A valid CSRF token is required for browser mutations.");
            return;
        }

        // The Product allowlist applies wherever Contributor is the strongest
        // role on what the request addresses.
        var productRole = projected.Role == IamRoles.Contributor;
        if (productRole)
        {
            var card = path.StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase)
                && IsParameterSegment(endpoint, 2)
                ? await FindTaskAsync(context, Uri.UnescapeDataString(path["/api/tasks/".Length..].Split('/', 2)[0]))
                : null;
            var productionApproval = card is not null
                && PermissionEvaluator.Evaluate(IamPermissions.EnvironmentProductionDeployApprove, subject, bindings,
                    PermissionTarget.ForProject(card.ProjectName, LegacyRoleMapping.ProductionEnvironment), SameProject).Allowed;
            if (!ContributorAccessPolicy.Allows(context.Request.Method, path, card?.State, productionApproval))
            {
                await Reject(context, 403, "role-denied", "The Product role cannot perform this action.");
                return;
            }
        }

        if (!decision.Allowed)
        {
            await RejectPermission(context, decision, required.Permission);
            return;
        }

        context.Items[HumanPrincipalItem] = new HumanPrincipal(ProjectedUser(human.User, projected, subject, bindings), human.Session);
        context.Items[RequestAccessItem] = new RequestAccess(subject, bindings, required.Permission, SameProject, projected.Role);
        await _next(context);
        if (productRole
            && !safeMethod && context.Response.StatusCode is >= 200 and < 400
            && path.StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase)
            && _timeline is not null)
        {
            var taskId = Uri.UnescapeDataString(path["/api/tasks/".Length..].Split('/', 2)[0]);
            var updated = _scanner?.FindPublishedJob(taskId);
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

    /// <summary>
    /// A request routing did not match executes no endpoint handler. Later
    /// middleware may still answer it, so it keeps the legacy checks against
    /// the persisted, downgrade-safe summary, which never exceeds the bindings.
    /// </summary>
    private async Task AuthorizeUnroutedHuman(HttpContext context, HumanPrincipal human, string path, bool safeMethod)
    {
        if (path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase) && !IsSelfServiceAuth(path)
            && human.User.Role != StudioRoles.Owner)
        {
            await Reject(context, 403, "owner-required", "Owner role is required.");
            return;
        }
        if (!safeMethod)
        {
            if (human.User.Role == StudioRoles.Viewer && !IsSelfServiceAuth(path)) { await Reject(context, 403, "role-denied", "Viewer role is read-only."); return; }
            if (!_store.ValidateCsrf(human.Session, context.Request.Headers["X-CSRF-Token"].FirstOrDefault()))
            {
                await Reject(context, 403, "csrf-invalid", "A valid CSRF token is required for browser mutations.");
                return;
            }
        }
        if (human.User.Role == StudioRoles.Contributor && !ContributorAccessPolicy.Allows(context.Request.Method, path, null, false))
        {
            await Reject(context, 403, "role-denied", "The Product role cannot perform this action.");
            return;
        }
        if (!await ProjectAllowedAsync(human.User, context, path))
        {
            await Reject(context, 403, "project-scope-denied", "This account is not a member of the requested project.");
            return;
        }
        await _next(context);
    }

    /// <summary>
    /// The named-permission check for a signed-in human on hub and protocol
    /// routes, which need no CSRF token. A request without a routed endpoint
    /// cannot execute a handler and is left to the 404 path.
    /// </summary>
    private async Task<bool> HumanPermissionAllowed(HttpContext context, HumanPrincipal human, string path)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null || RoutePermissionCatalog.IsMethodRejection(endpoint)) return true;
        var required = RoutePermissionCatalog.Resolve(endpoint, context.Request.Method);
        if (required is null)
        {
            await Reject(context, 403, "permission-undeclared", "This route declares no permission.");
            return false;
        }
        var (subject, bindings) = _store.PrincipalBindings(IamPrincipalKind.User, human.User.Id);
        var target = await TargetAsync(context, path, required, safeMethod: true);
        var decision = PermissionEvaluator.Evaluate(required.Permission, subject, bindings, target, SameProject);
        var projected = PermissionEvaluator.Project(required.Permission, subject, bindings, target, SameProject);
        if (await RejectedByRoleRequirement(context, projected.LegacyRole)) return false;
        if (!decision.Allowed)
        {
            await RejectPermission(context, decision, required.Permission);
            return false;
        }
        context.Items[HumanPrincipalItem] = new HumanPrincipal(ProjectedUser(human.User, projected, subject, bindings), human.Session);
        context.Items[RequestAccessItem] = new RequestAccess(subject, bindings, required.Permission, SameProject, projected.Role);
        return true;
    }

    /// <summary>
    /// Service accounts and migrated API tokens authenticate with a bearer and
    /// need no CSRF token. They act only through their own role bindings, never
    /// on access administration or the versioned Task Server plane; an
    /// unaddressed route needs a workspace binding. Handlers see a synthetic
    /// principal <c>service-account:&lt;id&gt;</c> projected like a human's, so collection
    /// filters and attribution apply unchanged.
    /// </summary>
    private async Task AuthorizeServiceAccount(HttpContext context, ServiceAccountPrincipal account, string path)
    {
        // SM-4: the policy step is the only way from an operator session to an
        // effect. A request outside the catalogue ends here, before any
        // binding is read; T1 waits for its grant below. A T2 request waits
        // below for the Owner's approval of exactly this request (SM-5), once
        // the floors and bindings admitted it; a T2 request the route floor
        // refuses to every operator session (access administration, the Task
        // Server plane) ends here and never becomes an approval request.
        OperatorOperation? operation = null;
        if (account.Operator is not null)
        {
            operation = await OperatorRequestClassifier.ClassifyAsync(context, IsSafe(context.Request.Method, path),
                context.RequestServices?.GetService<IOperatorGateEvidence>(), OperatorEnvironmentKind,
                key => RouteValue(context, path, context.GetEndpoint(), key));
            var gate = OperatorPolicy.TierGate(operation);
            var awaitsApproval = operation is { Tier: OperatorTier.T2 } && !IsTokenDeniedRoute(path);
            if (gate is not null && !awaitsApproval) context.Items[OperatorPolicyItem] = gate;
            if (gate is { Admitted: false } && !awaitsApproval)
            {
                await Reject(context, 403, gate.Reason!, gate.Message, policy: gate);
                return;
            }
            // A request naming an approval that cannot admit it (used, changed, expired, another session's)
            // is refused here, whatever became of its target; the approval is consumed only below.
            if (awaitsApproval && ApprovalHeader(context) is { } approvalId)
            {
                var check = _store.RedeemOperatorApproval(approvalId, account.AccountId, account.Operator.SessionId, operation!,
                    ((ParameterDigest)context.Items[ParameterDigestItem]!).Value, consume: false);
                if (!check.Admitted)
                {
                    context.Items[OperatorPolicyItem] = check;
                    await Reject(context, 403, check.Reason!, check.Message, policy: check);
                    return;
                }
            }
        }
        if (IsTokenDeniedRoute(path))
        {
            await Reject(context, 403, "token-route-denied", "Service accounts and API tokens cannot call access administration or Task Server routes.");
            return;
        }
        var endpoint = context.GetEndpoint();
        if (endpoint is not null && RoutePermissionCatalog.IsMethodRejection(endpoint))
        {
            await _next(context);
            return;
        }
        var required = endpoint is null ? null : RoutePermissionCatalog.Resolve(endpoint, context.Request.Method);
        if (required is null)
        {
            await Reject(context, 403, "permission-undeclared", "This route declares no permission.");
            return;
        }
        if (required.Permission == IamPermissions.SelfSession)
        {
            await Reject(context, 403, "human-session-required", "A service account has no Studio session.");
            return;
        }
        // An operator session never administers access, reads or writes the
        // audit trail, takes Owner-reserved installation changes, or resolves
        // a secret value, whatever its bindings say.
        if (account.Operator is not null && OperatorDeniedPermission(required.Permission))
        {
            await Reject(context, 403, "operator-route-denied",
                "An operator agent session cannot administer access, change the audit trail, or resolve secrets.");
            return;
        }
        var safeMethod = IsSafe(context.Request.Method, path);
        var (subject, bindings) = _store.PrincipalBindings(IamPrincipalKind.ServiceAccount, account.AccountId);
        context.Items[SkipTaskResolutionItem] = (safeMethod || account.Origin != ServiceAccountOrigins.ApiToken)
            && WorkspaceBindingsDecide(subject, bindings);
        var target = await TargetAsync(context, path, required, safeMethod);
        context.Items[AuditTargetItem] = target;
        var decision = PermissionEvaluator.Evaluate(required.Permission, subject, bindings, target, SameProject);
        var projected = PermissionEvaluator.Project(required.Permission, subject, bindings, target, SameProject);
        if (await RejectedByRoleRequirement(context, projected.LegacyRole)) return;
        if (!decision.Allowed)
        {
            await RejectPermission(context, decision, required.Permission);
            return;
        }
        if (!safeMethod && projected.LegacyRole == StudioRoles.Viewer)
        {
            await Reject(context, 403, "role-denied", "Viewer role is read-only.");
            return;
        }
        // The operator agent role keeps installation operation read-only: its
        // writes address projects, whatever principal holds it (OP-1).
        if (!safeMethod && projected.Role == IamRoles.OperatorAgent
            && target.Scope is not (ProjectRouteScope.Addressed or ProjectRouteScope.DeferredToHandler))
        {
            await Reject(context, 403, "project-scope-denied", "The operator agent role cannot make installation-wide changes.");
            return;
        }
        // An API token (AGT-3186) writes only project-addressed routes, even
        // with a workspace binding; an Access-area service account may hold
        // installation-wide writes through its own bindings.
        if (!safeMethod && account.Origin == ServiceAccountOrigins.ApiToken
            && target.Scope is not (ProjectRouteScope.Addressed or ProjectRouteScope.DeferredToHandler))
        {
            await Reject(context, 403, "project-scope-denied", "An API token cannot mutate installation-wide settings.");
            return;
        }
        var now = _clock.GetUtcNow().UtcDateTime;
        var user = new StudioUser
        {
            Id = account.ActorId, Username = account.ActorId, DisplayName = account.AccountName,
            Role = projected.LegacyRole, Projects = projected.Projects, PasswordHash = string.Empty,
            CreatedAt = now, PasswordChangedAt = now,
        };
        var session = new StudioSession
        {
            Id = account.ActorId, UserId = account.ActorId, TokenHash = string.Empty, CsrfHash = string.Empty,
            CreatedAt = now, LastSeenAt = now, ExpiresAt = now, AbsoluteExpiresAt = now,
        };
        if (operation is { Tier: OperatorTier.T1 })
        {
            var granted = _store.AdmitOperatorGrant(account.AccountId, operation,
                OperatorRequestClassifier.GrantTarget(operation, target, key => RouteValue(context, path, context.GetEndpoint(), key)),
                SameProject);
            context.Items[OperatorPolicyItem] = granted;
            if (!granted.Admitted)
            {
                await Reject(context, 403, granted.Reason!, granted.Message, policy: granted);
                return;
            }
        }
        if (operation is { Tier: OperatorTier.T2 } && !await AdmitOperatorApprovalAsync(context, account, operation, path)) return;
        context.Items[HumanPrincipalItem] = new HumanPrincipal(user, session);
        context.Items[RequestAccessItem] = new RequestAccess(subject, bindings, required.Permission, SameProject, projected.Role);
        await _next(context);
    }

    /// <summary>
    /// SM-5: a T2 operator request runs only under the Owner's approval of
    /// exactly this request. Without an approval id it opens (or repeats) an
    /// approval request and is refused with <c>approval-required</c>; with one
    /// it runs only when the approval belongs to this session, matches the
    /// operation and the parameter digest, is approved, unused, and inside
    /// its 15 minutes. The store marks the approval used before the handler
    /// runs; the result is recorded after it.
    /// </summary>
    private async Task<bool> AdmitOperatorApprovalAsync(HttpContext context, ServiceAccountPrincipal account, OperatorOperation operation, string path)
    {
        var op = account.Operator!;
        var digest = (ParameterDigest)context.Items[ParameterDigestItem]!;
        var approvalId = ApprovalHeader(context);
        OperatorPolicyDecision decision;
        if (approvalId is null)
        {
            if (!digest.Complete)
            {
                decision = OperatorPolicyDecision.Refuse(OperatorApprovalReasons.Unbindable, operation,
                    "This request body is too large to bind to an approval. The Owner runs it in Studio.");
                context.Items[OperatorPolicyItem] = decision;
                await Reject(context, 403, decision.Reason!, decision.Message, policy: decision);
                return false;
            }
            (OperatorApproval Approval, bool Created) requested;
            try
            {
                requested = _store.RequestOperatorApproval(new OperatorApprovalRequestFacts(
                    account.AccountId, op.SessionId, op.Actor, op.DelegatedBy, operation, context.Request.Method.ToUpperInvariant(), path,
                    context.Request.QueryString.Value ?? "", await OperatorApprovalParameters.DisplayAsync(context.Request), digest.Value,
                    AuditTarget(context, path),
                    OperatorInstructionSources.Normalize(context.Request.Headers[OperatorApprovalHeaders.InstructionSource].ToString()),
                    StatedReason(context.Request.Headers[OperatorApprovalHeaders.Reason].ToString())));
            }
            catch (SecurityOperationException exception)
            {
                await Reject(context, exception.Status, exception.Code, exception.Message);
                return false;
            }
            decision = OperatorPolicy.TierGate(operation)! with { ApprovalId = requested.Approval.Id };
            context.Items[OperatorPolicyItem] = decision;
            if (requested.Created)
                context.RequestServices?.GetService<OperatorApprovalNotifier>()?.Enqueue(_store.ToOperatorApprovalResponse(requested.Approval));
            await Reject(context, 403, decision.Reason!,
                $"'{operation.Id}' is high risk (T2). The Owner decides approval request {requested.Approval.Id} in Studio; "
                + $"repeat this exact request with the {OperatorApprovalHeaders.Approval} header once it is approved.",
                policy: decision);
            return false;
        }
        decision = _store.RedeemOperatorApproval(approvalId, account.AccountId, op.SessionId, operation, digest.Value);
        context.Items[OperatorPolicyItem] = decision;
        if (!decision.Admitted)
        {
            await Reject(context, 403, decision.Reason!, decision.Message, policy: decision);
            return false;
        }
        context.Items[OperatorApprovalItem] = decision.ApprovalId;
        return true;
    }

    private static string? ApprovalHeader(HttpContext context)
        => context.Request.Headers[OperatorApprovalHeaders.Approval].ToString().Trim() is { Length: > 0 } id ? id : null;

    /// <summary>The agent's stated reason: percent-decoded, trimmed, at most 500 characters.</summary>
    private static string? StatedReason(string header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        string decoded;
        try { decoded = Uri.UnescapeDataString(header); }
        catch (UriFormatException) { decoded = header; }
        decoded = decoded.Trim();
        return decoded.Length <= 500 ? decoded : decoded[..500];
    }

    /// <summary>Access administration and the versioned Task Server plane: no bearer principal reaches them.</summary>
    private static bool IsTokenDeniedRoute(string path)
        => path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The canonical environment key of an environment id a project declares, for the operator catalogue.</summary>
    private string? OperatorEnvironmentKind(string project, string environmentId)
        => ResolveEnvironmentKey(ProjectRouteAddress.Addressed(project), environmentId);

    private static bool OperatorDeniedPermission(string permission)
        => permission is IamPermissions.AccessRead or IamPermissions.AccessManage or IamPermissions.WorkspaceAdminister
            or IamPermissions.SecretsResolve;

    /// <summary>
    /// What a bearer request addressed, for its audit entry: the task a
    /// single-task route names and the project(s) it resolved to, or the
    /// workspace for an installation-wide route.
    /// </summary>
    private static string? AuditTarget(HttpContext context, string path)
    {
        var target = context.Items[AuditTargetItem] as PermissionTarget;
        var projects = target?.Projects.OfType<string>().Where(project => project.Length > 0).ToArray() ?? [];
        var scope = projects.Length > 0 ? "project:" + string.Join(",", projects)
            : target?.Scope == ProjectRouteScope.WorkspaceWide ? "workspace"
            : null;
        string? task = null;
        if (path.StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase) && IsParameterSegment(context.GetEndpoint(), 2))
            task = "task:" + Uri.UnescapeDataString(path["/api/tasks/".Length..].Split('/', 2)[0]);
        return scope is null ? task : task is null ? scope : scope + "/" + task;
    }

    /// <summary>
    /// <c>sha256:</c> over the method, path, query, and body of a bearer
    /// request. The body stays readable for the handler; a body above
    /// <see cref="MaxDigestedBodyBytes"/> contributes only its length.
    /// </summary>
    /// <summary>A request's parameter digest; <see cref="Complete"/> is false when the body was too large to digest in full.</summary>
    private sealed record ParameterDigest(string Value, bool Complete);

    private static async Task<ParameterDigest> ParameterDigestAsync(HttpRequest request, string path)
    {
        var complete = true;
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"{request.Method.ToUpperInvariant()}\n{path}\n{request.QueryString.Value}\n"));
        if (request.ContentLength is > MaxDigestedBodyBytes)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"body-length:{request.ContentLength}"));
            complete = false;
        }
        else if (request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0)
        {
            request.EnableBuffering();
            var buffer = new byte[81920];
            int read;
            long total = 0;
            while ((read = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted)) > 0 && total <= MaxDigestedBodyBytes)
            {
                hash.AppendData(buffer, 0, read);
                total += read;
            }
            complete = read == 0;
            request.Body.Position = 0;
        }
        return new ParameterDigest("sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset()), complete);
    }

    private static StudioUser ProjectedUser(
        StudioUser user, ProjectedAccess projected, IamSubject subject, IReadOnlyList<RoleBinding> bindings)
        => user with
        {
            Role = projected.LegacyRole,
            Projects = projected.Projects,
            ProductionApprovalProjects = PermissionEvaluator
                .GrantingBindings(IamPermissions.EnvironmentProductionDeployApprove, subject, bindings, LegacyRoleMapping.ProductionEnvironment)
                .Select(binding => binding.Scope.Project).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };

    /// <summary>
    /// The address the permission is evaluated for. An unaddressed write
    /// needs a workspace binding (review item 1) unless the route declares
    /// that its handler checks every project it touches.
    /// </summary>
    private async Task<PermissionTarget> TargetAsync(HttpContext context, string path, RequiredPermissionMetadata required, bool safeMethod)
    {
        var endpoint = context.GetEndpoint();
        var address = path.StartsWith("/api/secrets/workspace", StringComparison.OrdinalIgnoreCase)
            ? ProjectRouteAddress.WorkspaceWide
            : await ResolveRouteAddressAsync(context, path, endpoint);
        var scope = address.Scope == ProjectRouteScope.Unaddressed && !safeMethod && !required.ProjectBindingsSuffice
            ? ProjectRouteScope.WorkspaceWide
            : address.Scope;
        var environmentId = required.EnvironmentRouteValue is { } key
            ? RouteValue(context, path, endpoint, key)
            : required.EnvironmentBodyField is { } field
                ? await BodyEnvironmentIdAsync(context.Request, field)
                : null;
        var environment = required.Environment ?? ResolveEnvironmentKey(address, environmentId);
        return new PermissionTarget(scope, address.Projects, environment, context.Request.Query["project"].FirstOrDefault());
    }

    private static async Task<string?> BodyEnvironmentIdAsync(HttpRequest request, string field)
    {
        request.EnableBuffering();
        try
        {
            using var document = await System.Text.Json.JsonDocument.ParseAsync(request.Body,
                cancellationToken: request.HttpContext.RequestAborted);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
                if (string.Equals(property.Name, field, StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        ? property.Value.GetString() : null;
            return null;
        }
        catch (System.Text.Json.JsonException exception)
        {
            SilentCatch.Note(exception, "AccessSecurityMiddleware: invalid environment request body");
            return null;
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    /// <summary>
    /// The canonical key (<see cref="IamEnvironments"/>) of the environment id
    /// a route names, read from the addressed project's declarations. An id
    /// the project does not declare resolves to no environment, so only
    /// workspace and project bindings can grant the request.
    /// </summary>
    private string? ResolveEnvironmentKey(ProjectRouteAddress address, string? environmentId)
    {
        if (_environments is null || string.IsNullOrWhiteSpace(environmentId)
            || address.Scope != ProjectRouteScope.Addressed || address.Projects is not [{ } project]
            || string.IsNullOrWhiteSpace(project))
            return null;
        try
        {
            return IamEnvironments.ForDeclaration(_environments.Declarations(project), environmentId);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or UnauthorizedAccessException
                                          or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // An unreadable declaration resolves to no environment: the
            // request then needs a workspace or project binding (fail closed).
            SilentCatch.Note(exception, "AccessSecurityMiddleware: environment declaration unreadable");
            return null;
        }
    }

    /// <summary>
    /// A route value of the matched endpoint, read from the routing result or,
    /// when routing left none, from the path segment its pattern names.
    /// </summary>
    private static string? RouteValue(HttpContext context, string path, Endpoint? endpoint, string key)
    {
        if (context.Request.RouteValues[key]?.ToString() is { Length: > 0 } routed) return routed;
        if (endpoint is not RouteEndpoint route) return null;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var patternSegments = route.RoutePattern.PathSegments;
        for (var index = 0; index < patternSegments.Count && index < segments.Length; index++)
            if (patternSegments[index].Parts is [RoutePatternParameterPart parameter]
                && string.Equals(parameter.Name, key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(segments[index]);
        return null;
    }

    /// <summary>
    /// True when the matched route's pattern has a literal segment at
    /// <paramref name="index"/>, so the path segment there is no resource id.
    /// </summary>
    private static bool IsLiteralSegment(Endpoint? endpoint, int index)
        => endpoint is RouteEndpoint route
           && route.RoutePattern.PathSegments.Count > index
           && route.RoutePattern.PathSegments[index].Parts is [RoutePatternLiteralPart];

    /// <summary>
    /// True when the matched route's pattern has a parameter segment at
    /// <paramref name="index"/>.
    /// </summary>
    private static bool IsParameterSegment(Endpoint? endpoint, int index)
        => endpoint is RouteEndpoint route
           && route.RoutePattern.PathSegments.Count > index
           && route.RoutePattern.PathSegments[index].Parts is [RoutePatternParameterPart];

    private static bool IsSafe(string method, string path)
        => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)
           || IsReadOnlyPost(method, path);

    private static bool IsSelfServiceAuth(string path)
        => path is "/api/auth/logout" or "/api/auth/change-password" or "/api/auth/session" or "/api/auth/step-up"
           || path.StartsWith("/api/auth/sessions", StringComparison.Ordinal)
           || path.StartsWith("/api/auth/totp/", StringComparison.Ordinal)
           || path.StartsWith("/api/auth/passkeys/", StringComparison.Ordinal);

    private bool SameProject(string bound, string requested)
        => ProjectAccessAuthorization.SameProject(bound, requested, _projects);

    private static string? RequiredRunnerScope(string method, string path)
    {
        if (HttpMethods.IsPost(method) && path.Equals("/api/deploy/gate-releases", StringComparison.OrdinalIgnoreCase))
            return RunnerScopes.Release;
        if ((HttpMethods.IsGet(method) && path.StartsWith("/api/runner/release-candidates/", StringComparison.OrdinalIgnoreCase))
            || (HttpMethods.IsPut(method) && path.Equals("/api/runner/release-suite-verdicts", StringComparison.OrdinalIgnoreCase)))
            return RunnerScopes.Release;
        if (HttpMethods.IsPost(method) && path.StartsWith("/api/deploy/jobs/", StringComparison.OrdinalIgnoreCase))
            return RunnerScopes.Deploy;
        if (HttpMethods.IsGet(method) && path.StartsWith("/api/deploy/jobs/", StringComparison.OrdinalIgnoreCase))
            return RunnerScopes.Deploy;
        if ((HttpMethods.IsGet(method) || HttpMethods.IsPost(method))
            && path.StartsWith("/api/deploy/run-targets/", StringComparison.OrdinalIgnoreCase))
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
                || path.EndsWith("/review-claims", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/review-package-claims", StringComparison.OrdinalIgnoreCase))) return RunnerScopes.Claim;
            if (HttpMethods.IsPost(method) && path.EndsWith("/repository-credentials", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
            if (HttpMethods.IsPost(method) && path.EndsWith("/reports", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Events;
            if (HttpMethods.IsPost(method) && path.EndsWith("/capability-failures", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Events;
            if (HttpMethods.IsPost(method) && path.EndsWith("/cli-update/result", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Events;
            if (HttpMethods.IsPost(method) && path.Contains("/provider-canary/", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
            if (HttpMethods.IsPost(method) && path.EndsWith("/quota/claim", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
            if (HttpMethods.IsPut(method) && path.EndsWith("/quota/report", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Events;
            if (HttpMethods.IsGet(method) && (path.EndsWith("/cli-update", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/provider-comparison", StringComparison.OrdinalIgnoreCase))) return RunnerScopes.Claim;
            // Device sign-in and Git setup (HostSetupEndpoints): the host claims a session and reports on it.
            var segments = path.Split('/');
            if (segments.Length >= 7 && segments[5].Equals("device-login", StringComparison.OrdinalIgnoreCase)
                && (HttpMethods.IsGet(method) || HttpMethods.IsPost(method))) return RunnerScopes.Claim;
            if (segments.Length >= 7 && segments[5].Equals("git-setups", StringComparison.OrdinalIgnoreCase)
                && HttpMethods.IsPost(method))
                return segments.Length == 7 ? RunnerScopes.Claim : RunnerScopes.Events;
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
        // AGT-3400 S4: shadow review package attempts.
        if (path.StartsWith("/api/v1/review-packages/attempts/", StringComparison.OrdinalIgnoreCase))
        {
            if (HttpMethods.IsPost(method) && path.EndsWith("/lease/renew", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Lease;
            if (HttpMethods.IsPost(method) && path.EndsWith("/report", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Completion;
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
        if (IsRunnerTaskAttachmentRead(method, path)) return RunnerScopes.Claim;
        if (HttpMethods.IsGet(method) && path.Equals("/api/system/about", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/project-chat/claim", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Claim;
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/project-chat/renew", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Lease;
        if (HttpMethods.IsPost(method) && path.Equals("/api/runner/project-chat/complete", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Completion;
        if (HttpMethods.IsPost(method) && path.StartsWith("/api/clients/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/runner-git-capability", StringComparison.OrdinalIgnoreCase)) return RunnerScopes.Events;
        if (path.StartsWith("/api/runner/post-merge/projects/", StringComparison.OrdinalIgnoreCase))
            return HttpMethods.IsGet(method) ? RunnerScopes.Claim : RunnerScopes.Completion;
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
           || path.Equals("/healthz/container", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/healthz/drain", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/healthz/orchestration-engine", StringComparison.OrdinalIgnoreCase)
           || path.Equals(AgentStudio.Readiness.ServerReadinessEndpoints.ProbePath, StringComparison.OrdinalIgnoreCase);
    private static bool IsTelemetryIntake(string method, string path)
        => HttpMethods.IsPost(method)
           && (path.Equals("/api/telemetry/otlp/v1/metrics", StringComparison.OrdinalIgnoreCase)
               || path.Equals("/api/telemetry/otlp/v1/logs", StringComparison.OrdinalIgnoreCase));
    private static bool IsOpenAuth(string path)
        => path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/auth/bootstrap", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/auth/passkeys/assertion-options", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/auth/invite/accept", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/auth/runner-enroll", StringComparison.OrdinalIgnoreCase);

    private static bool IsRunnerTaskFileRead(string method, string path)
        => HttpMethods.IsGet(method)
           && path.StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase)
           && path.Contains("/files/", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>GET /api/tasks/{key}/attachments/{name}</c>, one attachment of a card.</summary>
    private static bool IsRunnerTaskAttachmentRead(string method, string path)
    {
        if (!HttpMethods.IsGet(method) || !path.StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase)) return false;
        var segments = path["/api/tasks/".Length..].Split('/');
        return segments.Length == 3 && segments[0].Length > 0 && segments[2].Length > 0
               && segments[1].Equals("attachments", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReadOnlyPost(string method, string path)
        => HttpMethods.IsPost(method)
           && path.Equals("/api/tasks/reference-status", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The card input a host reads for its run: <c>prompt.md</c> and the
    /// card's attachments, only while the host holds the task's live lease.
    /// </summary>
    private bool RunnerOwnsTaskInput(string path, string runnerId)
    {
        if (_leases is null) return false;
        var rest = path["/api/tasks/".Length..];
        string taskKey;
        var fileMarker = rest.IndexOf("/files/", StringComparison.OrdinalIgnoreCase);
        if (fileMarker > 0)
        {
            var relativePath = Uri.UnescapeDataString(rest[(fileMarker + "/files/".Length)..]);
            if (!relativePath.Equals("prompt.md", StringComparison.OrdinalIgnoreCase)) return false;
            taskKey = Uri.UnescapeDataString(rest[..fileMarker]);
        }
        else if (IsRunnerTaskAttachmentRead(HttpMethods.Get, path))
            taskKey = Uri.UnescapeDataString(rest.Split('/')[0]);
        else
            return false;
        var lease = _leases.Peek(taskKey).Lease;
        return lease is not null && string.Equals(lease.RunnerId, runnerId, StringComparison.Ordinal);
    }

    /// <summary>The task key of a versioned card read, <c>GET /api/v1/projects/{project}/tasks/{task}</c>.</summary>
    internal static string? RunnerCardReadKey(string method, string path)
    {
        if (!HttpMethods.IsGet(method) || !path.StartsWith("/api/v1/projects/", StringComparison.OrdinalIgnoreCase))
            return null;
        var parts = path.Split('/');
        return parts.Length == 7 && parts[5].Equals("tasks", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(parts[6])
            : null;
    }

    private bool RunnerHoldsLease(string taskKey, string runnerId)
        => _leases?.Peek(taskKey).Lease is { } lease && string.Equals(lease.RunnerId, runnerId, StringComparison.Ordinal);

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

    private async Task<bool> ProjectAllowedAsync(StudioUser user, HttpContext context, string path)
    {
        if (ProjectAccessAuthorization.HasUnrestrictedProjectAccess(user)) return true;
        return ProjectScopePolicy.Allows(
            await ResolveRouteAddressAsync(context, path, context.GetEndpoint()),
            context.Request.Query["project"].FirstOrDefault(),
            project => ProjectAccessAuthorization.Allows(user, project, _projects));
    }

    /// <summary>
    /// Derives the project(s) a request addresses from its route or addressed
    /// task. <c>?project=</c> is read here only where the query is itself the
    /// task address (<c>/core</c>); everywhere else it can only narrow. The
    /// matched <paramref name="endpoint"/>'s pattern decides whether a path
    /// segment is a resource id or a literal route name; without one (an
    /// unrouted request) the path alone decides.
    /// </summary>
    private async ValueTask<ProjectRouteAddress> ResolveRouteAddressAsync(HttpContext context, string path, Endpoint? endpoint = null)
    {
        var request = context.Request;
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
            var contextKey = path.StartsWith("/api/orchestrator/context/", StringComparison.OrdinalIgnoreCase)
                ? path["/api/orchestrator/context/".Length..]
                : path.StartsWith("/api/orchestrator/sessions/", StringComparison.OrdinalIgnoreCase)
                    ? path["/api/orchestrator/sessions/".Length..]
                    : null;
            if (contextKey is null) return ProjectRouteAddress.WorkspaceWide;
            foreach (var prefix in new[] { "project:", "task:", "workbench:" })
                if (contextKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return ProjectRouteAddress.Addressed(Uri.UnescapeDataString(contextKey[prefix.Length..].Split('/', 2)[0]));
            return ProjectRouteAddress.WorkspaceWide;
        }
        // The task collection filters per task; creation checks its body
        // project against the route's permission in the handler.
        if (path.Equals("/api/tasks", StringComparison.OrdinalIgnoreCase))
            return ProjectRouteAddress.DeferredToHandler;
        // The operator decision stream filters each event by its project; the
        // watcher lease belongs to the installation (operator agent level OP-2).
        // The batch triage read (OP-4) lists only the projects the caller may
        // read; its handler checks project.read per project.
        if (path.StartsWith("/api/operator/", StringComparison.OrdinalIgnoreCase))
            return ProjectRouteAddress.DeferredToHandler;
        // Report, supervisor, bus, and runtime routes name their project in the
        // segment after the area (/api/drift/{project}/...); their literal
        // routes, such as /api/drift/actions/code-pattern-drift, stay unaddressed.
        if ((path.StartsWith("/api/analysis/", StringComparison.OrdinalIgnoreCase)
             || path.StartsWith("/api/drift/", StringComparison.OrdinalIgnoreCase)
             || path.StartsWith("/api/supervisor/", StringComparison.OrdinalIgnoreCase)
             || path.StartsWith("/api/bus/", StringComparison.OrdinalIgnoreCase)
             || path.StartsWith("/api/runtime/", StringComparison.OrdinalIgnoreCase))
            && IsParameterSegment(endpoint, 2))
            return ProjectRouteAddress.Addressed(Uri.UnescapeDataString(path.Split('/', StringSplitOptions.RemoveEmptyEntries)[2]));
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
            // A literal route under /api/tasks/ names no task: the board and
            // other collection reads (grouped, code-review/defaults,
            // commit-owner/{sha}) filter their payload per task, and the
            // orphan-folder cleanup is an installation-wide operation.
            if (IsLiteralSegment(endpoint, 2))
                return taskId.Equals("orphan-folder", StringComparison.OrdinalIgnoreCase)
                    ? ProjectRouteAddress.WorkspaceWide
                    : ProjectRouteAddress.Unaddressed;
            // Every other /api/tasks/{id}/... route is single-task addressed. A
            // scoped account may act only on a task whose project it belongs to. If
            // that project cannot be resolved (unknown id or unavailable
            // scanner), only a workspace binding can reach the handler's 404.
            if (context.Items[SkipTaskResolutionItem] is true)
                return ProjectRouteAddress.WorkspaceWide;
            var identity = Uri.UnescapeDataString(taskId);
            if (_taskIndex is null && endpoint is null)
                // Unrouted requests cannot execute an endpoint handler. Keep
                // their legacy scope check for direct middleware fixtures.
                return ProjectRouteAddress.Addressed(_scanner?.FindPublishedJob(identity)?.ProjectName);
            return ProjectRouteAddress.Addressed(ResolveTaskProjects(identity));
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
    /// Every project a single-task handler could act on, including collisions
    /// between ids and keys. A query value cannot select one permitted match
    /// while the handler resolves another project's card.
    /// </summary>
    private string?[] ResolveTaskProjects(string taskId)
    {
        // Route handlers may resolve a colliding id by a different ordering or
        // by an explicit watch path. Every matching project must authorize it.
        var matches = _taskIndex?.GetMatchingCoreFacts(taskId);
        if (matches is null) return [null];
        return matches.Count == 0 ? new string?[] { null }
            : matches.Select(task => (string?)task.ProjectName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool WorkspaceBindingsDecide(IamSubject subject, IReadOnlyList<RoleBinding> bindings)
    {
        var own = bindings.Where(binding => PermissionEvaluator.AppliesTo(binding, subject)).ToArray();
        return own.Any(binding => binding.Role == IamRoles.Owner && binding.Scope.Kind == IamScopeKind.Workspace)
            || own.Length > 0 && own.All(binding => binding.Scope.Kind == IamScopeKind.Workspace);
    }

    private async ValueTask<TaskInfo?> FindTaskAsync(HttpContext context, string taskId, string? watchPath = null)
    {
        if (_scanner is null) return null;
        if (context.Items[TaskResolutionItem] is not Dictionary<(string, string?), TaskInfo?> resolved)
            context.Items[TaskResolutionItem] = resolved = [];
        var key = (taskId, watchPath);
        if (resolved.TryGetValue(key, out var task)) return task;
        task = _taskIndex is null
            ? _scanner.FindPublishedJob(taskId, watchPath)
            : _taskIndex.GetDetailFacts(taskId, watchPath).Info;
        resolved[key] = task;
        return task;
    }

    /// <summary>
    /// A refused permission decision: <c>project-scope-denied</c> when a binding
    /// grants the permission outside the addressed scope, else <c>permission-denied</c>.
    /// </summary>
    private static Task RejectPermission(HttpContext context, PermissionDecision decision, string permission)
        => Reject(context, 403, decision.ScopeDenied ? "project-scope-denied" : "permission-denied",
            decision.Reason ?? $"'{permission}' is required.");

    /// <summary>
    /// Applies the route's declared <see cref="StudioRoleRequirement"/>, if any,
    /// to the role projected for this request, so a refused Owner route keeps
    /// its <c>owner-required</c> answer.
    /// </summary>
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

    /// <summary>
    /// 404 for a route this server does not map. The body carries the code
    /// as <c>error</c>, like every refusal here, and as <c>code</c>, the
    /// field the host's Task Server client reads.
    /// </summary>
    private static async Task RejectUnmappedRoute(HttpContext context)
    {
        const string code = AgentStudio.TaskServer.Contracts.RouteAbsenceContract.ErrorCode;
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { error = code, code, message = "This server does not map the route." });
    }

    private static async Task Reject(
        HttpContext context,
        int status,
        string code,
        string message,
        string? loginUrl = null,
        OperatorPolicyDecision? policy = null)
    {
        // A refused write never reaches the provider. Preserve the real actor in its audit.
        if (status == StatusCodes.Status403Forbidden)
        {
            if (context.Items[ServiceAccountPrincipalItem] is ServiceAccountPrincipal account)
                AgentStudio.Secrets.SecretEndpoints.AuditRefusedWrite(context, AgentStudio.Secrets.SecretActor.ServiceAccount(account.AccountId), code);
            else if (context.Items[HumanPrincipalItem] is HumanPrincipal human)
                AgentStudio.Secrets.SecretEndpoints.AuditRefusedWrite(context, AgentStudio.Secrets.SecretActor.Human(human.User.Username), code);
        }
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        if (policy is not null)
            await context.Response.WriteAsJsonAsync(new
            {
                error = code, message, operation = policy.Operation?.Id, tier = policy.Operation?.Tier, approvalId = policy.ApprovalId,
            });
        else if (loginUrl is null)
            await context.Response.WriteAsJsonAsync(new { error = code, message });
        else
            await context.Response.WriteAsJsonAsync(new { error = code, message, loginUrl });
    }
}

public static class AccessSecurityMiddlewareExtensions
{
    public static IApplicationBuilder UseAccessSecurity(this IApplicationBuilder app) => app.UseMiddleware<AccessSecurityMiddleware>();
}
