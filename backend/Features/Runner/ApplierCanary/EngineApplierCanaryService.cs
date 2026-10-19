using System.Text.Json;

namespace AgentStudio.Runner;

public enum ApplierCanaryVerdict { Verified, Waiting, Missing }

/// <summary>When a watched engine decision counts as applied, still applying, or missing its effect.</summary>
public static class ApplierCanaryPolicy
{
    public static ApplierCanaryVerdict Judge(bool verified, DateTime observedAt, DateTime now, TimeSpan window)
        => verified ? ApplierCanaryVerdict.Verified
            : now - observedAt >= window ? ApplierCanaryVerdict.Missing
            : ApplierCanaryVerdict.Waiting;
}

/// <summary>
/// AGT-3218: checks the first engine decisions after a profile switch with
/// <see cref="EngineApplierEffectProbe"/>. When the required decisions show
/// their effects, the canary passes on this instance. When one decision still
/// lacks an effect after the canary window, the rollout rolls the profile back
/// to the monolith and the operator feed gets an alert with the evidence.
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

    /// <summary>Checks every watched decision once; returns the number of profiles it rolled back.</summary>
    internal int CheckOnce()
    {
        var rolledBack = 0;
        foreach (var (profile, observation) in rollout.PendingCanaryObservations())
        {
            var check = Probe(observation);
            var verdict = ApplierCanaryPolicy.Judge(check.Verified, observation.ObservedAt,
                time.GetUtcNow().UtcDateTime, rollout.CanaryWindow);
            if (verdict != ApplierCanaryVerdict.Missing)
            {
                rollout.RecordEffectCheck(profile, check);
                continue;
            }
            if (!rollout.FailCanary(profile, check)) continue;
            rolledBack++;
            Alert(profile, observation, check);
        }
        return rolledBack;
    }

    private EngineEffectCheck Probe(OrchestrationCanaryObservation observation)
    {
        try
        {
            return store.GetOccurrence(observation.Occurrence) is { } occurrence
                ? probe.Check(occurrence)
                : Unverified(observation, "occurrence", "The occurrence journal has no such occurrence.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException)
        {
            logger.LogWarning(exception, "applier-canary-probe-failed run={RunId}", observation.Occurrence);
            return Unverified(observation, "probe", "The effect probe could not read the card: " + exception.Message);
        }
    }

    private EngineEffectCheck Unverified(OrchestrationCanaryObservation observation, string missing, string evidence)
        => new(observation.Occurrence, observation.Action, false, [missing], evidence,
            time.GetUtcNow().UtcDateTime);

    private void Alert(string profile, OrchestrationCanaryObservation observation, EngineEffectCheck check)
    {
        logger.LogError("applier-canary-failed profile={Profile} run={RunId} action={Action} missing={Missing}",
            profile, observation.Occurrence, check.Action, string.Join(",", check.Missing));
        var occurrence = store.GetOccurrence(observation.Occurrence);
        var task = occurrence is null ? null : scanner.ScanAllJobs().FirstOrDefault(candidate =>
            candidate.ProjectName == occurrence.Run.ProjectId
            && (candidate.Id == occurrence.Run.TaskId || candidate.TaskKey == occurrence.Run.TaskId));
        var paths = task is not null ? [task.WatchPath]
            : scanner.GetWatchPaths().Select(entry => entry.Path).Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
        var topic = $"applier-canary:{profile}:{observation.Occurrence}";
        var card = task?.Key ?? task?.Id ?? occurrence?.Run.TaskId ?? "an unknown card";
        foreach (var path in paths)
        {
            if (operatorLog.Read(path).Any(entry => entry.Topic == topic)) continue;
            operatorLog.Append(path, new OrchestratorLogEntry
            {
                Ts = time.GetUtcNow().UtcDateTime,
                Kind = OrchestratorLogKinds.Alert,
                Topic = topic,
                JobId = task?.Id,
                Summary = $"Applier canary rolled {profile} back to the monolith: engine decision "
                    + $"{check.Action} on {card} is missing {string.Join(", ", check.Missing)}.",
                Reasoning = $"{check.Evidence} Occurrence {observation.Occurrence} was decided at "
                    + $"{observation.ObservedAt:u} and showed no such effect within "
                    + $"{rollout.CanaryWindow.TotalSeconds:0} seconds. Shadow mode compares decision receipts "
                    + $"and never runs the applier, so the first engine decisions are checked after a switch. "
                    + $"The monolith now decides pending {profile} occurrences; fix the applier, then run "
                    + "shadow and the canary again.",
            });
        }
    }
}
