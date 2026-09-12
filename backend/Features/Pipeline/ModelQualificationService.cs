namespace AgentStudio.Pipeline;

public enum TaskComplexity
{
    Small,
    Medium,
    Large,
}

/// <summary>
/// Replaceable economy seam. The built-in implementation ranks the live CLI
/// catalogue; a TokenEconomy adapter can implement the same contract once its
/// package is available without changing runner or persistence code.
/// </summary>
public interface IModelEconomyAdvisor
{
    ModelEconomySuggestion SuggestModel(
        IReadOnlyList<CliModelInfo> availableModels,
        TaskComplexity complexity);
}

public sealed record ModelEconomySuggestion(
    string Model,
    string? ThinkingLevel,
    int EstimatedSavingsPercent,
    string Basis);

/// <summary>
/// Local, zero-token fallback for TokenEconomy.SuggestModel. Model ids and
/// reasoning ladders come exclusively from the CLI catalogue.
/// </summary>
public sealed class CatalogueModelEconomyAdvisor : IModelEconomyAdvisor
{
    public ModelEconomySuggestion SuggestModel(
        IReadOnlyList<CliModelInfo> availableModels,
        TaskComplexity complexity)
    {
        var models = availableModels.Where(model => model.Available && !model.Deprecated).ToList();
        if (models.Count == 0) throw new InvalidOperationException("The CLI reported no available models.");

        // CLI catalogues are already capability ordered (best/default first).
        // Select a rung, never a concrete id, so newly discovered models enter
        // the ladder without a Studio release.
        var index = complexity switch
        {
            TaskComplexity.Large => 0,
            TaskComplexity.Medium => Math.Min(models.Count - 1, models.Count / 2),
            _ => models.Count - 1,
        };
        var selected = models[index];
        var levels = selected.ThinkingLevels ?? [];
        var levelIndex = complexity switch
        {
            TaskComplexity.Large => Math.Max(0, levels.Count - 1),
            TaskComplexity.Medium => Math.Max(0, levels.Count / 2),
            _ => 0,
        };
        var level = levels.Count == 0 ? selected.DefaultThinkingLevel : levels[levelIndex];
        var savings = models.Count <= 1 || index == 0
            ? 0
            : Math.Clamp((int)Math.Round(65d * index / (models.Count - 1)), 10, 65);
        return new ModelEconomySuggestion(
            selected.Id,
            level,
            savings,
            "live CLI model ladder + local economy policy");
    }
}

