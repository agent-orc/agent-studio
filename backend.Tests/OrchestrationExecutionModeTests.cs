using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

namespace AgentStudio.Tests;

public sealed class OrchestrationExecutionModeTests
{
    [Fact]
    public void Missing_mode_preserves_monolith_during_transition()
        => Assert.Equal(
            OrchestrationExecutionMode.Monolith,
            OrchestrationExecutionModeParser.Parse(null));

    [Fact]
    public void Engine_mode_disables_monolith_loop_ownership()
        => Assert.Equal(
            OrchestrationExecutionMode.Engine,
            OrchestrationExecutionModeParser.Parse("engine"));

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
                ["Orchestration:ExecutionMode"] = mode,
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

    // AGT-3055: the batch tick is the only hosted loop in Engine and Shadow
    // mode. There it publishes batch-plan occurrences and applies the engine
    // intent; without it no batch would close or advance.
    [Theory]
    [InlineData(OrchestrationExecutionMode.Engine)]
    [InlineData(OrchestrationExecutionMode.Shadow)]
    public void Non_monolith_mode_registers_only_the_batch_tick(OrchestrationExecutionMode mode)
    {
        var services = new ServiceCollection();

        services.AddOrchestrationExecutionLoops(mode);

        var hosted = Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService));
        Assert.Equal(typeof(AgentStudio.Pipeline.BatchGatePilotHostedService), hosted.ImplementationType);
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

        services.AddOrchestrationExecutionLoops(configuration);

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(RemoteReviewSettlementReconciler));
    }

    [Fact]
    public void Monolith_mode_registers_all_six_legacy_loop_owners()
    {
        var services = new ServiceCollection();

        services.AddOrchestrationExecutionLoops(OrchestrationExecutionMode.Monolith);

        Assert.Equal(6, services.Count(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(RemoteReviewSettlementReconciler));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(AgentStudio.Pipeline.BatchGatePilotHostedService));
    }
}
