using AgentStudio.Operator;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts.BatchGate;
using AgentStudio.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3792: on 15 November 2026 the gate host refused SSH for more than eight
/// hours. The planner formed and abandoned the same four-member batch again
/// and again, and nothing in the product said the host was down. Now a few
/// transport failures of one kind mark the host unreachable, the planner forms
/// no batch while a transport probe backs off, and the first answer lets the
/// pending members form one batch.
/// </summary>
public sealed class BatchGateHostUnreachableTests : BatchGatePilotServiceTests
{
    private const string Refused = "ssh exited 255: ssh: connect to host 172.30.83.1 port 12222: Connection refused";

    [Fact]
    public async Task Refusing_transport_holds_one_outage_forms_no_batch_and_runs_one_batch_after_recovery()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        _probe.Host = "kori-gate";
        _probe.Answer = () => new GateHostProbeResult(false, Refused);
        var monitor = factory.Services.GetRequiredService<GateHostOutageMonitor>();
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var firstFailure = _clock;

        // Another card's merge gate meets the refusing transport first. Its
        // three bounded attempts are three refusals of one kind.
        var merge = await new RemoteBuildTestGateRunner(new RefusingTransport(),
                NullLogger<RemoteBuildTestGateRunner>.Instance, hostHealth: monitor)
            .RunAsync(new BuildTestGateRequest(_repo, new string('a', 40), "merge-gate"), null, null,
                PostStepMode.Fail, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Equal(BuildTestGateVerdict.Fail, merge.Verdict);
        var outage = Assert.IsType<GateHostOutage>(monitor.Current);
        Assert.Equal(firstFailure, outage.SinceUtc);
        Assert.Equal("connection-refused", outage.Kind);
        Assert.Contains("Connection refused", outage.LastError, StringComparison.Ordinal);

        EnableBatchGate(factory, closeSize: 4);
        var members = Enumerable.Range(0, 4)
            .Select(index => SeedMember(factory, $"DOC-UNREACHABLE-{index}", $"docs/unreachable-{index}.md"))
            .ToArray();
        var keys = members.Select(member => member.Key).ToArray();
        _gate.Batch = request => Green(request.ExpectedSha);

        // Fifty minutes of outage, past the planner's maximum wait.
        for (var minute = 0; minute < 50; minute += 2)
        {
            _clock = _clock.AddMinutes(2);
            await pilot.TickAsync(CancellationToken.None);
        }

        // One outage state, probed with backoff; no batch formed or abandoned.
        Assert.Equal(firstFailure, Assert.IsType<GateHostOutage>(monitor.Current).SinceUtc);
        Assert.InRange(_probe.Probes, 3, 12);
        Assert.Equal(0, _gate.BatchRuns);
        Assert.Empty(store.ListManifestFolders());
        Assert.Equal(keys, store.ListPending().OrderBy(item => item.Subject.EnqueueSequence)
            .Select(item => item.Subject.TaskKey));
        foreach (var member in members) AssertLane(member.Key, TaskStates.AutoReview);
        var invariants = await pilot.CheckInvariantsAsync(ProjectName, CancellationToken.None);
        Assert.DoesNotContain(invariants.Findings, finding => finding.Kind == "orphaned-pending-member");

        // Operations and Needs you name the host, the waiting members and the owner's options.
        var health = Assert.IsType<PipelineHealthSnapshot>(factory.Services.GetRequiredService<PipelineHealthService>()
            .Snapshot(ProjectName, _clock.UtcDateTime));
        Assert.Equal("alarm", health.Status);
        var alert = Assert.Single(health.Alerts,
            item => item.Kind == PipelineHealthConventions.GateHostUnreachableKind);
        Assert.StartsWith("Gate host kori-gate unreachable since", alert.Summary, StringComparison.Ordinal);
        Assert.Contains(string.Join(", ", keys), alert.Detail, StringComparison.Ordinal);
        Assert.Contains("connection is refused", alert.Detail, StringComparison.Ordinal);
        Assert.Contains("RemoteGate:SshHost", alert.Detail, StringComparison.Ordinal);
        var fact = Assert.Single(factory.Services.GetServices<IOperatorDecisionFactSource>()
            .OfType<OperatorGateHostFactSource>().Single().Collect(_clock.UtcDateTime));
        Assert.Equal(OperatorDecisionTypes.GateHostUnreachable, fact.Type);
        Assert.Null(fact.Project);
        Assert.Equal(keys, fact.Cards);

        // The transport answers: the next due probe ends the outage and the
        // pending members form one batch on the same tick.
        _probe.Answer = () => new GateHostProbeResult(true);
        _clock = _clock.AddMinutes(5);
        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        Assert.Null(monitor.Current);
        var manifest = Assert.Single(store.ListManifests());
        Assert.Equal(keys.Order(), manifest.Members.Select(member => member.TaskKey).Order());
        AssertPhase(store, manifest.BatchId, BatchPhase.Published);
        Assert.Equal(1, _gate.BatchRuns);
        foreach (var member in members) AssertLane(member.Key, TaskStates.HumanReview);
        var ended = Assert.Single(OutageFeed(factory));
        Assert.StartsWith("Gate host outage from", ended.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Services.GetRequiredService<PipelineHealthService>()
                .Snapshot(ProjectName, _clock.UtcDateTime)!.Alerts,
            item => item.Kind == PipelineHealthConventions.GateHostUnreachableKind);
    }

    [Fact]
    public async Task Red_gate_still_escalates_its_member_and_opens_no_outage()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        _probe.Host = "kori-gate";
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-UNREACHABLE-RED", "docs/unreachable-red.md");
        // Disabled, the queued member takes its own per-task gate on the tick.
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(Enabled: false));
        _gate.Fallback = request => Red(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        AssertLane(member.Key, TaskStates.Escalated);
        Assert.Null(factory.Services.GetRequiredService<GateHostOutageMonitor>().Current);
        Assert.Equal(0, _probe.Probes);
        Assert.Empty(OutageFeed(factory));
    }

    private IReadOnlyList<OrchestratorLogEntry> OutageFeed(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<OrchestratorLog>().Read(_watchPath)
            .Where(entry => entry.Topic == BatchGatePilotService.HostOutageTopic).ToArray();

    /// <summary>The SSH bridge while the gate host refuses connections.</summary>
    private sealed class RefusingTransport : IRemoteGateTransport
    {
        public Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles, BuildProfile? profile, PostStepMode mode, TimeSpan timeout,
            CancellationToken ct)
            => throw new IOException(Refused);
    }
}
