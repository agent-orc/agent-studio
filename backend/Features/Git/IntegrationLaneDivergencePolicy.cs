namespace AgentStudio.Git;

/// <summary>Why one unpublished lane commit may or may not be dropped when the lane diverged.</summary>
public enum LaneCommitStatus
{
    /// <summary>Origin contains the delivery: as an ancestor, as a full tree, or patch-equivalent per <c>git cherry</c>.</summary>
    Contained,

    /// <summary>Origin contains a newer delivery of the same card, for example its rebased successor.</summary>
    Superseded,

    /// <summary>Genuinely unpublished work. It must never be dropped.</summary>
    Unpublished,
}

/// <summary>
/// Git and card facts for one first-parent commit the lane carries beyond the
/// merge base with the published branch. <paramref name="DeliverySha"/> is the
/// merged delivery (the second parent) and null for a commit that is no
/// delivery merge. <paramref name="SuccessorSha"/> is the card's current
/// delivery when it differs from the merged one.
/// </summary>
public sealed record LaneCommitFacts(
    string CommitSha,
    string? DeliverySha,
    bool DeliveryContained,
    string? CardKey = null,
    string? SuccessorSha = null,
    bool SuccessorPublished = false);

/// <summary>One classified lane commit, as logged in the receipt and raised in the stall event.</summary>
public sealed record LaneCommitClassification(
    string CommitSha,
    string? DeliverySha,
    LaneCommitStatus Status,
    string? CardKey,
    string? SuccessorSha)
{
    public string Describe()
    {
        var card = CardKey is null ? "" : $" {CardKey}";
        var delivery = DeliverySha is null ? "" : $" (delivery {Short(DeliverySha)})";
        return Status switch
        {
            LaneCommitStatus.Contained => $"{Short(CommitSha)}{card}{delivery} contained in origin",
            LaneCommitStatus.Superseded =>
                $"{Short(CommitSha)}{card}{delivery} superseded by published delivery {Short(SuccessorSha!)}",
            _ => DeliverySha is null
                ? $"{Short(CommitSha)} is not a delivery merge"
                : $"{Short(CommitSha)}{card}{delivery} is unpublished",
        };
    }

    private static string Short(string sha) => sha.Length > 9 ? sha[..9] : sha;
}

/// <summary>What a diverged integration lane must do.</summary>
public enum LaneDivergenceAction
{
    /// <summary>Every unpublished commit is in origin already: back up the lane and restart it at origin.</summary>
    RestartAtPublished,

    /// <summary>Genuinely unpublished work remains: keep the lane and raise the stall event.</summary>
    Stall,
}

public sealed record LaneDivergenceDecision(
    LaneDivergenceAction Action,
    IReadOnlyList<LaneCommitClassification> Commits)
{
    public IReadOnlyList<LaneCommitClassification> Unpublished =>
        Commits.Where(commit => commit.Status == LaneCommitStatus.Unpublished).ToArray();
}

/// <summary>
/// Pure policy for an integration lane that diverged from the published branch
/// (AGT-3215). An operator candidate pushed to origin while the lane held
/// merges of deliveries origin already carried, in the same or a rebased form,
/// used to fail every later merge with <c>branch-sync-failed</c> until a human
/// reset the lane. A lane commit may be dropped only when origin provably holds
/// its delivery or a newer delivery of the same card; anything else stalls.
/// </summary>
public static class IntegrationLaneDivergencePolicy
{
    public static LaneCommitClassification Classify(LaneCommitFacts facts)
    {
        var status = facts.DeliverySha is null
            ? LaneCommitStatus.Unpublished
            : facts.DeliveryContained
                ? LaneCommitStatus.Contained
                : IsSuperseded(facts)
                    ? LaneCommitStatus.Superseded
                    : LaneCommitStatus.Unpublished;
        return new LaneCommitClassification(
            facts.CommitSha,
            facts.DeliverySha,
            status,
            facts.CardKey,
            status == LaneCommitStatus.Superseded ? facts.SuccessorSha : null);
    }

    public static LaneDivergenceDecision Decide(IEnumerable<LaneCommitFacts> commits)
    {
        var classified = commits.Select(Classify).ToArray();
        var action = classified.Any(commit => commit.Status == LaneCommitStatus.Unpublished)
            ? LaneDivergenceAction.Stall
            : LaneDivergenceAction.RestartAtPublished;
        return new LaneDivergenceDecision(action, classified);
    }

    private static bool IsSuperseded(LaneCommitFacts facts)
        => !string.IsNullOrWhiteSpace(facts.CardKey)
           && !string.IsNullOrWhiteSpace(facts.SuccessorSha)
           && !string.Equals(facts.SuccessorSha, facts.DeliverySha, StringComparison.OrdinalIgnoreCase)
           && facts.SuccessorPublished;
}

/// <summary>The card a lane delivery belongs to and that card's current delivery.</summary>
public sealed record LaneDeliveryCard(string CardKey, string? CurrentDeliverySha);

/// <summary>
/// Card truth for lane deliveries. Merge commits on the lane carry no card
/// identity, so the host that owns the board answers which card merged a
/// delivery and which delivery that card carries now.
/// </summary>
public interface IIntegrationLaneDeliveryLineage
{
    LaneDeliveryCard? Find(string laneCommitSha, string deliverySha);
}
