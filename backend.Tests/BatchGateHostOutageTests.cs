using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3746: on 11 November 2026 the SSH tunnel to the gate host refused
/// connections for under four minutes and every in-flight gate parked its
/// members in Escalated. A gate host transport failure is now an outage of
/// the gate: members stay in Auto Review, one run probes with backoff, and
/// only a long outage escalates, once for the gate.
/// </summary>
public sealed class BatchGateHostOutageTests : BatchGatePilotServiceTests
{
    private static BuildTestGateResult HostRefused()
    {
        var reason = "Remote gate transport failure after bounded retries: ssh exited 255 "
            + $"(ssh: connect to host 172.30.{Random.Shared.Next(1, 250)}.1 port 12222: Connection refused)";
        return new BuildTestGateResult(BuildTestGateVerdict.Fail, null, 0, reason, reason, false, false)
        {
            FailureKind = BuildTestGateFailureKind.Environment,
        };
    }

    private SeededMember[] SeedPerTaskMembers(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory,
        string prefix, int count)
    {
        EnableBatchGate(factory, closeSize: 4);
        var members = Enumerable.Range(0, count)
            .Select(index => SeedMember(factory, $"{prefix}-{index}", $"docs/{prefix.ToLowerInvariant()}-{index}.md"))
            .ToArray();
        // Disabled, every queued member takes its own per-task gate on the tick.
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(Enabled: false));
        return members;
    }

    private IReadOnlyList<OrchestratorLogEntry> OutageFeed(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<OrchestratorLog>().Read(_watchPath)
            .Where(entry => entry.Topic == BatchGatePilotService.HostOutageTopic).ToArray();

    [Fact]
    public async Task Short_transport_outage_resumes_without_any_member_escalation()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        var members = SeedPerTaskMembers(factory, "DOC-OUTAGE-SHORT", 3);
        _gate.Fallback = _ => HostRefused();
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var openedAt = _clock;

        await pilot.TickAsync(CancellationToken.None);
        var probesDuringOutage = _gate.FallbackRuns;
        _clock = _clock.AddSeconds(10);
        await pilot.TickAsync(CancellationToken.None);

        // Inside the backoff step no further gate starts.
        Assert.Equal(probesDuringOutage, _gate.FallbackRuns);
        var outage = Assert.IsType<GateHostOutage>(pilot.HostOutage.Current);
        Assert.Equal(openedAt, outage.SinceUtc);
        Assert.StartsWith("gate host unreachable since", outage.WaitingReason, StringComparison.Ordinal);
        Assert.EndsWith("; waiting", outage.WaitingReason, StringComparison.Ordinal);
        foreach (var member in members)
        {
            AssertLane(member.Key, TaskStates.AutoReview);
            Assert.True(Assert.IsType<BatchGateOwnership>(Ownership(member.Key)).FallbackGateActive);
        }
        Assert.Empty(OutageFeed(factory));

        // The tunnel returns: the next due probe reaches the host and the gates resume.
        _gate.Fallback = request => Green(request.ExpectedSha);
        _clock = _clock.AddMinutes(4);
        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        Assert.Null(pilot.HostOutage.Current);
        Assert.True(_gate.FallbackRuns > probesDuringOutage);
        foreach (var member in members)
            Assert.NotEqual(TaskStates.Escalated, Lane(member.Key));
        var ended = Assert.Single(OutageFeed(factory));
        Assert.Equal(OrchestratorLogKinds.Alert, ended.Kind);
        Assert.Contains("Gate host outage from", ended.Summary, StringComparison.Ordinal);
        Assert.Contains(members[0].Key, ended.Summary, StringComparison.Ordinal);
        Assert.Contains("No member was escalated", ended.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_transport_outage_escalates_once_for_the_gate_and_no_member()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        var members = SeedPerTaskMembers(factory, "DOC-OUTAGE-LONG", 2);
        _gate.Fallback = _ => HostRefused();
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        for (var minute = 0; minute <= 80; minute += 5)
        {
            await pilot.TickAsync(CancellationToken.None);
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
            _clock = _clock.AddMinutes(5);
        }

        foreach (var member in members)
            AssertLane(member.Key, TaskStates.AutoReview);
        var escalation = Assert.Single(OutageFeed(factory));
        Assert.StartsWith("Gate escalated", escalation.Summary, StringComparison.Ordinal);
        Assert.True(pilot.HostOutage.Current?.Escalated);
        // Probes kept running with backoff: far fewer than one per member and tick.
        Assert.InRange(_gate.FallbackRuns, 2, 2 + 17);
    }

    [Fact]
    public async Task Real_red_run_still_proves_the_member_red()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        var member = SeedPerTaskMembers(factory, "DOC-OUTAGE-RED", 1)[0];
        _gate.Fallback = request => Red(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        AssertLane(member.Key, TaskStates.Escalated);
        Assert.Null(pilot.HostOutage.Current);
        Assert.Empty(OutageFeed(factory));
    }

    private string Lane(string key)
        => Assert.Single(TaskStates.All, state => Directory.Exists(Path.Combine(_watchPath, state, key)));
}

