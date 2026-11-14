namespace AgentStudio.OrchestrationDecisions;

/// <summary>What a member's card-local ownership marker says about one batch.</summary>
public enum BatchMemberOwnership
{
    /// <summary>The card has no marker.</summary>
    None,

    /// <summary>The marker is the member's queued marker: same review attempt, no batch, no per-task gate state.</summary>
    Queued,

    /// <summary>The marker names this batch and no batch run: the batch owns the member.</summary>
    ThisBatch,

    /// <summary>The marker names this batch and its released run: the publication released the member.</summary>
    ReleasedByThisBatch,

    /// <summary>Another batch, another review attempt or the per-task gate owns the card.</summary>
    Elsewhere,
}

/// <summary>
/// One claimed member as the host observed it. <paramref name="Pending"/> is
/// true while the member's queued record is unresolved. <paramref name="Live"/>
/// is true while a flight of this host settles the member (a per-task gate) or
/// an engine decision on its release is pending.
/// </summary>
public sealed record BatchInvariantMemberFact(
    string TaskKey, string? ReviewAttemptId, BatchMemberDisposition Disposition,
    bool Pending, BatchMemberOwnership Ownership, bool Live = false);

/// <summary>
/// One batch as the host observed it. <paramref name="Phase"/> is null for a
/// closed manifest without a phase record. <paramref name="Live"/> is true
/// while a flight or a coordinator lease works on the batch's scope; a live
/// batch can pass through any of these states on purpose.
/// </summary>
public sealed record BatchInvariantFacts(
    string BatchId, BatchPhase? Phase, bool Diagnostic, bool Live,
    IReadOnlyList<BatchInvariantMemberFact> Members);

public enum BatchInvariantKind
{
    /// <summary>A formed batch that no flight runs and whose claims are incomplete.</summary>
    OrphanedFormedBatch,

    /// <summary>An abandoned or red batch whose member's ownership still names it.</summary>
    TerminalBatchOwnsMember,

    /// <summary>A published batch whose admitted member is still queued.</summary>
    PublishedBatchUnresolvedMember,
}

public enum BatchInvariantRepair
{
    /// <summary>Report only; the regular recovery of the next tick settles it.</summary>
    None,

    /// <summary>Write the manifest's first phase record: formed, every member claimed.</summary>
    InitializeFormed,

    /// <summary>Write the member's ownership marker naming the formed batch.</summary>
    ClaimMember,

    /// <summary>Abandon the batch and return its claimed members to the queue.</summary>
    AbandonBatch,

    /// <summary>Return the members the terminal batch still owns to the queue.</summary>
    ReleaseMember,

    /// <summary>Pause the red batch so the paused-batch route hands its cohort to the per-task gate.</summary>
    PauseBatch,
}

public sealed record BatchInvariantFinding(
    BatchInvariantKind Kind, string BatchId, string? TaskKey, string Detail,
    BatchInvariantRepair Repair);

/// <summary>
/// AGT-3563: the consistency rules that join a batch's phase to the members it
/// claimed. The host collects the facts each tick and on demand, reports the
/// findings, and applies the repair the rule names under the batch's
/// coordinator lease.
/// </summary>
public static class BatchGateInvariantPolicy
{
    public static IReadOnlyList<BatchInvariantFinding> Assess(BatchInvariantFacts facts)
    {
        if (facts.Live) return [];
        if (facts.Phase is null)
            return [new(BatchInvariantKind.OrphanedFormedBatch, facts.BatchId, null,
                "closed manifest has no phase record", BatchInvariantRepair.InitializeFormed)];
        return facts.Phase switch
        {
            BatchPhase.Formed => Formed(facts),
            BatchPhase.Abandoned => Owned(facts, BatchInvariantRepair.ReleaseMember),
            // A diagnostic child never owns members; its parent does.
            BatchPhase.Red when !facts.Diagnostic => Owned(facts, BatchInvariantRepair.PauseBatch),
            BatchPhase.Published => Published(facts),
            _ => [],
        };
    }

    private static IReadOnlyList<BatchInvariantFinding> Formed(BatchInvariantFacts facts)
    {
        var claimed = facts.Members.Where(member => member.Disposition == BatchMemberDisposition.Claimed).ToArray();
        // A diagnostic child forms and assembles inside its parent's isolation
        // flight; outside a live flight nothing will ever run it.
        if (facts.Diagnostic)
            return [new(BatchInvariantKind.OrphanedFormedBatch, facts.BatchId, null,
                "diagnostic batch outside its isolation", BatchInvariantRepair.AbandonBatch)];
        var findings = new List<BatchInvariantFinding>();
        foreach (var member in claimed.Where(member => !member.Live))
        {
            var detail = !member.Pending ? "member is no longer queued"
                : member.Ownership switch
                {
                    BatchMemberOwnership.ThisBatch => null,
                    BatchMemberOwnership.Queued => "member ownership does not name the batch",
                    BatchMemberOwnership.None => "member has no ownership marker",
                    _ => "another route owns the member",
                };
            if (detail is not null)
                findings.Add(new(BatchInvariantKind.OrphanedFormedBatch, facts.BatchId, member.TaskKey,
                    detail, BatchInvariantRepair.ClaimMember));
        }
        // One unclaimable member retires the snapshot; the planner forms the
        // survivors again. Only a member still on its queued marker is claimed.
        var unclaimable = claimed.Any(member => !member.Pending
            || member.Ownership is not (BatchMemberOwnership.ThisBatch or BatchMemberOwnership.Queued));
        return unclaimable
            ? findings.Select(finding => finding with { Repair = BatchInvariantRepair.AbandonBatch }).ToArray()
            : findings;
    }

    private static IReadOnlyList<BatchInvariantFinding> Owned(BatchInvariantFacts facts,
        BatchInvariantRepair repair)
        => facts.Members
            .Where(member => member is { Pending: true, Live: false, Ownership: BatchMemberOwnership.ThisBatch })
            .Select(member => new BatchInvariantFinding(BatchInvariantKind.TerminalBatchOwnsMember,
                facts.BatchId, member.TaskKey,
                $"{facts.Phase.ToString()!.ToLowerInvariant()} batch still owns the member", repair))
            .ToArray();

    private static IReadOnlyList<BatchInvariantFinding> Published(BatchInvariantFacts facts)
        => facts.Members
            .Where(member => member is { Pending: true, Live: false, Disposition: BatchMemberDisposition.Published })
            .Select(member => new BatchInvariantFinding(BatchInvariantKind.PublishedBatchUnresolvedMember,
                facts.BatchId, member.TaskKey, "published member is still queued", BatchInvariantRepair.None))
            .ToArray();
}
