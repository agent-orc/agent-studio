namespace AgentStudio.Pipeline;

public interface IPipelineModelCatalogueProvider
{
    Task<CliModelCatalog> GetAsync(string cliType, CancellationToken ct);
}

public sealed class CliPipelineModelCatalogueProvider(AgentStudio.Cli.CliRouter router)
    : IPipelineModelCatalogueProvider
{
    public Task<CliModelCatalog> GetAsync(string cliType, CancellationToken ct)
        => router.Get(cliType).GetModelCatalogAsync(false, ct);
}

public sealed record PipelineStepEconomyRecommendation(
    string CliType,
    string Model,
    string? ThinkingLevel,
    int EstimatedSavingsPercent,
    string Basis);

/// <summary>
/// Applies the AGT-2146 SuggestModel seam to cheap pipeline calls. Spark ids
/// are discovered from the live Codex catalogue and never hardcoded.
/// </summary>
public sealed class PipelineStepEconomyAdvisor(
    IModelEconomyAdvisor economy,
    IPipelineModelCatalogueProvider catalogues,
    ILogger<PipelineStepEconomyAdvisor> logger)
{
    public async Task<PipelineStepEconomyRecommendation?> SuggestModelAsync(
        ProjectSettings? settings,
        string stepId,
        CancellationToken ct,
        bool forceEconomy = false)
    {
        var configured = PipelineStepConfigResolver.Lookup(settings, stepId);
        if ((!forceEconomy && configured?.EconomyModel != true)
            || !string.IsNullOrWhiteSpace(configured?.Model)) return null;

        try
        {
            var catalogue = await catalogues.GetAsync(CliTypes.Codex, ct);
            var sparkModels = catalogue.Models
                .Where(model => model.Available
                    && !model.Deprecated
                    && (model.Id.Contains("spark", StringComparison.OrdinalIgnoreCase)
                        || model.Label.Contains("spark", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (sparkModels.Count == 0)
            {
                logger.LogWarning(
                    "pipeline_model_economy_unavailable step={StepId} cli={CliType} catalogue={CatalogueSource}",
                    stepId, CliTypes.Codex, catalogue.Source ?? "unknown");
                return null;
            }

            var suggestion = economy.SuggestModel(sparkModels, TaskComplexity.Small);
            logger.LogInformation(
                "pipeline_model_economy_recommendation step={StepId} cli={CliType} model={Model} thinking={ThinkingLevel} savingsPct={SavingsPercent} catalogue={CatalogueSource}",
                stepId, CliTypes.Codex, suggestion.Model, suggestion.ThinkingLevel,
                suggestion.EstimatedSavingsPercent, catalogue.Source ?? "unknown");
            return new PipelineStepEconomyRecommendation(
                CliTypes.Codex,
                suggestion.Model,
                suggestion.ThinkingLevel,
                suggestion.EstimatedSavingsPercent,
                suggestion.Basis);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "pipeline_model_economy_failed step={StepId}; preserving configured runtime default",
                stepId);
            return null;
        }
    }
}
