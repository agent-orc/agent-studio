using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// MS-5: the runner composes the claimed run route with the shared
/// model-selection planner from the contracts package.
/// </summary>
public sealed class ModelSelectionPlannerReferenceTests
{
    [Fact]
    public void Runner_takes_the_claim_route_rule_from_the_contracts_planner()
    {
        var contracts = typeof(ModelSelectionPlanner).Assembly.GetName().Name;
        Assert.Contains(typeof(AgentRunner.TaskServerClient).Assembly.GetReferencedAssemblies(), name => name.Name == contracts);
        var source = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "runner", "TaskServerClient.cs"));
        Assert.Contains("ModelSelectionPlanner.ClaimRoute(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_refusal_fallback_alone_runs_in_a_clean_context()
    {
        var fallback = new ProviderModelFallback("gpt-6-sol", "gpt-5.6-sol", "capacity", "codex", "high");

        var route = ModelSelectionPlanner.ClaimRoute(null, null, fallback);

        Assert.Equal(new ClaimRouteSelection("codex", "gpt-5.6-sol", "high", CleanContext: true, HasRoute: true), route);
    }
}
