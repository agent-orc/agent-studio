namespace AgentRunner;

/// <summary>
/// Operator entry points that protect running reviews from a plain restart.
/// Both work purely from host-local slot state, so a deploy script can call
/// them without Task Server credentials.
/// </summary>
public static class ReviewDrainCommand
{
    /// <summary>
    /// Reports whether stopping the review daemon right now would discard gate
    /// work. Exit 0 means safe, exit 3 means refused.
    /// </summary>
    public static int RunRestartGuard(RunnerOptions options, Action<string> log)
    {
        var snapshot = ReviewDrainGuard.Inspect(options.StateDir);
        var decision = ReviewRestartGuardPolicy.Decide(snapshot, options.Force);
        switch (decision)
        {
            case ReviewRestartGuardDecision.Allow when snapshot.Busy > 0:
                log(
                    $"restart-guard forced: {snapshot.Busy} busy review slot(s) will lose their gate work " +
                    $"({string.Join(", ", snapshot.BusyAttemptIds)})");
                return 0;
            case ReviewRestartGuardDecision.Allow:
                log($"restart-guard ok: no busy review slots in {options.StateDir}");
                return 0;
            case ReviewRestartGuardDecision.RefuseBusy:
                log(
                    $"restart-guard refused: {snapshot.Busy} of {snapshot.Total} review slot(s) are busy " +
                    $"({string.Join(", ", snapshot.BusyAttemptIds)}). {ReviewDrainGuard.DrainHint}");
                return 3;
            default:
                log(
                    $"restart-guard refused: {snapshot.Unreadable} review slot record(s) under " +
                    $"{options.StateDir} could not be read, so busy slots cannot be ruled out. " +
                    ReviewDrainGuard.DrainHint);
                return 3;
        }
    }

    /// <summary>
    /// Asks the running review daemon to stop claiming and waits for its slots
    /// to finish. Exit 0 means drained, exit 3 means the bound ran out with work
    /// still in flight.
    /// </summary>
    public static async Task<int> RunDrainAsync(
        RunnerOptions options,
        Action<string> log,
        CancellationToken ct)
    {
        var reason = $"operator drain requested on {options.Hostname}";
        ReviewDrainGuard.RequestDrain(options.StateDir, reason);
        log(
            $"drain requested: {ReviewDrainGuard.MarkerPath(options.StateDir)}; " +
            "the review daemon stops claiming and exits once its slots are free");

        var deadline = DateTime.UtcNow.AddSeconds(options.DrainTimeoutSeconds);
        var lastBusy = -1;
        while (true)
        {
            var snapshot = ReviewDrainGuard.Inspect(options.StateDir);
            if (snapshot.Busy == 0)
            {
                log($"drain complete: no busy review slots left in {options.StateDir}");
                return 0;
            }
            if (snapshot.Busy != lastBusy)
            {
                lastBusy = snapshot.Busy;
                log(
                    $"draining: {snapshot.Busy} review slot(s) still running " +
                    $"({string.Join(", ", snapshot.BusyAttemptIds)})");
            }
            if (DateTime.UtcNow >= deadline)
            {
                log(
                    $"drain timed out after {options.DrainTimeoutSeconds}s with {snapshot.Busy} busy " +
                    $"review slot(s) ({string.Join(", ", snapshot.BusyAttemptIds)}). Stopping now still " +
                    "discards their gate work; raise --drain-timeout-seconds or investigate the slots.");
                return 3;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds)), ct);
        }
    }
}
