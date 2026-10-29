using AgentStudio.Shared;

namespace AgentStudio.Runner;

/// <summary><c>NotCounted</c>: neither a pass nor a failure; the next engine decision takes its place.</summary>
public enum ApplierCanaryVerdict { Verified, Waiting, Missing, NotCounted }

/// <summary>When a watched engine decision counts as applied, still applying, missing its effect, or not at all.</summary>
public static class ApplierCanaryPolicy
{
    /// <summary>
    /// A verified row check counts. A refusal by design (a card that left the
    /// source lane by a move this decision did not write, a gate decision whose
    /// card moved on before the handler's next pass), a failed probe, an ask, a
    /// checkpoint and a handoff to a review-input decision or an agent round do
    /// not count. A check that waits on something other than the applier (an
    /// integration in flight, a card missing from the snapshot) waits until the
    /// apply deadline and then does not count. An occurrence the applier has
    /// not completed waits until the apply deadline after the observation, and
    /// at least one effect window after its integration ended; a
    /// completed one waits for its effects for the effect window after
    /// CompletedAt, or for the apply deadline when only an effect of a later
    /// pass is missing. Every span starts no earlier than
    /// <paramref name="processStartedAt"/>, so downtime is left out.
    /// </summary>
    public static ApplierCanaryVerdict Judge(EngineEffectCheck check, DateTime observedAt, DateTime now,
        TimeSpan effectWindow, TimeSpan applyDeadline, DateTime? processStartedAt = null)
    {
        if (EngineEffectBasis.Refused(check.Basis)) return ApplierCanaryVerdict.NotCounted;
        if (check.Verified && check.CompletedAt is not null)
            return Counts(check) ? ApplierCanaryVerdict.Verified : ApplierCanaryVerdict.NotCounted;
        var started = processStartedAt ?? DateTime.MinValue;
        var observed = observedAt > started ? observedAt : started;
        if (check.WaitingOn is not null)
        {
            var from = check.CompletedAt is { } completed && completed > observed ? completed : observed;
            return now - from >= applyDeadline ? ApplierCanaryVerdict.NotCounted : ApplierCanaryVerdict.Waiting;
        }
        if (check.CompletedAt is not { } completedAt)
        {
            var ended = check.IntegrationEndedAt is { } at && at > started ? at : started;
            return now - observed >= applyDeadline
                && (check.IntegrationEndedAt is null || now - ended >= effectWindow)
                ? ApplierCanaryVerdict.Missing : ApplierCanaryVerdict.Waiting;
        }
        var allowed = check.AwaitingDownstream ? applyDeadline : effectWindow;
        var effectsFrom = completedAt > started ? completedAt : started;
        return now - effectsFrom >= allowed ? ApplierCanaryVerdict.Missing : ApplierCanaryVerdict.Waiting;
    }

    /// <summary>
    /// Only a verified row check of a completed occurrence counts toward a
    /// canary; effects the applier left before it completed prove nothing yet.
    /// </summary>
    public static bool Counts(EngineEffectCheck check)
        => check.Verified && check.Basis == EngineEffectBasis.Row && check.CompletedAt is not null;
}

