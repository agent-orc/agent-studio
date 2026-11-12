using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3563: a process that dies after any durable write of a batch transition
/// leaves a state the next tick of a restarted host brings to a valid outcome.
/// Each scenario runs once uninterrupted to count its writes; the crash theory
/// then stops the host after each one of them. A crash model that throws after
/// the write and refuses every later write of the dead process is installed
/// for the scenario's own asynchronous flow only.
/// </summary>
public sealed class BatchGatePilotFaultInjectionTests : BatchGatePilotServiceTests
{
    // Durable batch authority writes of each uninterrupted scenario. The
    // counting facts pin them, so the crash theories cover every write.
    private const int PublicationWrites = 35;
    private const int BaseMoveWrites = 37;
    private const int IsolationWrites = 61;

    // Each scenario's crash points are their own test class, so the gate runs
    // them in parallel with the rest of the suite instead of after this class.
    public sealed class PublicationCrashes : IDisposable
    {
        private readonly BatchGatePilotFaultInjectionTests _host = new();
        public static IEnumerable<object[]> Points => CrashPoints(PublicationWrites);
        public void Dispose() => _host.Dispose();

        [Fact]
        public async Task Scenario_performs_its_counted_durable_writes()
            => AssertWrites(PublicationWrites, await _host.PublicationScenarioAsync(crashAt: null));

        [Theory]
        [MemberData(nameof(Points))]
        public Task A_crash_after_any_write_from_formation_to_release_recovers(int crashAt)
            => _host.PublicationScenarioAsync(crashAt);
    }

    public sealed class BaseMoveCrashes : IDisposable
    {
        private readonly BatchGatePilotFaultInjectionTests _host = new();
        public static IEnumerable<object[]> Points => CrashPoints(BaseMoveWrites);
        public void Dispose() => _host.Dispose();

        [Fact]
        public async Task Scenario_performs_its_counted_durable_writes()
            => AssertWrites(BaseMoveWrites, await _host.BaseMoveScenarioAsync(crashAt: null));

        [Theory]
        [MemberData(nameof(Points))]
        public Task A_crash_after_any_write_of_an_abandon_and_re_formation_recovers(int crashAt)
            => _host.BaseMoveScenarioAsync(crashAt);
    }

    public sealed class IsolationCrashes : IDisposable
    {
        private readonly BatchGatePilotFaultInjectionTests _host = new();
        public static IEnumerable<object[]> Points => CrashPoints(IsolationWrites);
        public void Dispose() => _host.Dispose();

        [Fact]
        public async Task Scenario_performs_its_counted_durable_writes()
            => AssertWrites(IsolationWrites, await _host.IsolationScenarioAsync(crashAt: null));

        [Theory]
        [MemberData(nameof(Points))]
        public Task A_crash_after_any_write_of_a_red_isolation_recovers(int crashAt)
            => _host.IsolationScenarioAsync(crashAt);
    }

    /// <summary>Two documentation members form, gate green, publish and release in one tick.</summary>
    private Task<IReadOnlyList<string>> PublicationScenarioAsync(int? crashAt)
    {
        SeededMember[] members = [];
        return CrashAndRecoverAsync(crashAt,
            configure: factory =>
            {
                EnableBatchGate(factory, closeSize: 2);
                _gate.Batch = request => Green(request.ExpectedSha);
                _gate.Fallback = request => Green(request.ExpectedSha);
            },
            arrange: factory => members =
            [
                SeedMember(factory, "DOC-CRASH-A", "docs/crash-a.md"),
                SeedMember(factory, "DOC-CRASH-B", "docs/crash-b.md"),
            ],
            completed: () => members.All(member => LaneOf(member.Key) == TaskStates.HumanReview),
            assertOutcome: () =>
            {
                Assert.All(members, member => AssertLane(member.Key, TaskStates.HumanReview));
                Assert.True(TreeHas(RemoteTip(), "docs/crash-a.md") && TreeHas(RemoteTip(), "docs/crash-b.md"));
            });
    }

