using System.Text.Json;

namespace AgentRunner;

/// <summary>Outcome of the pre-stop check that guards a review daemon restart.</summary>
public enum ReviewRestartGuardDecision
{
    /// <summary>No gate work is at risk; a plain restart is safe.</summary>
    Allow,

    /// <summary>Review slots are busy. Drain first, or restart with --force.</summary>
    RefuseBusy,

    /// <summary>
    /// Slot state could not be read, so busy slots cannot be ruled out. The
    /// guard fails closed: an unreadable record is treated as work at risk.
    /// </summary>
    RefuseUnreadable,
}

/// <summary>Busy-slot census of one review state directory.</summary>
public sealed record ReviewSlotBusySnapshot(
    int Total,
    int Unreadable,
    IReadOnlyList<string> BusyAttemptIds)
{
    public int Busy => BusyAttemptIds.Count;
}

/// <summary>
/// Pure restart-admission decision for the review daemon. A restart that lands
/// on busy slots throws away tens of minutes of gate work per slot, so the
/// sanctioned path is a drain and the plain restart is refused.
/// </summary>
public static class ReviewRestartGuardPolicy
{
    /// <summary>
    /// Slot phases that still owe the Task Server a report. Terminal and
    /// leftover phases are excluded: restarting on them loses nothing.
    /// </summary>
    private static readonly string[] BusyPhases =
    [
        "preparing",
        "launching",
        "running",
        "finalizing",
        "handed-off",
        "report-submitting",
        "report-pending",
    ];

    public static bool IsBusyPhase(string? phase)
        => phase is not null
           && BusyPhases.Contains(phase, StringComparer.Ordinal);

    public static ReviewRestartGuardDecision Decide(ReviewSlotBusySnapshot snapshot, bool force)
    {
        if (force) return ReviewRestartGuardDecision.Allow;
        if (snapshot.Busy > 0) return ReviewRestartGuardDecision.RefuseBusy;
        return snapshot.Unreadable > 0
            ? ReviewRestartGuardDecision.RefuseUnreadable
            : ReviewRestartGuardDecision.Allow;
    }
}

/// <summary>
/// Host-local drain request, daemon acknowledgement, and busy-slot census for
/// the review daemon. The control records are plain files under the role's
/// <c>RUNNER_STATE_DIR</c> so an operator tool and a deploy script can read them
/// without talking to the Task Server.
/// </summary>
public static class ReviewDrainGuard
{
    public const string DrainRequestFileName = "review-drain-requested.json";
    public const string DrainAcknowledgementFileName = "review-drain-acknowledged.json";
    public const string DrainMode = "drain";
    public const string RestartGuardMode = "restart-guard";

    private const string ControlLockFileName = "review-drain-control.lock";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed record DrainRequest(
        string RequestId,
        string Reason,
        DateTime RequestedAtUtc,
        string Mode = DrainMode);

    public sealed record DrainAcknowledgement(
        string RequestId,
        int DaemonProcessId,
        int ActiveSlots,
        DateTime ObservedAtUtc);

    public static string MarkerPath(string stateDir)
        => Path.Combine(Path.GetFullPath(stateDir), DrainRequestFileName);

    public static string AcknowledgementPath(string stateDir)
        => Path.Combine(Path.GetFullPath(stateDir), DrainAcknowledgementFileName);

    private static string ControlLockPath(string stateDir)
        => Path.Combine(Path.GetFullPath(stateDir), ControlLockFileName);

    /// <summary>Asks the running daemon to stop claiming and finish its slots.</summary>
    public static DrainRequest RequestDrain(string stateDir, string reason)
        => RequestControl(stateDir, reason, DrainMode);

    /// <summary>
    /// Closes claim admission while a guarded replacement decides whether the
    /// daemon is safe to stop. Unlike a drain, an idle daemon remains alive
    /// until the request is withdrawn or the replacement sends SIGTERM.
    /// </summary>
    public static DrainRequest RequestRestartGuard(string stateDir, string reason)
        => RequestControl(stateDir, reason, RestartGuardMode);

    public static bool IsRestartGuard(DrainRequest request)
        => string.Equals(request.Mode, RestartGuardMode, StringComparison.Ordinal);

    private static DrainRequest RequestControl(string stateDir, string reason, string mode)
    {
        Directory.CreateDirectory(Path.GetFullPath(stateDir));
        using var controlLock = AcquireControlLock(stateDir);
        var existing = ReadDrainRequest(stateDir);
        if (existing is not null && string.Equals(mode, RestartGuardMode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Review control request {existing.RequestId} ({existing.Mode ?? DrainMode}) is already active.");
        }
        TryDelete(AcknowledgementPath(stateDir));
        var request = new DrainRequest(
            Guid.NewGuid().ToString("N"),
            reason,
            DateTime.UtcNow,
            mode);
        WriteAtomically(MarkerPath(stateDir), request);
        return request;
    }

    public static DrainRequest? ReadDrainRequest(string stateDir)
    {
        var path = MarkerPath(stateDir);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<DrainRequest>(File.ReadAllText(path), Json)
                  ?? new DrainRequest("unreadable", "unspecified", DateTime.UtcNow)
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A marker that exists but cannot be parsed is still a drain request.
            return new DrainRequest("unreadable", "unreadable drain marker", DateTime.UtcNow);
        }
    }

