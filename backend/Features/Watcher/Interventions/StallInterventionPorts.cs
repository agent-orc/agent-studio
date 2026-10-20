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
    DateTime? CalmSinceUtc);

/// <summary>Coding host admission (Settings &gt; Capacity). Running work is never stopped.</summary>
public interface IHostAdmissionControl
{
    HostAdmissionReading? Read(string host, double restoreFraction, DateTime nowUtc);

    /// <summary>Sets the host's maximum parallel runs and returns the value read back afterwards.</summary>
    int? SetAdmission(string host, int maxParallelism);
}

/// <summary>Outcome of one Operations Server restart; <see cref="Evidence"/> is shown in the receipt.</summary>
public sealed record ServiceRestartOutcome(bool Succeeded, string Evidence);

/// <summary>Restarts an allowlisted service through the Operations Server.</summary>
public interface IServiceRestartControl
{
    /// <summary>True when an Operations Agent on the host can restart the service.</summary>
    bool CanRestart(string host, string service);

    Task<ServiceRestartOutcome> RestartAsync(string host, string service, string idempotencyKey,
        string reason, CancellationToken ct);
}

/// <summary>Routes provider attempts away from a host whose provider login is invalid.</summary>
public interface IProviderRouteControl
{
    IReadOnlyList<string> HealthyHosts(string provider, string exceptHost);

    bool IsRerouted(string host, string provider);

    void Reroute(string host, string provider, string interventionId);

    void Restore(string host, string provider);
}

/// <summary>The batch gate queue of one project.</summary>
public interface IBatchGateControl
{
    StallGateFacts? Read(string project, DateTime nowUtc);

    /// <summary>Drops completed or stale members and returns the cards that left the queue.</summary>
    IReadOnlyList<string> Drop(string project, IReadOnlyList<string> cards, string reason);

    bool IsPending(string project, string card);

    /// <summary>Closes the forming batch now so its members run; true when the request is recorded.</summary>
    bool CloseNow(string project);
}

/// <summary>A continuation base pinned on a card.</summary>
public sealed record ContinuationPin(string Card, string Ref, string Sha, string Reason);

/// <summary>Continuation bases of cards whose run ended on a provider rate limit.</summary>
public interface IContinuationControl
{
    IReadOnlyList<StallRequeueCandidate> RateLimitedRuns(IReadOnlyList<string> cards);

    /// <summary>Pins the card's last real delivery as its next run's base; returns the pin read back.</summary>
    ContinuationPin? PinLastDelivery(string card, string interventionId);

    ContinuationPin? Read(string card);

    /// <summary>True when the card already left Ready, so its pinned base was claimed.</summary>
    bool LeftReady(string card);

    bool Clear(string card, string interventionId);
}
