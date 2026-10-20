using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace AgentStudio.Security;

/// <summary>
/// The named permission a route requires. <see cref="Environment"/> names a
/// fixed environment; <see cref="EnvironmentRouteValue"/> names the route
/// value that carries it.
/// </summary>
public sealed record RequiredPermissionMetadata(
    string Permission,
    string? Environment = null,
    string? EnvironmentRouteValue = null,
    bool ProjectBindingsSuffice = false);

/// <summary>
/// One route's permission split by method: <see cref="Read"/> for GET, HEAD,
/// and OPTIONS, <see cref="Write"/> for every other method.
/// </summary>
public sealed record MethodPermissionMetadata(string Read, string Write);

public static class RoutePermissionEndpointExtensions
{
    /// <summary>Declares the route's permission explicitly; it wins over <see cref="RoutePermissionCatalog"/>.</summary>
    public static TBuilder RequirePermission<TBuilder>(
        this TBuilder builder, string permission, string? environment = null, string? environmentRouteValue = null)
        where TBuilder : IEndpointConventionBuilder
    {
        if (!IamPermissions.IsKnown(permission))
            throw new ArgumentException($"Unknown permission '{permission}'.", nameof(permission));
        return builder.WithMetadata(new RequiredPermissionMetadata(permission, environment, environmentRouteValue));
    }

    /// <summary>Declares a read and a write permission for a route that serves both.</summary>
    public static TBuilder RequirePermissions<TBuilder>(this TBuilder builder, string read, string write)
        where TBuilder : IEndpointConventionBuilder
    {
        if (!IamPermissions.IsKnown(read)) throw new ArgumentException($"Unknown permission '{read}'.", nameof(read));
        if (!IamPermissions.IsKnown(write)) throw new ArgumentException($"Unknown permission '{write}'.", nameof(write));
        return builder.WithMetadata(new MethodPermissionMetadata(read, write));
    }
}

/// <summary>
/// Route permission declarations for every mapped route. A route either
/// carries <see cref="RequiredPermissionMetadata"/> itself or matches one rule
/// of this table by HTTP method and route pattern; the first matching rule
/// wins. Read rules may cover an area, while write rules name their routes or
/// a narrow family, so a new mutation needs a deliberate declaration. The
/// route inventory test fails for any route that resolves to nothing.
/// <para>
/// Pattern syntax: a literal segment, <c>{}</c> for a route parameter
/// segment (literal prefixes such as <c>task:{}</c> stay), <c>*</c> for any
/// one segment, and a trailing <c>**</c> for any rest, empty included.
/// Methods: <c>R</c> reads (GET, HEAD, OPTIONS), <c>W</c> writes, <c>*</c>
/// any, or one HTTP method.
/// </para>
/// </summary>
public static class RoutePermissionCatalog
{
    private sealed record Rule(string Methods, string[] Segments, RequiredPermissionMetadata Permission);

    private static readonly IReadOnlyList<Rule> Rules = Build();
    private static readonly HashSet<string> CachedMethods = new(StringComparer.Ordinal)
        { "GET", "HEAD", "OPTIONS", "POST", "PUT", "PATCH", "DELETE" };
    private static readonly ConcurrentDictionary<(Endpoint Endpoint, string Method), RequiredPermissionMetadata?> Cache = new();

    /// <summary>
    /// The endpoint's declared permission for a method. Only the standard
    /// methods are cached, so arbitrary method tokens cannot grow the cache.
    /// </summary>
    public static RequiredPermissionMetadata? Resolve(Endpoint endpoint, string method)
    {
        var normalized = method.ToUpperInvariant();
        return CachedMethods.Contains(normalized)
            ? Cache.GetOrAdd((endpoint, normalized), key => ResolveUncached(key.Endpoint, key.Method))
            : ResolveUncached(endpoint, normalized);
    }

    /// <summary>
    /// True for the endpoint routing selects when a path matches but its
    /// method does not. It executes no handler and answers 405 itself.
    /// </summary>
    public static bool IsMethodRejection(Endpoint endpoint)
        => endpoint is not RouteEndpoint
           && string.Equals(endpoint.DisplayName, "405 HTTP Method Not Supported", StringComparison.Ordinal);

    private static RequiredPermissionMetadata? ResolveUncached(Endpoint endpoint, string method)
        => endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()
           ?? (endpoint.Metadata.GetMetadata<MethodPermissionMetadata>() is { } split
               ? new RequiredPermissionMetadata(MethodMatches("R", method) ? split.Read : split.Write)
               : null)
           ?? (endpoint is RouteEndpoint route ? Resolve(method, route.RoutePattern) : null);

