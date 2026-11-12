using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Watcher;
using AgentStudio.Quota;
using AgentStudio.Registry;

namespace AgentStudio.Operator;

/// <summary>Reads one family of existing facts for the decision stream.</summary>
public interface IOperatorDecisionFactSource
{
    /// <summary>The event types this source reports; a failed read keeps their published keys.</summary>
    IReadOnlyList<string> Types { get; }

    /// <summary>The current facts, or null while the source has nothing to read yet; null keeps their published keys.</summary>
    IReadOnlyList<OperatorDecisionFact>? Collect(DateTime nowUtc);
}

/// <summary>Completed deploys already metered by workspace quota, without a new event store.</summary>
public sealed class OperatorDeployFactSource(WorkspaceQuotaService quota, ProjectRegistry projects)
    : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.DeployCompleted];

    public IReadOnlyList<OperatorDecisionFact> Collect(DateTime nowUtc)
        => quota.CompletedDeploys(nowUtc.AddDays(-7)).Select(deploy =>
        {
            var project = projects.FindById(deploy.ProjectId);
            return new OperatorDecisionFact(OperatorDecisionTypes.DeployCompleted,
                $"{OperatorDecisionTypes.DeployCompleted}|{deploy.ProjectId}|{deploy.JobId}|{deploy.CompletedAt:O}",
                project?.DisplayName, deploy.JobId, "Deployment completed.", deploy.CompletedAt!.Value, []);
        }).ToArray();
}

/// <summary>
/// Lanes, decision cards, quota waits and fallbacks from the published task
/// index. It reads the published generation only: a sweep never refreshes the
/// index, never starts a scan or a git process, and before the first
/// publication it reports nothing instead of an empty board.
/// </summary>
public sealed class OperatorTaskFactSource(
    Func<IReadOnlyList<TaskInfo>?> tasks,
    Func<TaskInfo, QuotaFallbackStatus?> fallbackOf) : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } =
    [
        OperatorDecisionTypes.EscalationOpened, OperatorDecisionTypes.HumanReviewReady,
        OperatorDecisionTypes.DecisionCardOpened, OperatorDecisionTypes.QuotaExhausted,
        OperatorDecisionTypes.QuotaFallbackActive,
    ];

    public IReadOnlyList<OperatorDecisionFact>? Collect(DateTime nowUtc)
        => tasks() is { } published ? OperatorDecisionFacts.FromTasks(published, fallbackOf) : null;
}

/// <summary>Open watcher stall cases (the watcher's own detector decides them).</summary>
public sealed class OperatorWatcherCaseFactSource(WatcherCaseStore cases, string? workspaceRoot) : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } =
        [OperatorDecisionTypes.BackendStalled, OperatorDecisionTypes.BatchGateStuck,
            OperatorDecisionTypes.ProgressIdleWithReady];

    public IReadOnlyList<OperatorDecisionFact>? Collect(DateTime nowUtc)
        => string.IsNullOrWhiteSpace(workspaceRoot) ? [] : OperatorDecisionFacts.FromWatcherCases(cases.All(workspaceRoot));
}

/// <summary>Red and isolating batches, and quarantined batch records, from the batch gate store.</summary>
public sealed class OperatorBatchGateFactSource(BatchGateStore store, BatchGatePilotService? pilot = null) : IOperatorDecisionFactSource
{
    // A closed manifest never changes, and a published or abandoned batch
    // never changes phase again: each is read once per folder.
    private readonly Dictionary<string, BatchGateManifest> _manifests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _settled = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Types { get; } =
        [OperatorDecisionTypes.BatchRed, OperatorDecisionTypes.BatchIsolating, OperatorDecisionTypes.BatchRecordUnreadable,
         OperatorDecisionTypes.BatchRunningWithoutGate];

    public IReadOnlyList<OperatorDecisionFact>? Collect(DateTime nowUtc)
    {
        var batches = new List<(BatchGateManifest, BatchGateState?)>();
        foreach (var folder in store.ListManifestFolders())
        {
            if (_settled.Contains(folder)) continue;
            if (!_manifests.TryGetValue(folder, out var manifest))
            {
                // AGT-3681: an unreadable manifest is quarantined by the store
                // and reported below; it is read again on the next sweep.
                if (!store.TryReadManifestIn(folder, out manifest)) continue;
                _manifests[folder] = manifest;
            }
            var state = LatestState(folder);
            if (state?.Reason == BatchGateStore.StateUnreadableReason) continue;
            if (state?.Phase is BatchPhase.Published or BatchPhase.Abandoned)
            {
                _settled.Add(folder);
                _manifests.Remove(folder);
                continue;
            }
            batches.Add((manifest, state));
        }
        var facts = OperatorDecisionFacts.FromBatches(batches).ToList();
        facts.AddRange(OperatorDecisionFacts.FromQuarantinedBatchRecords(store.ListQuarantined()));
        if (pilot is not null)
            foreach (var (manifest, state) in batches)
            {
                if (state is null || !pilot.RunningWithoutFlight(manifest, state, new DateTimeOffset(nowUtc))) continue;
                var delay = pilot.RecoveryStatus(manifest.BatchId);
                var cards = manifest.Members.Select(member => member.TaskKey).ToArray();
                facts.Add(new OperatorDecisionFact(OperatorDecisionTypes.BatchRunningWithoutGate,
                    $"{OperatorDecisionTypes.BatchRunningWithoutGate}|{manifest.BatchId}|{state.RecordedAtUtc:O}",
                    manifest.Scope.Project, cards.Length == 1 ? cards[0] : null,
                    $"Batch running without a live gate: recovery has not run ({delay.Reason}). "
                        + $"Next automatic retry at {delay.NextRetryAtUtc:HH:mm:ss} UTC.",
                    state.RecordedAtUtc.UtcDateTime,
                    [new OperatorEvidenceLink("batch-gate-state", "/api/projects/"
                        + Uri.EscapeDataString(manifest.Scope.Project) + "/batch-gate/state")], cards));
            }
        return facts;
    }

    private BatchGateState? LatestState(string folder)
    {
        // Unknown cannot establish a red or isolating decision fact.
        var state = store.LatestState(_manifests[folder].BatchId);
        return state.Phase == BatchPhase.Unknown ? null : state;
    }
}

/// <summary>Pending integration pushes past the batch gate's age alert.</summary>
public sealed class OperatorPendingPushFactSource(
    BatchIntegrationReservationService reservations,
    Func<string, string?> cardKeyOf) : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.PushPendingOverdue];

    public IReadOnlyList<OperatorDecisionFact>? Collect(DateTime nowUtc)
        => reservations.ProjectsWithPending()
            .SelectMany(project => OperatorDecisionFacts.FromPendingPushes(
                project, reservations.GetWaits(project), cardKeyOf))
            .ToArray();
}

/// <summary>Provider limits the runners recorded per CLI.</summary>
public sealed class OperatorProviderLimitFactSource(ProviderLimitRegistry limits) : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.QuotaExhausted];

    public IReadOnlyList<OperatorDecisionFact>? Collect(DateTime nowUtc)
        => OperatorDecisionFacts.FromProviderLimits(limits.Active(nowUtc));
}
