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

    [Fact]
    public void Engine_mode_registers_no_legacy_orchestration_hosted_loops()
    {
        var services = new ServiceCollection();

        services.AddOrchestrationExecutionLoops(OrchestrationExecutionMode.Engine);

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService));
    }

    // AGT-2762 added a fourth legacy loop owner (RemoteReviewEvidenceProjectionWorker),
    // AGT-2936 a fifth (RemoteReviewSettlementReconciler, the restart owner of
    // journaled review settlements) and AGT-3052 a sixth (BatchGatePilotHostedService,
    // the batch verification tick; it moves into the Orchestrator Engine with
    // AGT-3055, decision D14), so the count grew from 3 to 6.
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
