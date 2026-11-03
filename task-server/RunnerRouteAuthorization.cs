using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// SM-9: what a runner credential may reach beyond its scopes. Card reads need a
/// lease, run and review routes need the runner's own claim, project reads need
/// a bound project, and every other shared read is refused.
/// </summary>
public static class RunnerRouteAuthorization
{
    private const string BindingItem = "TaskServer.RunnerCredentialBinding";
    private const string CardLifecycleOnlyItem = "TaskServer.RunnerCardLifecycleOnly";
    public const string CredentialExpiresHeader = "X-Principal-Credential-Expires-At";

    public static async Task<RunnerBindingRefusal?> AuthorizeAsync(
        HttpContext context, TaskServerPrincipal principal, TaskServerScopeMetadata required, TaskServerStore store)
    {
        if (principal.Kind != TaskServerPrincipalKinds.Runner) return null;
        var pattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        var family = RunnerRoutePolicy.Classify(
            context.Request.Method, pattern, RunnerRoutePolicy.OnlyThroughSharedRead(required, principal.Scopes));
        if (family is RunnerRouteFamily.Unrestricted or RunnerRouteFamily.Shared) return null;
        if (family == RunnerRouteFamily.Denied)
            return new RunnerBindingRefusal(StatusCodes.Status403Forbidden, "runner-route-denied",
                "A runner credential may not read this route.");
        if (principal.RunnerId is not { } runnerId)
            return new RunnerBindingRefusal(StatusCodes.Status403Forbidden, "runner-credential-unbound",
                "This runner credential is not bound to a runner identity. Enrol one credential per host.");

        var ct = context.RequestAborted;
        string Route(string name) => Convert.ToString(context.Request.RouteValues[name]) ?? string.Empty;
        switch (family)
        {
            case RunnerRouteFamily.OwnHost:
            {
                var binding = await BindingAsync(context, principal, store);
                return string.Equals(binding.HostId, Route("hostId"), StringComparison.Ordinal)
                    ? null
                    : new RunnerBindingRefusal(StatusCodes.Status403Forbidden, "runner-host-mismatch",
                        "A runner credential may read only its own host.");
            }
            case RunnerRouteFamily.BoundProjectList:
                await BindingAsync(context, principal, store);
                return null;
            case RunnerRouteFamily.BoundProject:
                return RunnerCredentialBindingPolicy.ProjectRefusal(
                    await BindingAsync(context, principal, store),
                    await ProjectIdAsync(store, Route("projectId"), ct));
            case RunnerRouteFamily.BoundRepository:
                return RunnerCredentialBindingPolicy.ProjectRefusal(
                    await BindingAsync(context, principal, store),
                    await store.RepositoryProjectAsync(Route("repositoryId"), ct));
            case RunnerRouteFamily.LeasedCard:
            {
                var (access, _) = context.Request.RouteValues.ContainsKey("taskKey")
                    ? await store.RunnerCardKeyAccessAsync(runnerId, Route("taskKey"), ct)
                    : await store.RunnerCardAccessAsync(runnerId, Route("projectId"), Route("taskIdentity"), ct);
                if (access is RunnerCardAccess.Missing or RunnerCardAccess.Leased) return null;
                // Salvage retention asks whether a card it once ran is finished;
                // it learns the lifecycle and nothing of the card's content.
                if (access == RunnerCardAccess.Former && pattern == "/api/v1/projects/{projectId}/tasks/{taskIdentity}")
                {
                    context.Items[CardLifecycleOnlyItem] = true;
                    return null;
                }
                return new RunnerBindingRefusal(StatusCodes.Status403Forbidden, "runner-card-not-leased",
                    "A runner may read a card only while it holds that card's lease.");
            }
            case RunnerRouteFamily.ClaimedRun:
                return await store.RunnerOwnsRunAsync(runnerId, Route("runId"),
                        includeReviewedSource: HttpMethods.IsGet(context.Request.Method), ct)
                    ? null
                    : NotClaimant("run");
            case RunnerRouteFamily.ClaimedReviewAttempt:
                return await store.RunnerOwnsReviewAttemptAsync(runnerId, Route("attemptId"), ct)
                    ? null
                    : NotClaimant("review attempt");
            case RunnerRouteFamily.ClaimedReviewSubject:
                return await store.RunnerOwnsReviewSubjectAsync(runnerId, Route("subjectId"), ct)
                    ? null
                    : NotClaimant("review subject");
            case RunnerRouteFamily.ClaimedGateSubject:
                return await store.RunnerOwnsGateSubjectAsync(runnerId, Route("subjectId"), ct)
                    ? null
                    : NotClaimant("gate subject");
            case RunnerRouteFamily.ClaimedReviewSource:
                return await store.RunnerOwnsReviewSourceAsync(runnerId, Route("reviewSubjectId"), ct)
                    ? null
                    : NotClaimant("review source");
            default:
                return new RunnerBindingRefusal(StatusCodes.Status403Forbidden, "runner-route-denied",
                    "A runner credential may not read this route.");
        }
    }

