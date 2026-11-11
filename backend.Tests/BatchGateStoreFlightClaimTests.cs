using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3569: the durable flight claim of a project. Each store instance stands
/// for a separate process: only the claim record and its cross-process file
/// lock connect them.
/// </summary>
public sealed class BatchGateStoreFlightClaimTests : IDisposable
{
    private const string Project = "Batch-Pilot";
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "batch-flight-claim-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        BatchGateDurableWrites.ObserverForTesting = null;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_live_claim_refuses_a_second_process_and_the_project_name_case()
    {
        var first = new BatchGateStore(_root);
        var second = new BatchGateStore(_root);

        var granted = Assert.IsType<BatchFlightClaim>(first.TryClaimFlight(Project, "batch-a", "host-a", 5, Now).Claim);
        var refused = second.TryClaimFlight(Project.ToLowerInvariant(), "batch-b", "host-b", 5, Now.AddMinutes(1));

        Assert.Null(refused.Claim);
        Assert.Equal(BatchFlightClaimPolicy.Held, refused.Refusal);
        Assert.Equal(granted, refused.Current);
        Assert.Equal(BatchFlightStage.Reserving, granted.Stage);
        Assert.Equal(Now.AddMinutes(5), granted.StageDeadlineUtc);
        Assert.Equal(granted, second.LiveFlightClaim(Project, Now.AddMinutes(1)));
    }

    [Fact]
    public async Task Competing_processes_get_exactly_one_live_claim()
    {
        const int processes = 8;
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, processes).Select(index => Task.Run(() =>
        {
            var store = new BatchGateStore(_root);
            start.Wait();
            return store.TryClaimFlight(Project, $"batch-{index}", $"host-{index}", 5, Now).Claim;
        })).ToArray();
        start.Set();

        var grants = (await Task.WhenAll(tasks)).OfType<BatchFlightClaim>().ToArray();

