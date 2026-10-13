using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// AGT-3077: what the claim endpoint knows about a task's repository before
/// selection - the project's visibility and the repository URL the subject
/// resolves to at hand-out (<c>V1ReviewPlaneEndpoints.ToSubject</c>).
/// </summary>
public sealed record ReviewClaimRepositoryFact(bool ProjectPrivate, string? RepositoryUrl);

/// <summary>
/// AGT-3077: judges repository read access for a queued ReviewAttempt the way
/// the subject handed to the executor will look. A lease-expiry successor or a
/// settled-run recovery attempt carries no plan, and an attempt may carry no
/// repository URL; both are built at claim time from the project. Judging only
/// the sealed plan let such an attempt reach a host without the read
/// credential, which then deferred it and stalled the whole claim queue behind
/// it. A sealed plan's visibility and a stored URL still outrank the project.
/// </summary>
public static class ReviewClaimRepositoryAccessPolicy
{
    public static IReadOnlyList<string> MissingCapabilities(
        Contract.ReviewPlanDto? sealedPlan,
        string? subjectRepositoryUrl,
        ReviewClaimRepositoryFact? fact,
        IReadOnlySet<string> capabilities)
        => Contract.ReviewRepositoryAccessPolicy.MissingCapabilities(
            sealedPlan?.PrivateRepository ?? fact?.ProjectPrivate ?? false,
            subjectRepositoryUrl ?? fact?.RepositoryUrl,
            capabilities);
}
