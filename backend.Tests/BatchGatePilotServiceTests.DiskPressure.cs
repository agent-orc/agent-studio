using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3866: disk admission of the batch gate pilot and the disk pressure a
/// failed gate names. On 16 November 2026 the full suite failed on kori-gate
/// with twelve pilot tests red because the pilot measured the gate host's
/// 95 % full root file system and returned from every tick.
/// </summary>
public sealed class BatchGatePilotDiskPressureTests : BatchGatePilotServiceTests
{
    /// <summary>4 GiB free of 246 GiB: below both the 8 GiB and the 5 % threshold.</summary>
    private static readonly Contract.FixedHostFilesystemProbe Critical = new(4L << 30, 246L << 30);

    private readonly SwitchableProbe _disk = new();

    /// <summary>
    /// Reproduces the incident: the withdraw scenario of
    /// <c>Operator_withdraw_records_both_audits_and_abandonment_cause</c> stays
    /// unabandoned while the probe reports critical free space, and completes
    /// on the first tick after it recovers.
    /// </summary>
    [Fact]
    public async Task Critical_free_space_stops_the_tick_that_abandons_a_withdrawn_batch()
    {
        using var factory = BuildFactory(testServices: PinDisk);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        EnableBatchGate(factory, closeSize: 2);
        var member = SeedMember(factory, "DOC-DISK-A", "docs/disk-a.md");
        SeedMember(factory, "DOC-DISK-B", "docs/disk-b.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        Assert.Same(_disk, pilot.DiskProbe);
        var batch = ClosePendingBatch(factory);
        using (var moved = await client.PostAsJsonAsync($"/api/tasks/{member.Key}/move",
                   new MoveJobRequest { TargetState = TaskStates.Backlog,
                       WithdrawFromBatch = true, Reason = "Disk pressure reproduction." }))
            Assert.Equal(System.Net.HttpStatusCode.OK, moved.StatusCode);

        _disk.Current = Critical;
        await pilot.TickAsync(CancellationToken.None);

        Assert.NotEqual(BatchPhase.Abandoned, store.LatestState(batch.BatchId).Phase);
        using (var state = JsonDocument.Parse(await client.GetStringAsync(
                   $"/api/projects/{ProjectName}/batch-gate/state")))
        {
            var reason = state.RootElement.GetProperty("diskPauseReason").GetString();
            Assert.StartsWith(BatchGatePilotService.DiskPauseReasonPrefix, reason, StringComparison.Ordinal);
            Assert.Contains("4.0 GiB free (1.6%)", reason);
        }

        _disk.Current = Contract.FixedHostFilesystemProbe.Healthy;
        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(member.Key, AssertPhase(store, batch.BatchId, BatchPhase.Abandoned).CausedByTaskKey);
    }

    [Fact]
    public async Task Gate_failure_under_gate_host_disk_pressure_names_it_in_verdict_pause_and_parked_reason()
    {
        using var factory = BuildFactory(testServices: PinDisk);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-GATE-DISK", "docs/gate-disk.md");
        _gate.Batch = _ => GateHostDiskPressure(null);
        _gate.Fallback = request => GateHostDiskPressure(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        var manifest = Assert.Single(store.ListManifests());
        var paused = AssertPhase(store, manifest.BatchId, BatchPhase.Paused);
        Assert.StartsWith("GateInfra: " + GateDiskPressure.ReasonPrefix, paused.Reason, StringComparison.Ordinal);
        Assert.Contains("4294967296 bytes free", paused.Reason);
        Assert.Contains("1.6 %", paused.Reason);
        Assert.All(store.ListRuns(manifest.BatchId)
                .Select(run => store.ReadVerdict(manifest.BatchId, run.BatchRunId))
                .OfType<BatchGateRunVerdict>(),
            verdict => Assert.Equal(GateDiskPressure.Verdict, verdict.Classification));

        await pilot.TickAsync(CancellationToken.None);

        AssertLane(member.Key, TaskStates.Escalated);
        var parked = factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(member.Key, _watchPath)!.ParkedBlocker;
        Assert.Equal(HumanReviewEscalationCategories.GateInfrastructure, parked?.BlockerType);
        Assert.Contains("under disk pressure", parked?.Reason);
        Assert.Contains("/ on kori-gate had 4294967296 bytes free", parked?.Reason);
        Assert.DoesNotContain("could not run or complete", parked?.Reason);
    }

    [Fact]
    public async Task Gate_failure_while_the_api_host_disk_turns_critical_names_the_api_host_disk()
    {
        using var factory = BuildFactory(testServices: PinDisk);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        SeedMember(factory, "DOC-LOCAL-DISK", "docs/local-disk.md");
        _gate.Batch = _ =>
        {
            // The suite's own writes push this host below the threshold mid-run.
            _disk.Current = Critical;
            return Infrastructure();
        };
        _gate.Fallback = request => Red(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        var manifest = Assert.Single(store.ListManifests());
        var paused = AssertPhase(store, manifest.BatchId, BatchPhase.Paused);
        Assert.StartsWith("GateInfra: " + GateDiskPressure.ReasonPrefix + "instance data file system ",
            paused.Reason, StringComparison.Ordinal);
        Assert.Contains("4294967296 bytes free", paused.Reason);
    }

    [Fact]
    public void Attach_names_only_a_non_cancelled_failure_under_critical_disk()
    {
        var critical = Contract.HostDiskPressurePolicy.Decide(
            Critical.Measure([("gate workspaces", "/tmp")]), new Contract.HostDiskPressureThresholds());
        var healthy = Contract.HostDiskPressurePolicy.Decide(
            Contract.FixedHostFilesystemProbe.Healthy.Measure([("gate workspaces", "/tmp")]),
            new Contract.HostDiskPressureThresholds());

        var named = GateDiskPressure.Attach(Infrastructure(), critical, "kori-gate");

        Assert.Equal(BuildTestGateFailureKind.DiskFull, named.FailureKind);
        Assert.Equal("/tmp", named.DiskPressure?.Path);
        Assert.Equal(1.6, named.DiskPressure?.FreePercent);
        Assert.StartsWith(GateDiskPressure.ReasonPrefix + "gate workspaces file system /tmp on kori-gate had "
            + "4294967296 bytes free (4.0 GiB, 1.6 % of 246.0 GiB)", named.Reason, StringComparison.Ordinal);
        Assert.EndsWith("Original gate reason: GateInfra", named.Reason, StringComparison.Ordinal);
        Assert.Same(named, GateDiskPressure.Attach(named, critical, "kori-gate"));
        var green = Green("abc");
        Assert.Same(green, GateDiskPressure.Attach(green, critical, "kori-gate"));
        var cancelled = Infrastructure() with { FailureKind = BuildTestGateFailureKind.Cancellation };
        Assert.Same(cancelled, GateDiskPressure.Attach(cancelled, critical, "kori-gate"));
        var unpressured = Infrastructure();
        Assert.Same(unpressured, GateDiskPressure.Attach(unpressured, healthy, "kori-gate"));
    }

    /// <summary>What the remote worker reports for a run that failed on a full gate host.</summary>
    private static BuildTestGateResult GateHostDiskPressure(string? sha)
        => GateDiskPressure.Attach(
            new BuildTestGateResult(BuildTestGateVerdict.Fail, 1, 1, "12 tests failed",
                "diagnosis=environment", false, false)
            { TestedSha = sha, FailureKind = BuildTestGateFailureKind.Environment },
            Contract.HostDiskPressurePolicy.Decide(Critical.Measure([("gate workspaces", "/")]),
                new Contract.HostDiskPressureThresholds()),
            "kori-gate");

    private void PinDisk(IServiceCollection services)
    {
        services.RemoveAll<Contract.IHostFilesystemProbe>();
        services.AddSingleton<Contract.IHostFilesystemProbe>(_disk);
    }

    private sealed class SwitchableProbe : Contract.IHostFilesystemProbe
    {
        public volatile Contract.IHostFilesystemProbe Current = Contract.FixedHostFilesystemProbe.Healthy;

        public IReadOnlyList<Contract.HostFilesystemUsageDto> Measure(
            IEnumerable<(string Purpose, string Path)> paths,
            IEnumerable<(string Name, string Path)>? ownedDirectories = null)
            => Current.Measure(paths, ownedDirectories);
    }
}
