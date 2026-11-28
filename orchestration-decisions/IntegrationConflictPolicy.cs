namespace AgentStudio.OrchestrationDecisions;

public sealed record IntegrationRecoveryReservation(
    string Card, string Target, DateTimeOffset DeliveredAtUtc, IReadOnlyList<string> Paths,
    string? ReceiptDeliverySha = null);

public enum IntegrationConflictAction { None, StartAgentRound, LeaveForHumanReview }

/// <summary>Conflict recovery limits, retry admission and overlapping delivery order.</summary>
public static class IntegrationConflictPolicy
{
    public const int MaximumRounds = 2;

    public static IntegrationConflictAction Round(string outcome, int used, int maximum = MaximumRounds)
    {
        if (outcome is not ("Conflict" or "AgentRoundRequired")) return IntegrationConflictAction.None;
        return MayQueueRound(used, maximum) ? IntegrationConflictAction.StartAgentRound
            : IntegrationConflictAction.LeaveForHumanReview;
    }

    public static bool MayQueueRound(int used, int maximum = MaximumRounds)
        => Math.Max(0, used) < Math.Clamp(maximum, 1, MaximumRounds);

    public static bool MayRetry(bool deliveryChanged, bool targetChanged, bool targetTouchedConflictPaths)
        => deliveryChanged || (targetChanged && targetTouchedConflictPaths);

    public static bool MayIntegrate(string card, string target, DateTimeOffset deliveredAtUtc,
        IReadOnlyList<string>? changedPaths, IReadOnlyList<IntegrationRecoveryReservation> reservations)
        => !BlockingReservations(card, target, deliveredAtUtc, changedPaths, reservations).Any();

    public static IEnumerable<IntegrationRecoveryReservation> BlockingReservations(string card, string target,
        DateTimeOffset deliveredAtUtc, IReadOnlyList<string>? changedPaths,
        IReadOnlyList<IntegrationRecoveryReservation> reservations)
        => reservations.Where(reservation => reservation.Card != card
            && reservation.Target == target && reservation.DeliveredAtUtc < deliveredAtUtc
            && (changedPaths is null || reservation.Paths.Intersect(changedPaths, StringComparer.Ordinal).Any()));

    public static DateTimeOffset OrderAt(string card, string target, DateTimeOffset deliveredAtUtc,
        IReadOnlyList<IntegrationRecoveryReservation> reservations)
        => reservations.FirstOrDefault(item => item.Card == card && item.Target == target)?.DeliveredAtUtc
            ?? deliveredAtUtc;
}
