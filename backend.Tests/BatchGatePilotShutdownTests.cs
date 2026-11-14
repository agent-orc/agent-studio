using AgentStudio.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3755: a follow-up tick that outlives its host faulted on the disposed
/// service provider, and the finalizer rethrew it and aborted the test host.
/// </summary>
public sealed class BatchGatePilotShutdownTests : BatchGatePilotServiceTests
{
    [Fact]
    public async Task Follow_up_tick_in_flight_when_the_host_is_disposed_leaves_no_unobserved_exception()
    {
        using var probe = new UnobservedTaskExceptionProbe();
        var factory = BuildFactory();
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The tick then reads the gate queue, as the clean-repeat crash did
        // (GateBacklogged -> ConfiguredGateSlots), on the disposed provider.
        pilot.DuringTickForTesting = async () =>
        {
            entered.TrySetResult();
            await release.Task;
            pilot.GateBacklogged(ProjectName, []);
        };

        var followUp = pilot.ScheduleFollowUpTick();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await factory.DisposeAsync();
        release.SetResult();
        await followUp.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(pilot.Stopping, "Disposing the host must stop the pilot's follow-up ticks.");
        Assert.Same(Task.CompletedTask, pilot.ScheduleFollowUpTick());
        Assert.True(pilot.TickAsync(CancellationToken.None).IsCompletedSuccessfully);
        probe.AssertNoLeak(nameof(BatchGatePilotService));
    }
}
