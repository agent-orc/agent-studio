using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3746: a gate run that could not reach the gate host judged nothing.
/// Its members stay in Auto Review, in their pending pool or on their active
/// per-task marker, while one run at a time probes the host with backoff.
/// Any run that reaches the host ends the outage with one Operations event;
/// an outage longer than <see cref="GateHostOutagePolicy.EscalateMinutesConfigKey"/>
/// escalates once for the gate, never once per member.
/// <para>
/// AGT-3792: the monitor is the installation's one gate host health state,
/// shared with the gate transport and the orphan sweep. With a transport
/// probe no batch forms and no gate run starts while the host is unreachable;
/// each tick probes the host when the backoff allows, and the first answer
/// lets the pending members form one batch.
/// </para>
/// </summary>
public sealed partial class BatchGatePilotService
{
    public const string HostOutageTopic = "gate-host-outage";

    /// <summary>The verdict and lane label of a gate run that could not reach the gate host.</summary>
    private const string GateHostUnavailableVerdict = "gate-host-unavailable";

    public GateHostOutageMonitor HostOutage => _hostOutage;

    private static bool GateHostUnavailable(BuildTestGateResult gate)
        => MonolithTransitionAdapter.GateHostUnavailable(gate.Verdict == BuildTestGateVerdict.Fail,
            gate.FailureKind == BuildTestGateFailureKind.Cancellation, gate.Reason + "\n" + gate.Output,
            GateFailureTriagePolicy.FailingItems(gate.Output ?? string.Empty).Count);

    private TimeSpan HostOutageEscalateAfter()
        => _configuration?.GetValue<int?>(GateHostOutagePolicy.EscalateMinutesConfigKey) is > 0 and var minutes
            ? TimeSpan.FromMinutes(minutes) : GateHostOutagePolicy.DefaultEscalateAfter;

    /// <summary>
    /// Probes the gate host when the open outage's backoff allows. A probe
    /// that answers ends the outage; the planner forms the next batch on this
    /// same tick.
    /// </summary>
    private async Task ProbeGateHostAsync(CancellationToken ct)
    {
        if (await _hostOutage.ProbeIfDueAsync(ct).ConfigureAwait(false) is { } answer)
        {
            if (_hostOutage.Current is { } open)
                _logger.LogInformation(
                    "gate-host-probe host={Host} reachable=false since={Since} failed_probes={FailedProbes} error={Error}",
                    _hostOutage.Host, open.SinceUtc, open.FailedProbes, answer.Error);
            else
                _logger.LogInformation("gate-host-probe host={Host} reachable={Reachable}",
                    _hostOutage.Host, answer.Reachable);
        }
        if (_hostOutage.Current is { } outage)
            EscalateGateHostOutageIfDue(outage.WatchPaths.FirstOrDefault()
                ?? _scanner.GetWatchPaths().FirstOrDefault()?.Path ?? string.Empty);
    }

    /// <summary>A run that could not reach the host: opens or extends the outage.</summary>
    private void RecordGateHostUnavailable(string runKey, BuildTestGateResult gate, string watchPath)
    {
        var outage = _hostOutage.Unreachable(runKey, gate.Reason ?? gate.Output ?? "transport failure", watchPath);
        if (outage is null)
        {
            _logger.LogWarning(
                "gate-host-transport-failure run={RunKey} error={Error}: the gate host is not marked unreachable yet",
                runKey, gate.Reason);
            return;
        }
        _logger.LogWarning(
            "gate-host-unavailable run={RunKey} since={Since} failed_probes={FailedProbes} affected={Affected} error={Error}",
            runKey, outage.SinceUtc, outage.FailedProbes, outage.AffectedRuns.Count, outage.LastError);
        EscalateGateHostOutageIfDue(watchPath);
    }

    /// <summary>A run that the outage holds before it starts.</summary>
    private void HoldForGateHost(string runKey, string watchPath)
    {
        _hostOutage.Held(runKey, watchPath);
        EscalateGateHostOutageIfDue(watchPath);
    }

