using System.Text.Json.Serialization;
using AgentStudio.Operations.Contracts;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Security;

/// <summary>
/// The authority an operator agent request needs (security model SM-4):
/// T0 observe is always admitted to an active session, T1 routine needs a
/// standing grant, T2 high risk needs the Owner's approval of the exact request.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OperatorTier>))]
public enum OperatorTier
{
    [JsonStringEnumMemberName("T0")] T0,
    [JsonStringEnumMemberName("T1")] T1,
    [JsonStringEnumMemberName("T2")] T2,
}

/// <summary>What one operation acts on, and so which grant scope can cover it.</summary>
public static class OperatorScopeKinds
{
    public const string Workspace = "workspace";
    public const string Project = "project";
    public const string Environment = "environment";
    public const string Host = "host";

    public static readonly IReadOnlyList<string> All = [Workspace, Project, Environment, Host];
}

public static class OperatorOperationIds
{
    public const string Observe = "observe";
    public const string HostInspect = "host.inspect";
    public const string CardRequeue = "card.requeue";
    public const string CardSteer = "card.steer";
    public const string ServiceRestart = "service.restart";
    public const string HostDrain = "host.drain";
    public const string HostResume = "host.resume";
    public const string HostLogsCollect = "host.logs.collect";
    public const string HostConfigurationApply = "host.configuration.apply";
    public const string HostCertificateIssue = "host.certificate.issue";
    public const string HostCertificateRevoke = "host.certificate.revoke";
    public const string GateRerun = "gate.rerun";
    public const string StageDeploy = "stage.deploy";
    public const string ProductionPromote = "production.promote";
    public const string SecretsChange = "secrets.change";
    public const string ExposureChange = "exposure.change";
    public const string DataDelete = "data.delete";
    public const string HistoryRewrite = "history.rewrite";
    public const string GateBypass = "gate.bypass";
    public const string KeysRotate = "keys.rotate";
    public const string HostEnrollment = "host.enrollment";
    public const string AccessChange = "access.change";
    public const string GrantChange = "grant.change";
    public const string CatalogueChange = "catalogue.change";
}

/// <summary>One catalogued operation: exactly one tier and the kind of scope it acts on.</summary>
public sealed record OperatorOperation(string Id, OperatorTier Tier, string ScopeKind, string Title);

/// <summary>
/// How a route's request body refines its operation. The rule names the
/// operation a request has when the refinement does not change it.
/// </summary>
public enum OperatorRouteRefinement
{
    None,
    /// <summary>The target lane decides: a queue lane is a requeue, a delivered lane skips the gate.</summary>
    CardMove,
    /// <summary>The operation stays routine only for a commit with full gate evidence (D2); otherwise it bypasses the gate.</summary>
    GateEvidence,
    /// <summary>The target environment decides between a stage deploy and a production promotion, then gate evidence.</summary>
    ReleaseDeployment,
}

/// <summary>The request facts a refinement reads; the classifier collects them.</summary>
public sealed record OperatorRequestFacts(string? TargetLane = null, string? EnvironmentKind = null, bool FullGateEvidence = false);

