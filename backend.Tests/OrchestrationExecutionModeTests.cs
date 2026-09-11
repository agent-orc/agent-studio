using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
    public void Engine_mode_registers_no_legacy_orchestration_hosted_loops()
    {
        var services = new ServiceCollection();

        services.AddOrchestrationExecutionLoops(OrchestrationExecutionMode.Engine);

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService));
    }

    // AGT-2762 added a fourth legacy loop owner (RemoteReviewEvidenceProjectionWorker)
    // alongside the three named here, so the count grew from 3 to 4.
    [Fact]
    public void Monolith_mode_registers_all_four_legacy_loop_owners()
    {
        var services = new ServiceCollection();

        services.AddOrchestrationExecutionLoops(OrchestrationExecutionMode.Monolith);

        Assert.Equal(4, services.Count(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)));
    }
}
