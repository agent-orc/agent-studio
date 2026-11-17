using AgentStudio.Runner;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Pipeline;

/// <summary>A full suite of this process that waits for the batch lane, and why (AGT-3856).</summary>
public sealed record BatchLaneWaiter(string RunId, string? Project, string GateId,
    IReadOnlyList<string> Cards, DateTimeOffset SinceUtc, string Reason);

/// <summary>What the batch lane admission of this process sees right now (AGT-3856).</summary>
public sealed record BatchLaneAdmissionSnapshot(int BatchGates, int CompanionSuites, int Limit,
    IReadOnlyList<BatchLaneWaiter> Waiting);

/// <summary>
/// AGT-3856: while a batch gate runs, at most
/// <see cref="AgentStudio.OrchestrationDecisions.BatchGateLookAheadPlanner.BatchCompanionSuiteLimit"/>
/// other full suite of this process starts on the gate host. The batch lane
/// (batch gates, isolation children, base checks) is never held. Checks,
/// narrowed runs and build-only gates are no full suite and pass through.
/// The rule reads only gates dispatched by this process, the same scope as
/// <see cref="GateSlotUsageTracker"/>.
/// </summary>
public sealed class BatchLaneAdmission(TimeProvider? clock = null, ILogger<BatchLaneAdmission>? logger = null)
{
    private readonly object _lock = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ILogger<BatchLaneAdmission> _logger = logger ?? NullLogger<BatchLaneAdmission>.Instance;
    private readonly HashSet<string> _batchGates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _companions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BatchLaneWaiter> _waiting = new(StringComparer.Ordinal);
    private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Re-check interval of a waiting suite; a release wakes it earlier.</summary>
    internal TimeSpan RecheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A batch candidate gate or one of its isolation children.</summary>
    public static bool IsBatchGate(BuildTestGateRequest request)
        => request.GateId == "batch-gate" && request.LedgerBatchId is not null;

    /// <summary>Any gate of a batch flight: its candidate, isolation and base checks.</summary>
    public static bool IsBatchLane(BuildTestGateRequest request)
        => request.LedgerBatchId is not null && request.GateId is "batch-gate" or "batch-base";

    /// <summary>A complete test suite: the full level and no narrowed check.</summary>
    public static bool IsFullSuite(BuildTestGateRequest request)
        => request.NarrowCheck is null
           && TestExecutionLevels.Normalize(request.RequiredTestLevel, fallback: "") == TestExecutionLevels.Full;

    public BatchLaneAdmissionSnapshot Snapshot()
    {
        lock (_lock)
            return new(_batchGates.Count, _companions.Count,
                AgentStudio.OrchestrationDecisions.BatchGateLookAheadPlanner.BatchCompanionSuiteLimit,
                _waiting.Values.OrderBy(waiter => waiter.SinceUtc).ToArray());
    }

    /// <summary>
    /// Admits <paramref name="request"/> and returns the lease to dispose when
    /// its gate returns, or null for a gate this rule does not count. A full
    /// suite outside the batch lane waits while the rule refuses it and calls
    /// <see cref="BuildTestGateRequest.OnBatchLaneWaiting"/> with the reason.
    /// </summary>
    public async Task<IDisposable?> EnterAsync(BuildTestGateRequest request, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        if (IsBatchGate(request))
        {
            lock (_lock) _batchGates.Add(id);
            return new Lease(this, id, batch: true);
        }
        if (IsBatchLane(request) || !IsFullSuite(request)) return null;
        var reported = false;
        try
        {
            while (true)
            {
                Task released;
                string? refusal;
                lock (_lock)
                {
                    refusal = MonolithTransitionAdapter.BatchCompanionSuiteRefusal(
                        _batchGates.Count, _companions.Count);
                    if (refusal is null)
                    {
                        _waiting.Remove(id);
                        _companions.Add(id);
                        return new Lease(this, id, batch: false);
                    }
                    if (!_waiting.ContainsKey(id))
                        _waiting[id] = new BatchLaneWaiter(id, request.Project, request.GateId,
                            request.LedgerCards ?? (request.JobId is null ? [] : [request.JobId]),
                            _clock.GetUtcNow(), refusal);
                    released = _released.Task;
                }
                if (!reported)
                {
                    reported = true;
                    _logger.LogInformation(
                        "batch-lane-suite-waiting gate={GateId} project={Project} job={JobId} reason={Reason}",
                        request.GateId, request.Project, request.JobId, refusal);
                    request.OnBatchLaneWaiting?.Invoke(refusal);
                }
                await Task.WhenAny(released, Task.Delay(RecheckInterval, _clock, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            lock (_lock) _waiting.Remove(id);
        }
    }

    private void Exit(string id, bool batch)
    {
        TaskCompletionSource released;
        lock (_lock)
        {
            if (batch) _batchGates.Remove(id);
            else _companions.Remove(id);
            released = _released;
            _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        released.TrySetResult();
    }

    private sealed class Lease(BatchLaneAdmission owner, string id, bool batch) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Exit(id, batch);
        }
    }
}

/// <summary>Applies <see cref="BatchLaneAdmission"/> to every gate this process dispatches.</summary>
public sealed class BatchLaneAdmissionGateRunner(IBuildTestGateRunner inner, BatchLaneAdmission admission)
    : IBuildTestGateRunner
{
    public async Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
        IReadOnlyList<string>? changedFiles, BuildProfile? profile, PostStepMode mode,
        TimeSpan timeout, CancellationToken ct)
    {
        if (mode == PostStepMode.Off)
            return await inner.RunAsync(request, changedFiles, profile, mode, timeout, ct).ConfigureAwait(false);
        IDisposable? lease;
        try
        {
            lease = await admission.EnterAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return RemoteBuildTestGateRunner.Failure(request, BuildTestGateFailureKind.Cancellation,
                "The gate was cancelled while it waited for the batch lane.") with
                {
                    CancellationReason = GateCancellationReasons.Shutdown,
                };
        }
        using (lease)
            return await inner.RunAsync(request, changedFiles, profile, mode, timeout, ct).ConfigureAwait(false);
    }
}
