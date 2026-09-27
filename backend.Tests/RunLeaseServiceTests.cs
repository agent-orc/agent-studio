using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// §8.2C acceptance coverage for the fenced task-run lease (ADR-0060): a single
/// holder per task, monotonic fencing tokens, and stale-write rejection after a
/// TTL takeover — the split-brain guard.
/// </summary>
public sealed class RunLeaseServiceTests
{
    [Fact]
    public void Local_adapter_and_remote_claim_cannot_hold_the_same_task()
    {
        var leases = NewService();
        var identity = new RunnerIdentity("local-runner", "local", "host", "backend", "token", "1");
        var local = new LocalRunClaimAdapter(leases, identity, NullLogger.Instance);
        var booked = local.TryAcquire("AGT-1", () => { });
        Assert.True(booked.Granted);
        Assert.False(leases.TryAcquire(Acquire("AGT-1", "remote-runner")).Granted);
        local.Release("AGT-1");
        Assert.True(leases.TryAcquire(Acquire("AGT-1", "remote-runner")).Granted);
    }

    // §8.2C: "Two runner processes race the same ready task; only one gets a lease."
    [Fact]
    public void TwoRunnersRaceSameTask_OnlyOneGetsLease()
    {
        var service = NewService();

        var a = service.TryAcquire(Acquire("AGT-1", "runner-a"));
        var b = service.TryAcquire(Acquire("AGT-1", "runner-b"));

        Assert.True(a.Granted);
        Assert.Equal("Acquired", a.Outcome);
        Assert.Equal(1, a.Lease!.FencingToken);

        Assert.False(b.Granted);
        Assert.Equal("Held", b.Outcome);
        Assert.Equal("runner-a", b.Lease!.RunnerId); // contender sees the real holder
    }

    [Fact]
    public void SameRunner_ReacquireIsIdempotent_AndKeepsLeaseAndToken()
    {
        var service = NewService();
        var request = Acquire("AGT-1", "runner-a") with { IdempotencyKey = "claim-a" };

        var first = service.TryAcquire(request);
        var again = service.TryAcquire(request);

        Assert.True(first.Granted);
        Assert.True(again.Granted);
        Assert.Equal("AlreadyOwn", again.Outcome);
        Assert.Equal(first.Lease!.LeaseId, again.Lease!.LeaseId);
        Assert.Equal(first.Lease.FencingToken, again.Lease.FencingToken);
    }

    [Fact]
    public void SameRunner_NewAcquireDeliveryCannotActAsAnUnfencedHeartbeat()
    {
        var now = new DateTime(2026, 7, 21, 10, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var first = service.TryAcquire(Acquire("AGT-1", "runner-a", ttlSeconds: 30) with
        {
            IdempotencyKey = "claim-a",
        });
        var originalExpiry = first.Lease!.ExpiresAt;
        now = now.AddSeconds(10);

        var reacquire = service.TryAcquire(Acquire("AGT-1", "runner-a", ttlSeconds: 120) with
        {
            IdempotencyKey = "claim-b",
        });

        Assert.False(reacquire.Granted);
        Assert.Equal("Held", reacquire.Outcome);
        Assert.Equal(originalExpiry, reacquire.Lease!.ExpiresAt);
    }

    // §8.2C: Runner A loses heartbeat, Runner B acquires a higher fenced lease,
    // and A's stale heartbeat / release / writes are rejected.
    [Fact]
    public void ExpiredLease_AllowsHigherFencedTakeover_AndRejectsStaleHolder()
    {
        var now = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);

        var requestA = Acquire("AGT-1", "runner-a", ttlSeconds: 120) with
        {
            IdempotencyKey = "claim-a",
        };
        var a = service.TryAcquire(requestA);
        Assert.True(a.Granted);
        Assert.Equal(1, a.Lease!.FencingToken);
        var heartbeatA = Heartbeat(a.Lease) with
        {
            AttemptId = a.Lease.AttemptId,
            AuthorityEpoch = a.Lease.AuthorityEpoch,
            IdempotencyKey = "heartbeat-a",
        };
        Assert.Equal("Renewed", service.Renew(heartbeatA).Outcome);

        // A misses its heartbeat window; the lease lapses.
        now = now.AddSeconds(121);

        var b = service.TryAcquire(Acquire("AGT-1", "runner-b", ttlSeconds: 120));
        Assert.True(b.Granted);
        Assert.Equal("Acquired", b.Outcome);
        Assert.Equal(2, b.Lease!.FencingToken); // strictly higher fence

        // A wakes up and tries to keep working: every path is fenced off.
        var staleHeartbeat = service.Renew(Heartbeat(a.Lease));
        var staleRelease = service.Release(Release(a.Lease));

        Assert.False(staleHeartbeat.Granted);
        Assert.Equal("StaleToken", staleHeartbeat.Outcome);
        Assert.Equal("runner-b", staleHeartbeat.Lease!.RunnerId);

        Assert.Equal("StaleToken", staleRelease.Outcome);

        // Replaying the original claim delivery is also fenced. It must not be
        // mapped back to AlreadyOwn after a replacement attempt became current.
        var replayedAcquire = service.TryAcquire(requestA);
        Assert.False(replayedAcquire.Granted);
        Assert.Equal(AttemptWriteStatus.Superseded.ToString(), replayedAcquire.Outcome);
        var replayedHeartbeat = service.Renew(heartbeatA);
        Assert.False(replayedHeartbeat.Granted);
        Assert.Equal("StaleToken", replayedHeartbeat.Outcome);

        // The write gate agrees: A is no longer current, B is.
        Assert.False(service.IsCurrent("AGT-1", a.Lease.LeaseId, a.Lease.FencingToken, a.Lease.RunnerId));
        Assert.True(service.IsCurrent("AGT-1", b.Lease.LeaseId, b.Lease.FencingToken, b.Lease.RunnerId));
    }

