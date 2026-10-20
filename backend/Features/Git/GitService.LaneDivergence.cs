namespace AgentStudio.Git;

public partial class GitService
{
    /// <summary>
    /// The ref that keeps a diverged lane tip before the lane restarts at origin
    /// (AGT-3215). Keyed by the dropped tip, so a repeated restart of the same tip
    /// reuses its backup and never needs a clock.
    /// </summary>
    public static string IntegrationLaneBackupRef(string branch, string laneTip)
        => $"refs/agent-studio/integration-backup/{branch}/{laneTip}";

    /// <summary>
    /// Heals a lane that diverged from the published branch when origin already
    /// holds every unpublished delivery, or a newer delivery of the same card.
    /// The lane tip is kept under <see cref="IntegrationLaneBackupRef"/> and one
    /// receipt line names the dropped commits. Genuinely unpublished work stays
    /// on the lane and is returned as <see cref="IntegrationBranchSyncOutcome.Diverged"/>
    /// with the classified commits, so the caller can raise the stall event.
    /// </summary>
    private IntegrationBranchSyncResult ResolveLaneDivergence(
        string repoRoot,
        string integrationBranch,
        string publishedRef,
        string laneTip,
        string publishedTip,
        IIntegrationLaneDeliveryLineage? lineage)
    {
        var lane = IntegrationLaneRef(integrationBranch);
        var decision = IntegrationLaneDivergencePolicy.Decide(
            UnpublishedLaneCommitFacts(repoRoot, laneTip, publishedTip, lineage));

        if (decision.Action == LaneDivergenceAction.Stall)
        {
            var unpublished = decision.Unpublished
                .Select(commit => commit.DeliverySha is null
                    ? commit.CommitSha
                    : $"{commit.CommitSha} (delivery {commit.DeliverySha})")
                .ToArray();
            _logger.LogWarning(
                "Integration lane {Lane} ({LaneSha}) at {Path} diverged from {Published} ({PublishedSha}); unpublished commits: {Unpublished}",
                lane,
                AbbreviateSha(laneTip),
                repoRoot,
                publishedRef,
                AbbreviateSha(publishedTip),
                string.Join(", ", unpublished));
            return new(
                IntegrationBranchSyncOutcome.Diverged,
                $"Integration branch '{integrationBranch}' diverged from origin. " +
                $"The integration lane {lane} still carries unpublished commits: {string.Join(", ", unpublished)}.",
                decision.Commits);
        }

        var backup = IntegrationLaneBackupRef(integrationBranch, laneTip);
        if (GetBranchTip(repoRoot, backup) is null)
        {
            var (_, backupError, backupCode) = RunGitArgs(repoRoot, "update-ref", backup, laneTip, string.Empty);
            if (backupCode != 0)
                return new(
                    IntegrationBranchSyncOutcome.Error,
                    $"Integration lane of '{integrationBranch}' could not be backed up before its restart at origin: {backupError.Trim()}");
        }
        if (UpdateLaneRef(repoRoot, integrationBranch, publishedTip, laneTip) is { } resetError)
            return new(IntegrationBranchSyncOutcome.Error, resetError);

        _logger.LogInformation(
            "Integration lane {Lane} restarted at {Published} {PublishedSha}; backup {Backup}; dropped: {Dropped}",
            lane,
            publishedRef,
            AbbreviateSha(publishedTip),
            backup,
            decision.Commits.Count == 0 ? "none" : string.Join("; ", decision.Commits.Select(commit => commit.Describe())));
        return new(IntegrationBranchSyncOutcome.FastForwarded, null, decision.Commits);
    }

    private IReadOnlyList<LaneCommitFacts> UnpublishedLaneCommitFacts(
        string repoRoot,
        string laneTip,
        string publishedTip,
        IIntegrationLaneDeliveryLineage? lineage)
    {
        var (baseSha, _, baseCode) = RunGitArgs(repoRoot, "merge-base", laneTip, publishedTip);
        if (baseCode != 0) return [new LaneCommitFacts(laneTip, null, false)];
        var (commits, _, listCode) = RunGitArgs(
            repoRoot, "rev-list", "--first-parent", "--reverse", $"{baseSha.Trim()}..{laneTip}");
        if (listCode != 0) return [new LaneCommitFacts(laneTip, null, false)];

        var facts = new List<LaneCommitFacts>();
        foreach (var commit in commits.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (parents, _, parentCode) = RunGitArgs(repoRoot, "show", "-s", "--format=%P", commit);
            var pair = parents.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parentCode != 0 || pair.Length != 2)
            {
                facts.Add(new LaneCommitFacts(commit, null, false));
                continue;
            }

            var delivery = pair[1];
            if (IsDeliveryInPublished(repoRoot, delivery, publishedTip))
            {
                facts.Add(new LaneCommitFacts(commit, delivery, true));
                continue;
            }

            var card = lineage?.Find(commit, delivery);
            var successor = ReviewSubjectStore.IsValidResultSha(card?.CurrentDeliverySha)
                            && !string.Equals(card!.CurrentDeliverySha, delivery, StringComparison.OrdinalIgnoreCase)
                ? card.CurrentDeliverySha
                : null;
            facts.Add(new LaneCommitFacts(
                commit,
                delivery,
                false,
                card?.CardKey,
                successor,
                successor is not null && IsDeliveryInPublished(repoRoot, successor, publishedTip)));
        }
        return facts;
    }

    /// <summary>Ancestry, full-tree containment, or patch equivalence of every delivery commit.</summary>
    private bool IsDeliveryInPublished(string repoRoot, string deliverySha, string publishedTip)
        => IsDeliveryPublished(repoRoot, deliverySha, publishedTip)
           || IsDeliveryPatchEquivalent(repoRoot, deliverySha, publishedTip);

    /// <summary>
    /// <c>git cherry</c> lists each delivery commit origin lacks with <c>+</c>
    /// and each patch-equivalent one with <c>-</c>. It skips merge commits, and
    /// with them any conflict resolution, so a delivery that carries a merge is
    /// left to the tree check of <see cref="IsDeliveryPublished"/>.
    /// </summary>
    private static bool IsDeliveryPatchEquivalent(string repoRoot, string deliverySha, string publishedTip)
    {
        var (merges, _, mergesCode) = RunGitArgs(
            repoRoot, "rev-list", "--merges", "--count", $"{publishedTip}..{deliverySha}");
        if (mergesCode != 0 || merges.Trim() != "0") return false;
        var (cherry, _, cherryCode) = RunGitArgs(repoRoot, "cherry", publishedTip, deliverySha);
        if (cherryCode != 0) return false;
        var lines = cherry.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 && lines.All(line => line.StartsWith('-'));
    }
}