    /// <summary>The binding of the request's runner credential, resolved once per request.</summary>
    public static async Task<RunnerCredentialBinding> BindingAsync(
        HttpContext context, TaskServerPrincipal principal, TaskServerStore store)
    {
        if (context.Items.TryGetValue(BindingItem, out var cached) && cached is RunnerCredentialBinding binding)
            return binding;
        binding = await store.ResolveRunnerBindingAsync(principal, context.RequestAborted);
        context.Items[BindingItem] = binding;
        return binding;
    }

    /// <summary>The runner credential's binding, or null for any other caller.</summary>
    public static async Task<RunnerCredentialBinding?> RunnerBindingAsync(HttpContext context, TaskServerStore store)
        => context.TaskServerPrincipal() is { Kind: TaskServerPrincipalKinds.Runner } principal
            ? await BindingAsync(context, principal, store)
            : null;

    /// <summary>The request may see a card's lifecycle only: title and body are withheld.</summary>
    public static bool CardLifecycleOnly(HttpContext context)
        => context.Items.TryGetValue(CardLifecycleOnlyItem, out var value) && value is true;

    public static TaskDto LifecycleOnly(TaskDto task) => task with { Title = string.Empty, Body = null };

    /// <summary>The claim role check of a coding or review claim route, or null when admitted.</summary>
    public static async Task<IResult?> ClaimRefusalAsync(HttpContext context, TaskServerStore store, string claimRole)
    {
        if (await RunnerBindingAsync(context, store) is not { } binding) return null;
        return RunnerCredentialBindingPolicy.ClaimRefusal(binding, claimRole) is { } refusal
            ? Results.Json(new ApiError(refusal.Code, refusal.Message), statusCode: refusal.Status)
            : null;
    }

    public static async Task<IResult?> ReviewPackageAttemptRefusalAsync(
        HttpContext context, TaskServerStore store, string attemptId)
    {
        if (await RunnerBindingAsync(context, store) is not { } binding) return null;
        var refusal = RunnerCredentialBindingPolicy.ClaimRefusal(binding, RunnerCredentialRoles.Review)
            ?? RunnerCredentialBindingPolicy.ProjectRefusal(binding,
                await store.ReviewPackageProjectAsync(attemptId, context.RequestAborted));
        return refusal is null
            ? null
            : Results.Json(new ApiError(refusal.Code, refusal.Message), statusCode: refusal.Status);
    }

    private static async Task<string?> ProjectIdAsync(TaskServerStore store, string identity, CancellationToken ct)
        => (await store.ListProjectsAsync(null, ct))
            .FirstOrDefault(project => string.Equals(project.ProjectId, identity, StringComparison.Ordinal)
                                       || string.Equals(project.Name, identity, StringComparison.Ordinal))
            ?.ProjectId;

    private static RunnerBindingRefusal NotClaimant(string subject)
        => new(StatusCodes.Status403Forbidden, "runner-not-claimant",
            $"A runner may act on a {subject} only when it holds that claim.");
}
