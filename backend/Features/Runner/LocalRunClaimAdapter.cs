using System.Collections.Concurrent;
using AgentStudio.Shared;

namespace AgentStudio.Runner;

/// <summary>Books local CLI work in the same fenced run authority used by remote claims.</summary>
public sealed class LocalRunClaimAdapter(RunLeaseService leases, RunnerIdentity identity, ILogger logger)
{
    private readonly ConcurrentDictionary<string, (RunLeaseInfoDto Lease, CancellationTokenSource Stop)> _held =
        new(StringComparer.OrdinalIgnoreCase);

    public RunLeaseResponse TryAcquire(string taskKey, Action onAuthorityLost)
    {
        var result = leases.TryAcquire(new RunLeaseAcquireRequest(
            taskKey, identity.RunnerId, identity.RunnerName, identity.Hostname,
            Environment.ProcessId, identity.BackendName, RequestedTtlSeconds: 120,
            RepositoryId: $"local:{taskKey}", IdempotencyKey: $"local:{identity.RunnerId}:{Guid.NewGuid():N}"));
        if (!result.Granted || result.Lease is null) return result;
        var stop = new CancellationTokenSource();
        if (!_held.TryAdd(taskKey, (result.Lease, stop)))
        {
            stop.Dispose();
            leases.Release(ToRelease(result.Lease));
            return new RunLeaseResponse("Held", false, result.Lease, "Local run is already booked.");
        }
        _ = RenewUntilReleasedAsync(taskKey, result.Lease, stop.Token, onAuthorityLost);
        return result;
    }

    public void Release(string taskKey)
    {
        if (!_held.TryRemove(taskKey, out var held)) return;
        held.Stop.Cancel();
        try { leases.Release(ToRelease(held.Lease)); }
        catch (Exception exception) { logger.LogWarning(exception, "Local run lease release failed for {TaskKey}", taskKey); }
        held.Stop.Dispose();
    }

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
                if (result.Granted && result.StopRequest is null) continue;
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