/// <summary>
/// The operator agent's operation catalogue (security model SM-4, decisions
/// D2 and D3). Each catalogued operation has one tier and one scope kind.
/// Every request of an operator session resolves through it: a read is T0
/// observe; a write is catalogued only by a rule below and is refused
/// otherwise. Direct pushes to protected branches have no operation (D3): a
/// change reaches them as a card through the gate. Adding an operation or
/// moving it between tiers changes this file, which is itself T2
/// (<see cref="OperatorOperationIds.CatalogueChange"/>).
/// <para>
/// Route patterns use the syntax of <see cref="RoutePermissionCatalog"/>:
/// a literal segment, <c>{}</c> for a route parameter, <c>*</c> for any one
/// segment and a trailing <c>**</c> for any rest. Rules are narrow and must
/// not overlap; the inventory test fails for a route two rules match.
/// </para>
/// </summary>
public static class OperatorOperationCatalog
{
    public sealed record RouteRule(string Methods, string Pattern, string OperationId,
        OperatorRouteRefinement Refinement = OperatorRouteRefinement.None)
    {
        internal string[] Segments { get; } = Pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    public static readonly IReadOnlyList<OperatorOperation> Operations =
    [
        new(OperatorOperationIds.Observe, OperatorTier.T0, OperatorScopeKinds.Workspace,
            "Read board, cards, runs, logs, metrics, host status, audit and credential metadata"),
        new(OperatorOperationIds.HostInspect, OperatorTier.T0, OperatorScopeKinds.Host, "Inspect a host"),
        new(OperatorOperationIds.CardRequeue, OperatorTier.T1, OperatorScopeKinds.Project, "Requeue a card"),
        new(OperatorOperationIds.CardSteer, OperatorTier.T1, OperatorScopeKinds.Project, "Steer a card's run"),
        new(OperatorOperationIds.ServiceRestart, OperatorTier.T1, OperatorScopeKinds.Host, "Restart an allowlisted service"),
        new(OperatorOperationIds.HostDrain, OperatorTier.T1, OperatorScopeKinds.Host, "Drain a host"),
        new(OperatorOperationIds.HostResume, OperatorTier.T1, OperatorScopeKinds.Host, "Resume a host"),
        new(OperatorOperationIds.HostLogsCollect, OperatorTier.T0, OperatorScopeKinds.Host, "Collect allowlisted host logs"),
        new(OperatorOperationIds.HostConfigurationApply, OperatorTier.T1, OperatorScopeKinds.Host,
            "Apply a reviewed host configuration file"),
        new(OperatorOperationIds.HostCertificateIssue, OperatorTier.T1, OperatorScopeKinds.Host,
            "Issue a short host certificate"),
        new(OperatorOperationIds.HostCertificateRevoke, OperatorTier.T1, OperatorScopeKinds.Host,
            "Revoke a host certificate"),
        new(OperatorOperationIds.GateRerun, OperatorTier.T1, OperatorScopeKinds.Project, "Re-run a gate"),
        new(OperatorOperationIds.StageDeploy, OperatorTier.T1, OperatorScopeKinds.Environment,
            "Deploy a gate-verified commit to stage"),
        new(OperatorOperationIds.ProductionPromote, OperatorTier.T1, OperatorScopeKinds.Environment,
            "Promote a commit with full gate evidence"),
        new(OperatorOperationIds.SecretsChange, OperatorTier.T2, OperatorScopeKinds.Project, "Change secrets"),
        new(OperatorOperationIds.ExposureChange, OperatorTier.T2, OperatorScopeKinds.Project, "Change firewall or exposure"),
        new(OperatorOperationIds.DataDelete, OperatorTier.T2, OperatorScopeKinds.Project, "Delete data"),
        new(OperatorOperationIds.HistoryRewrite, OperatorTier.T2, OperatorScopeKinds.Project, "Force push or rewrite history"),
        new(OperatorOperationIds.GateBypass, OperatorTier.T2, OperatorScopeKinds.Project, "Bypass a gate"),
        new(OperatorOperationIds.KeysRotate, OperatorTier.T2, OperatorScopeKinds.Workspace, "Rotate keys"),
        new(OperatorOperationIds.HostEnrollment, OperatorTier.T2, OperatorScopeKinds.Host, "Enroll or remove a host"),
        new(OperatorOperationIds.AccessChange, OperatorTier.T2, OperatorScopeKinds.Workspace, "Change access"),
        new(OperatorOperationIds.GrantChange, OperatorTier.T2, OperatorScopeKinds.Workspace, "Change standing grants"),
        new(OperatorOperationIds.CatalogueChange, OperatorTier.T2, OperatorScopeKinds.Workspace,
            "Change the operation catalogue"),
    ];

    /// <summary>Typed host operations of the Operations Server, by their definition id.</summary>
    public static readonly IReadOnlyDictionary<string, string> TypedOperations = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [OperationCatalogue.HostInspect.Id] = OperatorOperationIds.HostInspect,
        [OperationCatalogue.ServiceRestart.Id] = OperatorOperationIds.ServiceRestart,
        [OperationCatalogue.HostDrain.Id] = OperatorOperationIds.HostDrain,
        [OperationCatalogue.HostResume.Id] = OperatorOperationIds.HostResume,
        [OperationCatalogue.HostLogsCollect.Id] = OperatorOperationIds.HostLogsCollect,
        [OperationCatalogue.HostConfigurationApply.Id] = OperatorOperationIds.HostConfigurationApply,
    };

    /// <summary>Lanes a requeue may move a card into: ahead of a run, never past the gate.</summary>
    public static readonly IReadOnlySet<string> QueueLanes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { TaskStates.Backlog, TaskStates.Preparation, TaskStates.OrchestratorPrep, TaskStates.Ready };

    /// <summary>Lanes only the gate and review may move a card into; a direct move bypasses the gate.</summary>
    public static readonly IReadOnlySet<string> DeliveredLanes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { TaskStates.HumanReview, TaskStates.Completed };

