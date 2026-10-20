using System.Diagnostics.CodeAnalysis;
using AgentStudio.Registry;

namespace AgentStudio.Secrets;

/// <summary>
/// Secret routes. People manage secrets per scope and see metadata only:
/// <c>/api/secrets/workspace</c>, <c>/api/projects/{project}/secrets</c> and
/// <c>/api/projects/{project}/environments/{environment}/secrets</c>. No human
/// route returns a value. Workloads resolve through
/// <c>POST /api/runner/secrets/resolve</c> with a service credential bound to
/// a live deploy-job or run lease.
/// </summary>
public static class SecretEndpoints
{
    public const string ResolvePath = "/api/runner/secrets/resolve";
    private const string LocalOperator = "local-operator";

    public static void MapSecretEndpoints(this WebApplication app)
    {
        app.MapGet("/api/secrets", (HttpContext context, ISecretProvider secrets, ISecretStore store,
            IKeyEncryptionKeySource keys, AccessSecurityStore security, IConfiguration configuration, ProjectRegistry registry,
            CancellationToken ct) => Run(async () =>
        {
            var caller = Caller.From(context, security, configuration, registry);
            var listing = await secrets.ListAsync(null, ct);
            var visible = listing.Records
                .Where(record => caller.CanSee(record.ParsedScope))
                .Select(SecretMetadataResponse.From).ToList();
            // An item without a readable scope claim counts as workspace-level for visibility.
            var unreadable = listing.Unreadable
                .Where(item => caller.CanSee(SecretScope.TryParse(item.Scope, out var claimed) ? claimed : SecretScope.Workspace))
                .Select(UnreadableSecretResponse.From).ToList();
            return Results.Ok(new SecretOverviewResponse(store.StoreId, keys.Describe(),
                caller.CanWrite(SecretScope.Workspace).Allowed, visible, unreadable));
        }));

        app.MapGet("/api/secrets/audit", (string? scope, int? limit, HttpContext context, ISecretProvider secrets,
            AccessSecurityStore security, IConfiguration configuration, ProjectRegistry registry, CancellationToken ct) => Run(async () =>
        {
            var caller = Caller.From(context, security, configuration, registry);
            var filter = string.IsNullOrWhiteSpace(scope) ? null : SecretScope.Parse(scope);
            if (filter is not null && !caller.CanSee(filter))
                throw new SecretAccessDeniedException("project-scope-denied", "This account is not a member of the project.");
            var entries = (await secrets.ReadAuditAsync(filter, limit ?? 200, ct))
                .Where(entry => SecretScope.TryParse(entry.Scope, out var entryScope) && caller.CanSee(entryScope))
                .ToList();
            return Results.Ok(new SecretAuditResponse(entries));
        }));

        MapScope(app.MapGroup("/api/secrets/workspace"), _ => SecretScope.Workspace);
        MapScope(app.MapGroup("/api/projects/{project}/secrets"),
            route => SecretScope.ForProject(Route(route, "project")));
        MapScope(app.MapGroup("/api/projects/{project}/environments/{environment}/secrets"),
            route => SecretScope.ForEnvironment(Route(route, "project"), Route(route, "environment")));

        app.MapPost(ResolvePath, (ResolveSecretRequest request, HttpContext context, AccessSecurityStore security,
            ISecretProvider secrets, ISecretWorkloadLeases leases, SecretAuditLog audit, TimeProvider clock,
            IConfiguration configuration, CancellationToken ct) => Run(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (SecurityProfiles.IsPublicDemo(configuration))
                throw new SecretAccessDeniedException("public-demo-denied", "Secrets are not available in the public demo.");
            // Fail closed in every profile: only an enrolled service credential
            // may receive a value, never a browser session.
            var runner = context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] as RunnerPrincipal
                         ?? security.AuthenticateRunner(ReadBearer(context));
            if (runner is null)
                return Results.Json(new { error = "runner-authentication-required", message = "A Runner service credential is required." },
                    statusCode: StatusCodes.Status401Unauthorized);
            var reference = SecretReference.Parse(request.Reference);
            var (workload, decision) = SecretWorkloadBinding.Bind(runner, request.Workload, leases);
            if (workload is null)
            {
                // A refused lease binding never reaches the provider; audit it here.
                audit.Append(new SecretAuditEntry
                {
                    At = clock.GetUtcNow().UtcDateTime,
                    Action = SecretAuditActions.Resolve,
                    Actor = SecretActor.Workload(runner.RunnerId).ToString(),
                    Scope = reference.Scope.Key,
                    Reference = reference.Unpinned().ToString(),
                    Version = reference.Version,
                    Purpose = SecretAccessPolicy.IsValidPurpose(request.Purpose) ? request.Purpose : null,
                    Workload = request.Workload?.Kind,
                    Outcome = "denied:" + decision.Code
                });
                throw new SecretAccessDeniedException(decision.Code, decision.Reason);
            }
            var resolved = await secrets.GetForWorkloadAsync(reference, workload, request.Purpose ?? string.Empty, ct);
            return Results.Ok(new ResolveSecretResponse(resolved.Requested.ToString(), resolved.Resolved.ToString(),
                resolved.Resolved.Version!.Value, resolved.Value));
        }));
    }

    private static void MapScope(RouteGroupBuilder group, Func<RouteValueDictionary, SecretScope> scopeOf)
    {
        group.MapGet("", (HttpContext context, ISecretProvider secrets, AccessSecurityStore security, IConfiguration configuration,
            ProjectRegistry registry, CancellationToken ct) => Run(async () =>
        {
            var scope = scopeOf(context.Request.RouteValues);
            Caller.From(context, security, configuration, registry).RequireSee(scope);
            return Results.Ok((await secrets.ListAsync(scope, ct)).Records.Select(SecretMetadataResponse.From).ToList());
        }));

        group.MapGet("/{name}", (string name, HttpContext context, ISecretProvider secrets, AccessSecurityStore security,
            IConfiguration configuration, ProjectRegistry registry, CancellationToken ct) => Run(async () =>
        {
            var scope = scopeOf(context.Request.RouteValues);
            Caller.From(context, security, configuration, registry).RequireSee(scope);
            var record = (await secrets.ListAsync(scope, ct)).Records.FirstOrDefault(item => item.Name == name)
                ?? throw new SecretNotFoundException(new SecretReference(scope, SecretReference.ValidateName(name)).ToString());
            return Results.Ok(SecretMetadataResponse.From(record));
        }));

        group.MapPost("", (PutSecretRequest request, HttpContext context, ISecretProvider secrets, AccessSecurityStore security,
            IConfiguration configuration, ProjectRegistry registry, CancellationToken ct) => Run(async () =>
        {
            var scope = scopeOf(context.Request.RouteValues);
            var actor = Caller.From(context, security, configuration, registry).RequireWrite(scope, context, request.Name);
            var record = await secrets.PutAsync(scope, request.Name ?? string.Empty, request.Value ?? string.Empty, actor, ct);
            var response = SecretMetadataResponse.From(record);
            return Results.Created(context.Request.Path + "/" + Uri.EscapeDataString(record.Name), response);
        }));

        group.MapPost("/{name}/rotate", (string name, RotateSecretRequest request, HttpContext context, ISecretProvider secrets,
            AccessSecurityStore security, IConfiguration configuration, ProjectRegistry registry, CancellationToken ct) => Run(async () =>
        {
            var scope = scopeOf(context.Request.RouteValues);
            var actor = Caller.From(context, security, configuration, registry).RequireWrite(scope, context);
            return Results.Ok(SecretMetadataResponse.From(await secrets.RotateAsync(scope, name, request.Value ?? string.Empty, actor, ct)));
        }));

        group.MapPost("/{name}/revoke", (string name, RevokeSecretRequest? request, HttpContext context, ISecretProvider secrets,
            AccessSecurityStore security, IConfiguration configuration, ProjectRegistry registry, CancellationToken ct) => Run(async () =>
        {
            var scope = scopeOf(context.Request.RouteValues);
            var actor = Caller.From(context, security, configuration, registry).RequireWrite(scope, context, version: request?.Version);
            return Results.Ok(SecretMetadataResponse.From(await secrets.RevokeAsync(scope, name, request?.Version, actor, ct)));
        }));

        group.MapDelete("/{name}", (string name, HttpContext context, ISecretProvider secrets, AccessSecurityStore security,
            IConfiguration configuration, ProjectRegistry registry, CancellationToken ct) => Run(async () =>
        {
            var scope = scopeOf(context.Request.RouteValues);
            var actor = Caller.From(context, security, configuration, registry).RequireWrite(scope, context);
            await secrets.DeleteAsync(scope, name, actor, ct);
            return Results.NoContent();
        }));
    }

    /// <summary>
    /// Audits a write on a secret route that was refused before the provider
    /// ran: by the access middleware (role, CSRF, project membership) or by the
    /// scope rights check here. The middleware does not read the body, so a
    /// refused put names its scope only; a malformed name is never echoed.
    /// Requests that are not secret writes are ignored.
    /// </summary>
    public static void AuditRefusedWrite(HttpContext context, SecretActor actor, string code, string? name = null, int? version = null)
    {
        if (!TryParseWriteRoute(context.Request.Method, context.Request.Path.Value, out var scope, out var action, out var routeName)) return;
        if (context.RequestServices?.GetService<SecretAuditLog>() is not { } audit) return;
        var clock = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        name = routeName ?? name;
        audit.AppendRefusal(new SecretAuditEntry
        {
            At = clock.GetUtcNow().UtcDateTime,
            Action = action,
            Actor = actor.ToString(),
            Scope = scope.Key,
            Reference = SecretReference.IsValidName(name) ? new SecretReference(scope, name!).ToString() : SecretReference.Prefix + scope.Key + "/",
            Version = version,
            Outcome = "denied:" + code
        });
    }

    /// <summary>Maps a mutating secret route to its scope, action and route name (null for a put).</summary>
    internal static bool TryParseWriteRoute(string method, string? path, [NotNullWhen(true)] out SecretScope? scope,
        [NotNullWhen(true)] out string? action, out string? name)
    {
        (scope, action, name) = (null, null, null);
        var segments = (path ?? string.Empty).Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        int rest;
        if (segments is ["api", "secrets", "workspace", ..])
            (scope, rest) = (SecretScope.Workspace, 3);
        else if (segments is ["api", "projects", var project, "secrets", ..]
                 && SecretScope.TryParse(project, out var projectScope) && projectScope.Kind == SecretScopeKind.Project)
            (scope, rest) = (projectScope, 4);
        else if (segments is ["api", "projects", var owner, "environments", var environment, "secrets", ..]
                 && SecretScope.TryParse(owner + "/" + environment, out var environmentScope))
            (scope, rest) = (environmentScope, 6);
        else return false;

        action = (method.ToUpperInvariant(), segments[rest..]) switch
        {
            ("POST", []) => SecretAuditActions.Put,
            ("POST", [_, "rotate"]) => SecretAuditActions.Rotate,
            ("POST", [_, "revoke"]) => SecretAuditActions.Revoke,
            ("DELETE", [_]) => SecretAuditActions.Delete,
            _ => null
        };
        if (action is null) return false;
        name = action == SecretAuditActions.Put ? null : segments[rest];
        return true;
    }

    private static async Task<IResult> Run(Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (SecretNotFoundException ex) { return Error(StatusCodes.Status404NotFound, ex.Code, ex.Message); }
        catch (SecretRevokedException ex) { return Error(StatusCodes.Status409Conflict, ex.Code, ex.Message); }
        catch (SecretAccessDeniedException ex) { return Error(StatusCodes.Status403Forbidden, ex.Code, ex.Message); }
        catch (CallerUnauthenticatedException ex) { return Error(StatusCodes.Status401Unauthorized, "authentication-required", ex.Message); }
        catch (SecretValidationException ex) { return Error(StatusCodes.Status400BadRequest, ex.Code, ex.Message); }
        catch (SecretKeyUnavailableException ex) { return Error(StatusCodes.Status503ServiceUnavailable, "secret-key-unavailable", ex.Message); }
        catch (SecretIntegrityException) { return Error(StatusCodes.Status500InternalServerError, "secret-integrity-failed", "A stored secret failed its integrity check."); }
    }

    private static IResult Error(int status, string code, string message)
        => Results.Json(new { error = code, message }, statusCode: status);

    private static string Route(RouteValueDictionary values, string key)
        => values.TryGetValue(key, out var value) && value is string text ? Uri.UnescapeDataString(text) : string.Empty;

    private static string? ReadBearer(HttpContext context)
        => context.Request.Headers.Authorization.FirstOrDefault() is { } header
           && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header[7..].Trim()
            : null;

    /// <summary>The person behind a management request and their rights.</summary>
    private sealed class Caller
    {
        private readonly StudioUser? _user;
        private readonly ProjectRegistry _registry;

        private Caller(SecretActor actor, StudioUser? user, ProjectRegistry registry)
        {
            Actor = actor;
            _user = user;
            _registry = registry;
        }

        public SecretActor Actor { get; }
        private string Role => _user?.Role ?? StudioRoles.Owner;
        private bool Unrestricted => _user is null || ProjectAccessAuthorization.HasUnrestrictedProjectAccess(_user);

        public static Caller From(HttpContext context, AccessSecurityStore security, IConfiguration configuration, ProjectRegistry registry)
        {
            if (SecurityProfiles.IsPublicDemo(configuration))
                throw new SecretAccessDeniedException("public-demo-denied", "Secrets are not available in the public demo.");
            var human = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal
                        ?? security.AuthenticateSession(
                            context.Request.Cookies[AccessSecurityStore.SessionCookieName]
                            ?? context.Request.Cookies[AccessSecurityStore.InsecureSessionCookieName], touch: false);
            if (human is not null) return new Caller(SecretActor.Human(human.User.Username), human.User, registry);
            if (SecurityProfiles.IsLocal(configuration)) return new Caller(SecretActor.Human(LocalOperator), null, registry);
            throw new CallerUnauthenticatedException();
        }

        public bool CanSee(SecretScope scope)
            => SecretManagementPolicy.CanSeeMetadata(scope, Unrestricted, Member);

        public void RequireSee(SecretScope scope)
        {
            if (!CanSee(scope))
                throw new SecretAccessDeniedException("project-scope-denied", "This account is not a member of the project.");
        }

        public SecretAccessDecision CanWrite(SecretScope scope)
            => SecretManagementPolicy.CanWrite(Role, Unrestricted, scope, scope.Project is not null && Member(scope.Project));

        /// <summary>A refused write never reaches the provider, so it is audited here.</summary>
        public SecretActor RequireWrite(SecretScope scope, HttpContext context, string? name = null, int? version = null)
        {
            var decision = CanWrite(scope);
            if (decision.Allowed) return Actor;
            AuditRefusedWrite(context, Actor, decision.Code, name, version);
            throw new SecretAccessDeniedException(decision.Code, decision.Reason);
        }

        private bool Member(string project)
            => _user is null || ProjectAccessAuthorization.Allows(_user, project, _registry);
    }

    private sealed class CallerUnauthenticatedException() : Exception("A signed-in Studio session is required.");
}
