namespace AgentStudio.TestSupport.Scenario;

/// <summary>Budgets shared by the deployment scenario and its runner contract test.</summary>
public static class ScenarioExecutionBudgets
{
    public const int CodingRunTimeoutSeconds = 45;

    // The contract test checks this against the runner's actual shutdown budget
    // without exposing a test-only API from the separately deployed runner.
    public static readonly TimeSpan WorkerCleanupTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CompletionTransportAllowance = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CodingCompletionTimeout = TimeSpan.FromSeconds(CodingRunTimeoutSeconds)
        + WorkerCleanupTimeout + CompletionTransportAllowance;
}