    /// <summary>Write rules. Reads (GET, HEAD, OPTIONS and read-only POSTs) are <see cref="OperatorOperationIds.Observe"/>.</summary>
    public static readonly IReadOnlyList<RouteRule> Routes =
    [
        // --- T1 routine ------------------------------------------------------
        new("POST", "/api/tasks/{}/move", OperatorOperationIds.CardRequeue, OperatorRouteRefinement.CardMove),
        new("POST", "/api/tasks/{}/restore-from-failed-pickup", OperatorOperationIds.CardRequeue),
        new("POST", "/api/tasks/{}/continue", OperatorOperationIds.CardSteer),
        new("POST", "/api/hosts/{}/certificates", OperatorOperationIds.HostCertificateIssue),
        new("POST", "/api/hosts/{}/certificates/{}/revoke", OperatorOperationIds.HostCertificateRevoke),
        new("POST", "/api/hosts/{}/inspect", OperatorOperationIds.HostInspect),
        new("POST", "/api/hosts/{}/restart", OperatorOperationIds.ServiceRestart),
        new("POST", "/api/hosts/{}/drain", OperatorOperationIds.HostDrain),
        new("POST", "/api/hosts/{}/resume", OperatorOperationIds.HostResume),
        new("POST", "/api/hosts/{}/logs", OperatorOperationIds.HostLogsCollect),
        new("POST", "/api/hosts/{}/configuration", OperatorOperationIds.HostConfigurationApply),
        new("POST", "/api/tasks/{}/integration/retry", OperatorOperationIds.GateRerun),
        new("POST", "/api/tasks/{}/pipeline/steps/{}/run", OperatorOperationIds.GateRerun),
        new("POST", "/api/projects/{}/environments/releases", OperatorOperationIds.StageDeploy,
            OperatorRouteRefinement.GateEvidence),
        new("POST", "/api/projects/{}/environments/promotions/approve", OperatorOperationIds.ProductionPromote,
            OperatorRouteRefinement.GateEvidence),
        new("POST", "/api/projects/{}/deploy/releases/{}/deployments", OperatorOperationIds.StageDeploy,
            OperatorRouteRefinement.ReleaseDeployment),

        // --- T2 high risk ----------------------------------------------------
        new("W", "/api/secrets/**", OperatorOperationIds.SecretsChange),
        new("W", "/api/projects/{}/secrets/**", OperatorOperationIds.SecretsChange),
        new("W", "/api/projects/{}/secret-bindings/**", OperatorOperationIds.SecretsChange),
        new("W", "/api/projects/{}/environments/{}/secrets/**", OperatorOperationIds.SecretsChange),
        new("W", "/api/projects/{}/urls/**", OperatorOperationIds.ExposureChange),
        new("POST", "/api/projects/{}/publish/website", OperatorOperationIds.ExposureChange),
        new("POST", "/api/projects/{}/publish/package", OperatorOperationIds.ExposureChange),
        new("DELETE", "/api/tasks/{}", OperatorOperationIds.DataDelete),
        new("DELETE", "/api/tasks/orphan-folder", OperatorOperationIds.DataDelete),
        new("W", "/api/projects/{}/environments/{}/volumes/**", OperatorOperationIds.DataDelete),
        new("POST", "/api/clients/retired/purge", OperatorOperationIds.DataDelete),
        new("W", "/api/git/cleanup/**", OperatorOperationIds.HistoryRewrite),
        new("W", "/api/git/branch-sweep/**", OperatorOperationIds.HistoryRewrite),
        new("W", "/api/git/branch-reclaim/**", OperatorOperationIds.HistoryRewrite),
        new("POST", "/api/tasks/{}/integration/rebase", OperatorOperationIds.HistoryRewrite),
        new("W", "/api/tasks/{}/merge/**", OperatorOperationIds.GateBypass),
        new("POST", "/api/tasks/{}/external-completion", OperatorOperationIds.GateBypass),
        new("W", "/api/projects/{}/environments/{}/telemetry/tokens", OperatorOperationIds.KeysRotate),
        new("W", "/api/projects/{}/environments/{}/telemetry/tokens/{}", OperatorOperationIds.KeysRotate),
        new("DELETE", "/api/clients/{}", OperatorOperationIds.HostEnrollment),
        new("DELETE", "/api/clients/{}/permanent", OperatorOperationIds.HostEnrollment),
        new("POST", "/api/clients/{}/retire", OperatorOperationIds.HostEnrollment),
        new("W", "/api/auth/runner-enrollments/**", OperatorOperationIds.HostEnrollment),
        new("DELETE", "/api/auth/runners/{}", OperatorOperationIds.HostEnrollment),
        new("POST", "/api/auth/runners/{}/credentials", OperatorOperationIds.KeysRotate),
        new("DELETE", "/api/auth/runners/{}/credentials/{}", OperatorOperationIds.KeysRotate),
        new("PUT", "/api/auth/runners/{}/projects", OperatorOperationIds.AccessChange),
        new("W", "/api/auth/iam/standing-grants/**", OperatorOperationIds.GrantChange),
        new("W", "/api/auth/users/**", OperatorOperationIds.AccessChange),
        new("POST", "/api/auth/invitations", OperatorOperationIds.AccessChange),
        new("W", "/api/auth/api-tokens/**", OperatorOperationIds.AccessChange),
        new("W", "/api/auth/iam/bindings/**", OperatorOperationIds.AccessChange),
        new("W", "/api/auth/iam/groups/**", OperatorOperationIds.AccessChange),
        new("W", "/api/auth/iam/service-accounts/**", OperatorOperationIds.AccessChange),
        new("W", "/api/auth/iam/operator-agents/**", OperatorOperationIds.AccessChange),
    ];

