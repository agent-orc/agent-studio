using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>Pure required-versus-present projection. Missing grants never imply a secret exists.</summary>
public static class CredentialInventoryPolicy
{
    public sealed record Host(string Id, IReadOnlySet<string> Roles,
        IReadOnlySet<string> ReadyCapabilities);
    public sealed record PrivateReview(string Repository, string TaskKey);
    public sealed record Repository(string Canonical, string IntegrationRef, string? ReleaseRef);

    public static IReadOnlyList<CredentialInventoryItemDto> Derive(
        IReadOnlyList<CredentialInventoryItemDto> present,
        IReadOnlyList<Host> hosts,
        IReadOnlyList<PrivateReview> privateReviews,
        IReadOnlyList<Repository> repositories)
    {
        var result = new List<CredentialInventoryItemDto>(present);
        foreach (var repository in repositories)
        {
            if (present.Any(item => item.Category == "repository" && item.HostId == "control" &&
                item.Repository == repository.Canonical && item.Permission == "write" &&
                item.State is not "missing")) continue;
            var branches = new[] { repository.IntegrationRef, repository.ReleaseRef }
                .Where(branch => branch is not null).Distinct(StringComparer.Ordinal).Cast<string>().ToArray();
            result.Add(new($"required:integrator:{repository.Canonical}", "repository", "control",
                "integrator_write", "Integrator repository write access", branches,
                repository.Canonical, "write", null, null, "missing", true,
                "Register and verify control-host write access for the integration and release branches.", []));
        }
        foreach (var host in hosts)
        {
            foreach (var capability in host.ReadyCapabilities.Where(key => key.StartsWith(
                         ReviewRepositoryAccessPolicy.ReadCapabilityPrefix, StringComparison.Ordinal)))
                result.Add(new($"observed:review:{host.Id}:{capability}", "repository", host.Id,
                    "review_read_file", "Verified review read credential", [capability],
                    capability[ReviewRepositoryAccessPolicy.ReadCapabilityPrefix.Length..], "read",
                    null, null, "ok", privateReviews.Any(review => capability.EndsWith(review.Repository,
                        StringComparison.Ordinal)), "Read probe passed on this review host.", []));
            if (host.Roles.Contains(ReviewCapabilities.ReviewExecutor))
            {
                foreach (var group in privateReviews.GroupBy(review => review.Repository, StringComparer.Ordinal))
                {
                    var key = ReviewRepositoryAccessPolicy.ReadCapabilityPrefix + group.Key;
                    if (host.ReadyCapabilities.Contains(key)) continue;
                    result.Add(new($"required:review:{host.Id}:{group.Key}", "repository", host.Id,
                        "review_read_file", "Private repository read access", [key], group.Key, "read", null,
                        null, "missing", true,
                        "Add a repository-scoped read credential to RUNNER_REVIEW_GIT_CREDENTIALS_FILE and verify the read probe.",
                        group.Select(review => review.TaskKey).Distinct(StringComparer.Ordinal).ToArray()));
                }
            }
            if (host.Roles.Contains(GateCapabilities.Executor))
            {
                // Gate receives an SSH bundle. No repository credential is required.
            }
        }
        foreach (var item in present.Where(item => item.Category == "repository" &&
                     item.Permission == "write"))
        {
            var host = hosts.FirstOrDefault(candidate => candidate.Id == item.HostId);
            if (host is null || !host.Roles.Contains(ReviewCapabilities.ReviewExecutor) &&
                !host.Roles.Contains(GateCapabilities.Executor)) continue;
            // A mixed Coding/Review host can hold a coding-only write credential.
            // The role binding, when present, is the authority boundary.
            if (host.Roles.Contains(ReviewCapabilities.CodingExecutor) &&
                !item.Scopes.Any(scope => scope.StartsWith("review:", StringComparison.Ordinal) ||
                    scope.StartsWith("gate:", StringComparison.Ordinal))) continue;
            result.Remove(item);
            result.Add(item with { State = "unexpected", Detail = "Review and gate roles must not hold repository write access." });
        }
        return result;
    }
}
