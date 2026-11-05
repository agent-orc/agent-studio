using AgentStudio.ProjectChat;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectChatStartupServiceTests
{
    [Fact]
    public async Task Stop_cancels_and_awaits_an_in_flight_writer()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var service = new ProjectChatStartupService(ct =>
        {
            started.SetResult();
            ct.WaitHandle.WaitOne();
            cancelled.SetResult();
            release.Wait();
        });

        await service.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = service.StopAsync(CancellationToken.None);
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            release.Set();
        }
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