    /// <summary>
    /// A formed batch meets a moved base before replay: it is abandoned, its
    /// members return to the queue and a successor forms on the new base.
    /// </summary>
    private Task<IReadOnlyList<string>> BaseMoveScenarioAsync(int? crashAt)
    {
        SeededMember[] members = [];
        return CrashAndRecoverAsync(crashAt,
            configure: factory =>
            {
                EnableBatchGate(factory, closeSize: 2, scope: "code");
                _gate.Batch = request => Green(request.ExpectedSha);
                _gate.Fallback = request => Green(request.ExpectedSha);
            },
            arrange: factory =>
            {
                members =
                [
                    SeedMember(factory, "CODE-CRASH-A", "src/crash-a.cs"),
                    SeedMember(factory, "CODE-CRASH-B", "src/crash-b.cs"),
                ];
                ClosePendingBatch(factory);
                AdvanceOriginDevelop("src/crash-base-move.cs");
                return members;
            },
            completed: () => members.All(member => LaneOf(member.Key) == TaskStates.HumanReview),
            assertOutcome: () =>
            {
                Assert.All(members, member => AssertLane(member.Key, TaskStates.HumanReview));
                Assert.True(TreeHas(RemoteTip(), "src/crash-a.cs") && TreeHas(RemoteTip(), "src/crash-b.cs")
                    && TreeHas(RemoteTip(), "src/crash-base-move.cs"));
            });
    }

    /// <summary>
    /// A red pair isolates: the green member publishes from its child batch
    /// and the culprit is escalated, by its isolation finding or its per-task gate.
    /// </summary>
    private Task<IReadOnlyList<string>> IsolationScenarioAsync(int? crashAt)
    {
        SeededMember[] members = [];
        return CrashAndRecoverAsync(crashAt,
            configure: factory =>
            {
                EnableBatchGate(factory, closeSize: 2);
                _gate.Batch = request => TreeHas(request.ExpectedSha!, "docs/crash-red.md")
                    ? Red(request.ExpectedSha) : Green(request.ExpectedSha);
                _gate.Fallback = request => TreeHas(request.ExpectedSha!, "docs/crash-red.md")
                    ? Red(request.ExpectedSha) : Green(request.ExpectedSha);
            },
            arrange: factory => members =
            [
                SeedMember(factory, "DOC-CRASH-GREEN", "docs/crash-green.md"),
                SeedMember(factory, "DOC-CRASH-RED", "docs/crash-red.md"),
            ],
            completed: () => LaneOf(members[0].Key) == TaskStates.HumanReview
                && LaneOf(members[1].Key) == TaskStates.Escalated,
            assertOutcome: () =>
            {
                AssertLane(members[0].Key, TaskStates.HumanReview);
                AssertLane(members[1].Key, TaskStates.Escalated);
                Assert.True(TreeHas(RemoteTip(), "docs/crash-green.md"));
                Assert.False(TreeHas(RemoteTip(), "docs/crash-red.md"));
            });
    }

    [Fact]
    public async Task Invariant_check_reports_an_orphaned_formation_and_the_next_tick_repairs_it()
    {
        using (var factory = BuildFactory())
        {
            _ = factory.CreateClient();
            EnableBatchGate(factory, closeSize: 2);
            SeedMember(factory, "DOC-ORPHANED-A", "docs/orphaned-a.md");
            SeedMember(factory, "DOC-ORPHANED-B", "docs/orphaned-b.md");
            var crash = new CrashAfterWrite(path => path.EndsWith("manifest.json", StringComparison.Ordinal));
            BatchGateDurableWrites.ObserverForTesting = crash.Observe;
            try
            {
                await Assert.ThrowsAsync<SimulatedProcessDeath>(() => factory.Services
                    .GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None));
            }
            finally { BatchGateDurableWrites.ObserverForTesting = null; }
        }

        using var restarted = BuildFactory();
        var client = restarted.CreateClient();
        EnableBatchGate(restarted, closeSize: 2);
        _gate.Batch = request => Green(request.ExpectedSha);
        var finding = Assert.Single(await Findings(client));
        Assert.Equal("orphaned-formed-batch", finding.GetProperty("kind").GetString());
        Assert.Equal("initialize-formed", finding.GetProperty("repair").GetString());
        // The state snapshot reads the manifest without a phase record as formed.
        using (var orphaned = JsonDocument.Parse(await client.GetStringAsync(
                   $"/api/projects/{ProjectName}/batch-gate/state")))
            Assert.Equal("formed", Assert.Single(orphaned.RootElement.GetProperty("batches")
                .EnumerateArray()).GetProperty("phase").GetString());

