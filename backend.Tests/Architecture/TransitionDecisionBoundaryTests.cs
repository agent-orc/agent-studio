using System.Text.RegularExpressions;
using AgentStudio.Orchestration.Decisions;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests.Architecture;

[Trait("Category", "Guard")]
public sealed class TransitionDecisionBoundaryTests
{
    private static readonly string[] OwnedPolicies =
    [
        "ReviewFollowUpPolicy", "ReviewRoundBudgetPolicy", "CardRoundBudgetPolicy",
        "OperatorSweepPolicy", "ReissueLoopBreaker", "RemoteDeliveryIntegrationPolicy",
        "RunTimeoutSalvageContinuationPolicy", "ReviewInfrastructureRepeatPolicy",
        "OrchestrationSettlementPolicy", "RepeatedReviewBlockPolicy", "StageDecisionPolicy",
        "CouncilDecisionPolicy", "LocalLoopDecisionPolicy", "ReviewBudgetPolicy", "TransitionDecisionPolicy",
        "RoundCounterPolicy", "ContinuationReplayPolicy", "RepeatedAspectDecisionPolicy",
    ];

    [Fact]
    public void Decision_types_are_owned_by_one_pure_assembly()
    {
        var assembly = typeof(TransitionDecisionPolicy).Assembly;
        Assert.Equal(assembly, typeof(ReviewFollowUpPolicy).Assembly);
        Assert.Equal(assembly, typeof(ReviewRoundBudgetPolicy).Assembly);
        Assert.Equal(assembly, typeof(OperatorSweepPolicy).Assembly);
        Assert.Equal(assembly, typeof(RemoteDeliveryIntegrationPolicy).Assembly);
        var referenced = assembly.GetReferencedAssemblies().Select(item => item.Name!).ToArray();
        Assert.DoesNotContain(referenced, name => name is "OrchestratorApi" or "task-server" or "orchestrator-engine"
            || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || name.StartsWith("System.Net.Http", StringComparison.Ordinal)
            || name.StartsWith("System.Diagnostics.Process", StringComparison.Ordinal));
    }

    [Fact]
    public void Hosts_cannot_define_transition_policies_or_construct_new_transition_decisions()
    {
        var root = RepositoryRoot.Find();
        var violations = new List<string>();
        foreach (var directory in new[] { "backend", "task-server", "orchestrator-engine", "contracts" })
        foreach (var file in Sources(Path.Combine(root, directory)))
        {
            var source = File.ReadAllText(file);
            if (DefinesRule(source)) violations.Add(Path.GetRelativePath(root, file));
        }
        Assert.True(violations.Count == 0, "Transition rules belong in orchestration-decisions: " + string.Join(", ", violations));
    }

    [Theory]
    [InlineData("class ReviewRoundBudgetPolicy { }")]
    [InlineData("return new TransitionDecision(TransitionAction.Escalate, reason);")]
    [InlineData("return used >= allowed ? TransitionAction.Escalate : TransitionAction.FindingRound;")]
    [InlineData("var lane = outcome switch { \"done\" => TaskStates.AutoReview };")]
    public void Guard_rejects_reintroduced_host_rules(string source) => Assert.True(DefinesRule(source));

    [Fact]
    public void Library_cannot_read_environment_or_boundaries()
    {
        var forbidden = new Regex(@"\b(File|Directory|Process|HttpClient|IConfiguration|Environment)\s*[.(]|\bDateTime(?:Offset)?\.(?:UtcNow|Now)\b");
        foreach (var file in Sources(Path.Combine(RepositoryRoot.Find(), "orchestration-decisions")))
            Assert.False(forbidden.IsMatch(File.ReadAllText(file)), Path.GetFileName(file));
    }

    private static bool DefinesRule(string source)
    {
        var owned = string.Join("|", OwnedPolicies);
        return Regex.IsMatch(source, @"\bclass\s+(?:" + owned + @")\b")
            || Regex.IsMatch(source, @"\bnew\s+TransitionDecision\s*\(")
            || Regex.IsMatch(source, @"\?\s*TransitionAction\.")
            || Regex.IsMatch(source, @"\boutcome\s+switch\s*\{[^}]*=>\s*TaskStates\.", RegexOptions.Singleline);
    }

    private static IEnumerable<string> Sources(string path) => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
        .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"));
}
