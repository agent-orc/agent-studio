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
    public static async Task<int> RunRestartGuardAsync(
        RunnerOptions options,
        Action<string> log,
        CancellationToken ct)
    {
        if (options.Force)
        {
            var forcedSnapshot = ReviewDrainGuard.Inspect(options.StateDir);
            if (forcedSnapshot.Busy > 0 || forcedSnapshot.Unreadable > 0)
            {
                log(
                    $"restart-guard forced: {forcedSnapshot.Busy} busy and " +
                    $"{forcedSnapshot.Unreadable} unreadable review slot record(s) may contain " +
                    $"gate work ({string.Join(", ", forcedSnapshot.BusyAttemptIds)})");
            }
            else
            {
                log($"restart-guard forced: no busy review slots in {options.StateDir}");
            }
            return 0;
        }

        var reason = $"guarded replacement requested on {options.Hostname}";
        ReviewDrainGuard.DrainRequest request;
        try
        {
            request = ReviewDrainGuard.RequestRestartGuard(options.StateDir, reason);
        }
        catch (InvalidOperationException exception)
        {
            log(
                $"restart-guard refused: {exception.Message} Wait for that operation to finish " +
                $"or withdraw it explicitly. {ReviewDrainGuard.DrainHint}");
            return 3;
        }
        var preserveAdmissionBarrier = false;
        try
        {
            log(
                $"restart-guard requested: id={request.RequestId}; waiting for the review " +
                "daemon to acknowledge closed claim admission");
            var acknowledgementTimeoutSeconds = options.ServerRequestTimeoutSeconds
                                                + Math.Max(2, options.PollSeconds * 2);
            var deadline = DateTime.UtcNow.AddSeconds(acknowledgementTimeoutSeconds);
            while (true)
            {
                var snapshot = ReviewDrainGuard.Inspect(options.StateDir);
                if (snapshot.Unreadable > 0)
                {
                    log(
                        $"restart-guard refused: {snapshot.Unreadable} review slot record(s) under " +
                        $"{options.StateDir} could not be read, so busy slots cannot be ruled out. " +
                        ReviewDrainGuard.DrainHint);
                    return 3;
                }

                var acknowledgement = ReviewDrainGuard.ReadDrainAcknowledgement(options.StateDir);
                var acknowledged = string.Equals(
                    acknowledgement?.RequestId,
                    request.RequestId,
                    StringComparison.Ordinal);
                if (acknowledged
                    && (acknowledgement!.ActiveSlots > 0 || snapshot.Busy > 0))
                {
                    log(
                        $"restart-guard refused: {snapshot.Busy} of {snapshot.Total} persisted " +
                        $"review slot(s) are busy and {acknowledgement.ActiveSlots} daemon slot(s) " +
                        $"are active ({string.Join(", ", snapshot.BusyAttemptIds)}). " +
                        ReviewDrainGuard.DrainHint);
                    return 3;
                }
                if (acknowledged)
                {
                    preserveAdmissionBarrier = options.HoldAdmission;
                    log(
                        $"restart-guard ok: daemonPid={acknowledgement!.DaemonProcessId} " +
                        $"acknowledged closed admission and no busy review slots remain in " +
                        $"{options.StateDir}" +
                        (preserveAdmissionBarrier ? "; admission remains closed for replacement" : string.Empty));
                    return 0;
                }
                if (DateTime.UtcNow >= deadline)
                {
                    log(
                        $"restart-guard refused: the review daemon did not acknowledge closed " +
                        $"admission within {acknowledgementTimeoutSeconds}s. " +
                        ReviewDrainGuard.DrainHint);
                    return 3;
                }
                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Min(250, options.PollSeconds * 1000)),
                    ct);
            }
        }
        finally
        {
            if (!preserveAdmissionBarrier)
            {
                if (!ReviewDrainGuard.WithdrawRequest(options.StateDir, request.RequestId))
                {
                    log(
                        $"restart-guard warning: request {request.RequestId} could not be " +
                        "withdrawn; review claim admission remains closed");
                }
            }
        }
    }

    /// <summary>
    /// Asks the running review daemon to stop claiming and waits for its slots
    /// to finish. Exit 0 means drained; exit 3 means state was unreadable, the
    /// daemon did not acknowledge stopped admission, or the bound ran out.
    /// </summary>
    public static async Task<int> RunDrainAsync(
        RunnerOptions options,
        Action<string> log,
        CancellationToken ct)
    {
        var reason = $"operator drain requested on {options.Hostname}";
        var request = ReviewDrainGuard.RequestDrain(options.StateDir, reason);
        log(
            $"drain requested: id={request.RequestId} " +
            $"path={ReviewDrainGuard.MarkerPath(options.StateDir)}; " +
            "the review daemon stops claiming and exits once its slots are free");

        var deadline = DateTime.UtcNow.AddSeconds(options.DrainTimeoutSeconds);
        var lastBusy = -1;
        var lastDaemonActive = -2;
        var acknowledgementAnnounced = false;
        while (true)
        {
            var snapshot = ReviewDrainGuard.Inspect(options.StateDir);
            if (snapshot.Unreadable > 0)
            {
                log(
                    $"drain refused: {snapshot.Unreadable} review slot record(s) under " +
                    $"{options.StateDir} could not be read, so stopped admission and an empty " +
                    "slot set cannot be proved. Repair the records before stopping the service.");
                return 3;
            }

            var acknowledgement = ReviewDrainGuard.ReadDrainAcknowledgement(options.StateDir);
            var acknowledged = string.Equals(
                acknowledgement?.RequestId,
                request.RequestId,
                StringComparison.Ordinal);
            if (acknowledged && !acknowledgementAnnounced)
            {
                acknowledgementAnnounced = true;
                log(
                    $"drain acknowledged: daemonPid={acknowledgement!.DaemonProcessId} " +
                    $"activeSlots={acknowledgement.ActiveSlots}; no new claims can start");
            }

            if (acknowledged
                && acknowledgement!.ActiveSlots == 0
                && snapshot.Busy == 0)
            {
                log(
                    $"drain complete: daemon acknowledged stopped admission and no busy " +
                    $"review slots remain in {options.StateDir}");
                return 0;
            }
            var daemonActive = acknowledged ? acknowledgement!.ActiveSlots : -1;
            if (snapshot.Busy != lastBusy || daemonActive != lastDaemonActive)
            {
                lastBusy = snapshot.Busy;
                lastDaemonActive = daemonActive;
                log(
                    $"draining: {snapshot.Busy} persisted review slot(s) still running " +
                    $"and {daemonActive} daemon slot(s) active " +
                    $"({string.Join(", ", snapshot.BusyAttemptIds)})");
            }
            if (DateTime.UtcNow >= deadline)
            {
                var detail = acknowledged
                    ? $"{snapshot.Busy} busy persisted slot(s) and {acknowledgement!.ActiveSlots} active daemon slot(s)"
                    : "no matching daemon acknowledgement";
                log(
                    $"drain timed out after {options.DrainTimeoutSeconds}s with {detail} " +
                    $"({string.Join(", ", snapshot.BusyAttemptIds)}). Stopping now still discards " +
                    "gate work; raise --drain-timeout-seconds or investigate the daemon and slots.");
                return 3;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds)), ct);
        }
    }
}
