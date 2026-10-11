using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.Pipeline;

public static class BatchGateStateSnapshotReader
{
    public static object Read(BatchGateStore store, string project,
        BatchGateFormationOptions settings, int history = 10,
        IReadOnlyDictionary<string, double>? risks = null)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = store.ListPending().Where(item =>
                string.Equals(item.Subject.Project, project, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Subject.EnqueueSequence).ToArray();
        var plan = !settings.Enabled
            ? new BatchPlanDecision("disabled", "batch gate disabled", null, [], 0, [])
            : store.LatestPlanDecision(project)
                ?? new BatchPlanDecision(pending.Length == 0 ? "idle" : "wait",
                    pending.Length == 0 ? "no eligible cards" : "awaiting planner tick",
                    null, [], 0, []);
        var manifests = store.ListManifests()
            .Where(item => string.Equals(item.Scope.Project, project, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.ClosedAtUtc)
            .Take(Math.Clamp(history, 0, 100)).ToArray();
        var batches = manifests.Select(manifest =>
        {
            var phase = store.LatestState(manifest.BatchId);
            return new
            {
                manifest.BatchId,
                manifest.ParentBatchId,
                phase = phase.Phase.ToString().ToLowerInvariant(),
                phaseReason = phase.Reason,
                manifest.BaseSha,
                candidateSha = phase.CandidateSha,
                members = manifest.Members.Select(member => new
                {
                    member.TaskKey,
                    replay = store.TryReadReplay(manifest.BatchId, member.TaskKey),
                    released = store.ReadPublication(manifest.BatchId) is { } publication
                        && store.TryReadMember(manifest.BatchId, member.TaskKey,
                            publication.BatchRunId) is not null,
                }).ToArray(),
                runs = store.ListRuns(manifest.BatchId).Select(run =>
                {
                    var verdict = store.ReadVerdict(manifest.BatchId, run.BatchRunId);
                    return new
                    {
                        run.BatchRunId, run.Host,
                        outcome = verdict?.Outcome,
                        classification = verdict?.Classification,
                        startedAtUtc = (DateTimeOffset?)run.StartedAtUtc,
                        finishedAtUtc = verdict?.CompletedAtUtc,
                    };
                }).ToArray(),
                publication = store.ReadPublication(manifest.BatchId),
            };
        }).ToArray();
        var members = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var item in pending)
            members[item.Subject.TaskKey] = new { status = "pending", batchId = (string?)null,
                phase = (string?)null, position = (int?)null, size = (int?)null };
        foreach (var manifest in manifests.Reverse())
        {
            var phase = store.LatestState(manifest.BatchId).Phase;
            var publication = store.ReadPublication(manifest.BatchId);
            for (var index = 0; index < manifest.Members.Count; index++)
            {
                var member = manifest.Members[index];
                var released = publication is not null && store.TryReadMember(
                    manifest.BatchId, member.TaskKey, publication.BatchRunId) is not null;
                members[member.TaskKey] = new
                {
                    status = released ? "published"
                        : phase == BatchPhase.Abandoned ? "fallback" : "in-batch",
                    batchId = manifest.BatchId,
                    phase = phase.ToString().ToLowerInvariant(),
                    position = (int?)(index + 1),
                    size = (int?)manifest.Members.Count,
                };
            }
        }
        return new
        {
            plan,
            pending = pending.Select(item => new
            {
                item.Subject.TaskKey,
                item.ReviewAttemptId,
                item.Subject.ResultSha,
                waitMinutes = Math.Max(0, (now - item.EnqueuedAtUtc).TotalMinutes),
                risk = risks?.GetValueOrDefault(item.Subject.TaskKey)
                    ?? (item.Subject.DocumentationOnly ? .08 : .2),
                exclusion = (string?)null,
            }).ToArray(),
            batches,
            members,
        };
    }
}
