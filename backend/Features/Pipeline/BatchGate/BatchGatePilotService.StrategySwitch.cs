namespace AgentStudio.Pipeline;

public sealed record GateStrategyCardTransition(string TaskKey, string Work, string Action);
public sealed record GateStrategyTransition(string PreviousStrategy, string Strategy,
    IReadOnlyList<GateStrategyCardTransition> Cards);

public sealed partial class BatchGatePilotService
{
    private bool UsesPostMerge(string project)
        => PostMergeModeService.Effective(_postMergeMode, project, _settings.Get(project))
            == IntegrationTestModes.PostMerge;

    /// <summary>Serializes the setting and queue handoff with batch formation.</summary>
    public async Task<GateStrategyTransition> SwitchTestingStrategyAsync(
        string project, string strategy, CancellationToken ct)
    {
        await _tick.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            strategy = TestingStrategies.Normalize(strategy)
                ?? throw new ArgumentException("Unknown testing strategy.", nameof(strategy));
            BatchGatePendingRecord[] activeFallbacks;
            lock (_fallbackFlights)
                activeFallbacks = _fallbackFlights.Values.Select(flight => flight.Pending).ToArray();
            var pending = _store.ListPending().Concat(activeFallbacks)
                .DistinctBy(item => item.ReviewAttemptId).Where(item => string.Equals(
                    item.Subject.Project, project, StringComparison.OrdinalIgnoreCase)).ToArray();
            var (previous, current) = _settings.SetTestingStrategy(project, strategy);
            if (previous == current) return new(previous, current, []);
            var cards = new List<GateStrategyCardTransition>();
            foreach (var item in pending)
            {
                var task = _scanner.FindJob(item.Subject.TaskKey, item.WatchPath);
                if (task is null || !Refresh(item).Subject.CurrentGeneration) continue;
                var ownership = BatchGateOwnershipStore.Read(task.FolderPath);
                var running = ownership?.BatchId is { } id
                    && _store.LatestState(id).Phase is (BatchPhase.Assembling or BatchPhase.Assembled
                        or BatchPhase.Running or BatchPhase.Green or BatchPhase.Publishing or BatchPhase.Red);
                var work = running ? "running-batch" : ownership?.FallbackGateActive == true
                    ? "per-task-gate" : "pending-batch";
                var action = UsesPostMerge(project)
                    ? running ? "retire-batch-before-publication-then-integrate"
                        : work == "per-task-gate" ? "cancel-per-task-gate-then-integrate-with-compile-check"
                        : "integrate-with-compile-check-tests-afterwards"
                    : "replan-under-new-strategy";
                cards.Add(new(item.Subject.TaskKey, work, action));
                _timeline?.Append(task.FolderPath, "testing-strategy-changed", TimelineActors.System,
                    $"Testing strategy changed from {previous} to {strategy}: {work}, {action}.",
                    details: new Dictionary<string, string>
                    {
                        ["previousStrategy"] = previous, ["strategy"] = strategy,
                        ["work"] = work, ["action"] = action,
                        ["reviewAttemptId"] = item.ReviewAttemptId,
                    });
            }
            if (UsesPostMerge(project))
            {
                _store.ClearCloseNow(project);
                FallbackFlight[] flights;
                lock (_fallbackFlights)
                    flights = _fallbackFlights.Values.Where(flight => string.Equals(
                        flight.Pending.Subject.Project, project, StringComparison.OrdinalIgnoreCase)).ToArray();
                // Gate continuations take the flight lock; cancel after releasing it.
                foreach (var flight in flights)
                    try { flight.Stall.Cancel(); }
                    catch (ObjectDisposedException ex)
                    {
                        SilentCatch.Note(ex, "Strategy switch met a fallback flight that already finished.");
                    }
                // Running batches already fence their gate and publication with BatchRouteEnabled.
                // Once retired, their durable pending members take the new integration path on a tick.
            }
            return new(previous, strategy, cards);
        }
        finally { _tick.Release(); }
    }
}
