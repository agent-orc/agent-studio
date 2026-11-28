namespace AgentStudio.OrchestrationDecisions;

/// <summary>Post-merge admission facts, independent of host queues and transports.</summary>
public static class PostMergeSchedulingPolicy
{
    public static bool DeadlineReached(DateTimeOffset? opened, int minutes, DateTimeOffset now)
        => opened is { } at && now - at >= TimeSpan.FromMinutes(Math.Max(0, minutes));

    public static bool NeedsOwner(DateTimeOffset? opened, int minutes, DateTimeOffset now)
        => opened is { } at && now - at > TimeSpan.FromMinutes(Math.Max(1, minutes) * 3d);

    public static bool MaySupersede(string phase)
        => phase is "queued" or "infra-retry" or "claimed" or "materializing";

    public static bool MayShareGateLane(string? active, string? candidate)
        => active == "post-merge" && candidate == "merge" || active == "merge" && candidate == "post-merge";

    /// <summary>Two bounded flights allow the reserved lane to claim while another executes.</summary>
    public static bool MayClaim(bool firstFlightActive, bool secondFlightActive)
        => !firstFlightActive || !secondFlightActive;

    /// <summary>Keep an integration grant free or held by an active merge.</summary>
    public static bool MayReservePostMergeGrant(int maxParallelism, int occupiedGrants,
        IReadOnlyCollection<string> activeKinds)
        => occupiedGrants < maxParallelism && (maxParallelism == 1
            || occupiedGrants < maxParallelism - 1
            || activeKinds.Any(kind => MayShareGateLane(kind, "post-merge")));

    /// <summary>Tie-breaker at the same creation instant, after age in PostMergeQueueOrder.</summary>
    public static int QueuePriority(string? kind) => kind == "post-merge" ? 1 : 0;
}

/// <summary>
/// FIFO admission key shared by the gate authorities. Age precedes kind so a
/// finite prefix bounds either queue's wait across later creation instants.
/// SQL authorities order the same tuple before applying their page limit.
/// </summary>
public readonly record struct PostMergeQueueOrder(DateTime CreatedAt, int Priority, long ArrivalOrder)
    : IComparable<PostMergeQueueOrder>
{
    public int CompareTo(PostMergeQueueOrder other)
    {
        var age = CreatedAt.CompareTo(other.CreatedAt);
        if (age != 0) return age;
        var priority = Priority.CompareTo(other.Priority);
        return priority != 0 ? priority : ArrivalOrder.CompareTo(other.ArrivalOrder);
    }
}

/// <summary>Fenced post-merge custody and infrastructure settlement rules.</summary>
public static class PostMergeCustodyPolicy
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    public static bool MayWrite(string? owner, long fence, string currentOwner, long currentFence,
        DateTimeOffset expires, DateTimeOffset now, bool acquiring, bool settling = false)
        => owner == currentOwner && (fence == currentFence || acquiring && fence == 0) && (expires > now || settling || acquiring && fence == 0);
    public static bool MayRecover(string owner, long fence, string currentOwner, long currentFence)
        => owner == currentOwner && fence == currentFence;
    public static bool MayReplace(DateTimeOffset started, string kind, DateTimeOffset? leaseExpires,
        DateTimeOffset now)
        => started.AddHours(kind == "nightly" ? 14 : 4) <= now && (leaseExpires is null || leaseExpires <= now);
    public static bool NeedsVerdict(string status) => status != "inconclusive";
    public static bool AdvancesWindow(string status, string kind)
        => status == "green" || kind == "window" && status == "red";
}
