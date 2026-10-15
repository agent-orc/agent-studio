using System.Text.RegularExpressions;
using AgentStudio.OrchestrationDecisions;
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
        "IntegrationResultPolicy",
        // AGT-3060 (I18): background deciders moved out of the monolith.
        "AiOperatorDecisionPolicy", "RunLivenessPolicy", "SteerTimeoutPolicy", "StaleProgressPolicy",
        "GateEnvironmentRetryPolicy", "GateEnvironmentContinuationPolicy",
        "DeliveryChainPolicy", "CauseBreakerClosePolicy", "IntakeRoutingPolicy", "AcceptanceRailDecisionPolicy",
    ];

    [Fact]
    public void Decision_types_are_owned_by_one_pure_assembly()
    {
        var assembly = typeof(TransitionDecisionPolicy).Assembly;
        Assert.Equal(assembly, typeof(ReviewFollowUpPolicy).Assembly);
        Assert.Equal(assembly, typeof(ReviewRoundBudgetPolicy).Assembly);
        Assert.Equal(assembly, typeof(OperatorSweepPolicy).Assembly);
        Assert.Equal(assembly, typeof(RemoteDeliveryIntegrationPolicy).Assembly);
        Assert.Equal(assembly, typeof(AiOperatorDecisionPolicy).Assembly);
        Assert.Equal(assembly, typeof(RunLivenessPolicy).Assembly);
        Assert.Equal(assembly, typeof(GateEnvironmentRetryPolicy).Assembly);
        Assert.Equal(assembly, typeof(DeliveryChainPolicy).Assembly);
        Assert.Equal(assembly, typeof(AcceptanceRailDecisionPolicy).Assembly);
        var referenced = assembly.GetReferencedAssemblies().Select(item => item.Name!).ToArray();
        Assert.DoesNotContain(referenced, name => name is "OrchestratorApi" or "task-server" or "orchestrator-engine"
            || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || name.StartsWith("System.Net.Http", StringComparison.Ordinal)
            || name.StartsWith("System.Diagnostics.Process", StringComparison.Ordinal));
    }

    [Fact]
    public void Library_has_one_namespace_and_no_host_friend_assemblies()
    {
        var root = RepositoryRoot.Find();
        foreach (var file in Sources(Path.Combine(root, "orchestration-decisions")))
        {
            var declarations = Regex.Matches(File.ReadAllText(file), @"\bnamespace\s+([\w.]+)")
                .Select(match => match.Groups[1].Value).ToArray();
            if (Path.GetFileName(file) == "GlobalUsings.cs")
                Assert.Empty(declarations);
            else
                Assert.Equal("AgentStudio.OrchestrationDecisions", Assert.Single(declarations));
        }
        var project = File.ReadAllText(Path.Combine(root, "orchestration-decisions", "Orchestration.Decisions.csproj"));
        Assert.DoesNotContain("InternalsVisibleTo", project, StringComparison.Ordinal);
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

    [Fact]
    public void Transition_entry_points_call_the_shared_decision_library()
    {
        var root = RepositoryRoot.Find();
        var adapter = File.ReadAllText(Path.Combine(root, "backend/Features/Runner/MonolithTransitionAdapter.cs"));
        Assert.Matches(@"Decide\(TransitionFacts facts\)\s*=>\s*TransitionDecisionPolicy\.Decide\(facts\)", adapter);
        Assert.Matches(@"ReviewBudget\(ReviewBudgetFacts facts\)\s*=>\s*ReviewBudgetPolicy\.Decide\(facts\)", adapter);
        Assert.Matches(@"Sweep\([^)]*\)\s*=>\s*OperatorSweepPolicy\.Decide\(", adapter);
        foreach (var path in new[] { "task-server/TaskServerTransitionAdapter.cs", "orchestrator-engine/EngineTransitionAdapter.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, path));
            Assert.Matches(@"Decide\(TransitionFacts facts\)\s*=>\s*TransitionDecisionPolicy\.Decide\(facts\)", source);
        }

        // Follow the returned value into the application path. A stray
        // adapter call elsewhere in the file cannot satisfy these checks.
        var decisionFlows = new Dictionary<string, string[]>
        {
            ["backend/Features/Runner/MonolithOrchestrationEndpoints.cs"] =
                [@"transition\s*=\s*MonolithTransitionAdapter\.Decide\([\s\S]{0,1500}StageDecisionPolicy\.ToStageAction\(transition\.Action\)"],
            ["backend/Features/Runner/V1ReviewPlaneEndpoints.cs"] =
                [@"var\s+budgetDecision\s*=\s*MonolithTransitionAdapter\.ReviewBudget\([\s\S]{0,1800}var\s+roundBudget\s*=\s*budgetDecision\.RoundBudget",
                 @"var\s+shadowDecision\s*=\s*MonolithTransitionAdapter\.Decide\([\s\S]{0,900}monolithAction:\s*shadowDecision\.Action"],
            ["backend/Features/Runner/ReviewDecisionOrchestrator.cs"] =
                [@"var\s+budgetDecision\s*=\s*MonolithTransitionAdapter\.ReviewBudget\([\s\S]{0,1800}var\s+roundBudget\s*=\s*budgetDecision\.RoundBudget"],
            ["backend/Features/Runner/OperatorSweeps/OperatorSweepService.cs"] =
                [@"var\s+decision\s*=\s*MonolithTransitionAdapter\.Sweep\([\s\S]{0,250}decision\.Action"],
        };
        foreach (var (path, flows) in decisionFlows)
        {
            var source = File.ReadAllText(Path.Combine(root, path));
            foreach (var flow in flows)
                Assert.Matches(flow, source);
        }

        // Every exported Decide method is a transition decision entry point,
        // regardless of its result type. This also covers policies added later.
        var directCalls = Sources(Path.Combine(root, "backend"))
            .Where(path => !path.EndsWith("MonolithTransitionAdapter.cs", StringComparison.Ordinal))
            .SelectMany(path => DirectDecisionCalls(File.ReadAllText(path))
                .Select(call => $"{Path.GetRelativePath(root, path)}: {call}"))
            .ToArray();
        Assert.True(directCalls.Length == 0, string.Join(", ", directCalls));
        var batch = File.ReadAllText(Path.Combine(root, "backend/Features/Pipeline/BatchGate/BatchGatePilotService.cs"));
        Assert.Matches(@"TransitionDecision BatchShadowDecision\([^)]*\)\s*=>\s*AgentStudio\.Runner\.MonolithTransitionAdapter\.StageGateTransition\(", batch);
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

    [Theory]
    [InlineData("ReviewFollowUpPolicy.Decide(findings, 0, 1)", "ReviewFollowUpPolicy.Decide(")]
    [InlineData("AgentStudio.OrchestrationDecisions.GateCancellationPolicy.Decide(a, b, c, d, false, false)", "GateCancellationPolicy.Decide(")]
    [InlineData("StageDecisionPolicy.Coding(task, attempt, state)", "StageDecisionPolicy.Coding(")]
    [InlineData("CardRoundBudgetPolicy.DecideForReview(budget, aspects, true)", "CardRoundBudgetPolicy.DecideForReview(")]
    public void Direct_call_guard_covers_non_receipt_decisions(string source, string expected)
        => Assert.Contains(expected, DirectDecisionCalls(source));

    private static string[] DirectDecisionCalls(string source)
    {
        var entryPoints = typeof(TransitionDecisionPolicy).Assembly.GetExportedTypes()
            .Where(type => type.Name.EndsWith("Policy", StringComparison.Ordinal))
            .SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(method => method.Name.StartsWith("Decide", StringComparison.Ordinal)
                    || method.ReturnType == typeof(TransitionDecision)
                    || method.ReturnType == typeof(ReviewBudgetDecision)
                    || method.ReturnType == typeof(OperatorSweepDecision)
                    || method.ReturnType == typeof(AiOperatorDecision)
                    || method.ReturnType == typeof(StagePolicyDecision))
                .Select(method => Regex.Escape(type.Name) + @"\." + Regex.Escape(method.Name)))
            .Distinct()
            .ToArray();
        var pattern = @"\b(?:" + string.Join("|", entryPoints) + @")\s*\(";
        return Regex.Matches(source, pattern).Select(match => match.Value).ToArray();
    }

    private static IEnumerable<string> Sources(string path) => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
        .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"));
}
