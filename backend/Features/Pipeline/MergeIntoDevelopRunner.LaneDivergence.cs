using AgentStudio.Git;

namespace AgentStudio.Pipeline;

public sealed partial class MergeIntoDevelopRunner
{
    /// <summary>
    /// Card truth for a diverged lane (AGT-3215): which card merged a lane
    /// delivery and which delivery it carries now. Null without a board, so a
    /// lane delivery can then only be dropped when origin contains it.
    /// </summary>
    private IIntegrationLaneDeliveryLineage? LaneDeliveryLineage(string project, string? watchPath)
    {
        if (_taskScanner is null) return null;
        var scanner = _taskScanner;
        return new TaskBoardLaneDeliveryLineage(() => scanner.ScanAllAutomationJobsWithArchive()
            .Where(job => string.Equals(job.ProjectName, project, StringComparison.OrdinalIgnoreCase)
                || (watchPath is not null && WatchPathComparison.PathsEqual(job.WatchPath, watchPath)))
            .ToArray());
    }

    /// <summary>
    /// Raises the typed lane stall event when genuinely unpublished lane work
    /// blocks this card, and clears it once the lane synchronizes again.
    /// </summary>
    private void ReportLaneSync(
        string project,
        string? watchPath,
        string branch,
        string jobId,
        IntegrationBranchSyncResult synchronized)
    {
        if (_laneStallAlarm is null) return;
        var now = _gateTime.GetUtcNow().UtcDateTime;
        if (synchronized.Success)
        {
            _laneStallAlarm.IntegrationLaneRecovered(project, branch, now);
            return;
        }
        if (synchronized.Outcome != IntegrationBranchSyncOutcome.Diverged
            || synchronized.LaneCommits is not { Count: > 0 } commits)
            return;
        _laneStallAlarm.IntegrationLaneStalled(new IntegrationLaneStall(
            project, watchPath, branch, jobId, commits, now));
    }
}
