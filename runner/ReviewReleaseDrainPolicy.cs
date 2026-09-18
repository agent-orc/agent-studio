using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Restart policy for the release mismatch a durable review worker creates
/// (AGT-2863). A worker deliberately outlives the daemon that launched it, so a
/// replacement daemon of a newer release finishes attempts that an older binary
/// is executing. The default keeps that behaviour and only reports it; drain
/// mode additionally holds claim admission until those attempts are done, so an
/// operator can roll out a worker-side hotfix without mixed releases.
///
/// Neither mode ever ends an adopted worker: AGT-2753's rule that a restart must
/// not discard running gate work outranks release uniformity.
/// </summary>
internal static class ReviewReleaseDrainPolicy
{
    /// <summary>
    /// The superseded attempts that are still running. An attempt that has left
    /// the active set no longer holds the gate, even if its slot record has not
    /// been reaped yet.
    /// </summary>
    internal static IReadOnlyList<string> RunningReleases(
        IReadOnlyDictionary<string, string> supersededAttempts,
        IEnumerable<string> activeAttemptIds)
        => activeAttemptIds
            .Where(supersededAttempts.ContainsKey)
            .Select(attemptId => supersededAttempts[attemptId])
            .ToArray();

    /// <summary>
    /// Closed admission while superseded workers run. Active slots always
    /// continue, so this decision can never end an adopted review.
    /// </summary>
    internal static ReviewSlotAdmissionDecision Held(IReadOnlyList<string> runningReleases)
        => new(
            false,
            $"release drain: {runningReleases.Count} adopted attempt(s) still run " +
            $"release {string.Join(", ", runningReleases.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}; " +
            "claims resume when they finish",
            null);
}
