using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Turns an empty review claim that names unclaimable pending attempts into an
/// operator warning (AGT-2987). Repeats for the same missing keys are held back
/// for <see cref="RepeatInterval"/> so a poll loop does not flood the journal,
/// while a changed key set is reported immediately.
/// </summary>
internal sealed class ReviewClaimEmptyWarning
{
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(15);

    private string? _lastSignature;
    private DateTime _lastLoggedAt;

    public string? Next(ReviewClaimResponse claim, DateTime nowUtc)
    {
        if (!string.Equals(claim.Status, "empty", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                claim.Reason,
                ReviewClaimEmptyReasons.UnclaimablePlanRequirements,
                StringComparison.Ordinal))
            return null;

        var missing = string.Join(",", claim.MissingCapabilities ?? []);
        if (string.Equals(missing, _lastSignature, StringComparison.Ordinal)
            && nowUtc - _lastLoggedAt < RepeatInterval)
            return null;
        _lastSignature = missing;
        _lastLoggedAt = nowUtc;

        var oldest = claim.UnclaimableAttempts?.FirstOrDefault();
        return $"review claim warning: reason={claim.Reason} missing={missing} "
               + $"unclaimableAttempts={claim.UnclaimableAttempts?.Count ?? 0} "
               + $"oldestAttempt={oldest?.AttemptId ?? "unknown"} oldestTask={oldest?.TaskKey ?? "unknown"}; "
               + "put the missing toolchain on this review unit's PATH and restart it, "
               + "or add the key to RUNNER_REQUIRED_CAPABILITIES. "
               + (claim.Message ?? string.Empty);
    }
}
