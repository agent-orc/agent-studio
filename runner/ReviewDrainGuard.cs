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
/// Host-local drain request and busy-slot census for the review daemon. Both
/// are plain files under the role's <c>RUNNER_STATE_DIR</c> so an operator tool
/// and a deploy script can read them without talking to the daemon or the
/// Task Server.
/// </summary>
public static class ReviewDrainGuard
{
    private const string MarkerFileName = "review-drain-requested.json";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed record DrainRequest(string Reason, DateTime RequestedAtUtc);

    public static string MarkerPath(string stateDir)
        => Path.Combine(Path.GetFullPath(stateDir), MarkerFileName);

    /// <summary>Asks the running daemon to stop claiming and finish its slots.</summary>
    public static void RequestDrain(string stateDir, string reason)
    {
        Directory.CreateDirectory(Path.GetFullPath(stateDir));
        File.WriteAllText(
            MarkerPath(stateDir),
            JsonSerializer.Serialize(new DrainRequest(reason, DateTime.UtcNow), Json));
    }

    public static DrainRequest? ReadDrainRequest(string stateDir)
    {
        var path = MarkerPath(stateDir);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<DrainRequest>(File.ReadAllText(path), Json)
                  ?? new DrainRequest("unspecified", DateTime.UtcNow)
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // A marker that exists but cannot be parsed is still a drain request.
            return new DrainRequest("unreadable drain marker", DateTime.UtcNow);
        }
    }

    public static void ClearDrainRequest(string stateDir)
    {
        var path = MarkerPath(stateDir);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // The next start clears it; a stale marker only pauses claiming.
        }
    }

    /// <summary>
    /// Counts busy review slots without throwing. Unlike
    /// <see cref="ReviewStateStore.LoadAll"/> this is a diagnostic read used by
    /// operator tooling, so a corrupt record is reported rather than fatal.
    /// </summary>
    public static ReviewSlotBusySnapshot Inspect(string stateDir)
    {
        var root = Path.Combine(Path.GetFullPath(stateDir), "reviews");
        if (!Directory.Exists(root)) return new ReviewSlotBusySnapshot(0, 0, []);

        var busy = new List<string>();
        var total = 0;
        var unreadable = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*.review-slot.json")
                     .OrderBy(item => item, StringComparer.Ordinal))
        {
            total++;
            try
            {
                var slot = JsonSerializer.Deserialize<PersistedReviewSlot>(
                    File.ReadAllText(path),
                    Json);
                if (slot is null)
                {
                    unreadable++;
                    continue;
                }
                if (ReviewRestartGuardPolicy.IsBusyPhase(slot.Phase))
                    busy.Add(slot.Claim.Attempt?.AttemptId ?? Path.GetFileName(path));
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
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
}