    public static DrainAcknowledgement? ReadDrainAcknowledgement(string stateDir)
    {
        var path = AcknowledgementPath(stateDir);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<DrainAcknowledgement>(File.ReadAllText(path), Json)
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records that the daemon has regained control after any claim already in
    /// flight, closed admission, and observed the stated active-slot count.
    /// </summary>
    public static void AcknowledgeDrain(
        string stateDir,
        DrainRequest request,
        int activeSlots)
    {
        Directory.CreateDirectory(Path.GetFullPath(stateDir));
        using var controlLock = AcquireControlLock(stateDir);
        var current = ReadDrainRequest(stateDir);
        if (!string.Equals(current?.RequestId, request.RequestId, StringComparison.Ordinal))
            return;
        WriteAtomically(
            AcknowledgementPath(stateDir),
            new DrainAcknowledgement(
                request.RequestId,
                Environment.ProcessId,
                activeSlots,
                DateTime.UtcNow));
    }

    /// <summary>
    /// Removes only the caller's temporary request. The request-id check and
    /// the host-local lock prevent an older guard from reopening admission
    /// after a newer drain or replacement request has taken ownership.
    /// </summary>
    public static bool WithdrawRequest(string stateDir, string requestId)
    {
        Directory.CreateDirectory(Path.GetFullPath(stateDir));
        using var controlLock = AcquireControlLock(stateDir);
        var current = ReadDrainRequest(stateDir);
        if (!string.Equals(current?.RequestId, requestId, StringComparison.Ordinal))
            return false;

        TryDelete(MarkerPath(stateDir));
        var remaining = ReadDrainRequest(stateDir);
        if (string.Equals(remaining?.RequestId, requestId, StringComparison.Ordinal))
            return false;
        var acknowledgement = ReadDrainAcknowledgement(stateDir);
        if (string.Equals(acknowledgement?.RequestId, requestId, StringComparison.Ordinal))
            TryDelete(AcknowledgementPath(stateDir));
        return true;
    }

    public static void ClearDrainState(string stateDir)
    {
        Directory.CreateDirectory(Path.GetFullPath(stateDir));
        using var controlLock = AcquireControlLock(stateDir);
        TryDelete(MarkerPath(stateDir));
        TryDelete(AcknowledgementPath(stateDir));
    }

    /// <summary>
    /// Counts busy review slots without throwing. Unlike
    /// <see cref="ReviewStateStore.LoadAll"/> this is a diagnostic read used by
    /// operator tooling, so a corrupt record is reported rather than fatal.
    /// </summary>
    public static ReviewSlotBusySnapshot Inspect(string stateDir)
    {
        var root = Path.Combine(Path.GetFullPath(stateDir), "reviews");
        var busy = new List<string>();
        var total = 0;
        var unreadable = 0;
        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(root, "*.review-slot.json")
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return new ReviewSlotBusySnapshot(0, 0, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ReviewSlotBusySnapshot(0, 1, []);
        }

        foreach (var path in paths)
        {
            total++;
            try
            {
                var slot = JsonSerializer.Deserialize<PersistedReviewSlot>(
                    File.ReadAllText(path),
                    Json);
                if (slot?.Claim?.Attempt?.AttemptId is not { Length: > 0 } attemptId
                    || slot.Claim.Subject is null
                    || slot.Claim.Lease is null
                    || !string.Equals(
                        slot.Claim.Attempt.AttemptId,
                        slot.Claim.Lease.AttemptId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        slot.Claim.Attempt.SubjectId,
                        slot.Claim.Subject.SubjectId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        slot.Claim.Subject.SubjectId,
                        slot.Claim.Lease.SubjectId,
                        StringComparison.Ordinal))
                {
                    unreadable++;
                    continue;
                }
                if (ReviewRestartGuardPolicy.IsBusyPhase(slot.Phase))
                    busy.Add(attemptId);
            }
            catch (Exception exception) when (
                exception is IOException
                    or JsonException
                    or InvalidDataException
                    or UnauthorizedAccessException)
            {
                unreadable++;
            }
        }
        return new ReviewSlotBusySnapshot(total, unreadable, busy);
    }

    /// <summary>
    /// The one line every refusal, SIGTERM log, and runbook step points at. Kept
    /// here so the daemon, the guard, and the deploy script cannot drift.
    /// </summary>
    public const string DrainHint =
        "Use 'agent-host --drain' (or 'agent-runner-deploy drain') to finish the running reviews "
        + "before stopping. A plain restart discards their gate work. Pass --force to override.";

    private static void WriteAtomically<T>(string path, T value)
    {
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(JsonSerializer.Serialize(value, Json));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temporary,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead
                    | UnixFileMode.OtherRead);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale control record keeps admission closed; never fail open.
        }
    }

    private static FileStream AcquireControlLock(string stateDir)
    {
        var path = ControlLockPath(stateDir);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    try
                    {
                        using var created = new FileStream(
                            path,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.ReadWrite,
                            1,
                            FileOptions.WriteThrough);
                        if (!OperatingSystem.IsWindows())
                        {
                            File.SetUnixFileMode(
                                path,
                                UnixFileMode.UserRead
                                | UnixFileMode.UserWrite
                                | UnixFileMode.GroupRead
                                | UnixFileMode.OtherRead);
                        }
                    }
                    catch (IOException)
                    {
                        // Another controller won creation; open that file below.
                    }
                }
                return new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None,
                    1,
                    FileOptions.None);
            }
            catch (IOException) when (attempt < 500)
            {
                Thread.Sleep(10);
            }
        }
    }
}