        await restarted.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);

        AssertLane("DOC-ORPHANED-A", TaskStates.HumanReview);
        AssertLane("DOC-ORPHANED-B", TaskStates.HumanReview);
        using var state = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/projects/{ProjectName}/batch-gate/state"));
        Assert.Empty(state.RootElement.GetProperty("invariants").GetProperty("findings").EnumerateArray());
    }

    [Fact]
    public async Task Invariant_check_reports_an_abandoned_batch_that_still_owns_members()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2, scope: "code");
        var a = SeedMember(factory, "CODE-OWNED-A", "src/owned-a.cs");
        var b = SeedMember(factory, "CODE-OWNED-B", "src/owned-b.cs");
        var manifest = ClosePendingBatch(factory);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        // An abandon whose process died before it returned the members.
        store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Abandoned, null, 1, _clock, "interrupted before release"));
        _gate.Batch = request => Green(request.ExpectedSha);

        var findings = await Findings(client);

        Assert.Equal([a.Key, b.Key], findings.Select(item => item.GetProperty("taskKey").GetString()).Order());
        Assert.All(findings, item =>
        {
            Assert.Equal("terminal-batch-owns-member", item.GetProperty("kind").GetString());
            Assert.Equal("abandoned", item.GetProperty("phase").GetString());
            Assert.Equal("release-member", item.GetProperty("repair").GetString());
        });
        Assert.Equal(a.ReviewAttemptId, findings.Single(item =>
            item.GetProperty("taskKey").GetString() == a.Key).GetProperty("reviewAttemptId").GetString());

        await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);

        AssertLane(a.Key, TaskStates.HumanReview);
        AssertLane(b.Key, TaskStates.HumanReview);
        Assert.Empty(await Findings(client));
    }

    [Fact]
    public async Task Invariant_check_reports_published_members_whose_release_a_crash_stopped()
    {
        IReadOnlyList<JsonElement> findings;
        using (var factory = BuildFactory())
        {
            var client = factory.CreateClient();
            EnableBatchGate(factory, closeSize: 2);
            SeedMember(factory, "DOC-UNRELEASED-A", "docs/unreleased-a.md");
            SeedMember(factory, "DOC-UNRELEASED-B", "docs/unreleased-b.md");
            _gate.Batch = request => Green(request.ExpectedSha);
            var published = false;
            var crash = new CrashAfterWrite(path =>
                (published |= path.EndsWith("publication.json", StringComparison.Ordinal))
                && path.EndsWith(".json", StringComparison.Ordinal)
                && Path.GetFileName(Path.GetDirectoryName(path)) == "state");
            BatchGateDurableWrites.ObserverForTesting = crash.Observe;
            var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
            try
            {
                // A flight that dies after its verified publication keeps the
                // phase for the next tick, so the tick itself may not fail.
                try
                {
                    await pilot.TickAsync(CancellationToken.None);
                    await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
                }
                catch (Exception ex) when (crash.Crashed) { AgentStudio.Diagnostics.SilentCatch.Note(ex, "dead host"); }
            }
            finally { BatchGateDurableWrites.ObserverForTesting = null; }
            Assert.True(crash.Crashed);
            findings = await Findings(client);
        }

        Assert.Equal(2, findings.Count);
        Assert.All(findings, item =>
        {
            Assert.Equal("published-batch-unresolved-member", item.GetProperty("kind").GetString());
            Assert.Equal("none", item.GetProperty("repair").GetString());
        });
        using var restarted = BuildFactory();
        var restartedClient = restarted.CreateClient();
        EnableBatchGate(restarted, closeSize: 2);
        await restarted.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);

        AssertLane("DOC-UNRELEASED-A", TaskStates.HumanReview);
        AssertLane("DOC-UNRELEASED-B", TaskStates.HumanReview);
        Assert.Empty(await Findings(restartedClient));
    }

    /// <summary>
    /// Runs the scenario under the crash model. Uninterrupted, it returns the
    /// durable writes it made. Interrupted, the dead host is disposed, its
    /// coordinator lease expires, and a restarted host ticks until the
    /// scenario completes; the outcome, the invariant check and the state
    /// history of every batch are then asserted.
    /// </summary>
    private async Task<IReadOnlyList<string>> CrashAndRecoverAsync(int? crashAt,
        Action<WebApplicationFactory<Program>> configure,
        Func<WebApplicationFactory<Program>, SeededMember[]> arrange,
        Func<bool> completed, Action assertOutcome)
    {
        const int Ticks = 4;
        var crash = new CrashAfterWrite(crashAt);
        using (var factory = BuildFactory())
        {
            _ = factory.CreateClient();
            configure(factory);
            arrange(factory);
            var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
            BatchGateDurableWrites.ObserverForTesting = crash.Observe;
            try
            {
                for (var tick = 0; tick < Ticks && !crash.Crashed && !completed(); tick++)
                {
                    try
                    {
                        await pilot.TickAsync(CancellationToken.None);
                        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
                    }
                    catch (Exception ex) when (crash.Crashed)
                    {
                        AgentStudio.Diagnostics.SilentCatch.Note(ex, "The modelled host died");
                    }
                }
                try { await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None); }
                catch (Exception ex) when (crash.Crashed)
                {
                    AgentStudio.Diagnostics.SilentCatch.Note(ex, "A flight of the modelled host died");
                }
            }
            finally { BatchGateDurableWrites.ObserverForTesting = null; }
            if (crashAt is null)
            {
                Assert.True(completed(), $"The uninterrupted scenario did not complete. Errors: {_errors}");
                assertOutcome();
                await AssertConsistentAuthorityAsync(factory);
                return crash.Writes;
            }
            Assert.True(crash.Crashed, $"Crash point {crashAt} is beyond the scenario's {crash.Writes.Count} writes.");
        }

        // A dead host renews nothing: its coordinator lease runs out.
        _clock = _clock.AddMinutes(3);
        using var restarted = BuildFactory();
        _ = restarted.CreateClient();
        configure(restarted);
        var recovered = restarted.Services.GetRequiredService<BatchGatePilotService>();
        for (var tick = 0; tick < Ticks && !completed(); tick++)
        {
            await recovered.TickAsync(CancellationToken.None);
            await recovered.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
            _clock = _clock.AddMinutes(1);
        }
        Assert.True(completed(),
            $"Crash after write {crashAt} ({crash.Writes[^1]}) did not recover. Errors: {_errors}");
        assertOutcome();
        await AssertConsistentAuthorityAsync(restarted);
        return crash.Writes;
    }

    /// <summary>No invariant finding, and each batch's history has every number up to its record's version.</summary>
    private static async Task AssertConsistentAuthorityAsync(WebApplicationFactory<Program> factory)
    {
        var report = await factory.Services.GetRequiredService<BatchGatePilotService>()
            .CheckInvariantsAsync(ProjectName, CancellationToken.None);
        Assert.True(report.Findings.Count == 0, string.Join("; ", report.Findings));
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        foreach (var manifest in store.ListManifests())
        {
            var record = Assert.IsType<BatchPhaseRecord>(store.ReadPhaseRecord(manifest.BatchId));
            var history = Directory.EnumerateFiles(Path.Combine(store.BatchDirectory(manifest.BatchId), "state"), "*.json")
                .Select(path => long.Parse(Path.GetFileNameWithoutExtension(path), System.Globalization.CultureInfo.InvariantCulture))
                .Order().ToArray();
            Assert.Equal(Enumerable.Range(1, (int)record.Version).Select(number => (long)number), history);
        }
    }

    private static void AssertWrites(int expected, IReadOnlyList<string> writes)
        => Assert.True(writes.Count == expected,
            $"The scenario made {writes.Count} durable writes, the crash theory covers {expected}. "
            + "Update the constant so every write keeps a crash point:\n  "
            + string.Join("\n  ", writes.Select(Describe)));

    private static string Describe(string path)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(path));
        return parent + "/" + Path.GetFileName(path);
    }

    private static IEnumerable<object[]> CrashPoints(int writes)
        => Enumerable.Range(1, writes).Select(point => new object[] { point });

    private string? LaneOf(string key)
        => TaskStates.All.SingleOrDefault(state => Directory.Exists(Path.Combine(_watchPath, state, key)));

    private static async Task<IReadOnlyList<JsonElement>> Findings(HttpClient client)
    {
        using var report = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/projects/{ProjectName}/batch-gate/invariants"));
        return report.RootElement.GetProperty("findings").EnumerateArray()
            .Select(item => item.Clone()).ToArray();
    }

    private sealed class SimulatedProcessDeath(string path) : Exception($"The host died after writing {path}.");

    /// <summary>
    /// The modelled host dies right after the write numbered <c>crashAt</c>
    /// (or the first one <c>crashesAfter</c> selects): that write is durable,
    /// the call that made it throws, and every later write of the dead host
    /// throws before it starts.
    /// </summary>
    private sealed class CrashAfterWrite
    {
        private readonly int? _crashAt;
        private readonly Func<string, bool>? _crashesAfter;
        private readonly List<string> _writes = [];
        private bool _crashed;

        public CrashAfterWrite(int? crashAt) => _crashAt = crashAt;
        public CrashAfterWrite(Func<string, bool> crashesAfter) => _crashesAfter = crashesAfter;

        public bool Crashed { get { lock (_writes) return _crashed; } }
        public IReadOnlyList<string> Writes { get { lock (_writes) return _writes.ToArray(); } }

        public void Observe(string path, BatchGateWriteStage stage)
        {
            lock (_writes)
            {
                if (_crashed) throw new SimulatedProcessDeath(path);
                if (stage == BatchGateWriteStage.Starting) return;
                _writes.Add(path);
                if (_writes.Count != _crashAt && _crashesAfter?.Invoke(path) != true) return;
                _crashed = true;
            }
            throw new SimulatedProcessDeath(path);
        }
    }
}
