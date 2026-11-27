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

    public static int QueuePriority(string? kind) => kind == "post-merge" ? 1 : 0;
}
