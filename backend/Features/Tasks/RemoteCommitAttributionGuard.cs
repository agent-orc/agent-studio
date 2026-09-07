using System.Text.RegularExpressions;
using AgentStudio.Git;
using AgentStudio.Shared;

namespace AgentStudio.Tasks;

public sealed record RemoteCommitAttributionResult(
    bool Accepted,
    IReadOnlyList<TaskCommitInfo> Commits,
    string? Warning);

/// <summary>
/// Converts an exact remote runner branch range into task commit attribution.
/// The whole range is rejected when either the branch belongs to another task
/// or a commit subject explicitly names another task key.
/// </summary>
public static partial class RemoteCommitAttributionGuard
{
    [GeneratedRegex(@"\b[A-Z][A-Z0-9]{1,15}-\d+\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TaskKeyPattern();

    // Immutable ResultEnvelope refs are task-neutral by design:
    // <project>/results/run_<id>/fence-<n>/<result-sha>. Their identity is not the ref
    // name but the fenced result SHA the caller already verified against the
    // branch tip (InspectRemoteDeliveryCommitRange), so the task-key suffix
    // rule must not apply to them - it rejected EVERY envelope delivery and
    // left canonical remote cards without attributed commits ("kein Branch"
    // on reviewed cards, AGT-2434/AGT-2445).
    [GeneratedRegex(@"(^|/)results/run_[0-9a-f]{32}/(?:fence-[1-9][0-9]*/)?[0-9a-f]{40}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ImmutableResultRefPattern();

    // A divergent salvage publishes the run's own result to
    // <card-branch>-collision-<local-sha>-<remote-sha> because the canonical
    // branch kept a foreign tip. That branch still belongs to this card, so its
    // range must be attributable - otherwise the reviewed delivery arrives with
    // an empty commits[], which is half of what AGT-2220 showed on 28.07.
    [GeneratedRegex(@"-collision-[0-9a-f]{7,64}-[0-9a-f]{7,64}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DivergentCollisionSuffixPattern();

    public static RemoteCommitAttributionResult Attribute(
        string taskKey,
        string deliveryBranch,
        IReadOnlyList<GitCommitInfo> commits,
        string? repository = null)
    {
        var expected = taskKey.Trim();
        var normalizedBranch = TaskIntegrationBranch.Name(deliveryBranch);
        var lastSegment = normalizedBranch
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        var branchTaskKey = lastSegment is null
            ? null
            : DivergentCollisionSuffixPattern().Replace(lastSegment, string.Empty);
        if (!string.Equals(branchTaskKey, expected, StringComparison.OrdinalIgnoreCase)
            && !ImmutableResultRefPattern().IsMatch(normalizedBranch))
        {
            return Rejected(
                $"Remote commit attribution rejected: branch '{deliveryBranch}' does not belong to task '{expected}'.");
        }

        foreach (var commit in commits)
        {
            var foreign = TaskKeyPattern().Matches(commit.Subject)
                .Select(match => match.Value)
                .FirstOrDefault(key => !string.Equals(key, expected, StringComparison.OrdinalIgnoreCase));
            if (foreign is not null)
            {
                return Rejected(
                    $"Remote commit attribution rejected for '{expected}': commit {commit.ShortSha} names foreign task '{foreign}'.");
            }
        }

        var attributed = commits.Select(commit => new TaskCommitInfo
        {
            Sha = commit.Sha,
            ShortSha = commit.ShortSha,
            Message = commit.Subject,
            Repository = repository,
            Branch = TaskIntegrationBranch.Name(deliveryBranch),
            FilesChanged = commit.FilesChanged,
            At = commit.AuthorDateUtc,
            Attribution = CommitAttributionKinds.Automatic,
            Confidence = 1.0,
        }).ToList();
        return new RemoteCommitAttributionResult(true, attributed, null);
    }

    private static RemoteCommitAttributionResult Rejected(string warning)
        => new(false, [], warning);
}
