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
        services.AddHostedService<AutoReviewPostProcessingWorker>();
        services.AddHostedService<AutoReviewPostProcessingRecoveryService>();
        services.AddHostedService<RemoteReviewEvidenceProjectionWorker>();
        return services;
    }
}
