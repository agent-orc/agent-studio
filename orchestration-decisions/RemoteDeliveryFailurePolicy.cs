namespace AgentStudio.Runner;

/// <summary>
/// Pure retry decision for consecutive Remote completion deliveries that lack
/// a complete immutable result envelope.
/// </summary>
public static class RemoteDeliveryFailurePolicy
{
    public const string DeliveryFailed = "delivery-failed";
    public const int MaximumAttempts = 2;

    public static RemoteDeliveryFailureDecision Decide(int previousConsecutiveFailures)
        => ForAttempt(Math.Max(0, previousConsecutiveFailures) + 1);

    public static RemoteDeliveryFailureDecision ForAttempt(int attempt)
    {
        var normalized = Math.Clamp(attempt, 1, MaximumAttempts);
        return new RemoteDeliveryFailureDecision(
            normalized,
            MaximumAttempts,
            normalized >= MaximumAttempts);
    }
}

public sealed record RemoteDeliveryFailureDecision(
    int Attempt,
    int MaximumAttempts,
    bool Escalate);
