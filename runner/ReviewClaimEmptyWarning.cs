using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Turns an unclaimable review claim or a required host-separation wait into
/// an operator warning. Identical reasons are rate limited so polling does not
/// flood the journal.
/// </summary>
internal sealed class ReviewClaimEmptyWarning
{
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(15);

    private string? _lastSignature;
    private DateTime _lastLoggedAt;

    public string? Next(ReviewClaimResponse claim, DateTime nowUtc)
    {
        if (string.Equals(claim.Status, "empty", StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.Reason, ReviewClaimEmptyReasons.DifferentHostRequired, StringComparison.Ordinal))
        {
            var signature = $"{claim.Reason}:{claim.Message}";
            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal)
                && nowUtc - _lastLoggedAt < RepeatInterval)
                return null;
            _lastSignature = signature;
            _lastLoggedAt = nowUtc;
            return $"review claim waiting: reason={claim.Reason}; {claim.Message}";
        }
        if (!string.Equals(claim.Status, "empty", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                claim.Reason,
                ReviewClaimEmptyReasons.UnclaimablePlanRequirements,
                StringComparison.Ordinal))
            return null;

        var missing = string.Join(",", claim.MissingCapabilities ?? []);
        var unclaimableSignature = $"{claim.Reason}:{missing}";
        if (string.Equals(unclaimableSignature, _lastSignature, StringComparison.Ordinal)
            && nowUtc - _lastLoggedAt < RepeatInterval)
            return null;
        _lastSignature = unclaimableSignature;
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
