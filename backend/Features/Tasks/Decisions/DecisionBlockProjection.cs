namespace AgentStudio.Tasks;

public static class DecisionBlockProjection
{
    public static List<string> BlockedBy(WaitsOnStatus? waitsOn) =>
        waitsOn?.Items.Where(item => item.PendingDecision)
            .Select(item => item.Key).ToList() ?? [];
}
