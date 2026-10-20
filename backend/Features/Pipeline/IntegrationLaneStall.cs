using AgentStudio.Git;

namespace AgentStudio.Pipeline;

/// <summary>
/// One merge refused because the integration lane diverged from origin while it
/// still carries genuinely unpublished work (AGT-3215). <paramref name="Commits"/>
/// are the classified lane commits; the unpublished ones block integration.
/// </summary>
public sealed record IntegrationLaneStall(
    string Project,
    string? WatchPath,
    string Branch,
    string JobId,
    IReadOnlyList<LaneCommitClassification> Commits,
    DateTime DetectedAtUtc);

/// <summary>Receiver of the integration-lane stall event (implemented by the pipeline health sensor).</summary>
public interface IIntegrationLaneStallAlarm
{
    void IntegrationLaneStalled(IntegrationLaneStall stall);
    void IntegrationLaneRecovered(string project, string branch, DateTime recoveredAtUtc);
}

/// <summary>
/// Answers lane-delivery lineage from the board: a card owns a lane merge when
/// its provenance, integration records, review subject, or attributed commits
/// name the merge or its delivery. The card's current delivery is the one its
/// review subject carries now. Jobs are read once, on the first question.
/// </summary>
public sealed class TaskBoardLaneDeliveryLineage(Func<IReadOnlyList<TaskInfo>> jobs) : IIntegrationLaneDeliveryLineage
{
    private readonly Lazy<IReadOnlyList<TaskInfo>> _jobs = new(jobs);

    public LaneDeliveryCard? Find(string laneCommitSha, string deliverySha)
    {
        foreach (var job in _jobs.Value)
        {
            var current = AcceptedIntegrationPublishedDeliveryGuard.CurrentDeliverySha(job);
            if (Same(current, deliverySha) || Owns(job, laneCommitSha, deliverySha))
                return new LaneDeliveryCard(job.Id, current);
        }
        return null;
    }

    private static bool Owns(TaskInfo job, string laneCommitSha, string deliverySha)
        => Same(job.Provenance?.Merge?.MergeCommit, laneCommitSha)
           || job.IntegrationRecords.Any(record =>
               Same(record.IntegrationSha, laneCommitSha)
               || Same(record.SourceSha, deliverySha)
               || record.CommitShas.Any(sha => Same(sha, deliverySha)))
           || DeliveryRefResolver.AttributedCommitShas(job.FolderPath).Any(sha => Same(sha, deliverySha));

    private static bool Same(string? left, string right)
        => !string.IsNullOrWhiteSpace(left) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
