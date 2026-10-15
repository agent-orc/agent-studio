using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Runner;

public static class OrchestrationLoopRegistration
{
    public static IServiceCollection AddOrchestrationExecutionLoops(
        this IServiceCollection services)
    {
        // AGT-3055: the batch tick runs in every mode. It owns the batch decision
        // only where BatchGatePilotService.OwnerOfBatchPlan selects Monolith; in
        // Shadow mode it also records the engine occurrence, and in Engine mode it
        // only publishes batch-plan occurrences and applies the engine intent after
        // the authority floors. Without it nothing would advance queued batches.
        services.AddHostedService<AgentStudio.Pipeline.BatchGatePilotHostedService>();
        // Keep the workers alive across operator switches. Each transition's
        // decision entry point reads its current profile; startup mode cannot
        // decide which workers will be needed after a runtime rollback.
        services.AddHostedService(provider =>
            provider.GetRequiredService<ReviewDecisionOrchestrator>());
        // AGT-2860: restart-safe resume of the post-review delivery sequence.
        // Driven from the boot recovery scan and from every deferral pass of the
        // worker below, so a card never depends on an in-memory retry counter to
        // reach its integration.
        services.AddSingleton<AutoReviewDeliveryResumeService>();
        services.AddHostedService<AutoReviewPostProcessingWorker>();
        services.AddHostedService<AutoReviewPostProcessingRecoveryService>();
        services.AddHostedService<RemoteReviewEvidenceProjectionWorker>();
        services.AddHostedService<RemoteReviewSettlementReconciler>();
        return services;
    }
}
