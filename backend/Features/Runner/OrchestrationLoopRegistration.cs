using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Runner;

public static class OrchestrationLoopRegistration
{
    public static IServiceCollection AddOrchestrationExecutionLoops(
        this IServiceCollection services,
        OrchestrationExecutionMode mode)
    {
        if (mode != OrchestrationExecutionMode.Monolith)
            return services;

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
        return services;
    }
}