/// <summary>
/// AGT-3218: checks the first engine decisions after a profile switch with
/// <see cref="EngineApplierEffectProbe"/>. When the required decisions show
/// their effects, the canary passes on this instance. When one decision still
/// lacks an effect after its deadline, the rollout rolls the profile back to
/// the monolith (unless an operator forced the engine) and the operator feed
/// gets an alert that names the card, the evidence and the manual redrive.
/// After a start it judges nothing until the recovery service ran one pass
/// over the applying occurrences, and it counts every deadline from the start
/// at the earliest, so a restart is no missing effect.
/// </summary>
public sealed class EngineApplierCanaryService(
    OrchestrationRolloutStore rollout,
    MonolithOrchestrationStore store,
    EngineApplierEffectProbe probe,
    TaskScannerService scanner,
    OrchestratorLog operatorLog,
    TimeProvider time,
    ILogger<EngineApplierCanaryService> logger) : BackgroundService
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private readonly DateTime _startedAt = time.GetUtcNow().UtcDateTime;
    private readonly int _recoveryPassesAtStart = store.ApplyingRecoveryPasses;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await scanner.WaitForFirstPublicationAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { CheckOnce(); }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "applier-canary-check-incomplete");
                }
                await Task.Delay(CheckInterval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
    }

    /// <summary>
    /// Checks every watched decision once; returns the number of canaries it
    /// failed. A failure on one observation leaves the others to be checked.
    /// </summary>
    internal int CheckOnce()
    {
        // The canary status shows the wait, so a recovery loop that never
        // finishes a pass is visible instead of a canary that never judges.
        rollout.AwaitingRecoveryPass = store.ApplyingRecoveryPasses <= _recoveryPassesAtStart;
        if (rollout.AwaitingRecoveryPass) return 0;
        var failed = 0;
        foreach (var (profile, observation) in rollout.PendingCanaryObservations())
        {
            try
            {
                if (CheckObservation(profile, observation)) failed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "applier-canary-observation-incomplete profile={Profile} run={RunId}",
                    profile, observation.Occurrence);
            }
        }
        return failed;
    }

    private bool CheckObservation(string profile, OrchestrationCanaryObservation observation)
    {
        var occurrence = store.GetOccurrence(observation.Occurrence);
        var check = Probe(observation, occurrence);
        var verdict = ApplierCanaryPolicy.Judge(check, observation.ObservedAt, time.GetUtcNow().UtcDateTime,
            rollout.CanaryWindow, rollout.ApplyDeadline, _startedAt);
        if (verdict != ApplierCanaryVerdict.Missing)
        {
            rollout.RecordEffectCheck(profile, check, verdict);
            if (check.Basis == EngineEffectBasis.ProbeFailed) AlertProbe(profile, observation, occurrence, check);
            return false;
        }
        if (rollout.FailCanary(profile, check) is not { } status) return false;
        Alert(profile, observation, occurrence, check, status);
        return true;
    }

    private EngineEffectCheck Probe(OrchestrationCanaryObservation observation,
        MonolithOrchestrationOccurrence? occurrence)
    {
        if (occurrence is null)
            return Unverified(observation, "occurrence", "The occurrence journal has no such occurrence.");
        var since = observation.ObservedAt > _startedAt ? observation.ObservedAt : _startedAt;
        try { return probe.Check(occurrence, since, store.IsApplying(occurrence.Run.RunId)); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A probe defect is no applier defect: the decision does not count,
            // and the operator feed gets the exception.
            logger.LogWarning(exception, "applier-canary-probe-failed run={RunId}", observation.Occurrence);
            return Unverified(observation, "probe", "The effect probe could not read the decision: "
                + exception.Message, EngineEffectBasis.ProbeFailed);
        }
    }

    private EngineEffectCheck Unverified(OrchestrationCanaryObservation observation, string missing, string evidence,
        string basis = EngineEffectBasis.Row)
        => new(observation.Occurrence, observation.Action, false, [missing], evidence,
            time.GetUtcNow().UtcDateTime, basis);

    private void AlertProbe(string profile, OrchestrationCanaryObservation observation,
        MonolithOrchestrationOccurrence? occurrence, EngineEffectCheck check)
    {
        var task = Card(occurrence);
        var card = task?.Key ?? task?.Id ?? occurrence?.Run.TaskId ?? "an unknown card";
        Append(profile, observation, task, new OrchestratorLogEntry
        {
            Ts = time.GetUtcNow().UtcDateTime,
            Kind = OrchestratorLogKinds.Alert,
            Topic = $"applier-canary-probe:{profile}:{observation.Occurrence}",
            JobId = task?.Id,
            Summary = $"Applier canary could not check engine decision {check.Action} on {card}; it does not count.",
            Reasoning = $"{check.Evidence} Occurrence {observation.Occurrence} was decided at "
                + $"{observation.ObservedAt:u}. A probe failure is no applier defect, so {profile} keeps its "
                + "state and the next engine decision takes the place of this one. Check the card's effects by hand.",
        });
    }

    private TaskInfo? Card(MonolithOrchestrationOccurrence? occurrence)
        => occurrence is null ? null : scanner.ScanAllJobs().FirstOrDefault(candidate =>
            candidate.ProjectName == occurrence.Run.ProjectId
            && (candidate.Id == occurrence.Run.TaskId || candidate.TaskKey == occurrence.Run.TaskId));

    private void Alert(string profile, OrchestrationCanaryObservation observation,
        MonolithOrchestrationOccurrence? occurrence, EngineEffectCheck check, OrchestrationProfileStatus status)
    {
        logger.LogError("applier-canary-failed profile={Profile} run={RunId} action={Action} missing={Missing} state={State}",
            profile, observation.Occurrence, check.Action, string.Join(",", check.Missing), status.State);
        var task = Card(occurrence);
        var card = task?.Key ?? task?.Id ?? occurrence?.Run.TaskId ?? "an unknown card";
        var outcome = status.State == "rolled-back"
            ? $"The monolith now decides pending {profile} occurrences. A direct switch back to engine needs a "
              + "new canary on this instance; fix the applier, then run shadow and the canary again."
            : $"{profile} stays on the engine because an operator forced it; roll it back by hand if the applier is at fault.";
        Append(profile, observation, task, new OrchestratorLogEntry
        {
            Ts = time.GetUtcNow().UtcDateTime,
            Kind = OrchestratorLogKinds.Alert,
            Topic = $"applier-canary:{profile}:{observation.Occurrence}",
            JobId = task?.Id,
            Summary = (status.State == "rolled-back"
                    ? $"Applier canary rolled {profile} back to the monolith: "
                    : $"Applier canary failed for {profile}: ")
                + $"engine decision {check.Action} on {card} is missing {string.Join(", ", check.Missing)}.",
            Reasoning = $"{check.Evidence} Occurrence {observation.Occurrence} was decided at "
                + $"{observation.ObservedAt:u}"
                + (check.CompletedAt is { } completedAt ? $" and completed at {completedAt:u}" : " and never completed")
                + ". Shadow mode compares decision receipts and never runs the applier, so the first engine "
                + $"decisions are checked after a switch. {outcome} {Redrive(occurrence, task, card, check)}",
        });
    }

    // Once per topic and feed: the card's project, or every project when the card is unknown.
    private void Append(string profile, OrchestrationCanaryObservation observation, TaskInfo? task,
        OrchestratorLogEntry entry)
    {
        var paths = task is not null ? [task.WatchPath]
            : scanner.GetWatchPaths().Select(item => item.Path).Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
        foreach (var path in paths)
        {
            try
            {
                if (operatorLog.Read(path).Any(existing => existing.Topic == entry.Topic)) continue;
                operatorLog.Append(path, entry);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogError(exception, "applier-canary-alert-unwritten profile={Profile} run={RunId} path={Path}",
                    profile, observation.Occurrence, path);
            }
        }
    }

    /// <summary>
    /// The rollback re-decides only pending and leased occurrences, so the card
    /// of the failed decision needs a person.
    /// </summary>
    internal static string Redrive(MonolithOrchestrationOccurrence? occurrence, TaskInfo? task, string card,
        EngineEffectCheck check)
    {
        if (occurrence is null)
            return $"Manual redrive: find {card} on the board and move it to the lane its review needs.";
        var lane = task?.State ?? occurrence.ExpectedSourceState;
        var decided = EngineApplierContract.Find(check.Action)?.Lane is { } rowLane
            && rowLane != EngineApplierContract.StageLane ? rowLane : null;
        if (occurrence.Run.Status != "completed")
            return $"Manual redrive: occurrence {occurrence.Run.RunId} is still {occurrence.Run.Status}, and the "
                + "recovery loop keeps retrying the engine decision every 30 seconds. If the card cannot take it, "
                + $"move {card} from {lane} to {(decided is null ? "Ready" : decided)} on the board.";
        var target = check.Missing.Contains(nameof(EngineApplierEffectKind.LaneMove)) && decided is not null
            ? decided : TaskStates.Ready;
        var batch = check.Missing.Contains(nameof(EngineApplierEffectKind.BatchAdmission))
            ? " For DeferToBatch the re-arm pass admits the review on its next pass; move the card only if it stays without a batch owner."
            : string.Empty;
        return $"Manual redrive: occurrence {occurrence.Run.RunId} is completed, so nothing decides it again. "
            + $"{card} is in {lane}; move it to {target} on the board{(target == TaskStates.Ready ? " to run it again" : "")}, "
            + $"or to {TaskStates.HumanReview} to decide it yourself.{batch}";
    }
}