    private static readonly Dictionary<string, OperatorOperation> ById =
        Operations.ToDictionary(operation => operation.Id, StringComparer.Ordinal);

    public static OperatorOperation? Find(string? id) => id is not null && ById.TryGetValue(id, out var operation) ? operation : null;

    /// <summary>The catalogue operation of a typed host operation, or <c>null</c> when it is not catalogued.</summary>
    public static OperatorOperation? ForTypedOperation(string typedOperationId)
        => TypedOperations.TryGetValue(typedOperationId, out var id) ? Find(id) : null;

    /// <summary>
    /// The rule that catalogues a write to a normalized route pattern such as
    /// <c>/api/tasks/{}/move</c>, or <c>null</c> when the write is not catalogued.
    /// </summary>
    public static RouteRule? ResolveWrite(string method, string normalizedPattern)
    {
        var segments = normalizedPattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Routes.FirstOrDefault(rule => MethodMatches(rule.Methods, method) && SegmentsMatch(rule.Segments, segments));
    }

    /// <summary>Every write rule matching the pattern; the inventory test requires at most one.</summary>
    public static IReadOnlyList<RouteRule> MatchingWrites(string method, string normalizedPattern)
    {
        var segments = normalizedPattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Routes.Where(rule => MethodMatches(rule.Methods, method) && SegmentsMatch(rule.Segments, segments)).ToList();
    }

    /// <summary>
    /// The operation a request has once its refinement read the facts.
    /// <c>null</c> means the request is not catalogued.
    /// </summary>
    public static OperatorOperation? Refine(RouteRule rule, OperatorRequestFacts facts)
    {
        var id = rule.Refinement switch
        {
            OperatorRouteRefinement.CardMove => facts.TargetLane is { } lane
                ? QueueLanes.Contains(lane) ? OperatorOperationIds.CardRequeue
                : DeliveredLanes.Contains(lane) ? OperatorOperationIds.GateBypass
                : null
                : null,
            OperatorRouteRefinement.GateEvidence => facts.FullGateEvidence ? rule.OperationId : OperatorOperationIds.GateBypass,
            OperatorRouteRefinement.ReleaseDeployment => facts.EnvironmentKind switch
            {
                IamEnvironments.Stage or IamEnvironments.Production when !facts.FullGateEvidence => OperatorOperationIds.GateBypass,
                IamEnvironments.Stage => OperatorOperationIds.StageDeploy,
                IamEnvironments.Production => OperatorOperationIds.ProductionPromote,
                _ => null,
            },
            _ => rule.OperationId,
        };
        return Find(id);
    }

    /// <summary>Every operation a rule can resolve to, for the inventory test.</summary>
    public static IReadOnlyList<string> PossibleOperations(RouteRule rule) => rule.Refinement switch
    {
        OperatorRouteRefinement.CardMove => [OperatorOperationIds.CardRequeue, OperatorOperationIds.GateBypass],
        OperatorRouteRefinement.GateEvidence => [rule.OperationId, OperatorOperationIds.GateBypass],
        OperatorRouteRefinement.ReleaseDeployment =>
            [OperatorOperationIds.StageDeploy, OperatorOperationIds.ProductionPromote, OperatorOperationIds.GateBypass],
        _ => [rule.OperationId],
    };

    private static bool MethodMatches(string rule, string method) => rule switch
    {
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
}
