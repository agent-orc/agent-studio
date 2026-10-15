namespace AgentStudio.Runner;

/// <summary>
/// AGT-3060 (I18, AGT-W65 D14 A): the Orchestrator Engine is the only decider.
/// A production host pins every transition profile and every AI Operator
/// decider to the engine. The Monolith and Shadow decision modes, and with them
/// the rollout's rollback to the monolith policy, remain available only to test
/// hosts so the existing decision tables keep their in-process coverage.
/// </summary>
public static class OrchestrationDecisionAuthority
{
    public const string ConfigurationKey = "Orchestration:DecisionAuthority";
    public const string Engine = "Engine";
    public const string RetiredError = "orchestration-monolith-decisions-retired";

    /// <summary>True when only the engine may decide (every non-test host).</summary>
    public static bool IsEngineOnly(IConfiguration configuration)
        => string.Equals(configuration[ConfigurationKey], Engine, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Called once by a non-test host before services start. A configured
    /// Monolith or Shadow transition fails startup instead of silently deciding
    /// in the monolith or silently being ignored.
    /// </summary>
    public static void RequireEngine(ConfigurationManager configuration)
    {
        var configured = configuration.GetSection("Orchestration:Transitions").GetChildren()
            .Where(item => !string.IsNullOrWhiteSpace(item.Value)
                && OrchestrationExecutionModeParser.Parse(item.Value) != OrchestrationExecutionMode.Engine)
            .Select(item => $"{item.Key}={item.Value}")
            .ToArray();
        if (configured.Length > 0)
            throw new InvalidOperationException(
                "Orchestration:Transitions accepts only Engine outside a test host; the Orchestrator Engine "
                + "is the only decider (AGT-W65 D14). Remove: " + string.Join(", ", configured));
        // The last source wins over appsettings reloads and environment values.
        configuration.AddInMemoryCollection([new(ConfigurationKey, Engine)]);
    }
}
