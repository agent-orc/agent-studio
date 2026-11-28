using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Typed fresh-run triggers after the sole resumed mechanical round.</summary>
public static class MechanicalRoundFallbackPolicy
{
    internal static bool RequiresPreparedCheckout(string? reason)
        => reason is "semantic-conflict" or "invalid-session-after-resume" or "resume-token-ceiling"
            or "resume-duration-ceiling" or "resumed-round-inconclusive" or "resumed-round-failed";

    public static ExecutionOutcomeDecision AsTypedDecision(
        ExecutionOutcomeDecision decision,
        string reason) => decision with
    {
        Outcome = ExecutionOutcomeKind.MechanicalFallback,
        RecoveryAction = ExecutionRecoveryAction.StartFreshAttemptFromSalvage,
        ConsumesProductDefectBudget = false,
        ConsumesCompletionBudget = false,
        ConsumesCodingReworkBudget = false,
        Detail = reason,
    };

    public static string? Reason(
        bool resumedMechanical,
        RunOutcomeKind outcome,
        ExecutionOutcomeKind typedOutcome,
        string? stderr)
    {
        if (!resumedMechanical) return null;
        if (outcome is RunOutcomeKind.Blocked or RunOutcomeKind.NeedsInput)
            return "semantic-conflict";
        if (typedOutcome == ExecutionOutcomeKind.InvalidSession)
            return "invalid-session-after-resume";
        if (typedOutcome == ExecutionOutcomeKind.Timeout)
            return stderr?.Contains("token ceiling", StringComparison.OrdinalIgnoreCase) == true
                ? "resume-token-ceiling" : "resume-duration-ceiling";
        if (typedOutcome is ExecutionOutcomeKind.CliCrash or ExecutionOutcomeKind.ProtocolInconclusive)
            return "resumed-round-inconclusive";
        return null;
    }
}