    public static RequiredPermissionMetadata? Resolve(string method, RoutePattern pattern)
        => Resolve(method, Normalize(pattern));

    /// <summary>Resolves a normalized pattern such as <c>/api/tasks/{}/move</c>.</summary>
    public static RequiredPermissionMetadata? Resolve(string method, string normalizedPattern)
    {
        var segments = Split(normalizedPattern);
        return Rules.FirstOrDefault(rule => MethodMatches(rule.Methods, method) && SegmentsMatch(rule.Segments, segments))
            ?.Permission;
    }

    /// <summary>The methods a route serves; a route without method metadata serves all of them.</summary>
    public static IReadOnlyList<string> Methods(Endpoint endpoint)
        => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods
            ? methods
            : ["GET", "POST", "PUT", "PATCH", "DELETE"];

    public static string Normalize(RoutePattern pattern)
        => "/" + string.Join('/', pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternParameterPart => "{}",
            RoutePatternSeparatorPart separator => separator.Content,
            _ => "{}",
        }))));

    private static bool MethodMatches(string rule, string method) => rule switch
    {
        "*" => true,
        "R" => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method),
        "W" => !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)),
        _ => string.Equals(rule, method, StringComparison.OrdinalIgnoreCase),
    };

    private static bool SegmentsMatch(string[] rule, string[] path)
    {
        for (var index = 0; index < rule.Length; index++)
        {
            if (rule[index] == "**") return true;
            if (index >= path.Length) return false;
            if (rule[index] == "*") continue;
            if (!string.Equals(rule[index], path[index], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return rule.Length == path.Length;
    }

    private static string[] Split(string pattern) => pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static List<Rule> Build()
    {
        var rules = new List<Rule>();
        void Add(string methods, string permission, params string[] patterns)
        {
            foreach (var pattern in patterns)
                rules.Add(new Rule(methods, Split(pattern), new RequiredPermissionMetadata(permission)));
        }
        // An unaddressed write normally needs a workspace binding. These
        // handlers check every project they touch, or touch none.
        void AddProjectChecked(string methods, string permission, params string[] patterns)
        {
            foreach (var pattern in patterns)
                rules.Add(new Rule(methods, Split(pattern), new RequiredPermissionMetadata(permission, ProjectBindingsSuffice: true)));
        }
        void Under(string methods, string permission, string prefix, params string[] suffixes)
            => Add(methods, permission, [.. suffixes.Select(suffix => prefix + "/" + suffix)]);
        void Environment(string methods, string permission, string pattern, string? environment, string? routeValue = null)
            => rules.Add(new Rule(methods, Split(pattern), new RequiredPermissionMetadata(permission, environment, routeValue)));

        // --- Anonymous, own session, and access administration -------------
        Add("*", IamPermissions.Anonymous, "/healthz/**");
        Add("R", IamPermissions.Anonymous, "/api/auth/status", "/recovery");
        Add("POST", IamPermissions.Anonymous,
            "/api/auth/bootstrap", "/api/auth/login", "/api/auth/invite/accept", "/api/auth/runner-enroll");
        Add("R", IamPermissions.SelfSession, "/api/auth/session");
        Add("POST", IamPermissions.SelfSession, "/api/auth/logout", "/api/auth/change-password");
        Add("R", IamPermissions.RunnerService, "/api/auth/runner");
        Add("R", IamPermissions.AccessRead,
            "/api/auth/users/**", "/api/auth/runners/**", "/api/auth/runner-enrollments/**", "/api/auth/api-tokens/**",
            "/api/auth/iam/**");
        Add("W", IamPermissions.AccessManage,
            "/api/auth/users/**", "/api/auth/invitations", "/api/auth/runners/**", "/api/auth/runner-enrollments/**",
            "/api/auth/api-tokens/**", "/api/auth/iam/**");
        // First-run setup writes Git credentials and probes repositories for
        // the installation; the checklist is the Owner's.
        Add("*", IamPermissions.WorkspaceAdminister, "/api/first-run/**");

        // --- Runner and engine service routes (credential scopes decide) ----
        Add("*", IamPermissions.RunnerService,
            "/api/v1/orchestration/**", "/api/v1/runners/**", "/api/v1/gates/**", "/api/v1/runs/**",
            "/api/runner/artifacts/**", "/api/runner/lease/**", "/api/pull-gates/**", "/api/gate-artifacts/**");
        Add("R", IamPermissions.ProjectRead, "/api/v1/reviews/attempts/{}");
        Add("W", IamPermissions.RunnerService, "/api/v1/reviews/attempts/**");
        Add("R", IamPermissions.WorkspaceRead, "/api/v1/protocol");
        Add("POST", IamPermissions.WorkspaceRead, "/api/v1/protocol/compatibility");
        Add("POST", IamPermissions.RunnerService,
            "/api/runner/claim", "/api/runner/completion", "/api/runner/events", "/api/runner/logs",
            "/api/runner/orchestrator-decisions/**", "/api/runner/project-chat/claim", "/api/runner/project-chat/renew",
            "/api/runner/project-chat/complete", "/api/runner/epic-planning-prompt", "/api/runner/replay/events",
            "/api/clients/{}/runner-git-capability", "/api/deploy/jobs/**", "/api/deploy/gate-releases");
        // The proxy to a standalone Task Server declares its own split
        // (TaskServerPlaneProxy.MapTaskServerPlaneProxy).
        Add("*", IamPermissions.WorkspaceOperate, "/api/v1/management/**");

        // --- Environments and production approval --------------------------
        Environment("POST", IamPermissions.EnvironmentDeploy, "/api/projects/{}/environments/releases", "stage");
        Environment("POST", IamPermissions.EnvironmentProductionDeployApprove,
            "/api/projects/{}/environments/promotions/*", LegacyRoleMapping.ProductionEnvironment);
        Environment("POST", IamPermissions.EnvironmentProductionDeploy,
            "/api/projects/{}/environments/{}/rollback", null, "environmentId");
        Environment("PUT", IamPermissions.EnvironmentProductionDeployApprove, "/api/tasks/{}/release",
            LegacyRoleMapping.ProductionEnvironment);

        // --- Workspace administration, settings, and operation -------------
        Add("PUT", IamPermissions.WorkspaceAdminister, "/api/workspaces/{}/usage-calendar");
        Add("POST", IamPermissions.WorkspaceAdminister, "/api/projects/path-import");
        Add("POST", IamPermissions.WorkspaceSettingsWrite, "/api/projects");
        Add("W", IamPermissions.WorkspaceSettingsWrite,
            "/api/workspaces/**", "/api/watch-paths/**", "/api/tags/**", "/api/admin/**",
            "/api/cli/quota/caps", "/api/cli/quota/fallback-preference", "/api/cli/quota/model-routes",
            "/api/cli/quota/wait-policy", "/api/cli/maintenance-model", "/api/cli/model-routing/**",
            "/api/clients/{}/defaults", "/api/clients/{}/runner-capacity", "/api/git/branch-sweep/settings",
            "/api/orchestration/rollout/**");
        Add("POST", IamPermissions.WorkspaceRead, "/api/token-pricing/calculate", "/api/component-routing/resolve");
        Add("W", IamPermissions.WorkspaceOperate,
            "/api/cli/quota/refresh/**", "/api/cli/{}/session", "/api/cli/{}/working-memory", "/api/clients/**",
            "/api/crash-recovery/**", "/api/git/branch-sweep/**", "/api/git/cleanup/**", "/api/git/branch-reclaim/**",
            "/api/maintenance/**", "/api/attempts/**", "/api/cause-breakers/**", "/api/watcher/**",
            "/api/orchestration/decision-receipts/**", "/api/devtools/**", "/api/_internal/**", "/api/tasks/orphan-folder");
        Add("R", IamPermissions.WorkspaceRead,
            "/api/system/**", "/api/environment/**", "/api/cli/**", "/api/clients/**", "/api/watch-paths/**",
            "/api/workspaces/**", "/api/workspace/**", "/api/tags/**", "/api/search/**", "/api/usage/**",
            "/api/token-pricing/**", "/api/admin/**", "/api/agent-rules", "/api/adhoc-usage/**", "/api/diagnostics/**",
            "/api/devtools/**", "/api/filesystem-layer/**", "/api/orchestration/rollout/**", "/api/cause-breakers/**",
            "/api/gate-economics", "/api/watcher/**", "/api/crash-recovery/**", "/api/git/branch-sweep/**",
            "/api/git/cleanup/**", "/api/auto-review/**", "/api/runner/status", "/api/runner/orchestrator-feed",
            "/api/runner/token-summary-aggregate/**", "/api/runner/queue-starvation", "/api/runner/auto-review-queue",
            "/api/runner/auto-review-parallelism-recommendation", "/api/runner/coding-yield-recommendation",
            "/api/runner/project-chat/**", "/api/runner/global/**", "/api/attempts/health", "/api/pipeline/**",
            "/api/parked-cards", "/api/pickup-holds", "/api/concept-docs/**");
        Add("*", IamPermissions.WorkspaceRead, "/hubs/**");

        // --- Project chat --------------------------------------------------
        Add("W", IamPermissions.ProjectChatWrite,
            "/api/runner/{}/orchestrator-chat/**", "/api/runner/project:{}/orchestrator-chat",
            "/api/runner/task:{}/{}/orchestrator-chat", "/api/runner/workbench:{}/{}/orchestrator-chat",
            "/api/orchestrator/**");

        // --- Cards ---------------------------------------------------------
        Add("POST", IamPermissions.ProjectRead, "/api/tasks/reference-status");
        // Creation, reorder, and batch-move check every affected project in
        // the handler; title and prompt helpers touch no project.
        AddProjectChecked("POST", IamPermissions.ProjectCardsWrite, "/api/tasks", "/api/tasks/reorder", "/api/tasks/batch-move",
            "/api/title/generate", "/api/prompt/enhance");
        Add("POST", IamPermissions.ProjectCardsWrite, "/api/epics/{}/sub-tasks");
        Add("DELETE", IamPermissions.ProjectCardsWrite, "/api/tasks/{}");
        Under("W", IamPermissions.ProjectCardsWrite, "/api/tasks/{}",
            "title", "tags", "references", "waits-on", "files/{}", "state", "epic", "dossier", "task-type", "model",
            "cli-type", "thinking-level", "requires-integration", "move", "move-to-top", "attachments",
            "preview-comments", "change-project", "intake", "review-priority", "decision", "decision/migrate",
            "decision/reopen", "promote-concept", "concept-dossier", "planning-closure");
        Under("W", IamPermissions.ProjectCardsWrite, "/api/projects/{}", "auto-tag/backfill", "auto-tag/backfill-jobs", "tag-maintenance/**");

        // --- Runs, pipeline, integration, and project actions --------------
        Under("W", IamPermissions.ProjectRunsOperate, "/api/tasks/{}",
            "start", "stop", "continue", "failure/continue", "re-evaluate", "restore-from-failed-pickup",
            "pipeline/**", "integration/**", "integration-records", "commits/integration", "git/**", "code-review",
            "external-completion", "summary/**", "context-usage/refresh", "conversation/invalidate",
            "older-brief-delivery/decision", "open-in-vscode", "review-evidence/**", "preview-feedback",
            "merge", "merge/**");
        Under("W", IamPermissions.ProjectRunsOperate, "/api/runner/{}",
            "start", "stop", "mode", "orchestrator-log/override");
        Add("W", IamPermissions.ProjectRunsOperate,
            "/api/runner/integration-lease/**", "/api/analysis/**", "/api/drift/**", "/api/supervisor/**");
        Under("W", IamPermissions.ProjectRunsOperate, "/api/projects/{}",
            "batch-gate/close-now", "batch-gate/members/**", "queue-health/**", "operator-sweeps/**",
            "pipeline-steps/**", "completed-lane/**", "delivery-claims/**", "external-verifications", "test-runs/**",
            "publish/package", "publish/website", "model-migrations/**", "security/audit", "skill-readiness/**",
            "proposals/**", "proposals", "design/**", "graph/**", "wiki/grading/**", "visual-evidence/**",
            "execution/proposal", "checkout/**", "gate-result-cache", "urls/{}/start", "urls/test", "urls/{}/process");

        // --- Documents -----------------------------------------------------
        Under("W", IamPermissions.ProjectDocsWrite, "/api/projects/{}",
            "wiki/**", "workbenches/**", "security/files/**", "security/meta");

        // --- Project settings ----------------------------------------------
        Add("W", IamPermissions.ProjectSettingsWrite, "/api/projects/{}");
        Add("PUT", IamPermissions.ProjectSettingsWrite, "/api/projects/{}/*");
        Under("W", IamPermissions.ProjectSettingsWrite, "/api/projects/{}",
            "areas/{}/glossary", "build-profile", "build-profile/validate", "execution/override", "test-execution",
            "publish/automation", "urls", "urls/**", "ownership-mappings/**");

        // --- Project reads -------------------------------------------------
        Add("R", IamPermissions.ProjectRead,
            "/api/projects/**", "/api/tasks/**", "/api/runner/{}/**", "/api/runner/project:{}/**",
            "/api/runner/task:{}/**", "/api/runner/workbench:{}/**", "/api/runner/integration-lease/**",
            "/api/orchestrator/**", "/api/analysis/**", "/api/drift/**", "/api/bus/**", "/api/runtime/**",
            "/api/supervisor/**", "/api/git/**", "/api/attempts/**", "/api/audits/**", "/api/epics/**",
            "/api/workbenches/**", "/api/orchestration/**");
        Add("POST", IamPermissions.ProjectRead, "/api/projects/{}/deployment/compile");
        return rules;
    }
}
