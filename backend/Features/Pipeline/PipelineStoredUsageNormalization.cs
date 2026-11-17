using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>Read compatibility for pipeline counters captured before input provenance existed.</summary>
internal static class PipelineStoredUsageNormalization
{
    /// <summary>Cost-only view of executions; shared telemetry readers keep recorded counters.</summary>
    public static IReadOnlyList<PipelineStepRunSummary> CostExecutions(
        ITokenPricing pricing, PipelineStepExecution step)
        => StepCostMeasurement.Executions(pricing, step).Select(Normalize).ToList();

    public static PipelineStepExecution Normalize(PipelineStepExecution step)
    {
        if (!NeedsNormalization(step.Model, step.InputTokens, step.CacheReadTokens,
                step.InputIncludesCached, step.UsageNormalization)) return step;

        return step with
        {
            InputTokens = ProviderUsageNormalization.OpenAi(step.InputTokens, step.CacheReadTokens).InputTokens,
            InputIncludesCached = true,
            UsageNormalization = ProviderUsageNormalization.OpenAiInputIncludesCachedV1,
        };
    }

    public static PipelineStepRunSummary Normalize(PipelineStepRunSummary run)
    {
        if (!NeedsNormalization(run.Model, run.InputTokens, run.CacheReadTokens,
                run.InputIncludesCached, run.UsageNormalization)) return run;

        return run with
        {
            InputTokens = ProviderUsageNormalization.OpenAi(run.InputTokens, run.CacheReadTokens).InputTokens,
            InputIncludesCached = true,
            UsageNormalization = ProviderUsageNormalization.OpenAiInputIncludesCachedV1,
        };
    }

    private static bool NeedsNormalization(string? model, long input, long cacheRead,
        bool? inputIncludesCached, string? marker)
        => inputIncludesCached is null
           && !string.Equals(marker, ProviderUsageNormalization.OpenAiInputIncludesCachedV1, StringComparison.Ordinal)
           && ProviderUsageNormalization.IsOpenAiModel(model)
           && cacheRead > 0
           && input >= cacheRead;
}
