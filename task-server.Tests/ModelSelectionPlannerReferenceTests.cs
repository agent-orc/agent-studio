using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// MS-5: the Task Server decides provider-refusal siblings with the shared
/// model-selection planner from the contracts package instead of its own copy.
/// </summary>
public sealed class ModelSelectionPlannerReferenceTests
{
    [Fact]
    public void Task_server_takes_the_rejection_sibling_rule_from_the_contracts_planner()
    {
        var contracts = typeof(ModelSelectionPlanner).Assembly.GetName().Name;
        Assert.Contains(typeof(TaskServerStore).Assembly.GetReferencedAssemblies(), name => name.Name == contracts);
        var source = File.ReadAllText(Path.Combine(ProtocolTests.RepositoryRoot(), "task-server", "ProviderRejectionRecovery.cs"));
        Assert.Contains("ModelSelectionPlanner.RejectionSibling(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejection_sibling_matches_the_declared_cli_and_model_as_given()
    {
        ProviderModelFallback[] declared =
        [
            new("gpt-6-sol", "gpt-5.6-sol", "capacity", "codex", null),
            new("claude-opus-5-5", "claude-opus-5", "capacity", "claude", null),
        ];

        Assert.Same(declared[0], ModelSelectionPlanner.RejectionSibling(
            declared, item => (item.CliType, item.From), "codex", "GPT-6-SOL"));
        Assert.Same(declared[1], ModelSelectionPlanner.RejectionSibling(
            declared, item => (item.CliType, item.From), "claude", "claude-opus-5-5"));
        Assert.Null(ModelSelectionPlanner.RejectionSibling(
            declared, item => (item.CliType, item.From), "claude", "gpt-6-sol"));
        Assert.Null(ModelSelectionPlanner.RejectionSibling(
            declared, item => (item.CliType, item.From), "codex", "gpt-6.1-sol"));
    }
}