public sealed class GateHostOutagePolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 11, 30, 8, TimeSpan.Zero);

    [Theory]
    [InlineData("Remote gate transport failure after bounded retries: ssh exited 255", 0, true)]
    [InlineData("ssh: connect to host 10.0.0.1 port 12222: Connection refused", 0, true)]
    [InlineData("Remote gate transport failure after bounded retries: ssh exited 255", 2, false)]
    [InlineData("deterministic-suite-red", 0, false)]
    [InlineData("fatal: unable to access origin", 0, false)]
    public void Classifies_gate_host_transport_failures(string evidence, int failingItems, bool expected)
        => Assert.Equal(expected, GateHostOutagePolicy.IsHostUnavailable(true, false, evidence, failingItems));

    [Fact]
    public void Cancelled_or_passing_runs_are_never_host_unavailability()
    {
        Assert.False(GateHostOutagePolicy.IsHostUnavailable(true, true, "ssh exited 255", 0));
        Assert.False(GateHostOutagePolicy.IsHostUnavailable(false, false, "ssh exited 255", 0));
    }

    [Fact]
    public void Probes_back_off_and_escalation_fires_once()
    {
        Assert.True(GateHostOutagePolicy.MayStartRun(null, Start, 0, Start));
        Assert.False(GateHostOutagePolicy.MayStartRun(Start, Start, 1, Start.AddSeconds(29)));
        Assert.True(GateHostOutagePolicy.MayStartRun(Start, Start, 1, Start.AddSeconds(30)));
        Assert.False(GateHostOutagePolicy.MayStartRun(Start, Start, 9, Start.AddMinutes(4)));
        Assert.True(GateHostOutagePolicy.MayStartRun(Start, Start, 9, Start.AddMinutes(5)));
        var limit = GateHostOutagePolicy.DefaultEscalateAfter;
        Assert.False(GateHostOutagePolicy.EscalatesGate(Start, Start.AddMinutes(59), limit, false));
        Assert.True(GateHostOutagePolicy.EscalatesGate(Start, Start.AddMinutes(60), limit, false));
        Assert.False(GateHostOutagePolicy.EscalatesGate(Start, Start.AddMinutes(90), limit, true));
    }

    [Fact]
    public void Monitor_admits_one_probe_per_backoff_step_and_reports_the_closed_outage()
    {
        var clock = new MutableClock(Start);
        var monitor = new GateHostOutageMonitor(clock);
        Assert.True(monitor.TryStartRun());
        monitor.Unreachable("AGT-A", "ssh exited 255");
        monitor.Held("AGT-B");
        Assert.False(monitor.TryStartRun());
        clock.Now = Start.AddSeconds(30);
        Assert.True(monitor.TryStartRun());
        Assert.False(monitor.TryStartRun());
        Assert.Null(monitor.EscalationDue(TimeSpan.FromMinutes(60)));
        clock.Now = Start.AddMinutes(61);
        Assert.NotNull(monitor.EscalationDue(TimeSpan.FromMinutes(60)));
        Assert.Null(monitor.EscalationDue(TimeSpan.FromMinutes(60)));
        var ended = Assert.IsType<GateHostOutageEnded>(monitor.Reachable());
        Assert.Equal(Start, ended.SinceUtc);
        Assert.Equal(["AGT-A", "AGT-B"], ended.AffectedRuns);
        Assert.True(ended.Escalated);
        Assert.Null(monitor.Reachable());
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
