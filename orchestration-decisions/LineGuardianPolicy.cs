namespace AgentStudio.OrchestrationDecisions;

public enum GuardianPermission { Refused, Allowed, OwnerRequired }
public sealed record GuardianOperationFacts(string Operation, RedTestTriageFacts Incident,
    bool HasCard = false, bool ActiveHeld = false, bool ExplicitOwnerGrant = false,
    DateTimeOffset? GrantExpires = null, int UsesRemaining = 0, DateTimeOffset Now = default);
public sealed record GuardianBudgetFacts(bool Attempted, long WeeklyTokens, double IncidentCoreMinutes,
    double WeeklyCoreMinutes);

/// <summary>RT-D8 B: bounded authority independent of dispatch. Unknown operations fail closed.</summary>
public static class LineGuardianPolicy
{
    public const int ClassificationTokens = 8_000;
    public const int WeeklyTokens = 60_000;
    public const int IncidentCoreMinutes = 90;
    public const int WeeklyCoreMinutes = 600;
    public static bool ComputeExhausted(GuardianBudgetFacts f) => f.IncidentCoreMinutes >= IncidentCoreMinutes
        || f.WeeklyCoreMinutes >= WeeklyCoreMinutes;
    public static bool MayClassify(GuardianBudgetFacts f) => !f.Attempted
        && f.WeeklyTokens + ClassificationTokens <= WeeklyTokens
        && f.IncidentCoreMinutes < IncidentCoreMinutes && f.WeeklyCoreMinutes < WeeklyCoreMinutes;
    public static GuardianPermission Authorize(GuardianOperationFacts f)
    {
        var i = f.Incident;
        var tier2 = f.Operation switch
        {
            "quarantine.hold.start" => i.Category == RedTestCategory.Base
                || i.ProjectQuarantines.Count >= RedTestTriageBounds.MaximumTotal
                || i.ProjectQuarantines.Count(q => q.Kind == RedTestQuarantineKind.Held) >= RedTestTriageBounds.MaximumHeld,
            "quarantine.report-only.start" => i.Category == RedTestCategory.Base
                || i.ProjectQuarantines.Count >= RedTestTriageBounds.MaximumTotal,
            "quarantine.renew" => i.ActiveQuarantine?.Kind == RedTestQuarantineKind.ReportOnly
                || i.ActiveQuarantine?.Age >= RedTestTriageBounds.HeldMaximumAge
                || i.ActiveQuarantine?.Renewals >= 1,
            "quarantine.lift" => !LiftEvidence(i),
            _ => false,
        };
        if (tier2) return f.ExplicitOwnerGrant && f.GrantExpires > f.Now && f.UsesRemaining > 0
            ? GuardianPermission.Allowed : GuardianPermission.OwnerRequired;
        var allowed = f.Operation switch
        {
            "observe" or "triage.classify" => true,
            "quarantine.hold.start" => i.FirstResult == RedTestResult.Fail && i.SameShaRepeat == RedTestResult.Fail,
            "quarantine.report-only.start" => f.HasCard && RedTestTriagePolicy.Decide(i).Classification
                is RedTestClassification.Flaky or RedTestClassification.Environment,
            "quarantine.renew" => i.ActiveQuarantine is { Kind: RedTestQuarantineKind.Held, Renewals: 0 }
                && i.RepairCardLane is TaskStates.Progress or TaskStates.AutoReview,
            "quarantine.lift" => LiftEvidence(i),
            "hold.area" => f.ActiveHeld,
            "repair.create" => true,
            "repair.first" => f.HasCard && i.RepairCardLane is TaskStates.Preparation or TaskStates.Ready,
            "revert.propose" => RedTestTriagePolicy.Decide(i).Classification == RedTestClassification.Regression
                && RedTestTriagePolicy.CanProposeRevert(1, i.RevertAppliesCleanly, i.LaterMergeCoupled),
            _ => false,
        };
        return allowed ? GuardianPermission.Allowed : GuardianPermission.Refused;
    }
    private static bool LiftEvidence(RedTestTriageFacts f) => f.LiftRuns.Count >= 2
        && f.LiftRuns.TakeLast(2).All(r => r.Result == RedTestResult.Pass && r.StandardEnvelope && r.ContainsRepairIntegration);
}