    [Fact]
    public void Heartbeat_ExtendsLease_SoTheHolderKeepsItAcrossTheOriginalTtl()
    {
        var now = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);

        var a = service.TryAcquire(Acquire("AGT-1", "runner-a", ttlSeconds: 60));

        now = now.AddSeconds(40);
        var renew = service.Renew(Heartbeat(a.Lease!, ttlSeconds: 60));
        Assert.True(renew.Granted);
        Assert.Equal("Renewed", renew.Outcome);

        // Past the ORIGINAL expiry but within the renewed window: still current,
        // and a contender is still held off.
        now = now.AddSeconds(40); // 80s in; original 60s lease would have lapsed at 60s
        Assert.True(service.IsCurrent("AGT-1", a.Lease!.LeaseId, a.Lease.FencingToken, a.Lease.RunnerId));
        var contender = service.TryAcquire(Acquire("AGT-1", "runner-b"));
        Assert.False(contender.Granted);
        Assert.Equal("Held", contender.Outcome);
    }

    [Fact]
    public void Release_ThenReacquire_MintsAStrictlyHigherFencingToken()
    {
        var service = NewService();

        var a = service.TryAcquire(Acquire("AGT-1", "runner-a"));
        var released = service.Release(Release(a.Lease!));
        var b = service.TryAcquire(Acquire("AGT-1", "runner-b"));

        Assert.Equal("Released", released.Outcome);
        Assert.True(b.Granted);
        Assert.Equal(2, b.Lease!.FencingToken); // monotonic across release
    }

    [Fact]
    public void Renew_WithWrongFencingToken_IsRejectedAsStale()
    {
        var service = NewService();
        var a = service.TryAcquire(Acquire("AGT-1", "runner-a"));

        var forged = a.Lease! with { FencingToken = a.Lease.FencingToken + 1 };
        var renew = service.Renew(Heartbeat(forged));

        Assert.False(renew.Granted);
        Assert.Equal("StaleToken", renew.Outcome);
    }

    [Fact]
    public void Renew_WithWrongLeaseId_IsRejectedAsStale()
    {
        var service = NewService();
        var a = service.TryAcquire(Acquire("AGT-1", "runner-a"));

        var forged = a.Lease! with { LeaseId = "lease-from-another-holder" };
        var renew = service.Renew(Heartbeat(forged));

        Assert.False(renew.Granted);
        Assert.Equal("StaleToken", renew.Outcome);
    }

    [Fact]
    public void DifferentTasksDoNotBlockEachOther_AndFenceIndependently()
    {
        var service = NewService();

        var one = service.TryAcquire(Acquire("AGT-1", "runner-a"));
        var two = service.TryAcquire(Acquire("AGT-2", "runner-a"));

        Assert.True(one.Granted);
        Assert.True(two.Granted);
        Assert.Equal(1, one.Lease!.FencingToken);
        Assert.Equal(1, two.Lease!.FencingToken); // per-task fence, not global
    }

    [Fact]
    public void Peek_ReportsFreeThenHeld()
    {
        var service = NewService();

        Assert.Equal("Free", service.Peek("AGT-1").Outcome);

        service.TryAcquire(Acquire("AGT-1", "runner-a"));
        var held = service.Peek("AGT-1");

        Assert.Equal("Held", held.Outcome);
        Assert.Equal("runner-a", held.Lease!.RunnerId);
    }

    [Fact]
    public void Renew_UnknownTask_IsNotHeld()
    {
        var service = NewService();
        var renew = service.Renew(new RunLeaseHeartbeatRequest("AGT-nope", "lease", 1, "runner-a"));
        Assert.Equal("NotHeld", renew.Outcome);
    }

    [Theory]
    [InlineData("", "runner-a")]
    [InlineData("AGT-1", "")]
    public void TryAcquire_RequiresTaskKeyAndRunnerId(string taskKey, string runnerId)
    {
        var service = NewService();
        var result = service.TryAcquire(new RunLeaseAcquireRequest(taskKey, runnerId, "name", "host", 1, "stable"));
        Assert.Equal("Invalid", result.Outcome);
        Assert.False(result.Granted);
    }

    private static RunLeaseService NewService(Func<DateTime>? utcNow = null)
        => new(NullLogger<RunLeaseService>.Instance, utcNow);

    private static RunLeaseAcquireRequest Acquire(string task, string runner, int ttlSeconds = 120)
        => new(task, runner, runner, "host-" + runner, 4321, "stable", ttlSeconds);

    private static RunLeaseHeartbeatRequest Heartbeat(RunLeaseInfoDto lease, int ttlSeconds = 120)
        => new(lease.TaskKey, lease.LeaseId, lease.FencingToken, lease.RunnerId, ttlSeconds);

    private static RunLeaseReleaseRequest Release(RunLeaseInfoDto lease)
        => new(lease.TaskKey, lease.LeaseId, lease.FencingToken, lease.RunnerId);
}
