namespace AgentStudio.OrchestrationDecisions;

/// <summary>What a per-task fallback gate of a batch-deferred review waits for (AGT-3694).</summary>
public static class PerTaskFallbackWaitKinds
{
    /// <summary>An engine-owned GateResult decision on the gate outcome is open.</summary>
    public const string EngineDecision = "engine-decision";

    /// <summary>The flight waits for one of the per-task gate slots of the process.</summary>
    public const string PerTaskSlot = "per-task-slot";

    /// <summary>The flight waits while a batch runs or a formed batch waits to start.</summary>
    public const string BatchSlot = "batch-slot";

    /// <summary>The gate request waits for a machine gate slot.</summary>
    public const string MachineGateSlot = "machine-gate-slot";

    /// <summary>The flight builds its subject (fetch, merge) before the gate.</summary>
    public const string Preparing = "preparing";

    /// <summary>The gate holds a machine slot and runs.</summary>
    public const string GateRunning = "gate-running";

    /// <summary>The passed delivery waits for its per-task integration.</summary>
    public const string Integration = "integration";

    /// <summary>A durable per-task gate marker has no flight; the next planner tick resumes it.</summary>
    public const string ResumePending = "resume-pending";
}

public enum PerTaskFallbackWaitAction
{
    /// <summary>The wait is within its bound; the host reports it and waits.</summary>
    Wait,

    /// <summary>The wait passed its bound. The host raises a stall event and ends the wait.</summary>
    Stalled,
}

/// <summary>
/// One open engine decision of a per-task gate outcome. <paramref name="OpenSinceUtc"/>
/// is the creation time of the occurrence; <paramref name="LeaseExpiresAtUtc"/> is
/// the expiry of a leased occurrence's lease.
/// </summary>
public sealed record EngineDecisionWaitFacts(
    string Status, DateTimeOffset OpenSinceUtc, DateTimeOffset? LeaseExpiresAtUtc,
    bool ApplyingInThisProcess, DateTimeOffset NowUtc);

/// <summary>One per-task fallback flight of this process and the stage it is in.</summary>
public sealed record FallbackFlightFacts(
    string Stage, DateTimeOffset StageSinceUtc, TimeSpan GateTimeout, DateTimeOffset NowUtc);

public sealed record PerTaskFallbackWaitDecision(PerTaskFallbackWaitAction Action, string Reason)
{
    public bool Stalled => Action == PerTaskFallbackWaitAction.Stalled;
}

/// <summary>
/// AGT-3694: every wait of a per-task fallback is bounded. On 10 November 2026
/// two cards stayed in Auto Review for twelve hours with a spent start budget,
/// because an engine decision on their gate outcome stayed open and every tick
/// returned before the budget. The rule names when an open engine decision or a
/// fallback flight counts as stalled; the host then raises a stall event and
/// escalates the card as GateInfra instead of waiting again.
/// </summary>
public static class PerTaskFallbackWaitPolicy
{
    /// <summary>Card timeline kind of a fallback wait that passed its bound.</summary>
    public const string StallEventKind = "batch-fallback-stalled";

    /// <summary>An engine decision open this long is stalled, whatever its status.</summary>
    public static readonly TimeSpan EngineDecisionBound = TimeSpan.FromMinutes(30);

    /// <summary>A leased decision whose lease expired this long ago has no live engine.</summary>
    public static readonly TimeSpan LeaseGrace = TimeSpan.FromMinutes(2);

    /// <summary>Time a running gate may exceed its own gate timeout before it is stalled.</summary>
    public static readonly TimeSpan GateGrace = TimeSpan.FromMinutes(15);

    /// <summary>A queue stage (slot or batch wait) is stalled after this many gate timeouts.</summary>
    public const int QueueStageGateTimeouts = 4;

    /// <summary>A cancelled flight that has not stopped after this time is detached.</summary>
    public static readonly TimeSpan CancellationGrace = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan DefaultGateTimeout = TimeSpan.FromMinutes(60);

    /// <summary>
    /// An application running in this process is never stalled; its own retry
    /// ladder bounds it. A leased decision is stalled once its lease expired
    /// for <see cref="LeaseGrace"/>, and every open decision once it was open
    /// for <see cref="EngineDecisionBound"/>.
    /// </summary>
    public static PerTaskFallbackWaitDecision EngineDecision(EngineDecisionWaitFacts facts)
    {
        if (facts.Status is not ("pending" or "leased" or "applying"))
            return new(PerTaskFallbackWaitAction.Wait, $"engine decision is {facts.Status}");
        if (facts.ApplyingInThisProcess)
            return new(PerTaskFallbackWaitAction.Wait, "engine decision is applying in this process");
        if (facts.Status == "leased" && facts.LeaseExpiresAtUtc is { } expiry
            && facts.NowUtc - expiry >= LeaseGrace)
            return new(PerTaskFallbackWaitAction.Stalled,
                $"engine lease expired at {expiry.UtcDateTime:yyyy-MM-dd HH:mm} UTC and was not renewed");
        var open = facts.NowUtc - facts.OpenSinceUtc;
        return open >= EngineDecisionBound
            ? new(PerTaskFallbackWaitAction.Stalled,
                $"engine decision {facts.Status} for {Minutes(open)} min, bound {Minutes(EngineDecisionBound)} min")
            : new(PerTaskFallbackWaitAction.Wait,
                $"engine decision {facts.Status} for {Minutes(open)} min");
    }

    /// <summary>The bound of one flight stage for a project gate timeout.</summary>
    public static TimeSpan StageBound(string stage, TimeSpan gateTimeout)
    {
        var timeout = gateTimeout > TimeSpan.Zero ? gateTimeout : DefaultGateTimeout;
        return Queued(stage) ? timeout * QueueStageGateTimeouts : timeout + GateGrace;
    }

    /// <summary>A flight is stalled once its current stage outlived <see cref="StageBound"/>.</summary>
    public static PerTaskFallbackWaitDecision Flight(FallbackFlightFacts facts)
    {
        var bound = StageBound(facts.Stage, facts.GateTimeout);
        var age = facts.NowUtc - facts.StageSinceUtc;
        return age >= bound
            ? new(PerTaskFallbackWaitAction.Stalled,
                $"{facts.Stage} for {Minutes(age)} min, bound {Minutes(bound)} min")
            : new(PerTaskFallbackWaitAction.Wait, $"{facts.Stage} for {Minutes(age)} min");
    }

    /// <summary>
    /// Only a stage the flight owns is cancelled when it stalls. A queue stage
    /// waits for work with its own bound (another flight, a batch flight with
    /// its lease deadline, the machine queue budget), and the integration
    /// stage belongs to the integration coordinator; those stages are reported.
    /// </summary>
    public static bool CancelsStalledStage(string stage)
        => stage is PerTaskFallbackWaitKinds.Preparing or PerTaskFallbackWaitKinds.GateRunning;

    /// <summary>A cancelled flight still running after <see cref="CancellationGrace"/> is detached.</summary>
    public static bool DetachesCancelledFlight(DateTimeOffset cancelledAtUtc, DateTimeOffset nowUtc)
        => nowUtc - cancelledAtUtc >= CancellationGrace;

    private static bool Queued(string stage)
        => stage is PerTaskFallbackWaitKinds.PerTaskSlot or PerTaskFallbackWaitKinds.BatchSlot
            or PerTaskFallbackWaitKinds.MachineGateSlot;

    private static long Minutes(TimeSpan span) => (long)Math.Floor(Math.Max(0, span.TotalMinutes));
}
