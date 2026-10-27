using System.Collections.Concurrent;
using AgentStudio.Shared;

namespace AgentStudio.Runner;

/// <summary>Books local CLI work in the same fenced run authority used by remote claims.</summary>
public sealed class LocalRunClaimAdapter(
    RunLeaseService leases, RunnerIdentity identity, ILogger logger,
    RemoteRunStopRequestStore? stops = null)
{
    private sealed record HeldRun(RunLeaseInfoDto Lease, CancellationTokenSource Stop)
    {
        public int StopObserved;
    }

    private readonly ConcurrentDictionary<string, HeldRun> _held =
        new(StringComparer.OrdinalIgnoreCase);

    internal SemaphoreSlim StopGate(string taskKey) => leases.LocalStopGate(taskKey);

    public RunLeaseResponse TryAcquire(string taskKey, Action onAuthorityLost)
    {
        var result = leases.TryAcquire(new RunLeaseAcquireRequest(
            taskKey, identity.RunnerId, identity.RunnerName, identity.Hostname,
            Environment.ProcessId, identity.BackendName, RequestedTtlSeconds: 120,
            RepositoryId: $"local:{taskKey}", IdempotencyKey: $"local:{identity.RunnerId}:{Guid.NewGuid():N}"));
        if (!result.Granted || result.Lease is null) return result;
        var stop = new CancellationTokenSource();
        if (!_held.TryAdd(taskKey, new HeldRun(result.Lease, stop)))
        {
            stop.Dispose();
            leases.Release(ToRelease(result.Lease));
            return new RunLeaseResponse("Held", false, result.Lease, "Local run is already booked.");
        }
        _ = RenewUntilReleasedAsync(taskKey, result.Lease, stop.Token, onAuthorityLost);
        return result;
    }

    /// <summary>Observe a stop only for this local attempt and fence before the next step starts.</summary>
    public bool StopRequested(string taskKey)
    {
        if (!_held.TryGetValue(taskKey, out var held)) return false;
        if (Volatile.Read(ref held.StopObserved) != 0) return true;
        if (stops?.Observe(taskKey, held.Lease.AttemptId, held.Lease.FencingToken) is null)
            return false;
        Interlocked.Exchange(ref held.StopObserved, 1);
        return true;
    }

    public void Release(string taskKey)
    {
        if (!_held.TryRemove(taskKey, out var held)) return;
        held.Stop.Cancel();
        try
        {
            var gate = StopGate(taskKey);
            gate.Wait();
            try { ReleaseHeld(taskKey, held); }
            finally { gate.Release(); }
        }
        catch (Exception exception) { logger.LogWarning(exception, "Local run lease release failed for {TaskKey}", taskKey); }
        held.Stop.Dispose();
    }

    // Called by finalization while the gate is held through the review move.
    internal void ReleaseWhileStopGateHeld(string taskKey)
    {
        if (!_held.TryRemove(taskKey, out var held)) return;
        held.Stop.Cancel();
        try { ReleaseHeld(taskKey, held); }
        catch (Exception exception) { logger.LogWarning(exception, "Local run lease release failed for {TaskKey}", taskKey); }
        held.Stop.Dispose();
    }

    private void ReleaseHeld(string taskKey, HeldRun held)
    {
        var stopped = Volatile.Read(ref held.StopObserved) != 0;
        if (!stopped)
        {
            try { stopped = StopRequestedForRelease(taskKey, held); }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Local stop receipt observation failed for {TaskKey}", taskKey);
            }
        }
        leases.Release(ToRelease(held.Lease) with { Outcome = stopped ? "Stopped" : null },
            stopped ? () => stops?.Clear(taskKey, held.Lease.AttemptId,
                held.Lease.FencingToken, "settled") : null);
    }

    private bool StopRequestedForRelease(string taskKey, HeldRun held)
        => Volatile.Read(ref held.StopObserved) != 0
           || stops?.Observe(taskKey, held.Lease.AttemptId, held.Lease.FencingToken) is not null;

    private async Task RenewUntilReleasedAsync(
        string taskKey, RunLeaseInfoDto lease, CancellationToken ct, Action onAuthorityLost)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var result = leases.Renew(new RunLeaseHeartbeatRequest(
                    taskKey, lease.LeaseId, lease.FencingToken, identity.RunnerId,
                    RequestedTtlSeconds: 120, AttemptId: lease.AttemptId,
                    AuthorityEpoch: lease.AuthorityEpoch));
                if (result.Granted && !StopRequested(taskKey)) continue;
                logger.LogWarning("Local run authority ended for {TaskKey}: {Outcome}", taskKey, result.Outcome);
                onAuthorityLost();
                return;
            }
        }
        catch (OperationCanceledException exception) when (ct.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Local run renewal ended after release for {TaskKey}", taskKey);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Local run renewal failed for {TaskKey}", taskKey);
            onAuthorityLost();
        }
    }

    private RunLeaseReleaseRequest ToRelease(RunLeaseInfoDto lease)
        => new(lease.TaskKey, lease.LeaseId, lease.FencingToken, identity.RunnerId,
            lease.AttemptId, lease.AuthorityEpoch, $"local-release:{lease.LeaseId}");
}
