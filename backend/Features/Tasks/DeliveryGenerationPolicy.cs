namespace AgentStudio.Tasks;

/// <summary>Evaluates one repository's delivery history. Callers supply repository-specific Git facts.</summary>
internal static class DeliveryGenerationPolicy
{
    internal static IReadOnlyList<TaskRepositoryCommitMembership> Evaluate(
        IReadOnlyList<TaskCommitInfo> commits,
        Func<string, bool> isAncestor,
        Func<string, bool> isReleased,
        Func<string, bool> isContentIntegrated)
    {
        var currentGeneration = commits.Select(commit => commit.DeliveryGeneration ?? 0).DefaultIfEmpty().Max();
        return commits.Select((commit, index) =>
        {
            var ancestor = isAncestor(commit.Sha);
            var rule = ancestor ? CommitIntegrationRules.Ancestor : CommitIntegrationRules.Missing;
            string? replacement = null;
            if (TaskCommitSupersession.IsReplaced(commit)
                || commit.DeliveryGeneration is { } generation && generation < currentGeneration)
            {
                rule = CommitIntegrationRules.Superseded;
                replacement = commit.SupersededBySha
                    ?? commits.LastOrDefault(candidate => candidate.DeliveryGeneration == currentGeneration)?.Sha;
            }
            else if (!ancestor && commit.DeliveryGeneration is null)
            {
                // A known current generation never uses legacy equivalence heuristics.
                // Older attempt identifiers are producer identities. Unnumbered
                // history still needs path coverage or exact content proof.
                var successor = commits.Skip(index + 1).FirstOrDefault(candidate =>
                    !TaskCommitSupersession.IsReplaced(candidate)
                    && CanReplaceLegacy(commit, candidate)
                    && isAncestor(candidate.Sha)
                    && CoversPaths(commit.Files, candidate.Files));
                var unscoped = string.IsNullOrWhiteSpace(commit.RunAttemptId)
                    && string.IsNullOrWhiteSpace(commit.ResultSha);
                var earlierAttempt = commits.Skip(index + 1).Any(candidate =>
                    !string.IsNullOrWhiteSpace(commit.RunAttemptId)
                    && !string.IsNullOrWhiteSpace(candidate.RunAttemptId)
                    && !SameKnownAttempt(commit, candidate));
                if ((unscoped || successor is null && earlierAttempt) && isContentIntegrated(commit.Sha))
                {
                    rule = CommitIntegrationRules.IntegratedByContent;
                }
                else if (successor is not null)
                {
                    rule = CommitIntegrationRules.Superseded;
                    replacement = successor.Sha;
                }
            }

            if (rule == CommitIntegrationRules.Missing
                && TaskIntegrationStatusService.IsZeroFileLifecycleMarker(commit))
                rule = CommitIntegrationRules.LifecycleMarker;

            return new TaskRepositoryCommitMembership
            {
                Sha = commit.Sha,
                Repository = commit.Repository,
                DeliveryGeneration = commit.DeliveryGeneration,
                IntegrationRule = rule,
                SupersededBySha = replacement,
                OnIntegrationBranch = ancestor || rule == CommitIntegrationRules.IntegratedByContent,
                OnReleaseBranch = isReleased(commit.Sha),
            };
        }).ToList();
    }

    private static bool CanReplaceLegacy(TaskCommitInfo older, TaskCommitInfo newer)
    {
        if (SameKnownAttempt(older, newer)) return false;
        if (!string.IsNullOrWhiteSpace(older.RunAttemptId))
            return !string.IsNullOrWhiteSpace(newer.RunAttemptId);
        if (!string.IsNullOrWhiteSpace(older.ResultSha))
            return !string.IsNullOrWhiteSpace(newer.ResultSha);
        return true;
    }

    private static bool SameKnownAttempt(TaskCommitInfo older, TaskCommitInfo newer)
        => !string.IsNullOrWhiteSpace(older.RunAttemptId)
           && string.Equals(older.RunAttemptId, newer.RunAttemptId, StringComparison.OrdinalIgnoreCase)
           || !string.IsNullOrWhiteSpace(older.ResultSha)
           && string.Equals(older.ResultSha, newer.ResultSha, StringComparison.OrdinalIgnoreCase);

    private static bool CoversPaths(IReadOnlyList<string> older, IReadOnlyList<string> newer)
    {
        // Partial overlap does not prove that the older delivery was replaced.
        var paths = newer.Select(NormalizePath).ToHashSet(StringComparer.Ordinal);
        return older.Count > 0 && older.All(path => paths.Contains(NormalizePath(path)));
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }
}
