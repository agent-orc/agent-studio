namespace AgentStudio.Pipeline;

public enum BatchGatePreCheckAction { Admit, Run, ReturnToAgent }

public static class BatchGatePreCheckPolicy
{
    public static BatchGatePreCheckAction Decide(bool enabled, string? outcome,
        DateTimeOffset enqueuedAtUtc, DateTimeOffset now,
        int deadlineMinutes, bool batchOwnsMember)
    {
        if (!enabled || batchOwnsMember) return BatchGatePreCheckAction.Admit;
        if (outcome == "red") return BatchGatePreCheckAction.ReturnToAgent;
        if (outcome == "green" || now >= enqueuedAtUtc.AddMinutes(deadlineMinutes))
            return BatchGatePreCheckAction.Admit;
        return BatchGatePreCheckAction.Run;
    }
}
