using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Auto-review status snapshot. The legacy backend's
/// <c>AutoReviewStatusSnapshot</c> is an in-memory tick counter fed
/// exclusively by the legacy <c>ReviewDecisionOrchestrator</c> hosted-service
/// loop, which has not moved to the standalone Task Server - porting just the
/// counters would report all-zero forever. Instead this reads the Task
/// Server's own durable state directly: which tasks are actually sitting in
/// the <see cref="StudioTaskLanes.AutoReview"/> lane right now. Tick counters
/// (accept/reissue/escalate/aspectsRun) stay at zero until the orchestrator
/// tick loop itself is ported; <c>pending</c> and <c>activeJobs</c> are real.
/// </summary>
public sealed partial class TaskServerStore
{
    public async Task<StudioAutoReviewStatus> GetAutoReviewStatusAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT p.name, t.task_key, t.updated_at
              FROM tasks t
              JOIN projects p ON p.id = t.project_id
             WHERE t.state = $state
             ORDER BY t.updated_at DESC;
            """, ("$state", StudioTaskLanes.AutoReview));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var active = new List<StudioAutoReviewActivity>();
        while (await reader.ReadAsync(ct))
            active.Add(new StudioAutoReviewActivity(
                reader.GetString(0), reader.GetString(1), "processing", Parse(reader.GetString(2))));

        return new StudioAutoReviewStatus(
            LastTickAt: null, Accept: 0, Reissue: 0, Escalate: 0, AspectsRun: 0,
            Pending: active.Count, CurrentJob: null, CurrentProject: null, ActiveJobs: active);
    }
}