public sealed record ModelQualificationDecision
{
    public DateTime At { get; init; }
    public string Event { get; init; } = "decision";
    public string DecisionId { get; init; } = Guid.NewGuid().ToString("N");
    public string JobId { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public string CliType { get; init; } = string.Empty;
    public string TaskType { get; init; } = string.Empty;
    public string PolicyVersion { get; init; } = string.Empty;
    public string PolicyTier { get; init; } = string.Empty;
    public string PolicyWikiPath { get; init; } = string.Empty;
    public bool EconomyMode { get; init; }
    public bool EconomyDowngraded { get; init; }
    public string? CorrectnessFloorTier { get; init; }
    public string Surface { get; init; } = string.Empty;
    public string Complexity { get; init; } = string.Empty;
    public int Score { get; init; }
    public string RecommendedModel { get; init; } = string.Empty;
    public string? RecommendedThinkingLevel { get; init; }
    public string SelectedModel { get; init; } = string.Empty;
    public string? SelectedThinkingLevel { get; init; }
    public string SelectionSource { get; init; } = string.Empty;
    public int EstimatedSavingsPercent { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string CatalogueSource { get; init; } = string.Empty;
    public IReadOnlyList<BetterModelCandidate> BetterCandidates { get; init; } = [];
}

public sealed record ModelQualificationOutcome
{
    public DateTime At { get; init; }
    public string Event { get; init; } = "outcome";
    public string JobId { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public string? Model { get; init; }
    public string? ThinkingLevel { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? Verdict { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public int Attempt { get; init; }
}

public sealed class ModelQualificationService
{
    public const string LogFileName = "model-qualification.jsonl";

    private readonly ModelRoutingPolicyRegistry _policy;
    private readonly IModelRoutingModeProvider _routingMode;
    private readonly IJsonlAppender _jsonl;
    private readonly ILogger<ModelQualificationService> _logger;
    private readonly BetterModelCandidateService? _betterCandidates;
    private readonly AgentStudio.Projects.ProjectSettingsService? _projectSettings;

    public ModelQualificationService(
        ModelRoutingPolicyRegistry policy,
        IModelRoutingModeProvider routingMode,
        IJsonlAppender jsonl,
        ILogger<ModelQualificationService> logger,
        BetterModelCandidateService? betterCandidates = null,
        AgentStudio.Projects.ProjectSettingsService? projectSettings = null)
    {
        _policy = policy;
        _routingMode = routingMode;
        _jsonl = jsonl;
        _logger = logger;
        _betterCandidates = betterCandidates;
        _projectSettings = projectSettings;
    }

    public ModelQualificationDecision Qualify(
        TaskInfo task,
        string prompt,
        CliModelCatalog catalogue,
        IReadOnlyList<TaskInfo> projectHistory,
        DateTime? nowUtc = null)
    {
        _ = projectHistory;
        var recommendation = _policy.Recommend(
            task.TaskType,
            catalogue,
            _routingMode.EconomyMode,
            task.Title,
            prompt);
        var complexity = recommendation.Tier switch
        {
            "luna-medium" => TaskComplexity.Small,
            "terra-medium" => TaskComplexity.Medium,
            _ => TaskComplexity.Large,
        };
        var surface = recommendation.CorrectnessFloorTier switch
        {
            "sol-xhigh" => "correctness-critical",
            "sol-medium" => "cross-subsystem contract",
            _ => "task-type convention",
        };

        var modelExplicit = task.ModelExplicit;
        var thinkingExplicit = task.ThinkingLevelExplicit;
        var selectedModel = modelExplicit && !string.IsNullOrWhiteSpace(task.Model)
            ? task.Model!
            : recommendation.Model;
        var selectedThinking = thinkingExplicit
            ? task.ThinkingLevel
            : string.Equals(selectedModel, recommendation.Model, StringComparison.OrdinalIgnoreCase)
                ? recommendation.ThinkingLevel
                : ModelMetadataRegistry.ResolveThinkingLevel(task.CliType, selectedModel, recommendation.ThinkingLevel);
        var source = modelExplicit || thinkingExplicit
            ? "task-override"
            : recommendation.EconomyDowngraded ? "policy-economy" : "policy";
        var reason = $"{recommendation.Reason}; " +
                     (source == "task-override"
                         ? $"card override wins, selected {selectedModel} at {selectedThinking ?? "model default"}"
                         : $"selected policy recommendation; expected saving about {recommendation.EstimatedSavingsPercent}% vs top rung");
        var capabilityClass = _projectSettings?.Get(task.ProjectName).BenchmarkCapabilityClass ?? "CodingAgent";
        var candidates = _betterCandidates?.Find(selectedModel, selectedThinking, capabilityClass, nowUtc) ?? [];

        return new ModelQualificationDecision
        {
            At = nowUtc ?? DateTime.UtcNow,
            JobId = task.Id,
            Project = task.ProjectName,
            CliType = CliTypes.Normalize(task.CliType),
            TaskType = task.TaskType,
            PolicyVersion = recommendation.PolicyVersion,
            PolicyTier = recommendation.Tier,
            PolicyWikiPath = recommendation.PolicyWikiPath,
            EconomyMode = recommendation.EconomyMode,
            EconomyDowngraded = recommendation.EconomyDowngraded,
            CorrectnessFloorTier = recommendation.CorrectnessFloorTier,
            Surface = surface,
            Complexity = complexity.ToString().ToLowerInvariant(),
            Score = recommendation.Score,
            RecommendedModel = recommendation.Model,
            RecommendedThinkingLevel = recommendation.ThinkingLevel,
            SelectedModel = selectedModel,
            SelectedThinkingLevel = selectedThinking,
            SelectionSource = source,
            EstimatedSavingsPercent = source == "task-override" ? 0 : recommendation.EstimatedSavingsPercent,
            Reason = reason,
            CatalogueSource = catalogue.Source ?? "unknown",
            BetterCandidates = candidates,
        };
    }

    public async Task RecordDecisionAsync(string jobFolder, ModelQualificationDecision decision, CancellationToken ct)
    {
        BetterCandidateDecisionStore.Append(jobFolder, new BetterCandidateDecisionSnapshot
        {
            At = decision.At,
            Model = decision.SelectedModel,
            Effort = decision.SelectedThinkingLevel,
            Source = "model-qualification",
            Candidates = decision.BetterCandidates,
        });
        try
        {
            await _jsonl.AppendAsync(Path.Combine(jobFolder, LogFileName), decision, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "model-qualification decision log failed for {JobId}", decision.JobId);
        }
    }

    public async Task RecordOutcomeAsync(string jobFolder, ModelQualificationOutcome outcome)
    {
        try
        {
            await _jsonl.AppendAsync(Path.Combine(jobFolder, LogFileName), outcome);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "model-qualification outcome log failed for {JobId}", outcome.JobId);
        }
    }

}
