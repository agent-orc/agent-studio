using AgentStudio.Pipeline;

namespace AgentStudio.Tokens;

public sealed record RoutingWhatIfRequest(DateTime From, DateTime To, string Consumer, string AlternativeModel,
    string? WorkspaceId = null, bool? EconomyMode = null);

public sealed record RoutingWhatIfResult(int Receipts, decimal? ActualUsd, decimal? AlternativeUsd,
    decimal? SavingUsd, decimal? SavingPercent, decimal? ProjectedWeeklySavingUsd,
    int UnpricedActual, int UnpricedAlternative, string Coverage, string QualityCaveat, string CorrectnessFloor);

/// <summary>Replays normalized receipt counters against prices valid when each call was made.</summary>
public static class RoutingWhatIf
{
    public static bool Valid(RoutingWhatIfRequest request) => request.From.Kind == DateTimeKind.Utc
        && request.To.Kind == DateTimeKind.Utc && request.To > request.From
        && request.To - request.From <= TimeSpan.FromDays(93)
        && request.Consumer is "coding" or "review-aspect" or "one-shot"
        && (!request.EconomyMode.HasValue || request.Consumer == "coding")
        && !string.IsNullOrWhiteSpace(request.AlternativeModel) && request.AlternativeModel.Length <= 200;

    public static RoutingWhatIfResult Calculate(ITokenPricing pricing, RoutingWhatIfRequest request,
        IReadOnlyList<UsageCostInput> inputs, Func<OrchestratorLogEntry, string?>? selectModel = null)
    {
        decimal actual = 0, alternative = 0;
        var count = 0;
        var missingActual = 0;
        var missingAlternative = 0;
        foreach (var entry in inputs.SelectMany(input => input.Entries))
        {
            var at = UsageCockpitProjection.AsUtc(entry.Ts);
            if (at < request.From.ToUniversalTime() || at >= request.To.ToUniversalTime()
                || entry.TokenUsage is not { } usage || Consumer(entry) != request.Consumer) continue;
            count++;
            var original = pricing.Estimate(usage.Model, usage.InputTokens, usage.OutputTokens,
                usage.CacheReadTokens, usage.CacheCreationTokens, at);
            var proposed = pricing.Estimate(selectModel is null ? request.AlternativeModel : selectModel(entry), usage.InputTokens, usage.OutputTokens,
                usage.CacheReadTokens, usage.CacheCreationTokens, at);
            if (original.ModelKnown) actual += original.Total; else missingActual++;
            if (proposed.ModelKnown) alternative += proposed.Total; else missingAlternative++;
        }
        decimal? saving = count > 0 && missingActual == 0 && missingAlternative == 0 ? actual - alternative : null;
        return new(count, count > 0 && missingActual == 0 ? actual : null,
            count > 0 && missingAlternative == 0 ? alternative : null, saving,
            actual > 0 && saving.HasValue ? saving / actual * 100 : null,
            saving * 7m / (decimal)(request.To - request.From).TotalDays,
            missingActual, missingAlternative,
            inputs.Any(input => input.Freshness.Status != "complete") ? "partial" : "complete",
            "Same token counts at historical prices. Model quality, thinking, retries and token usage may change; this is an estimate, not a quality guarantee.",
            "Correctness floors still apply at admission. A hypothetical price does not authorize a route below a task's floor or override a hard pin.");
    }

    public static string? EconomyModel(ModelRoutingPolicyRegistry policy, OrchestratorLogEntry entry,
        TaskDetail? card, bool enabled)
    {
        if (card is null) return null;
        if (card.Info.ModelExplicit || card.Info.PinMode == "hard") return entry.TokenUsage?.Model;
        var vendor = ModelMetadataRegistry.FindByLabelOrAlias(entry.TokenUsage?.Model)?.Vendor;
        if (vendor is null) return null;
        var cli = vendor == "anthropic" ? "claude" : vendor == "openai" ? "codex" : "gemini";
        var catalogue = new CliModelCatalog { Models = ModelMetadataRegistry.All
            .Where(model => model.Vendor == vendor && model.Available && !model.Deprecated)
            .Select(model => ModelMetadataRegistry.ToCliModelInfo(model, cli)).ToList() };
        return policy.Recommend(card.Info.TaskType, catalogue, enabled, card.Info.Title, card.PromptMarkdown).Model;
    }

    internal static string Consumer(OrchestratorLogEntry entry)
    {
        var kind = TokenUsageKindPolicy.ForLedger(entry.ParticipantId, entry.Topic, true);
        if (kind == TokenUsageKinds.ReviewAspect) return "review-aspect";
        // Projectless and orchestrator/support calls are one-shots. Do not infer coding
        // from the classifier's legacy catch-all for an unknown participant.
        return TokenParticipants.IsAgent(entry.ParticipantId) ? "coding" : "one-shot";
    }
}
