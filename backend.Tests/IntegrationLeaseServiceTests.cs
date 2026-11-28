using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class IntegrationLeaseServiceTests
{
    [Fact]
    public void TryAcquire_QueuesContenderUntilCurrentHolderReleases()
    {
        var service = NewService();
        var first = Request("task-a", "runner-a");
        var second = Request("task-b", "runner-b");

        var acquired = service.TryAcquire(first);
        var queued = service.TryAcquire(second);

        Assert.True(acquired.Granted);
        Assert.Equal("Acquired", acquired.Outcome);
        Assert.Equal(1, acquired.Lease!.FencingToken);
        Assert.False(queued.Granted);
        Assert.Equal("Queued", queued.Outcome);
        Assert.Equal(1, queued.QueuePosition);
        Assert.Equal("task-a", queued.Lease!.TaskKey);

        var released = service.Release(new IntegrationLeaseReleaseRequest(
            acquired.Lease.ProjectName,
            acquired.Lease.IntegrationBranch,
            acquired.Lease.LeaseId,
            acquired.Lease.FencingToken,
            acquired.Lease.RunnerId));
        var secondAcquire = service.TryAcquire(second);

        Assert.Equal("Released", released.Outcome);
        Assert.True(secondAcquire.Granted);
        Assert.Equal("Acquired", secondAcquire.Outcome);
        Assert.Equal("task-b", secondAcquire.Lease!.TaskKey);
        Assert.Equal(2, secondAcquire.Lease.FencingToken);
    }

    [Fact]
    public void TryAcquire_DifferentIntegrationBranchesDoNotBlockEachOther()
    {
        var service = NewService();

        var develop = service.TryAcquire(Request("task-a", "runner-a", branch: "develop"));
        var release = service.TryAcquire(Request("task-b", "runner-b", branch: "release/1.0"));

        Assert.True(develop.Granted);
        Assert.True(release.Granted);
        Assert.Equal("develop", develop.Lease!.IntegrationBranch);
        Assert.Equal("release/1.0", release.Lease!.IntegrationBranch);
    }

    [Fact]
    public void TryAcquire_SameOwnerIsIdempotent()
    {
        var service = NewService();
        var request = Request("task-a", "runner-a");

        var first = service.TryAcquire(request);
        var again = service.TryAcquire(request);

        Assert.True(first.Granted);
        Assert.True(again.Granted);
        Assert.Equal("AlreadyOwn", again.Outcome);
        Assert.Equal(first.Lease!.LeaseId, again.Lease!.LeaseId);
        Assert.Equal(first.Lease.FencingToken, again.Lease.FencingToken);
    }

    [Fact]
    public void ExpiredLease_AllowsHigherFencedAcquire_AndRejectsStaleHeartbeat()
    {
        var now = new DateTime(2026, 6, 9, 12, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var first = service.TryAcquire(Request("task-a", "runner-a", ttlSeconds: 30));
        Assert.True(first.Granted);

        now = now.AddSeconds(31);
        var second = service.TryAcquire(Request("task-b", "runner-b", ttlSeconds: 30));
        var staleHeartbeat = service.Renew(new IntegrationLeaseHeartbeatRequest(
            first.Lease!.ProjectName,
            first.Lease.IntegrationBranch,
            first.Lease.LeaseId,
            first.Lease.FencingToken,
            first.Lease.RunnerId,
            RequestedTtlSeconds: 30));

        Assert.True(second.Granted);
        Assert.Equal(2, second.Lease!.FencingToken);
        Assert.False(staleHeartbeat.Granted);
        Assert.Equal("StaleToken", staleHeartbeat.Outcome);
        Assert.Equal("task-b", staleHeartbeat.Lease!.TaskKey);
    }

    [Fact]
    public void TryAcquire_TwoWaitersKeepFifoPositionAcrossPollsAndRelease()
    {
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var holder = service.TryAcquire(Request("task-a", "runner-a"));
        var first = Request("task-b", "runner-b");
        var second = Request("task-c", "runner-c");
        Assert.Equal(1, service.TryAcquire(first).QueuePosition);
        Assert.Equal(2, service.TryAcquire(second).QueuePosition);

        for (var poll = 0; poll < 3; poll++)
        {
            now = now.AddSeconds(1);
            Assert.Equal(1, service.TryAcquire(first).QueuePosition);
            Assert.Equal(2, service.TryAcquire(second).QueuePosition);
            Assert.Equal(2, service.Peek(first.ProjectName, first.IntegrationBranch).QueuePosition);
        }

        Release(service, holder);
        for (var poll = 0; poll < 3; poll++)
        {
            var queued = service.TryAcquire(second);
            Assert.False(queued.Granted);
            Assert.Equal(2, queued.QueuePosition);
            Assert.Null(queued.Lease);
            Assert.Contains("free", queued.Message!);
            Assert.DoesNotContain("held by another runner", queued.Message!);
        }

        var firstGrant = service.TryAcquire(first);
        Assert.True(firstGrant.Granted);
        Assert.Equal("task-b", firstGrant.Lease!.TaskKey);
        Assert.Equal(2, firstGrant.Lease.FencingToken);
        var waitingForFirst = service.TryAcquire(second);
        Assert.Equal(1, waitingForFirst.QueuePosition);
        Assert.Equal("task-b", waitingForFirst.Lease!.TaskKey);
        Release(service, firstGrant);

        var secondGrant = service.TryAcquire(second);
        Assert.True(secondGrant.Granted);
        Assert.Equal("task-c", secondGrant.Lease!.TaskKey);
        Assert.Equal(3, secondGrant.Lease.FencingToken);
        Assert.Equal(0, service.Peek(first.ProjectName, first.IntegrationBranch).QueuePosition);
    }

    [Fact]
    public void Peek_FreeLaneWithWaitersReportsQueued()
    {
        var service = NewService();
        var holder = service.TryAcquire(Request("task-a", "runner-a"));
        var first = Request("task-b", "runner-b");
        service.TryAcquire(first);
        service.TryAcquire(Request("task-c", "runner-c"));
        Release(service, holder);

        var peek = service.Peek(first.ProjectName, first.IntegrationBranch);

        Assert.Equal("Queued", peek.Outcome);
        Assert.Null(peek.Lease);
        Assert.Equal(2, peek.QueuePosition);
        Assert.DoesNotContain("held by another runner", peek.Message ?? "");
    }

    [Fact]
    public void TryAcquire_AbandonedRemoteHeadExpiresWhileActiveSuccessorKeepsWaiting()
    {
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var holder = service.TryAcquire(Request("task-a", "runner-a", ttlSeconds: 2700));
        var abandoned = Request("task-b", "runner-b");
        var active = Request("task-c", "runner-c");
        service.TryAcquire(abandoned);
        service.TryAcquire(active);

        now = now.AddMinutes(5);
        Assert.Equal(2, service.TryAcquire(active).QueuePosition);
        Release(service, holder);
        now = now.AddMinutes(5).AddTicks(-1);
        var beforeExpiry = service.TryAcquire(active);
        Assert.Equal(2, beforeExpiry.QueuePosition);
        Assert.False(beforeExpiry.Granted);
        Assert.Null(beforeExpiry.Lease);
        Assert.DoesNotContain("held by another runner", beforeExpiry.Message!);
        now = now.AddTicks(1);

        var granted = service.TryAcquire(active);
        Assert.True(granted.Granted);
        Assert.Equal(2, granted.Lease!.FencingToken);
        Assert.Equal(1, service.TryAcquire(abandoned).QueuePosition);
        Release(service, granted);
        Assert.True(service.TryAcquire(abandoned).Granted);
    }

    [Fact]
    public void Peek_PrunesAbandonedRemoteWaitersAtIdleDeadline()
    {
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var holder = service.TryAcquire(Request("task-a", "runner-a", ttlSeconds: 2700));
        var first = Request("task-b", "runner-b");
        service.TryAcquire(first);
        service.TryAcquire(Request("task-c", "runner-c"));
        now = now.AddMinutes(10);

        var held = service.Peek(first.ProjectName, first.IntegrationBranch);
        Assert.Equal("Held", held.Outcome);
        Assert.Equal(holder.Lease, held.Lease);
        Assert.Equal(0, held.QueuePosition);
        Release(service, holder);
        Assert.Equal("Free", service.Peek(first.ProjectName, first.IntegrationBranch).Outcome);
    }

    [Fact]
    public void TryAcquire_PollRefreshesIdleDeadlineWithoutLosingHeadPosition()
    {
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var holder = service.TryAcquire(Request("task-a", "runner-a", ttlSeconds: 2700));
        var first = Request("task-b", "runner-b");
        var second = Request("task-c", "runner-c");
        service.TryAcquire(first);
        service.TryAcquire(second);
        for (var poll = 0; poll < 3; poll++)
        {
            now = now.AddMinutes(5);
            Assert.Equal(1, service.TryAcquire(first).QueuePosition);
            Assert.Equal(2, service.TryAcquire(second).QueuePosition);
        }

        Release(service, holder);
        Assert.Equal(2, service.TryAcquire(second).QueuePosition);
        Assert.True(service.TryAcquire(first).Granted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WaitAcquireAsync_CancellationRemovesOnlyCancelledWaiter(bool cancelHead)
    {
        var service = NewService();
        var holder = service.TryAcquire(Request("task-a", "runner-a"));
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var first = Request("task-b", "runner-b");
        var second = Request("task-c", "runner-c");
        var firstWait = service.WaitAcquireAsync(first, TimeSpan.FromDays(1), firstCancellation.Token);
        var secondWait = service.WaitAcquireAsync(second, TimeSpan.FromDays(1), secondCancellation.Token);
        try
        {
            Assert.Equal(2, service.Peek(first.ProjectName, first.IntegrationBranch).QueuePosition);
            (cancelHead ? firstCancellation : secondCancellation).Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelHead ? firstWait : secondWait);
            var held = service.Peek(first.ProjectName, first.IntegrationBranch);
            Assert.Equal(holder.Lease, held.Lease);
            Assert.Equal(1, held.QueuePosition);
            Release(service, holder);
            Assert.True(service.TryAcquire(cancelHead ? second : first).Granted);
        }
        finally
        {
            firstCancellation.Cancel();
            secondCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstWait);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondWait);
        }
    }

    [Fact]
    public void Release_WrongOwnerCannotReleaseHolderOrDisturbQueue()
    {
        var service = NewService();
        var holder = service.TryAcquire(Request("task-a", "runner-a"));
        var first = Request("task-b", "runner-b");
        service.TryAcquire(first);
        service.TryAcquire(Request("task-c", "runner-c"));
        var lease = holder.Lease!;

        var rejected = service.Release(new IntegrationLeaseReleaseRequest(
            lease.ProjectName, lease.IntegrationBranch, lease.LeaseId, lease.FencingToken, "runner-b"));

        Assert.Equal("StaleToken", rejected.Outcome);
        Assert.Equal(lease, rejected.Lease);
        Release(service, holder);
        Assert.True(service.TryAcquire(first).Granted);
    }

    private static void Release(IntegrationLeaseService service, IntegrationLeaseResponse acquired)
    {
        var lease = acquired.Lease!;
        Assert.Equal("Released", service.Release(new IntegrationLeaseReleaseRequest(
            lease.ProjectName, lease.IntegrationBranch, lease.LeaseId, lease.FencingToken, lease.RunnerId)).Outcome);
    }

    private static IntegrationLeaseService NewService(Func<DateTime>? utcNow = null)
        => new(NullLogger<IntegrationLeaseService>.Instance, utcNow);

    private static IntegrationLeaseAcquireRequest Request(
        string task,
        string runner,
        string project = "agent-taskboard",
        string branch = "develop",
        int ttlSeconds = 600)
        => new(
            project,
            branch,
            task,
            runner,
            "host-a",
            1234,
            "stable",
            ttlSeconds);
}
