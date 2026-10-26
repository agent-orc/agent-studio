using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace AgentStudio.Security;

/// <summary>
/// The named permission a route requires. <see cref="Environment"/> names a
/// fixed canonical environment key (<see cref="IamEnvironments"/>);
/// <see cref="EnvironmentRouteValue"/> names the route value that carries an
/// environment id, which resolves to the key of its declaration.
/// <see cref="EnvironmentBodyField"/> names a JSON request field carrying an
/// environment id when the route does not contain it.
/// </summary>
public sealed record RequiredPermissionMetadata(
    string Permission,
    string? Environment = null,
    string? EnvironmentRouteValue = null,
    bool ProjectBindingsSuffice = false,
    string? EnvironmentBodyField = null);

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
    // Endpoints can retain their request delegates and the application service
    // provider. A static dictionary with Endpoint keys retains every disposed
    // test host for the lifetime of the process.
    private sealed record CachedResolution(RequiredPermissionMetadata? Permission);
    private static readonly ConditionalWeakTable<Endpoint, ConcurrentDictionary<string, CachedResolution>> Cache = new();

    /// <summary>
    /// The endpoint's declared permission for a method. Only the standard
    /// methods are cached, so arbitrary method tokens cannot grow the cache.
    /// </summary>
    public static RequiredPermissionMetadata? Resolve(Endpoint endpoint, string method)
    {
        var normalized = method.ToUpperInvariant();
        return CachedMethods.Contains(normalized)
            ? Cache.GetValue(endpoint, static _ => new ConcurrentDictionary<string, CachedResolution>(StringComparer.Ordinal))
                .GetOrAdd(normalized, key => new CachedResolution(ResolveUncached(endpoint, key))).Permission
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
        void BodyEnvironment(string methods, string permission, string pattern, string field)
            => rules.Add(new Rule(methods, Split(pattern), new RequiredPermissionMetadata(permission, EnvironmentBodyField: field)));

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
        Add("R", IamPermissions.RunnerService, "/api/quota/admission/{}");
        Add("R", IamPermissions.RunnerService, "/api/deploy/run-targets/{}/desired-state");
        Add("POST", IamPermissions.RunnerService, "/api/deploy/run-targets/{}/observed-state");
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
            "/api/runner/project-chat/complete", "/api/runner/replay/events",
            "/api/clients/{}/runner-git-capability", "/api/deploy/jobs/**", "/api/deploy/gate-releases");
        Add("GET", IamPermissions.RunnerService, "/api/deploy/jobs/{}/{}/artifact");
        // The proxy to a standalone Task Server declares its own split
        // (TaskServerPlaneProxy.MapTaskServerPlaneProxy).
        Add("*", IamPermissions.WorkspaceOperate, "/api/v1/management/**");

        // --- Environments and production approval --------------------------
        // Environment keys are canonical kinds (IamEnvironments): a release
        // goes to the stage environment, a promotion to production, and a
        // rollback names an environment id the middleware resolves to the
        // kind of its declaration.
        Environment("POST", IamPermissions.EnvironmentDeploy, "/api/projects/{}/environments/releases", IamEnvironments.Stage);
        Environment("POST", IamPermissions.EnvironmentProductionDeployApprove,
            "/api/projects/{}/environments/promotions/*", IamEnvironments.Production);
        Environment("POST", IamPermissions.EnvironmentProductionDeploy,
            "/api/projects/{}/environments/{}/rollback", null, "environmentId");
        Environment("PUT", IamPermissions.EnvironmentProductionDeployApprove, "/api/tasks/{}/release",
            IamEnvironments.Production);
        Add("R", IamPermissions.WorkspaceRead, "/api/deploy/matrix", "/api/deploy/cards");
        Environment("POST", IamPermissions.EnvironmentProductionDeploy,
            "/api/projects/{}/environments/{}/stop", null, "environmentId");
        Environment("POST", IamPermissions.EnvironmentProductionDeploy,
            "/api/projects/{}/environments/{}/run-target-removal/confirm", null, "environmentId");
        Add("R", IamPermissions.ProjectRead,
            "/api/projects/{}/deploy/environments", "/api/projects/{}/deploy/status",
            "/api/projects/{}/deploy/releases", "/api/projects/{}/deploy/releases/page",
            "/api/projects/{}/deploy/releases/{}",
            "/api/projects/{}/deploy/deployments", "/api/projects/{}/deploy/deployments/page",
            "/api/projects/{}/deploy/deployments/{}",
            "/api/projects/{}/deploy/deployments/{}/log", "/api/projects/{}/deploy/audit",
            "/api/projects/{}/deploy/runbooks", "/api/projects/{}/deploy/runbooks/runs",
            "/api/projects/{}/deploy/runbooks/runs/{}", "/api/projects/{}/deploy/runbooks/runs/{}/log");
        Environment("POST", IamPermissions.EnvironmentDeploy,
            "/api/projects/{}/deploy/environments/{}/runbooks/{}/runs", null, "environmentId");
        Environment("POST", IamPermissions.EnvironmentProductionDeployApprove,
            "/api/projects/{}/deploy/runbooks/runs/{}/approve", IamEnvironments.Production);
        Environment("POST", IamPermissions.EnvironmentProductionDeployApprove,
            "/api/projects/{}/deploy/runbooks/runs/{}/reject", IamEnvironments.Production);
        BodyEnvironment("POST", IamPermissions.EnvironmentDeploy,
            "/api/projects/{}/deploy/releases/{}/deployments", "environmentId");
        Environment("POST", IamPermissions.EnvironmentProductionDeployApprove,
            "/api/projects/{}/deploy/deployments/{}/approve", IamEnvironments.Production);
        Environment("POST", IamPermissions.EnvironmentProductionDeployApprove,
            "/api/projects/{}/deploy/deployments/{}/reject", IamEnvironments.Production);
        Environment("POST", IamPermissions.EnvironmentProductionDeploy,
            "/api/projects/{}/deploy/deployments/{}/intervention", IamEnvironments.Production);
        Add("POST", IamPermissions.EnvironmentProductionDeploy,
            "/api/projects/{}/deploy/deployments/{}/cancel");
        Environment("POST", IamPermissions.EnvironmentProductionDeploy,
            "/api/projects/{}/deploy/environments/{}/redeploy", null, "environmentId");
        Environment("POST", IamPermissions.EnvironmentProductionDeploy,
            "/api/projects/{}/deploy/environments/{}/rollback", null, "environmentId");
        Environment("R", IamPermissions.ProjectRead,
            "/api/projects/{}/environments/{}/volumes/**", null, "environmentId");
        Environment("W", IamPermissions.EnvironmentProductionDeploy,
            "/api/projects/{}/environments/{}/volumes/**", null, "environmentId");

        // OTLP intake checks its own environment token. Studio reads require
        // project access; issuing or revoking an ingest token changes project
        // configuration and is reserved to a project administrator.
        Add("POST", IamPermissions.Anonymous,
            "/api/telemetry/otlp/v1/metrics", "/api/telemetry/otlp/v1/logs");
        Add("R", IamPermissions.ProjectRead, "/api/projects/{}/environments/{}/telemetry/**");
        Add("W", IamPermissions.ProjectSettingsWrite,
            "/api/projects/{}/environments/{}/telemetry/tokens",
            "/api/projects/{}/environments/{}/telemetry/tokens/{}");

        // --- Secrets: write without read; no human route returns a value ----
        // Only a Runner credential bound to a live lease resolves a value.
        Add("POST", IamPermissions.RunnerService, "/api/runner/secrets/resolve");
        Add("PUT", IamPermissions.WorkspaceAdminister, "/api/secrets/backends");
        Environment("R", IamPermissions.SecretsRead,
            "/api/projects/{}/secret-bindings/{}", null, "environment");
        Environment("PUT", IamPermissions.SecretsWrite,
            "/api/projects/{}/secret-bindings/{}/{}", null, "environment");
        Add("R", IamPermissions.SecretsRead,
            "/api/secrets", "/api/secrets/audit", "/api/secrets/workspace/**", "/api/projects/{}/secrets/**");
        Add("W", IamPermissions.SecretsWrite, "/api/secrets/workspace/**", "/api/projects/{}/secrets/**");
        Environment("R", IamPermissions.SecretsRead, "/api/projects/{}/environments/{}/secrets/**", null, "environment");
        Environment("W", IamPermissions.SecretsWrite, "/api/projects/{}/environments/{}/secrets/**", null, "environment");

        // Receipt handlers filter reads and check the receipt's project before
        // recording an intent. A scoped operator can therefore use its own
        // receipts without an installation-wide operation binding.
        Add("R", IamPermissions.ProjectRead, "/api/orchestration/decision-receipts/**");
        AddProjectChecked("POST", IamPermissions.ProjectRunsOperate,
            "/api/orchestration/decision-receipts/{}/operator-intent");

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
        // These reads accept host paths or authority ids rather than a project
        // address. They must not inherit project-scoped workspace.read.
        Add("R", IamPermissions.WorkspaceAdminister, "/api/filesystem-layer/**");
        Add("R", IamPermissions.WorkspaceOperate,
            "/api/attempts/tasks/{}", "/api/attempts/runs/{}", "/api/attempts/reviews/{}");
        Add("R", IamPermissions.WorkspaceOperate,
            "/api/pipeline/acceptance-rail", "/api/pipeline/integration-bounce/metrics");
        // Static CLI catalogues carry no host state. Session transcripts,
        // working memory, quota and model probes are installation-wide.
        Add("R", IamPermissions.WorkspaceRead,
            "/api/cli/types", "/api/cli/contracts", "/api/cli/model-migrations");
        // These return configuration or cached data without reading sessions.
        Add("R", IamPermissions.WorkspaceRead,
            "/api/cli/{}/models", "/api/cli/maintenance-model", "/api/cli/{}/effective-mode",
            "/api/cli/quota", "/api/cli/quota/caps", "/api/cli/quota/wait-policy",
            "/api/cli/quota/model-routes");
        Add("R", IamPermissions.WorkspaceOperate, "/api/cli/**");
        // These collections expose installation-wide prompts, runner identity,
        // recovery and operational records without per-project filtering.
        Add("R", IamPermissions.WorkspaceOperate,
            "/api/admin/prompts/**", "/api/admin/config/**", "/api/admin/git-telemetry/**", "/api/clients/**",
            "/api/watcher/**", "/api/crash-recovery/**", "/api/cause-breakers/**",
            "/api/workspace/summary", "/api/workspaces/{}/quota/**",
            "/api/workspaces/{}/projects/{}/quota", "/api/auto-review/status",
            "/api/system/execution-infrastructure", "/api/agent-rules", "/api/adhoc-usage/**");
        Add("W", IamPermissions.WorkspaceOperate,
            "/api/cli/quota/refresh/**", "/api/cli/{}/session", "/api/cli/{}/working-memory", "/api/clients/**",
            "/api/crash-recovery/**", "/api/git/branch-sweep/**", "/api/git/cleanup/**", "/api/git/branch-reclaim/**",
            "/api/maintenance/**", "/api/attempts/**", "/api/cause-breakers/**", "/api/watcher/**",
            "/api/devtools/**", "/api/_internal/**", "/api/tasks/orphan-folder");
        Add("R", IamPermissions.WorkspaceRead,
            "/api/system/**", "/api/environment/**", "/api/cli/**", "/api/clients/**", "/api/watch-paths/**",
            "/api/workspaces/**", "/api/workspace/**", "/api/tags/**", "/api/search/**", "/api/usage/**",
            "/api/token-pricing/**", "/api/admin/**", "/api/agent-rules", "/api/adhoc-usage/**", "/api/diagnostics/**",
            "/api/devtools/**", "/api/orchestration/rollout/**", "/api/cause-breakers/**",
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
        Add("DELETE", IamPermissions.ProjectCardsWrite, "/api/tasks/{}");
        Under("W", IamPermissions.ProjectCardsWrite, "/api/tasks/{}",
            "title", "tags", "references", "waits-on", "files/{}", "state", "dossier", "task-type", "model",
            "cli-type", "thinking-level", "requires-integration", "move", "move-to-top", "attachments",
            "preview-comments", "change-project", "intake", "review-priority", "decision", "decision/migrate",
            "decision/reopen", "promote-concept", "concept-dossier", "planning-closure");
        Under("POST", IamPermissions.ProjectRunsOperate, "/api/tasks/{}",
            "review-observations", "review-observations/{}/later-evidence", "review-observations/{}/finding-outcomes");
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
        Add("POST", IamPermissions.WorkspaceOperate, "/api/projects/{}/operator-candidate-verifications");
        Add("POST", IamPermissions.ProjectRunsOperate, "/api/projects/{}/batch-gate/queue-stalls/release");

        // --- Documents -----------------------------------------------------
        Under("W", IamPermissions.ProjectDocsWrite, "/api/projects/{}",
            "wiki/**", "workbenches/**", "security/files/**", "security/meta");

        // --- Project settings ----------------------------------------------
        Add("W", IamPermissions.ProjectSettingsWrite, "/api/projects/{}");
        Add("PUT", IamPermissions.ProjectSettingsWrite, "/api/projects/{}/*");
        Under("W", IamPermissions.ProjectSettingsWrite, "/api/projects/{}",
            "areas/{}/glossary", "build-profile", "build-profile/validate", "execution/override", "test-execution",
            "publish/automation", "urls", "urls/**", "ownership-mappings/**", "pipeline-definition/**");

        // --- Project reads -------------------------------------------------
        Add("R", IamPermissions.ProjectRead,
            "/api/projects/**", "/api/tasks/**", "/api/runner/{}/**", "/api/runner/project:{}/**",
            "/api/runner/task:{}/**", "/api/runner/workbench:{}/**", "/api/runner/integration-lease/**",
            "/api/orchestrator/**", "/api/analysis/**", "/api/drift/**", "/api/bus/**", "/api/runtime/**",
            "/api/supervisor/**", "/api/git/**", "/api/audits/**",
            "/api/workbenches/**", "/api/orchestration/**");
        Add("POST", IamPermissions.ProjectRead, "/api/projects/{}/deployment/compile");
        return rules;
    }
}