        var claim = Assert.Single(grants);
        Assert.Equal(1, claim.Fence);
        Assert.Equal(claim, new BatchGateStore(_root).ReadFlightClaim(Project));
    }

    [Fact]
    public void A_release_frees_the_scope_and_the_next_grant_raises_the_fence()
    {
        var store = new BatchGateStore(_root);
        var first = store.TryClaimFlight(Project, "batch-a", "host-a", 5, Now).Claim!;

        Assert.True(store.ReleaseFlightClaim(first, "flight finished", Now.AddMinutes(1)));
        var next = new BatchGateStore(_root).TryClaimFlight(Project, "batch-b", "host-b", 5, Now.AddMinutes(1)).Claim;

        Assert.Equal(2, Assert.IsType<BatchFlightClaim>(next).Fence);
        // The first holder's grant is gone: it can no longer advance, renew or release.
        Assert.False(store.IsFlightClaimLive(first, Now.AddMinutes(1)));
        Assert.Null(store.AdvanceFlightClaim(first, BatchFlightStage.Assembling, 30, Now.AddMinutes(1)));
        Assert.Null(store.RenewFlightClaim(first, Now.AddMinutes(1)));
        Assert.False(store.ReleaseFlightClaim(first, "late", Now.AddMinutes(1)));
        Assert.True(store.IsFlightClaimLive(next!, Now.AddMinutes(1)));
    }

    [Fact]
    public void Renewal_keeps_a_claim_live_but_never_past_its_step_deadline()
    {
        var store = new BatchGateStore(_root);
        var claim = store.TryClaimFlight(Project, "batch-a", "host-a", 5, Now).Claim!;

        var renewed = Assert.IsType<BatchFlightClaim>(store.RenewFlightClaim(claim, Now.AddMinutes(1.5)));
        Assert.Equal(Now.AddMinutes(1.5) + BatchFlightClaimPolicy.HeartbeatTtl, renewed.HeartbeatExpiresAtUtc);
        Assert.True(store.IsFlightClaimLive(renewed, Now.AddMinutes(3)));

        Assert.Null(store.RenewFlightClaim(renewed, Now.AddMinutes(5)));
        Assert.False(store.IsFlightClaimLive(renewed, Now.AddMinutes(5)));
    }

    [Fact]
    public void Advancing_a_step_sets_its_deadline_and_needs_a_live_claim()
    {
        var store = new BatchGateStore(_root);
        var claim = store.TryClaimFlight(Project, "batch-a", "host-a", 5, Now).Claim!;

        var gating = Assert.IsType<BatchFlightClaim>(
            store.AdvanceFlightClaim(claim, BatchFlightStage.Gating, 165, Now.AddMinutes(1.5)));
        Assert.Equal(Now.AddMinutes(1.5 + 165), gating.StageDeadlineUtc);
        Assert.Equal(165, gating.StageDeadlineMinutes);
        // Past the reservation deadline the gate step is still live.
        Assert.True(store.IsFlightClaimLive(gating, Now.AddMinutes(3)));
        // The heartbeat lapsed: no step change revives the claim.
        Assert.Null(store.AdvanceFlightClaim(gating, BatchFlightStage.Publishing, 15, Now.AddMinutes(4)));
    }

    [Fact]
    public void An_expired_claim_keeps_the_scope_closed_until_its_outcome_is_settled()
    {
        var holder = new BatchGateStore(_root);
        var other = new BatchGateStore(_root);
        var claim = holder.TryClaimFlight(Project, "batch-a", "host-a", 5, Now).Claim!;
        Assert.Null(other.ExpireFlightClaim(claim, BatchFlightExpiryOutcome.Retry, "too early", false, Now.AddMinutes(1)));

        var expired = Assert.IsType<BatchFlightClaim>(other.ExpireFlightClaim(claim,
            BatchFlightExpiryOutcome.VisibleGate, "deadline", settled: false, Now.AddMinutes(6)));

        Assert.Equal(BatchFlightClaimExpiry.StageDeadlinePassed, expired.End!.Expiry);
        Assert.False(expired.End.Settled);
        Assert.Equal(BatchFlightClaimPolicy.AwaitingSettlement,
            other.TryClaimFlight(Project, "batch-b", "host-b", 5, Now.AddMinutes(6)).Refusal);
        // The stale holder cannot turn the expiry into a release.
        Assert.False(holder.ReleaseFlightClaim(claim, "flight finished", Now.AddMinutes(6)));
        Assert.Null(other.ExpireFlightClaim(claim, BatchFlightExpiryOutcome.Retry, "twice", true, Now.AddMinutes(7)));

        Assert.True(other.SettleFlightClaim(expired, Now.AddMinutes(7)));
        Assert.False(other.SettleFlightClaim(expired, Now.AddMinutes(7)));
        Assert.Equal(2, other.TryClaimFlight(Project, "batch-b", "host-b", 5, Now.AddMinutes(7)).Claim!.Fence);
    }

    [Fact]
    public void A_host_that_knows_the_owner_is_gone_expires_the_claim_before_its_heartbeat_lapses()
    {
        var store = new BatchGateStore(_root);
        var claim = store.TryClaimFlight(Project, "batch-a", "host-a", 5, Now).Claim!;

        Assert.Null(store.ExpireFlightClaim(claim, BatchFlightExpiryOutcome.Recover, "alive", true, Now.AddSeconds(10)));
        var expired = store.ExpireFlightClaim(claim, BatchFlightExpiryOutcome.Recover, "restart", true,
            Now.AddSeconds(10), ownerGone: true);

        Assert.Equal(BatchFlightClaimExpiry.HeartbeatLost, expired!.End!.Expiry);
        Assert.True(store.TryClaimFlight(Project, "batch-a", "host-b", 5, Now.AddSeconds(10)).Claim is not null);
    }

    [Fact]
    public void A_crash_while_writing_a_claim_leaves_the_previous_record_intact()
    {
        var store = new BatchGateStore(_root);
        var first = store.TryClaimFlight(Project, "batch-a", "host-a", 5, Now).Claim!;
        BatchGateDurableWrites.ObserverForTesting = (path, stage) =>
        {
            if (stage == BatchGateWriteStage.Starting && path.EndsWith("claim.json", StringComparison.Ordinal))
                throw new IOException("process died before the claim write");
        };

        Assert.Throws<IOException>(() => store.ReleaseFlightClaim(first, "flight finished", Now.AddMinutes(1)));
        BatchGateDurableWrites.ObserverForTesting = null;

        Assert.Equal(first, new BatchGateStore(_root).ReadFlightClaim(Project));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(
            Directory.EnumerateFiles(_root, "claim.json", SearchOption.AllDirectories).Single())!, "*.tmp"));
    }
}
