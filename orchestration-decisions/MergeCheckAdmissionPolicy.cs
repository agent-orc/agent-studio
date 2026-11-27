namespace AgentStudio.OrchestrationDecisions;

/// <summary>One reserved merge check beside the bounded regular gate lanes.</summary>
public static class MergeCheckAdmissionPolicy
{
    public static bool MayClaim(bool integration, int regularActive, int integrationActive, int regularSlots)
        => integration ? integrationActive == 0 : regularActive < regularSlots;

    public static int Priority(bool integration) => integration ? 0 : 1;
}
