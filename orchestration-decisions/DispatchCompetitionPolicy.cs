namespace AgentStudio.OrchestrationDecisions;

/// <summary>DS-11 consumes these outcomes; evaluating them never transfers a lease.</summary>
public sealed record DispatchCompetitionFacts(double? ChampionValue, double? ChallengerValue,
    int ShadowIntegratedCards, int ShadowWaves, int AlternatingLiveWaves,
    bool AheadInEveryLiveWave, int GuardCards, double? LiveConflictsPer100,
    double? BaselineShadowConflictsPer100, bool SameGuardCards);
public sealed record DispatchCompetitionEvaluation(bool EligibleForAlternatingLive,
    bool ChampionEligible, bool GuardExceeded, string FallbackStrategy, int FeedImportance, bool OwnerUndo)
{
    public string AuthorityTier => "T1";
}

public static class DispatchCompetitionPolicy
{
    public static DispatchCompetitionEvaluation Evaluate(DispatchCompetitionFacts facts)
    {
        var shadow = facts.ShadowIntegratedCards >= 100 && facts.ShadowWaves >= 2
            && facts.ChampionValue.HasValue && facts.ChallengerValue >= facts.ChampionValue + 5;
        var guard = facts.SameGuardCards && facts.GuardCards == 20
            && facts.LiveConflictsPer100.HasValue && facts.BaselineShadowConflictsPer100.HasValue
            && facts.LiveConflictsPer100 >= facts.BaselineShadowConflictsPer100 + 10;
        return new(shadow, shadow && facts.AlternatingLiveWaves >= 4 && facts.AheadInEveryLiveWave,
            guard, DispatchStrategies.LaneEntry, 70, true);
    }
}
