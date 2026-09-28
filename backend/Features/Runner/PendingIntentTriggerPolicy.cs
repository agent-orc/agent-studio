namespace AgentStudio.Runner;

/// <summary>Shared business provenance for local pickup and remote claims of queued work.</summary>
internal static class PendingIntentTriggerPolicy
{
    public static RunTriggerMetadata Resolve(PendingIntent intent, string? ownerClientId)
    {
        var reason = intent.SavedReason ?? string.Empty;
        var trigger = reason.Contains("integration", StringComparison.OrdinalIgnoreCase)
            ? RunTriggers.IntegrationRecovery
            : reason.Contains("timeout", StringComparison.OrdinalIgnoreCase)
              || reason.Contains("salvage", StringComparison.OrdinalIgnoreCase)
                ? RunTriggers.TimeoutContinuation
            : reason.Contains("loop-continuation", StringComparison.OrdinalIgnoreCase)
                ? RunTriggers.Replan
            : reason.Contains("crash", StringComparison.OrdinalIgnoreCase)
              || reason.Contains("provider", StringComparison.OrdinalIgnoreCase)
                ? RunTriggers.RecoveryAfterCrash
            : reason.Contains("gate", StringComparison.OrdinalIgnoreCase)
                ? RunTriggers.GateFailure
            : RunTriggers.OperatorContinue;
        var actor = trigger switch
        {
            RunTriggers.TimeoutContinuation => "watchdog",
            RunTriggers.OperatorContinue => intent.TriggeredBy ?? $"operator {ownerClientId ?? "local-default"}",
            _ => "pipeline",
        };
        var sentence = trigger switch
        {
            RunTriggers.IntegrationRecovery => $"Integration recovery was queued after {reason}.",
            RunTriggers.TimeoutContinuation => "The watchdog queued a bounded continuation after a timed-out run.",
            RunTriggers.Replan => "The pipeline queued the orchestrator's answer to an agent planning question.",
            RunTriggers.RecoveryAfterCrash => $"The pipeline queued recovery after {reason}.",
            RunTriggers.GateFailure => $"The pipeline queued a continuation after {reason}.",
            _ => intent.TriggerReason ?? "An operator continuation was queued while the task could not start immediately.",
        };
        return new RunTriggerMetadata(
            trigger,
            actor,
            sentence,
            $"reason={reason}; prompt={RunTriggerMetadata.PromptPreview(intent.Prompt)}");
    }
}
