using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.Projects;

/// <summary>One bounded read of batch authority for the cycle-time build.</summary>
public sealed class BatchGateCycleEvidence
{
    private readonly IReadOnlyList<BatchGateManifest> _manifests;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<BatchGateRunRecord>> _runs;
    private readonly IReadOnlyDictionary<string, BatchGateState?> _states;
    private readonly IReadOnlyList<BatchGateExecutionFact> _executions;
    private readonly IReadOnlyList<BatchGatePendingRecord> _pending;
    private readonly BatchGateStore _store;

    private BatchGateCycleEvidence(BatchGateStore store, IReadOnlyList<BatchGateManifest> manifests,
        IReadOnlyDictionary<string, IReadOnlyList<BatchGateRunRecord>> runs,
        IReadOnlyDictionary<string, BatchGateState?> states,
        IReadOnlyList<BatchGateExecutionFact> executions,
        IReadOnlyList<BatchGatePendingRecord> pending)
    {
        _store = store; _manifests = manifests; _runs = runs; _states = states;
        _executions = executions; _pending = pending;
    }

    public static BatchGateCycleEvidence Load(BatchGateStore store, string project)
    {
        var manifests = store.ListManifests().Where(m => m.Scope.Project == project).ToList();
        var runs = new Dictionary<string, IReadOnlyList<BatchGateRunRecord>>(StringComparer.Ordinal);
        var states = new Dictionary<string, BatchGateState?>(StringComparer.Ordinal);
        foreach (var manifest in manifests)
        {
            runs[manifest.BatchId] = store.ListRuns(manifest.BatchId);
            try { states[manifest.BatchId] = store.LatestState(manifest.BatchId); }
            catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidOperationException)
            { states[manifest.BatchId] = null; }
        }
        return new BatchGateCycleEvidence(store, manifests, runs, states,
            store.ListExecutions(project), store.ListAllPending().Where(p => p.Subject.Project == project).ToList());
    }

    public IReadOnlyList<FlowSegment> ForTask(string taskKey, IReadOnlyList<string>? reviewAttemptIds = null,
        DateTime? terminalAt = null, IReadOnlyList<TimelineEvent>? ledger = null)
    {
        var result = new List<FlowSegment>();
        var relevant = _manifests.Where(m => m.Members.Any(member => member.TaskKey == taskKey))
            .OrderBy(m => m.ClosedAtUtc).ToList();
        DateTime? cursor = null;
        DateTime? reviewExit = null;
        for (var manifestIndex = 0; manifestIndex < relevant.Count; manifestIndex++)
        {
            var manifest = relevant[manifestIndex];
            var member = manifest.Members.First(m => m.TaskKey == taskKey);
            var reviewCompleted = member.ReviewCompletedAtUtc.UtcDateTime;
            // A later review is a new queue episode, even when an older batch
            // also held this card. Carry the cursor only within that episode.
            cursor = cursor is null || cursor < reviewCompleted ? reviewCompleted : cursor;
            reviewExit = NextReviewExit(reviewCompleted);
            var runs = _runs[manifest.BatchId].OrderBy(r => r.StartedAtUtc).ToList();
            foreach (var run in runs)
            {
                var execution = _executions.FirstOrDefault(e => e.BatchId == manifest.BatchId
                    && e.BatchRunId == run.BatchRunId);
                var start = execution?.StartedAtUtc.UtcDateTime ?? run.StartedAtUtc.UtcDateTime;
                if (start > cursor) Add(cursor.Value, start, "resourceWait", resource: "gateHost");
                if (execution is not null)
                {
                    var end = execution.CompletedAtUtc.UtcDateTime;
                    var queueEnd = start.AddMilliseconds(Math.Max(0, execution.QueueWaitMs));
                    if (queueEnd > end) queueEnd = end;
                    Add(start, queueEnd, "resourceWait", resource: "gateHost", executor: execution.Host);
                    Add(queueEnd, end, "active", step: "batchGate", executor: execution.Host);
                    cursor = end;
                }
                else
                {
                    var verdict = ReadVerdict(manifest.BatchId, run.BatchRunId);
                    var runState = _states[manifest.BatchId];
                    var terminalState = runState?.Phase is BatchPhase.Green or BatchPhase.Red
                        or BatchPhase.Published or BatchPhase.Abandoned or BatchPhase.Paused;
                    var end = verdict?.CompletedAtUtc.UtcDateTime
                        ?? (terminalState ? runState?.RecordedAtUtc.UtcDateTime : null) ?? start;
                    Add(start, end, "active", step: verdict is null ? "batchGateNoVerdict" : "batchGate",
                        executor: run.Host);
                    cursor = end;
                }
            }
            var state = _states[manifest.BatchId];
            if (state?.Phase is BatchPhase.Paused or BatchPhase.Abandoned && state.RecordedAtUtc.UtcDateTime > cursor)
            {
                Add(cursor!.Value, state.RecordedAtUtc.UtcDateTime,
                    "resourceWait", resource: "gateHost");
                cursor = state.RecordedAtUtc.UtcDateTime;
            }
            if (state?.Phase is BatchPhase.Paused or BatchPhase.Abandoned
                && manifestIndex == relevant.Count - 1
                && terminalAt is DateTime endAt && endAt > state.RecordedAtUtc.UtcDateTime)
                Add(state.RecordedAtUtc.UtcDateTime, endAt, "systemHold", kind: "batchDefect");
        }
        foreach (var id in reviewAttemptIds ?? [])
        {
            var pending = _pending.FirstOrDefault(p => p.ReviewAttemptId == id);
            foreach (var execution in _executions.Where(e => e.BatchId is null
                         && e.EvidencePath.Contains(id, StringComparison.Ordinal)))
            {
                var start = execution.StartedAtUtc.UtcDateTime;
                var claimAt = ledger?.Where(e => e.Kind == TimelineEventKinds.ReviewAttemptClaimed
                        && (e.Details?.GetValueOrDefault("attemptId") ?? e.RunId) == id)
                    .OrderBy(e => e.Ts).Select(e => (DateTime?)e.Ts).FirstOrDefault();
                reviewExit = NextReviewExit(pending?.Subject.ReviewCompletedAtUtc.UtcDateTime ?? claimAt ?? start);
                if (pending is not null && start > pending.Subject.ReviewCompletedAtUtc.UtcDateTime)
                    Add(pending.Subject.ReviewCompletedAtUtc.UtcDateTime, start,
                        "resourceWait", resource: "gateHost");
                var end = execution.CompletedAtUtc.UtcDateTime;
                var queueEnd = start.AddMilliseconds(Math.Max(0, execution.QueueWaitMs));
                if (queueEnd > end) queueEnd = end;
                Add(start, queueEnd, "resourceWait", resource: "gateHost", executor: execution.Host);
                Add(queueEnd, end, "active", step: "batchGate", executor: execution.Host);
            }
        }
        return result;

        DateTime? NextReviewExit(DateTime reviewCompleted) => ledger?
            .Where(e => e.Kind == TimelineEventKinds.LaneChanged && e.Ts >= reviewCompleted
                && e.Details?.GetValueOrDefault("from") == TaskStates.AutoReview
                && e.Details?.GetValueOrDefault("to") != TaskStates.AutoReview)
            .OrderBy(e => e.Ts).Select(e => (DateTime?)e.Ts).FirstOrDefault() ?? terminalAt;

        void Add(DateTime from, DateTime to, string @class, string? step = null,
            string? resource = null, string? kind = null, string? executor = null)
        {
            if (reviewExit is DateTime exit && to > exit) to = exit;
            if (to > from) result.Add(new FlowSegment(@class, step, resource, kind, from, to,
                (to - from).TotalSeconds, Executor: executor));
        }
    }

    private BatchGateRunVerdict? ReadVerdict(string batchId, string runId)
    {
        try { return _store.ReadVerdict(batchId, runId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException) { return null; }
    }
}
