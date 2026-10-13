namespace AgentStudio.Tokens;

/// <summary>
/// Attributes agent calls that recorded no model to the run's model and
/// prices them at that model. Calls that recorded a model, and calls of
/// other participants, keep their own model and price.
/// </summary>
public sealed class RunModelPricing(ITokenPricing pricing)
{
    public TaskTokenSummary Apply(TaskTokenSummary summary, string? runModel)
    {
        var fallbackLabel = ModelIdentity.Label(runModel);
        if (string.IsNullOrWhiteSpace(fallbackLabel)) return summary;
        var fallbackId = ModelIdentity.StoredId(runModel);

        var entries = summary.Entries
            .Select(call => RecordedNoModel(call)
                ? Reprice(call with { Model = fallbackId, DisplayModel = fallbackLabel }, runModel)
                : call)
            .ToList();
        var hasAgentFallbackRow = entries.Any(call =>
            string.Equals(call.Model, fallbackId, StringComparison.Ordinal)
            && TokenParticipants.IsAgent(call.ParticipantId));
        var adoptsRunModel = string.IsNullOrWhiteSpace(summary.LastModel) && hasAgentFallbackRow;

        return summary with
        {
            LastModel = adoptsRunModel ? fallbackLabel : summary.LastModel,
            LastModelId = adoptsRunModel ? fallbackId : summary.LastModelId,
            Entries = entries,
            EstimatedApiCostUsd = entries.Sum(call => call.EstimatedApiCostUsd),
            AllModelsPriced = entries.Count > 0 && entries.All(call => call.ModelPriced),
        };
    }

    private TaskTokenCall Reprice(TaskTokenCall call, string? runModel)
    {
        var cost = pricing.Estimate(runModel, call.InputTokens, call.OutputTokens,
            call.CacheReadTokens, call.CacheCreationTokens, call.Ts);
        return call with
        {
            EstimatedApiCostUsd = cost.Total,
            ModelPriced = cost.ModelKnown,
        };
    }

    private static bool RecordedNoModel(TaskTokenCall call)
        => TokenParticipants.IsAgent(call.ParticipantId)
           && string.IsNullOrWhiteSpace(ModelIdentity.Label(call.Model));
}
