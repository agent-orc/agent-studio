using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class InteractiveChatAdmissionTests
{
    [Fact]
    public async Task Claims_chat_while_every_coding_slot_is_occupied()
    {
        Assert.Equal(0, InteractiveChatAdmission.FreeCodingSlots(5, 5, 0));
        var work = new RemoteChatWorkItem(
            "chat-1", "claim-1", RemoteChatWorkKinds.Turn,
            "project", "Project", "origin", "main", "question", "model", null,
            DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2));
        var claims = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new InteractiveChatAdmission(
            _ => Task.FromResult(++claims == 1
                ? new RemoteChatWorkClaimResponse(RemoteChatWorkClaimStatuses.Claimed, work)
                : new RemoteChatWorkClaimResponse(RemoteChatWorkClaimStatuses.Empty)),
            (_, _) => { started.SetResult(); return finish.Task; },
            _ => { });

        await admission.PollOnceAsync(CancellationToken.None);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, admission.ActiveCount);
        finish.SetResult(0);
    }

    [Fact]
    public async Task Claims_multiple_chats_without_an_interactive_slot_ceiling()
    {
        var claims = 0;
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new InteractiveChatAdmission(
            _ =>
            {
                var next = ++claims;
                return Task.FromResult(next <= 3
                    ? new RemoteChatWorkClaimResponse(RemoteChatWorkClaimStatuses.Claimed,
                        new RemoteChatWorkItem(
                            $"chat-{next}", $"claim-{next}", RemoteChatWorkKinds.Turn,
                            "project", "Project", "origin", "main", "question", "model", null,
                            DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2)))
                    : new RemoteChatWorkClaimResponse(RemoteChatWorkClaimStatuses.Empty));
            },
            (_, _) => finish.Task,
            _ => { });

        await admission.PollOnceAsync(CancellationToken.None);

        Assert.Equal(3, admission.ActiveCount);
        finish.SetResult(0);
        await admission.DrainAsync();
    }

    [Fact]
    public void Light_chat_does_not_borrow_capacity_and_heavy_chat_borrows_one()
    {
        var now = DateTime.UtcNow;
        Assert.False(InteractiveChatAdmission.IsHeavy(now.AddSeconds(-10), now));
        Assert.True(InteractiveChatAdmission.IsHeavy(now.AddSeconds(-31), now));
        Assert.True(InteractiveChatAdmission.IsHeavy(now.AddSeconds(-10), now, sustainedCpu: true));
        Assert.Equal(1, InteractiveChatAdmission.FreeCodingSlots(5, 4, 0));
        Assert.Equal(0, InteractiveChatAdmission.FreeCodingSlots(5, 4, 1));
        Assert.Equal(0, InteractiveChatAdmission.FreeCodingSlots(5, 5, 1));
        Assert.Equal(1, RemoteProjectChatRunner.NextHighCpuSamples(0, 35));
        Assert.Equal(2, RemoteProjectChatRunner.NextHighCpuSamples(1, 35));
        Assert.Equal(0, RemoteProjectChatRunner.NextHighCpuSamples(2, 5));
    }
}
