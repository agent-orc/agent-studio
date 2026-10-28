using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using AgentStudio.TestSupport;

namespace AgentStudio.Tests;

public sealed class OrchestrationExecutionModeTests
{
    [Fact]
    public void Missing_mode_preserves_monolith_during_transition()
        => Assert.Equal(
            OrchestrationExecutionMode.Monolith,
            OrchestrationExecutionModeParser.Parse(null));

    [Fact]
    public void Engine_profile_value_parses()
        => Assert.Equal(
            OrchestrationExecutionMode.Engine,
            OrchestrationExecutionModeParser.Parse("engine"));

    [Fact]
    public void Global_mode_does_not_select_any_transition_profile()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Orchestration:ExecutionMode"] = "Engine" }).Build();
        Assert.All(OrchestrationRolloutStore.Profiles, profile =>
            Assert.Equal(OrchestrationExecutionMode.Monolith,
                MonolithTransitionSwitch.For(configuration, profile)));
    }

    [Fact]
    public void Ambiguous_mode_fails_closed()
        => Assert.Throws<InvalidOperationException>(
            () => OrchestrationExecutionModeParser.Parse("both"));

    [Fact]
    public void One_transition_can_use_engine_while_the_others_keep_monolith_ownership()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            { ["Orchestration:Transitions:ReviewReport"] = "Engine" }).Build();
        Assert.Equal(OrchestrationExecutionMode.Engine,
            MonolithTransitionSwitch.For(configuration, "ReviewReport"));
        Assert.Equal(OrchestrationExecutionMode.Monolith,
            MonolithTransitionSwitch.For(configuration, "CodingCompletion"));
        Assert.Equal(OrchestrationExecutionMode.Monolith,
            MonolithTransitionSwitch.For(configuration, "GateResult"));
    }

    [Theory]
    [InlineData("OrchestratorPrepHostedService", "OrchestratorPrepRules.Decide(")]
    [InlineData("MetaCycleHostedService", "MetaCycleRules.BuildReport(")]
    [InlineData("AutoInterventionHostedService", "AutoInterventionMapping.MapAdvisory(")]
    public void Engine_mode_keeps_supervisor_decider_on_its_monolith_path(
        string service, string decisionCall)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Orchestration:ExecutionMode"] = "Engine" }).Build();
        Assert.Equal(OrchestrationExecutionMode.Monolith,
            MonolithTransitionSwitch.For(configuration, service));

        var root = RepositoryRoot.Find();
        var program = File.ReadAllText(Path.Combine(root, "backend/Host/Program.cs"));
        var source = File.ReadAllText(Path.Combine(root, $"backend/Features/Supervisor/{service}.cs"));
        Assert.Contains($"AddHostedService<{service}>()", program);
        Assert.Contains(decisionCall, source);
        Assert.DoesNotContain("MonolithTransitionSwitch.For", source);
    }

    // AGT-3055 fix round: engine ownership of BatchPlan is opt-in and refused
    // behind the Task Server proxy, where the engine cannot claim its occurrences.
    [Theory]
    [InlineData("Engine", null, OrchestrationExecutionMode.Engine, false)]
    [InlineData("Shadow", null, OrchestrationExecutionMode.Shadow, false)]
    [InlineData(null, null, OrchestrationExecutionMode.Monolith, false)]
    [InlineData("Engine", "http://127.0.0.1:5071", OrchestrationExecutionMode.Monolith, true)]
    [InlineData("Shadow", "http://127.0.0.1:5071", OrchestrationExecutionMode.Monolith, true)]
    [InlineData(null, "http://127.0.0.1:5071", OrchestrationExecutionMode.Monolith, false)]
    public void Batch_plan_owner_is_opt_in_and_refused_behind_the_task_server_proxy(
        string? mode, string? taskServer, OrchestrationExecutionMode expected, bool refused)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Orchestration:Transitions:BatchPlan"] = mode,
                ["TaskServer:BaseUrl"] = taskServer,
            }).Build();

        var owner = AgentStudio.Pipeline.BatchGatePilotService.OwnerOfBatchPlan(configuration, out var refusal);

        Assert.Equal(expected, owner);
        Assert.Equal(refused, refusal is not null);
        if (refused) Assert.Contains("TaskServer:BaseUrl", refusal);
    }

    [Fact]
    public void Rollout_switch_refuses_batch_plan_shadow_behind_the_task_server_proxy()
    {
        var store = new OrchestrationRolloutStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TaskServer:BaseUrl"] = "http://127.0.0.1:5071" }).Build());

        var error = Assert.Throws<InvalidOperationException>(() =>
            store.Switch("BatchPlan", "shadow", "operator", "pilot"));

        Assert.StartsWith("orchestration-profile-batch-plan-task-server-proxy", error.Message);
    }

    // Operator rollback changes a profile while the host is running, so all
    // workers must already be present regardless of its startup setting.
    [Theory]
    [InlineData(OrchestrationExecutionMode.Monolith)]
    [InlineData(OrchestrationExecutionMode.Engine)]
    [InlineData(OrchestrationExecutionMode.Shadow)]
    public void All_modes_register_review_and_delivery_workers(OrchestrationExecutionMode mode)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Orchestration:Transitions:ReviewReport"] = mode.ToString() }).Build();
        Assert.Equal(mode, MonolithTransitionSwitch.For(configuration, "ReviewReport"));
        var services = new ServiceCollection();
        services.AddOrchestrationExecutionLoops();
        AssertLoopOwners(services);
    }

    [Fact]
    public void Explicit_monolith_profile_registers_legacy_review_loops_under_global_engine_mode()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Orchestration:ExecutionMode"] = "Engine",
                ["Orchestration:Transitions:ReviewReport"] = "Monolith",
            }).Build();
        var services = new ServiceCollection();

        services.AddOrchestrationExecutionLoops();

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(RemoteReviewSettlementReconciler));
    }

    [Fact]
    public void Monolith_mode_registers_all_seven_loop_owners()
    {
        var services = new ServiceCollection();

        services.AddOrchestrationExecutionLoops();

        AssertLoopOwners(services);
    }

    private static void AssertLoopOwners(IServiceCollection services)
    {
        var hosted = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToArray();
        Assert.Equal(new Type[]
        {
            typeof(AgentStudio.Pipeline.BatchGatePilotHostedService),
            typeof(AutoReviewPostProcessingWorker),
            typeof(AutoReviewPostProcessingRecoveryService),
            typeof(PostWorkSubjectReconcilerService),
            typeof(RemoteReviewEvidenceProjectionWorker),
            typeof(RemoteReviewSettlementReconciler),
        }, hosted.Where(descriptor => descriptor.ImplementationType is not null)
            .Select(descriptor => descriptor.ImplementationType!).ToArray());
        Assert.Single(hosted, descriptor => descriptor.ImplementationFactory is not null);
        Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(AutoReviewDeliveryResumeService)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
    }
}
