using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The projects one runner credential may work on (SM-9). Only an explicit host
/// project policy grants projects; a host without one holds none.
/// </summary>
public sealed record RunnerProjectScope(bool AllProjects, IReadOnlySet<string> ProjectIds)
{
    public static readonly RunnerProjectScope None = new(false, new HashSet<string>(StringComparer.Ordinal));

    public bool IsEmpty => !AllProjects && ProjectIds.Count == 0;

    public bool Allows(string? projectId)
        => projectId is not null && (AllProjects || ProjectIds.Contains(projectId));
}

/// <summary>
/// What one authenticated runner credential is bound to: its runner identity,
/// the host that identity runs on, its role and the projects of that host.
/// </summary>
public sealed record RunnerCredentialBinding(
    string PrincipalId,
    string? RunnerId,
    string? HostId,
    string? Role,
    RunnerProjectScope Projects);

public sealed record RunnerBindingRefusal(int Status, string Code, string Message);

public static class RunnerCredentialRoles
{
    public const string Coding = HostRoles.Coding;
    public const string Review = HostRoles.Review;

    public static bool IsValid(string? role) => role is null or Coding or Review;
}

public enum RunnerHostRegistration
{
    /// <summary>The credential is already bound to the requested host.</summary>
    Accept,
    /// <summary>First registration: pin the requested host to the credential.</summary>
    Bind,
    Refuse,
}

/// <summary>Pure SM-9 decisions about a runner credential's host, role and projects.</summary>
public static class RunnerCredentialBindingPolicy
{
    /// <summary>
    /// The host enrolment names the role first, then the role recorded at
    /// enrolment. Without either, a credential that can claim only one kind of
    /// work has that role; one that can claim both keeps its scopes as its role.
    /// </summary>
    public static string? Role(string? enrolledRole, string? recordedRole, IReadOnlySet<string> scopes)
    {
        if (enrolledRole is RunnerCredentialRoles.Coding or RunnerCredentialRoles.Review) return enrolledRole;
        if (recordedRole is RunnerCredentialRoles.Coding or RunnerCredentialRoles.Review) return recordedRole;
        var coding = scopes.Contains(TaskServerScopes.RunsClaim);
        var review = scopes.Contains(TaskServerScopes.ReviewsClaim);
        return coding == review ? null : coding ? RunnerCredentialRoles.Coding : RunnerCredentialRoles.Review;
    }

    /// <summary>An explicit policy grants its projects; a missing policy grants none.</summary>
    public static RunnerProjectScope Projects(HostProjectPolicyDto? policy)
        => policy is null
            ? RunnerProjectScope.None
            : new RunnerProjectScope(policy.AllowAllProjects,
                policy.AllowedProjectIds.ToHashSet(StringComparer.Ordinal));

    /// <summary>
    /// The host an enrolment names wins; otherwise the first registration pins
    /// the host and every later registration must name the same one.
    /// </summary>
    public static RunnerHostRegistration HostRegistration(
        string? enrolledHostId, string? boundHostId, string requestedHostId)
    {
        var expected = enrolledHostId ?? boundHostId;
        if (expected is null) return RunnerHostRegistration.Bind;
        return string.Equals(expected, requestedHostId, StringComparison.Ordinal)
            ? boundHostId is null ? RunnerHostRegistration.Bind : RunnerHostRegistration.Accept
            : RunnerHostRegistration.Refuse;
    }

    /// <summary>Why the credential may not claim work of <paramref name="claimRole"/>, or null.</summary>
    public static RunnerBindingRefusal? ClaimRefusal(RunnerCredentialBinding binding, string claimRole)
    {
        if (binding.RunnerId is null)
            return new RunnerBindingRefusal(StatusCodes.Status403Forbidden, "runner-credential-unbound",
                "This runner credential is not bound to a runner identity. Enrol one credential per host.");
        if (binding.Role is not null && !string.Equals(binding.Role, claimRole, StringComparison.Ordinal))
            return new RunnerBindingRefusal(StatusCodes.Status403Forbidden, "runner-role-mismatch",
                $"This runner credential is bound to the {binding.Role} role and cannot claim {claimRole} work.");
        return null;
    }

    /// <summary>Why the credential may not read or act on work of <paramref name="projectId"/>, or null.</summary>
    public static RunnerBindingRefusal? ProjectRefusal(RunnerCredentialBinding binding, string? projectId)
        => binding.Projects.Allows(projectId)
            ? null
            : new RunnerBindingRefusal(StatusCodes.Status403Forbidden, "runner-project-not-bound",
                "This runner credential is not bound to the addressed project.");
}

/// <summary>How a route is authorized for a runner credential beyond its scope (SM-9).</summary>
public enum RunnerRouteFamily
{
    /// <summary>The route's scope and its own identity checks decide; the scope is runner-only.</summary>
    Unrestricted,
    /// <summary>Installation-wide metadata without card or project content.</summary>
    Shared,
    /// <summary>The <c>hostId</c> route value must be the credential's host.</summary>
    OwnHost,
    /// <summary>A project list, filtered to the bound projects by the handler.</summary>
    BoundProjectList,
    /// <summary>The <c>projectId</c> route value must be a bound project.</summary>
    BoundProject,
    /// <summary>The <c>repositoryId</c> route value must be the repository of a bound project.</summary>
    BoundRepository,
    /// <summary>The addressed card must be leased to this runner.</summary>
    LeasedCard,
    /// <summary>The addressed run must be this runner's claim.</summary>
    ClaimedRun,
    /// <summary>The addressed review attempt must be this runner's claim.</summary>
    ClaimedReviewAttempt,
    /// <summary>The addressed review subject must have an attempt claimed by this runner.</summary>
    ClaimedReviewSubject,
    /// <summary>The addressed gate subject must have an attempt claimed by this runner.</summary>
    ClaimedGateSubject,
    /// <summary>A review source the runner reviews, or one whose card it gates.</summary>
    ClaimedReviewSource,
    /// <summary>Reachable through the shared read scope only; a runner never needs it.</summary>
    Denied,
}

