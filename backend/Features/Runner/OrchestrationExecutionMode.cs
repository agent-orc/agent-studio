namespace AgentStudio.Runner;

/// <summary>
/// Transitional ownership switch for orchestration flow execution. Exactly one
/// process family owns review, council, post-processing, gate, and completion
/// loops: the legacy monolith or the API-only orchestrator-engine.
/// </summary>
public enum OrchestrationExecutionMode
{
    Monolith,
    Shadow,
    Engine,
}

public static class OrchestrationExecutionModeParser
{
    public static OrchestrationExecutionMode Parse(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return OrchestrationExecutionMode.Monolith;
        return Enum.TryParse<OrchestrationExecutionMode>(configured, true, out var mode)
            ? mode
            : throw new InvalidOperationException(
                "Orchestration:ExecutionMode must be 'Monolith', 'Shadow', or 'Engine'.");
    }
}
