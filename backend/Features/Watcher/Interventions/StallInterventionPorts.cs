namespace AgentStudio.Watcher;

/// <summary>A coding host's admission and current load, as the admission control reads it now.</summary>
/// <param name="CalmSinceUtc">
/// Start of the current run of samples whose load per core stays under the
/// restore threshold (target times the restore fraction); null while above it.
/// </param>
public sealed record HostAdmissionReading(
    string ClientId,
    string Host,
    int? Admission,
    int Cores,
    double? Load1,
    double? CpuPercent,
    double? MemoryUsedPercent,
    double TargetPercent,
    int ActiveSlots,
    double? LoadPerCore,
    DateTime? CalmSinceUtc,
    DateTime? SampleUtc = null,
    double? SwapBytesPerSecond = null,
    DateTime? CapacityStampUtc = null);

/// <summary>
/// Coding host admission (Settings &gt; Capacity), addressed by the host's
/// client identity id; a display name is never resolved. Running work is never stopped.
/// </summary>
public interface IHostAdmissionControl
{
    HostAdmissionReading? Read(string clientId, double restoreFraction, DateTime nowUtc);

    /// <summary>Sets the host's maximum parallel runs and returns the value read back afterwards.</summary>
    int? SetAdmission(string clientId, int maxParallelism);

    /// <summary>
    /// Changes admission only while it still reads <paramref name="expected"/>
    /// and, when <paramref name="expectedStampUtc"/> is given, nobody saved the
    /// capacity since that write. Returns the capacity stamp of this write, or
    /// null when it was refused.
    /// </summary>
    DateTime? TrySetAdmission(string clientId, int expected, int replacement, DateTime? expectedStampUtc = null);
}

/// <summary>
/// What a review daemon or gate worker runs now and which waiting items it can
/// claim itself. <see cref="BusySlots"/> is null when the worker's load is unknown.
/// </summary>
public sealed record ServiceWorkloadReading(int? BusySlots, bool RunningWork, int ClaimableWaiting,
    StallGateSlotFacts? GateSlots = null);

/// <summary>Reads a worker's running and claimable work, so a restart never ends work in flight.</summary>
public interface IServiceWorkload
{
    ServiceWorkloadReading? Read(string host, string role, DateTime nowUtc);

    /// <summary>Fence new claims on a review host until the restart attempt settles.</summary>
    IDisposable? TryFenceClaims(string host, string role);

    bool HasRestartFence(string host, string role) => false;
    void ReleaseRestartFence(string host, string role) { }
}

/// <summary>Outcome of one Operations Server restart; <see cref="Evidence"/> is shown in the receipt.</summary>
public sealed record ServiceRestartOutcome(bool Succeeded, string Evidence, bool TerminalConfirmed = true);

/// <summary>Restarts an allowlisted service through the Operations Server.</summary>
public interface IServiceRestartControl
{
    /// <summary>True when an Operations Agent on the host can restart the service.</summary>
    bool CanRestart(string host, string service);

    Task<ServiceRestartOutcome> RestartAsync(string host, string service, string idempotencyKey,
        string reason, CancellationToken ct);

    /// <summary>True if the accepted attempt is terminal, false if active or unreadable, null if absent.</summary>
    Task<bool?> IsTerminalAsync(string idempotencyKey, CancellationToken ct)
        => Task.FromResult<bool?>(false);
}

/// <summary>Routes provider attempts away from a host whose provider login is invalid.</summary>
public interface IProviderRouteControl
{
    IReadOnlyList<string> HealthyHosts(string provider, string exceptHost);

    /// <summary>True when the host has a review registration; only a review host's attempts are routed by provider.</summary>
    bool IsReviewHost(string host);

    /// <summary>True only for a review host whose provider capability is excluded and no longer admitted.</summary>
    bool IsRerouted(string host, string provider);

    /// <summary>True while the durable provider exclusion still blocks this host.</summary>
    bool IsExcluded(string host, string provider);

    /// <summary>The intervention that owns the host's provider exclusion, or null when none is in force.</summary>
    string? ExcludedBy(string host, string provider);

    void Reroute(string host, string provider, string interventionId);

    /// <summary>Lifts the exclusion only while <paramref name="interventionId"/> still owns it.</summary>
    void Restore(string host, string provider, string interventionId);
}

/// <summary>The batch gate queue of one project.</summary>
public interface IBatchGateControl
{
    StallGateFacts? Read(string project, DateTime nowUtc);

    /// <summary>
    /// Drops exactly the frozen review attempts whose card the authority key
    /// still names as completed or stale; returns the attempts that left the queue.
    /// </summary>
    IReadOnlyList<string> Drop(string project, IReadOnlyList<string> reviewAttemptIds, string reason);

    bool IsPending(string project, string reviewAttemptId);

    /// <summary>Closes the forming batch now so its members run; true when the request is recorded.</summary>
    bool CloseNow(string project);
}

/// <summary>A continuation base pinned on a card.</summary>
public sealed record ContinuationPin(string Card, string Ref, string Sha, string Reason);

/// <summary>Continuation bases of cards whose run ended on a provider rate limit.</summary>
public interface IContinuationControl
{
    IReadOnlyList<StallRequeueCandidate> RateLimitedRuns(IReadOnlyList<string> cards);

    /// <summary>
    /// Pins the card's last completed, reviewed delivery as its next run's
    /// base after re-checking that the card still waits in Ready after a
    /// rate-limited run with no base or an empty salvage; returns the pin read back.
    /// </summary>
    ContinuationPin? PinLastDelivery(string card, string interventionId);

    ContinuationPin? Read(string card);

    /// <summary>True when the card already left Ready, so its pinned base was claimed.</summary>
    bool LeftReady(string card);

    bool Clear(string card, string interventionId);
}
