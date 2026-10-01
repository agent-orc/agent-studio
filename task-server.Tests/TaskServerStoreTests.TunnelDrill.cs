using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport.TunnelDrill;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// AGT-2937 (Dossier AGT-W65 D9): server half of the tunnel-loss drill for
/// coding attempts. The real <see cref="TaskServerStore"/> on an isolated temp
/// database decides; the outage is modelled as the runner falling silent while
/// a manual clock advances. All times are synthetic drill time.
/// </summary>
public sealed partial class TaskServerStoreTests
{
    private static readonly DateTimeOffset DrillStart = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Tunnel_drill_server_restart_beyond_expiry_re_adopts_exact_current_coding_attempt_once()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DrillStart);
        var evidence = new List<string>();
        var first = Store(temp.Path, clock);
        await first.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(first);
        await first.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await first.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a", RequestedTtlSeconds: 120), "runner-a", default);
        var lease = claim.Lease!;
        var grantedExpiry = lease.ExpiresAt;
        evidence.Add($"{clock.GetUtcNow():o} claim run={claim.Run!.RunId} fence={lease.Fence} expiresAt={grantedExpiry:o}");

        clock.Advance(TimeSpan.FromMinutes(5));
        var expiredRenewal = await Assert.ThrowsAsync<TaskServerConflictException>(() => first.RenewLeaseAsync(
            claim.Run.RunId,
            new LeaseRenewRequest("runner-a", lease.InstanceId, lease.LeaseId, lease.Fence, 120),
            "runner-a",
            default));
        Assert.Equal("lease-expired-process-unknown", expiredRenewal.Code);
        evidence.Add($"{clock.GetUtcNow():o} renew rejected code={expiredRenewal.Code}");

        var restarted = Store(temp.Path, clock);
        await restarted.InitializeAsync();
        evidence.Add($"{clock.GetUtcNow():o} task-server restart");
        var registration = await restarted.RegisterRunnerAsync(
            "runner-a",
            Runner("replacement-instance") with { ActiveAttempts = [ActiveCodingAttempt(claim.Run.RunId, task.TaskKey, lease)] },
            "runner-a",
            default);
        var adoption = Assert.Single(registration.AttemptAdoptions!);
        Assert.Equal("adopted", adoption.Status);
        Assert.True(adoption.ExpiresAt > clock.GetUtcNow().UtcDateTime);
        evidence.Add($"{clock.GetUtcNow():o} re-adoption status={adoption.Status} expiresAt={adoption.ExpiresAt:o}");

        var completion = Completion(lease, claim.Run.RunId);
        var completed = await restarted.CompleteRunAsync(claim.Run.RunId, completion, "runner-a", default);
        var replayed = await restarted.CompleteRunAsync(claim.Run.RunId, completion, "runner-a", default);
        Assert.Equal(completed.Status, replayed.Status);
        Assert.Equal("4-auto-review", (await restarted.GetTaskAsync(project.ProjectId, task.TaskKey, default))!.State);
        evidence.Add($"{clock.GetUtcNow():o} completion key={completion.IdempotencyKey} status={completed.Status} replay={replayed.Status}");

        TunnelDrillReport.Record(
            "task-server",
            new TunnelDrillOutage("server-restart-exact-readoption", DrillStart.UtcDateTime, clock.GetUtcNow().UtcDateTime, "runner->task-server (silent runner, manual clock)"),
            [
                new TunnelDrillAttempt("server-restart-exact-readoption", "coding", claim.Run.RunId, lease.Fence, 0,
                    grantedExpiry, null, null, null, null, null, completion.IdempotencyKey,
                    $"adopted; completion {completed.Status} applied once", "4-auto-review"),
            ],
            evidence);
    }

    [Fact]
    public async Task Tunnel_drill_superseded_coding_generation_cannot_change_authority_and_replacement_gets_a_new_fence()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DrillStart);
        var evidence = new List<string>();
        var first = Store(temp.Path, clock);
        await first.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(first);
        await first.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await first.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a", RequestedTtlSeconds: 120), "runner-a", default);
        var lease = claim.Lease!;

        clock.Advance(TimeSpan.FromMinutes(5));
        var restarted = Store(temp.Path, clock);
        await restarted.InitializeAsync();
        await restarted.ResolveUnknownAttemptAsync(
            claim.Run!.RunId,
            new ResolveUnknownAttemptRequest("drill: runner route lost beyond authority; worker stopped at stop-before"),
            "operator",
            default);
        evidence.Add($"{clock.GetUtcNow():o} unknown attempt resolved run={claim.Run.RunId}");
        await restarted.RegisterRunnerAsync("runner-b", Runner("instance-b"), "runner-b", default);
        var replacement = await restarted.ClaimAsync(
            new ClaimRequest("runner-b", "instance-b", RequestedTtlSeconds: 120), "runner-b", default);
        Assert.Equal("claimed", replacement.Status);
        Assert.NotEqual(claim.Run.RunId, replacement.Run!.RunId);
        Assert.True(replacement.Lease!.Fence > lease.Fence);
        evidence.Add($"{clock.GetUtcNow():o} replacement claim run={replacement.Run.RunId} fence={replacement.Lease.Fence}");

        var staleRegistration = await restarted.RegisterRunnerAsync(
            "runner-a",
            Runner("replacement-instance") with { ActiveAttempts = [ActiveCodingAttempt(claim.Run.RunId, task.TaskKey, lease)] },
            "runner-a",
            default);
        var staleAdoption = Assert.Single(staleRegistration.AttemptAdoptions!);
        Assert.NotEqual("adopted", staleAdoption.Status);
        var staleCompletion = await Assert.ThrowsAnyAsync<Exception>(() => restarted.CompleteRunAsync(
            claim.Run.RunId, Completion(lease, claim.Run.RunId), "runner-a", default));
        Assert.Equal("3-progress", (await restarted.GetTaskAsync(project.ProjectId, task.TaskKey, default))!.State);
        evidence.Add($"{clock.GetUtcNow():o} stale re-adoption status={staleAdoption.Status}; stale completion rejected: {staleCompletion.Message}");

        TunnelDrillReport.Record(
            "task-server",
            new TunnelDrillOutage("server-superseded-coding", DrillStart.UtcDateTime, clock.GetUtcNow().UtcDateTime, "runner->task-server (silent runner, manual clock)"),
            [
                new TunnelDrillAttempt("server-superseded-coding", "coding", claim.Run.RunId, lease.Fence, 0,
                    lease.ExpiresAt, null, null, null, null, null, null,
                    $"re-adoption {staleAdoption.Status}; completion rejected", "superseded"),
                new TunnelDrillAttempt("server-superseded-coding", "coding", replacement.Run.RunId, replacement.Lease.Fence, 0,
                    replacement.Lease.ExpiresAt, null, null, null, null, null, null, "new admitted claim", "3-progress"),
            ],
            evidence);
    }

    private static RunnerActiveAttempt ActiveCodingAttempt(string runId, string taskKey, LeaseDto lease)
        => new(RunnerAttemptKinds.Coding, runId, taskKey, lease.LeaseId, lease.Fence, LeaseInstanceId: lease.InstanceId);

    private static CompleteRunRequest Completion(LeaseDto lease, string runId)
        => new(
            "runner-a",
            lease.InstanceId,
            lease.LeaseId,
            lease.Fence,
            "blocked",
            IdempotencyKey: $"completion:{runId}:drill",
            Sequence: 1);
}
