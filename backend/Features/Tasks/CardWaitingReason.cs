namespace AgentStudio.Tasks;

/// <summary>A short card line derived only from the server's recorded pickup facts.</summary>
public static class CardWaitingReason
{
    public static TaskInfo Attach(TaskInfo task) => task with { WaitingReason = Select(task) };

    public static string? Select(TaskInfo task)
    {
        if (task.State != TaskStates.Ready) return null;
        if (task.WaitsOn is { Blocked: true } waits)
        {
            if (waits.CycleDetected) return "waits for a dependency cycle to be resolved";
            var target = waits.Items.Where(item => !item.Fulfilled)
                .OrderByDescending(item => item.Key == task.PickupHold?.AttentionTargetKey)
                .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (target is not null)
            {
                var state = target.TargetParked ? "parked" : target.TargetState switch
                {
                    TaskStates.Progress => "in progress",
                    TaskStates.Escalated => "escalated",
                    TaskStates.HumanReview => "human review",
                    TaskStates.AutoReview => "auto review",
                    TaskStates.Ready => "ready",
                    TaskStates.CodeNotComplete => "parked",
                    TaskStates.Completed => "completed",
                    TaskStates.Archive => "archived",
                    _ => target.Resolved ? "open" : "missing",
                };
                return $"waits for {target.Key} ({state})";
            }
        }
        if (task.RemoteDispatchRejection is { Code: "host-disk-pressure" } disk) return disk.Reason;
        if (task.PickupHold?.Mechanism is PickupHoldMechanisms.DispatchRejection or PickupHoldMechanisms.HostCapabilityMissing
            && task.RemoteDispatchRejection is { } rejection && rejection.RejectedAtUtc != default)
            return $"dispatch rule since {rejection.RejectedAtUtc.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC: {rejection.Reason}";
        if (task.PickupHold is { } hold)
            return hold.Mechanism is PickupHoldMechanisms.DispatchRejection or PickupHoldMechanisms.HostCapabilityMissing
                ? $"dispatch rule: {task.RemoteDispatchRejection?.Reason ?? hold.Reason}"
                : hold.Mechanism == "dispatch-plan" ? $"dispatch rule: {hold.Reason}" : hold.Reason;
        if (task.LiveStatus?.Queue is { Kind: "runner", ReasonCode: { } code } queue)
            return code switch
            {
                "slots-full" => "waits for a free slot",
                "quota-defer" or "provider-limit" => "quota",
                _ => queue.Reason ?? code.Replace('-', ' '),
            };
        return null;
    }
}
