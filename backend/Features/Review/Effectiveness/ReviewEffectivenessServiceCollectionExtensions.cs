using AgentStudio.Pipeline;

namespace AgentStudio.Review;

public static class ReviewEffectivenessServiceCollectionExtensions
{
    /// <summary>The review-effectiveness ledger and its pipeline step observer (AGT-3269).</summary>
    public static IServiceCollection AddReviewEffectiveness(this IServiceCollection services)
    {
        services.AddSingleton<ReviewEffectivenessStore>();
        services.AddSingleton<ReviewEffectivenessService>();
        services.AddSingleton<IPipelineStepObserver>(sp => sp.GetRequiredService<ReviewEffectivenessService>());
        return services;
    }
}
