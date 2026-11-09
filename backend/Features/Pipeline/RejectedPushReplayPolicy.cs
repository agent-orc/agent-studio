namespace AgentStudio.Pipeline;

public enum RejectedPushReplayAction { ReuseGreenOnNewBase, ReturnToBatchQueue }

/// <summary>Decides whether a green per-task gate covers a moved origin base.</summary>
public static class RejectedPushReplayPolicy
{
    public static RejectedPushReplayAction Decide(
        int replayCount, bool oldBaseIsAncestor, bool gatePassed,
        IReadOnlyList<string>? deliveryPaths, IReadOnlyList<string>? movedBasePaths)
    {
        if (replayCount != 0 || !oldBaseIsAncestor || !gatePassed
            || deliveryPaths is null || movedBasePaths is null)
            return RejectedPushReplayAction.ReturnToBatchQueue;
        var delivery = deliveryPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return movedBasePaths.Any(delivery.Contains)
            ? RejectedPushReplayAction.ReturnToBatchQueue
            : RejectedPushReplayAction.ReuseGreenOnNewBase;
    }
}
