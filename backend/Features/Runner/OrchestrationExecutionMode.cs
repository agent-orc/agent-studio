namespace AgentStudio.Runner;

/// <summary>
/// Ownership setting for an individual orchestration transition profile.
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
                "Orchestration transition mode must be 'Monolith', 'Shadow', or 'Engine'.");
    }
}
