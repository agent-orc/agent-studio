namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// One run that delivered work but never emitted a terminal sentinel, named as
/// the incident it is. <see cref="Cause"/> is the observed reason the sentinel
/// is missing; <see cref="Reason"/> is the completion reason recorded on the
/// run; <see cref="GateItem"/> is the board-visible incident line.
/// </summary>
public sealed record MissingSentinelIncident(string Cause, string Reason, string GateItem);

/// <summary>
/// Pure decision for the "completed out-of-band" path.
/// <para>
/// Before AGT-2820, a remote run that produced a verified delivery without a
/// terminal sentinel was reconciled straight into <c>5-human-review</c> with one
/// status line: "Result: Completed out-of-band (agent-runner-01). Remote work
/// completed without a terminal sentinel." On 2026-09-14/15 that happened to
/// AGT-2794, AGT-2817 and AGT-2819 - deliveries of 29, 43 and 61 files, pushed
/// to result branches, never reviewed. The acceptance guard in Human Review
/// correctly refused all three because the delivery is not in <c>develop</c>, so
/// the work was neither reviewed nor integrated, and nothing on the card said
/// so.
/// </para>
/// <para>
/// A missing sentinel is a statement about the run, not about the work. The
/// delivery is real and unreviewed, so the run is reported as a delivery that
/// goes to the review lane, carrying an incident gate item that names the cause
/// and refuses to read as an acceptance.
/// </para>
/// </summary>
public static class MissingSentinelIncidentPolicy
{
    public const string GateKey = "missing-terminal-sentinel";

    /// <summary>
    /// Returns the incident, or null when this run is not one: the outcome
    /// concluded normally, or there is no verified delivery to review.
    /// </summary>
    public static MissingSentinelIncident? Evaluate(
        ExecutionOutcomeKind outcome,
        ExecutionRawFacts? facts,
        bool deliveryVerified,
        string? resultRef,
        string? resultSha,
        string host)
    {
        if (outcome != ExecutionOutcomeKind.ProtocolInconclusive) return null;
        if (!deliveryVerified
            || string.IsNullOrWhiteSpace(resultRef)
            || string.IsNullOrWhiteSpace(resultSha))
            return null;

        var cause = Cause(facts);
        var delivery = $"{resultRef}@{resultSha}";
        var reason =
            $"Incident ({GateKey}): the run delivered {delivery} but never emitted a terminal " +
            $"sentinel; {cause}. The delivery is unreviewed, so it goes to review rather than " +
            "being stamped as a completion.";
        var gateItem =
            $"{GateKey}: host={host}; delivery={delivery}; cause={cause}; " +
            $"{Observations(facts)}. This run is an incident, not a completion: it is routed to " +
            "the review lane so the delivery is graded, and nothing here is an acceptance.";
        return new MissingSentinelIncident(cause, reason, gateItem);
    }

    /// <summary>
    /// The most specific cause the raw facts support, strongest evidence first.
    /// A signal outranks an exit code because a killed worker cannot have
    /// chosen its exit, and a host kill under load is the cause this path sees
    /// most often.
    /// </summary>
    private static string Cause(ExecutionRawFacts? facts)
    {
        if (facts is null) return "no process facts were captured for the worker";
        if (facts.OomKilled) return "the host killed the worker under memory pressure";
        if (facts.Signal is { } signal and > 0)
            return $"the worker was killed by signal {signal}";
        if (facts.HostShutdown) return "the host shut the worker down";
        if (facts.LeaseLost) return "the run lease was lost while the worker was in flight";
        if (facts.TimedOut) return "the worker exceeded its run budget";
        if (facts.LaunchFailed) return "the worker never started";
        if (facts.TransportState != ExecutionTransportState.Connected)
            return $"the worker transport was {facts.TransportState.ToString().ToLowerInvariant()}";
        if (facts.DurableOutputState is DurableOutputState.Missing or DurableOutputState.LocalOnly)
            return $"the durable output stream is {facts.DurableOutputState.ToString().ToLowerInvariant()}";
        if (facts.ExitCode is { } exitCode and not 0)
            return $"the worker exited {exitCode} without emitting a sentinel";
        return "the worker exited cleanly but emitted no sentinel";
    }

    private static string Observations(ExecutionRawFacts? facts)
        => facts is null
            ? "facts=none"
            : $"exit={facts.ExitCode?.ToString() ?? "none"}; " +
              $"signal={facts.Signal?.ToString() ?? "none"}; " +
              $"transport={facts.TransportState.ToString().ToLowerInvariant()}; " +
              $"durableOutput={facts.DurableOutputState.ToString().ToLowerInvariant()}; " +
              $"oomKilled={facts.OomKilled.ToString().ToLowerInvariant()}; " +
              $"timedOut={facts.TimedOut.ToString().ToLowerInvariant()}; " +
              $"leaseLost={facts.LeaseLost.ToString().ToLowerInvariant()}";
}
