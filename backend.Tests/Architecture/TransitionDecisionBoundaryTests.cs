using System.Text.RegularExpressions;
using AgentStudio.Orchestration.Decisions;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace OrchestratorApi.Tests.Architecture;

[Trait("Category", "Guard")]
public sealed class TransitionDecisionBoundaryTests
{
    [Fact]
    public void Transition_policy_types_are_owned_by_the_decision_library()
    {
        var owner = typeof(TransitionDecisionPolicy).Assembly;
        Type[] policies = [typeof(ReviewFollowUpPolicy), typeof(ReviewRoundBudgetPolicy),
            typeof(CardRoundBudgetPolicy), typeof(OperatorSweepPolicy), typeof(SweepTransitionPolicy), typeof(ReissueLoopBreaker), typeof(RepeatedAspectBlockRules),
            typeof(RemoteDeliveryIntegrationPolicy), typeof(RunTimeoutSalvageContinuationPolicy),
            typeof(LostWorkerContinuationPolicy), typeof(CodingCompletionPolicy),
            typeof(AgentStudio.Tasks.RemoteCompletionEnvelopePolicy), typeof(RemoteDeliveryFailurePolicy),
            typeof(CompletionEvidencePolicy), typeof(GateTransitionPolicy), typeof(AutomaticRoundPolicy), typeof(AgentStudio.Review.CouncilDecisionPolicy),
            typeof(AgentStudio.TaskServer.OrchestrationSettlementRules),
            typeof(AgentStudio.TaskServer.RepeatedReviewBlockRules), typeof(StageTransitionPolicy)];
        Assert.All(policies, type => Assert.Same(owner, type.Assembly));
    }

    [Fact]
    public void Decision_library_has_no_host_dependencies_or_effect_boundaries()
    {
        var root = Root();
        var library = Path.Combine(root, "orchestration-decisions");
        var project = File.ReadAllText(Path.Combine(library, "Orchestration.Decisions.csproj"));
        var references = Regex.Matches(project, "ProjectReference Include=\"([^\"]+)\"");
        Assert.Single(references);
        Assert.EndsWith("TaskServer.Contracts.csproj", references[0].Groups[1].Value);
        foreach (var file in Directory.EnumerateFiles(library, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.False(Regex.IsMatch(text,
                @"\b(?:File|Directory|Process|HttpClient|IConfiguration|TaskScannerService|DbConnection)\s*[.(<]|DateTime(?:Offset)?\.(?:UtcNow|Now)"),
                $"Decision code must accept facts instead of reading a host boundary: {file}");
        }
    }

    [Theory]
    [InlineData("backend/Features/Runner/MonolithDecisionAdapter.cs")]
    [InlineData("task-server/TaskServerDecisionAdapter.cs")]
    [InlineData("orchestrator-engine/EngineDecisionAdapter.cs")]
    [InlineData("task-server/OrchestrationSettlementPolicy.cs")]
    [InlineData("task-server/RepeatedReviewBlockPolicy.cs")]
    [InlineData("backend/Features/Runner/RepeatedAspectBlockPolicy.cs")]
    public void Adapters_forward_facts_without_implementing_transition_rules(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(Root(), relativePath));
        Assert.DoesNotMatch(@"\b(if|switch|while|for|foreach)\s*[(\{]", text);
        Assert.Contains("=>", text);
    }

    [Fact]
    public void Host_handlers_do_not_restore_the_extracted_rules()
    {
        var root = Root();
        var lease = File.ReadAllText(Path.Combine(root, "backend/Features/Tasks/LeaseEndpoints.cs"));
        Assert.Contains("MonolithDecisionAdapter.Coding", lease);
        Assert.DoesNotContain("var targetState = outcome switch", lease);
        var review = File.ReadAllText(Path.Combine(root, "backend/Features/Runner/V1ReviewPlaneEndpoints.cs"));
        Assert.Contains("MonolithDecisionAdapter.PrepareReview", review);
        Assert.Contains("TransitionDecisionPolicy.CanRetryInfrastructure", review);
        Assert.DoesNotContain("var retry = infrastructureFailure &&", review);
        var stages = File.ReadAllText(Path.Combine(root, "orchestrator-engine/OrchestrationStageHandlers.cs"));
        Assert.DoesNotContain("gate.Phase switch", stages);
        Assert.DoesNotContain("Normalize(reviewOutcome) switch", stages);
        Assert.Contains("StageTransitionPolicy.Council", stages);
    }

    private static string Root()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "agent-taskboard.sln"))) path = path.Parent;
        return path?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