    private void EscalateGateHostOutageIfDue(string watchPath)
    {
        if (_hostOutage.EscalationDue(HostOutageEscalateAfter()) is not { } outage) return;
        _logger.LogError(
            "gate-host-outage-escalated since={Since} failed_probes={FailedProbes} affected={Affected}",
            outage.SinceUtc, outage.FailedProbes, outage.AffectedRuns.Count);
        Service<OrchestratorLog>()?.Append(watchPath, new OrchestratorLogEntry
        {
            Ts = _time.GetUtcNow().UtcDateTime,
            Kind = OrchestratorLogKinds.Alert,
            Topic = HostOutageTopic,
            Summary = $"Gate escalated: the gate host has been unreachable since "
                + $"{outage.SinceUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC "
                + $"({Minutes((long)(_time.GetUtcNow() - outage.SinceUtc).TotalMilliseconds)} min, "
                + $"{outage.FailedProbes} failed probe(s)). {outage.AffectedRuns.Count} run(s) wait in "
                + $"{TaskStates.AutoReview}: {string.Join(", ", outage.AffectedRuns)}. "
                + MonolithTransitionAdapter.GateHostOwnerAdvice(_hostOutage.Host),
            Reasoning = outage.LastError,
        });
    }

    /// <summary>A run reached the host. Ends an open outage; <see cref="OnGateHostOutageEnded"/> reports it.</summary>
    private void RecordGateHostReachable() => _hostOutage.Reachable();

    /// <summary>
    /// The one Operations event of a closed outage, whoever reached the host:
    /// a batch or per-task gate, a merge gate, or the transport probe.
    /// </summary>
    private void OnGateHostOutageEnded(GateHostOutageEnded ended)
    {
        _logger.LogWarning(
            "gate-host-outage-ended since={Since} ended={Ended} failed_probes={FailedProbes} affected={Affected}",
            ended.SinceUtc, ended.EndedUtc, ended.FailedProbes, string.Join(",", ended.AffectedRuns));
        var watchPaths = ended.WatchPaths.Count > 0 ? ended.WatchPaths
            : _scanner.GetWatchPaths().Select(entry => entry.Path).Where(path => !string.IsNullOrEmpty(path))
                .ToArray();
        foreach (var watchPath in watchPaths)
            Service<OrchestratorLog>()?.Append(watchPath, new OrchestratorLogEntry
            {
                Ts = ended.EndedUtc.UtcDateTime,
                Kind = OrchestratorLogKinds.Alert,
                Topic = HostOutageTopic,
                Summary = $"Gate host outage from {ended.SinceUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss} to "
                    + $"{ended.EndedUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC "
                    + $"({ended.FailedProbes} failed probe(s){(ended.Escalated ? ", escalated" : "")}). "
                    + $"Affected runs resumed: {string.Join(", ", ended.AffectedRuns)}. No member was escalated.",
            });
    }

    /// <summary>
    /// The per-task gate could not reach the host. The marker stays active
    /// with a fresh start budget, so the next due probe resumes the gate.
    /// </summary>
    private async Task HoldFallbackForGateHostAsync(TaskInfo live, BatchGatePendingRecord pending,
        BuildTestGateResult gate, CancellationToken ct)
    {
        RecordGateHostUnavailable(pending.Subject.TaskKey, gate, live.WatchPath);
        var ownership = BatchGateOwnershipStore.Read(live.FolderPath);
        await BatchGateOwnershipStore.WriteAsync(live.FolderPath,
            ownership?.ReviewAttemptId == pending.ReviewAttemptId
                ? ownership with { FallbackGateActive = true, FallbackGateStarts = 0 }
                : new BatchGateOwnership(pending.ReviewAttemptId, pending.Subject, FallbackGateActive: true),
            ct).ConfigureAwait(false);
        Lane(pending, GateHostUnavailableVerdict, outcome: "resume-per-task",
            reason: _hostOutage.Current?.WaitingReason ?? gate.Reason);
    }

    /// <summary>
    /// The batch candidate could not reach the host. The batch is abandoned
    /// with the readable waiting reason and its members return to the pool.
    /// </summary>
    private void HoldBatchForGateHost(BatchGateManifest manifest, BatchGateRunRecord run,
        BuildTestGateResult gate, IReadOnlyList<BatchGatePendingRecord> gated, long fence)
    {
        RecordVerdict(run, gate, GateHostUnavailableVerdict);
        RecordGateHostUnavailable(run.BatchRunId, gate,
            gated.FirstOrDefault()?.WatchPath ?? ProjectWatchPath(manifest.Scope.Project, gated) ?? string.Empty);
        var reason = _hostOutage.Current?.WaitingReason ?? "gate host transport failed; waiting";
        State(manifest, BatchPhase.Abandoned, null, fence, reason);
        ReturnToPending(manifest);
        foreach (var member in gated)
            Lane(member, GateHostUnavailableVerdict, outcome: "requeued", reason: reason);
    }
}
