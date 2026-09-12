namespace AgentStudio.Docs;

public sealed record WorkbenchReviewRelatedCard(bool Exists, string? State, DateTime EnteredLaneAtUtc);

public static class WorkbenchReviewPolicy
{
    public static bool IsDue(
        DateTimeOffset reviewedAt,
        DateTimeOffset now,
        int thresholdDays,
        IReadOnlyCollection<WorkbenchReviewRelatedCard> relatedCards)
    {
        if (reviewedAt <= now.AddDays(-Math.Max(1, thresholdDays))) return true;
        return relatedCards.Count > 0 && relatedCards.All(card =>
            card.Exists
            && card.State == TaskStates.Completed
            && DateTime.SpecifyKind(card.EnteredLaneAtUtc, DateTimeKind.Utc) > reviewedAt.UtcDateTime);
    }
}
