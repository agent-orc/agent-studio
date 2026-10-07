namespace AgentStudio.Pipeline;

/// <summary>
/// What one model-backed step execution ran on and spent: the effective
/// model and level, which resolution level chose it, and the four token
/// counts from the same call receipt the token ledger records. Executors build
/// it from the one-shot or CLI result they already hold, so the step row and
/// the ledger read one measurement.
/// </summary>
public sealed record StepModelUsage(
    string? Model,
    string? ThinkingLevel,
    string? ModelSource,
    long InputTokens = 0,
    long OutputTokens = 0,
    long CacheReadTokens = 0,
    long CacheCreationTokens = 0,
    bool? InputIncludesCached = null)
{
    /// <summary>
    /// Build from a one-shot call receipt. The receipt's model wins over the
    /// requested model because quota admission may have substituted another.
    /// </summary>
    public static StepModelUsage From(
        OrchestratorTokenUsage? usage,
        string? requestedModel,
        string? thinkingLevel,
        string? modelSource)
        => new(
            Model: string.IsNullOrWhiteSpace(usage?.Model) ? requestedModel : usage!.Model,
            ThinkingLevel: string.IsNullOrWhiteSpace(usage?.ThinkingLevel) ? thinkingLevel : usage!.ThinkingLevel,
            ModelSource: modelSource,
            InputTokens: usage?.InputTokens ?? 0,
            OutputTokens: usage?.OutputTokens ?? 0,
            CacheReadTokens: usage?.CacheReadTokens ?? 0,
            CacheCreationTokens: usage?.CacheCreationTokens ?? 0,
            InputIncludesCached: usage?.InputIncludesCached);

    /// <summary>Copy the model, level, source and tokens onto a step row.</summary>
    public PipelineStepExecution ApplyTo(PipelineStepExecution row) => row with
    {
        Model = string.IsNullOrWhiteSpace(Model) ? row.Model : Model,
        ThinkingLevel = string.IsNullOrWhiteSpace(ThinkingLevel) ? row.ThinkingLevel : ThinkingLevel,
        ModelSource = string.IsNullOrWhiteSpace(ModelSource) ? row.ModelSource : ModelSource,
        InputTokens = InputTokens,
        OutputTokens = OutputTokens,
        CacheReadTokens = CacheReadTokens,
        CacheCreationTokens = CacheCreationTokens,
        InputIncludesCached = InputIncludesCached ?? row.InputIncludesCached,
        CostBasis = StepCostBasis.Model,
    };
}

/// <summary>
/// Ambient record of the model behind the decision currently being carried
/// out. A decision executor opens a scope right after its model call returns;
/// everything it then records in the same async flow (the decision step row
/// and the orchestrator chat line mirrored to the bus as <c>decidedByModel</c>)
/// reads the same value. Outside a scope the decision was taken by rule.
/// </summary>
public static class DecisionModelContext
{
    private static readonly AsyncLocal<StepModelUsage?> CurrentUsage = new();

    public static StepModelUsage? Current => CurrentUsage.Value;

    public static IDisposable Use(StepModelUsage usage)
    {
        var previous = CurrentUsage.Value;
        CurrentUsage.Value = usage;
        return new Scope(previous);
    }

    private sealed class Scope(StepModelUsage? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CurrentUsage.Value = previous;
        }
    }
}