/// <summary>
/// Classifies Task Server routes for a runner credential. Every route a runner
/// reaches only through <c>tasks:read</c> is denied unless it is listed here,
/// so a new Studio or management read never becomes a runner read by default.
/// </summary>
public static class RunnerRoutePolicy
{
    private const string Get = "GET";

    public static readonly IReadOnlyDictionary<(string Method, string Pattern), RunnerRouteFamily> Routes =
        new Dictionary<(string Method, string Pattern), RunnerRouteFamily>
        {
            [(Get, "/api/v1/artifact-limits")] = RunnerRouteFamily.Shared,
            // The Studio attach handler refuses every runner credential itself, with an operator-readable reason.
            [("POST", "/api/v1/protocol/attach")] = RunnerRouteFamily.Shared,
            [(Get, "/api/v1/failure-fingerprints")] = RunnerRouteFamily.Shared,
            [(Get, "/api/v1/source-facts/review-material/timings")] = RunnerRouteFamily.Shared,
            [(Get, "/api/v1/hosts/{hostId}/runtime-capacity")] = RunnerRouteFamily.OwnHost,
            [(Get, "/api/v1/hosts/{hostId}/placement-advertisement")] = RunnerRouteFamily.OwnHost,
            [(Get, "/api/v1/projects")] = RunnerRouteFamily.BoundProjectList,
            [(Get, "/api/v1/projects/{projectId}/repository")] = RunnerRouteFamily.BoundProject,
            [(Get, "/api/v1/source-facts/{repositoryId}/diffs")] = RunnerRouteFamily.BoundRepository,
            [(Get, "/api/v1/source-facts/{repositoryId}/path-histories")] = RunnerRouteFamily.BoundRepository,
            [(Get, "/api/v1/source-facts/{repositoryId}/blames")] = RunnerRouteFamily.BoundRepository,
            [(Get, "/api/v1/source-facts/{repositoryId}/reachability")] = RunnerRouteFamily.BoundRepository,
            [(Get, "/api/v1/projects/{projectId}/tasks/{taskIdentity}")] = RunnerRouteFamily.LeasedCard,
            [(Get, "/api/v1/projects/{projectId}/tasks/{taskIdentity}/attempts")] = RunnerRouteFamily.LeasedCard,
            [(Get, "/api/v1/projects/{projectId}/tasks/{taskIdentity}/history")] = RunnerRouteFamily.LeasedCard,
            [(Get, "/api/v1/projects/{projectId}/tasks/{taskIdentity}/continuations")] = RunnerRouteFamily.LeasedCard,
            [(Get, "/api/v1/projects/{projectId}/tasks/{taskIdentity}/continuations/{commandId}")] = RunnerRouteFamily.LeasedCard,
            [(Get, "/api/v1/runner/tasks/{taskKey}/attachments/{fileName}")] = RunnerRouteFamily.LeasedCard,
            [(Get, "/api/v1/reviews/subjects/{subjectId}")] = RunnerRouteFamily.ClaimedReviewSubject,
            [(Get, "/api/v1/gates/subjects/{subjectId}")] = RunnerRouteFamily.ClaimedGateSubject,
            [(Get, "/api/v1/gates/review-sources/{reviewSubjectId}")] = RunnerRouteFamily.ClaimedReviewSource,
        };

    private const string RunPrefix = "/api/v1/runs/{runId}";
    private const string ReviewAttemptPrefix = "/api/v1/reviews/attempts/{attemptId}";

    /// <param name="onlyThroughSharedRead">The credential satisfies the route's scope only through <c>tasks:read</c>.</param>
    public static RunnerRouteFamily Classify(string method, string? pattern, bool onlyThroughSharedRead)
    {
        if (pattern is null) return onlyThroughSharedRead ? RunnerRouteFamily.Denied : RunnerRouteFamily.Unrestricted;
        if (Routes.TryGetValue((method.ToUpperInvariant(), pattern), out var family)) return family;
        // Every run and review-attempt route acts on one claim: reads, renewals and reports alike.
        if (pattern == RunPrefix || pattern.StartsWith(RunPrefix + "/", StringComparison.Ordinal))
            return RunnerRouteFamily.ClaimedRun;
        if (pattern == ReviewAttemptPrefix || pattern.StartsWith(ReviewAttemptPrefix + "/", StringComparison.Ordinal))
            return RunnerRouteFamily.ClaimedReviewAttempt;
        return onlyThroughSharedRead ? RunnerRouteFamily.Denied : RunnerRouteFamily.Unrestricted;
    }

    /// <summary>True when <c>tasks:read</c> is the only scope of the credential that satisfies the route.</summary>
    public static bool OnlyThroughSharedRead(TaskServerScopeMetadata required, IReadOnlySet<string> granted)
    {
        var satisfying = new[] { required.Scope }.Concat(required.Alternatives ?? [])
            .Where(granted.Contains)
            .ToArray();
        return satisfying.Length > 0 && satisfying.All(scope => scope == TaskServerScopes.TasksRead);
    }
}
