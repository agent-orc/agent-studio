using AgentStudio.Pipeline;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3568: the formation planner commits each formation and replacement as
/// one durable intent. A repack never retires a formed batch before its
/// successor is durable, and after a restart or a concurrent planner every
/// pending generation has exactly one owning batch.
/// </summary>
public sealed class BatchGateFormationIntentTests : BatchGatePilotServiceTests
{
    // Durable batch authority writes of the uninterrupted repack: intent,
    // successor manifest and first record, four ownership markers, two
    // retirements (record and history entry each) and the intent outcome.
    private const int RepackWrites = 13;
    private static readonly string[] Keys = ["CODE-INTENT-A", "CODE-INTENT-B", "CODE-INTENT-C", "CODE-INTENT-D"];

    public static IEnumerable<object[]> RepackCrashPoints
        => Enumerable.Range(1, RepackWrites).Select(point => new object[] { point });

    [Fact]
    public async Task Repack_performs_its_counted_durable_writes_and_retires_after_the_successor()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        var (first, second) = ArrangeTwoFormedBatches(factory);
        var crash = new CrashAfterWrite(crashAt: null);
        BatchGateDurableWrites.ObserverForTesting = crash.Observe;
        try { await Pilot(factory).RepackFormedBatchesAsync(CancellationToken.None); }
        finally { BatchGateDurableWrites.ObserverForTesting = null; }

        var writes = crash.Writes.ToList();
        Assert.True(writes.Count == RepackWrites, $"The repack made {writes.Count} durable writes, the crash "
            + "theory covers " + RepackWrites + ":\n  " + string.Join("\n  ", writes));
        // The successor's first phase record precedes every retirement.
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var successor = AssertSingleOwnerLayout(factory);
        var successorRecord = writes.IndexOf(Path.Combine(store.BatchDirectory(successor.BatchId), "phase.json"));
        Assert.True(successorRecord >= 0);
        foreach (var old in new[] { first, second })
            Assert.True(writes.IndexOf(Path.Combine(store.BatchDirectory(old.BatchId), "phase.json")) > successorRecord);
        Assert.Equal(first.ClosedAtUtc, successor.QueuedSinceUtc);
    }

    [Theory]
    [MemberData(nameof(RepackCrashPoints))]
    public async Task A_crash_after_any_repack_write_leaves_one_owner_per_generation_after_restart(int crashAt)
    {
        BatchGateManifest first, second;
        using (var factory = BuildFactory())
        {
            _ = factory.CreateClient();
            (first, second) = ArrangeTwoFormedBatches(factory);
            var crash = new CrashAfterWrite(crashAt);
            BatchGateDurableWrites.ObserverForTesting = crash.Observe;
            try
            {
                await Assert.ThrowsAsync<SimulatedProcessDeath>(() =>
                    Pilot(factory).RepackFormedBatchesAsync(CancellationToken.None));
            }
            finally { BatchGateDurableWrites.ObserverForTesting = null; }
            AssertNoRetirementBeforeSuccessor(factory, first, second);
        }

        _clock = _clock.AddMinutes(3);
        using var restarted = BuildFactory();
        _ = restarted.CreateClient();
        Configure(restarted);
        var pilot = Pilot(restarted);
        // The start of the restarted host's tick, before any start pass.
        pilot.ReconcilePhaseRecords();
        pilot.RecoverFormationIntents();
        await pilot.RepackFormedBatchesAsync(CancellationToken.None);

        var store = restarted.Services.GetRequiredService<BatchGateStore>();
        Assert.Empty(store.ListOpenIntents());
        AssertSingleOwnerLayout(restarted);
        Assert.Equal(BatchPhase.Abandoned, store.LatestState(first.BatchId).Phase);
        Assert.Equal(BatchPhase.Abandoned, store.LatestState(second.BatchId).Phase);
    }

    [Fact]
    public async Task Concurrent_planners_repacking_the_same_batches_form_exactly_one_successor()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        ArrangeTwoFormedBatches(factory);
        // A second planner shares the durable store and the file locks but
        // none of the first planner's in-memory state, as a second process does.
        var planners = new[] { Pilot(factory), ActivatorUtilities.CreateInstance<BatchGatePilotService>(factory.Services) };
        using var start = new Barrier(planners.Length);

        await Task.WhenAll(planners.Select(planner => Task.Run(async () =>
        {
            start.SignalAndWait(HangGuard);
            await planner.RepackFormedBatchesAsync(CancellationToken.None);
        })));

        var store = factory.Services.GetRequiredService<BatchGateStore>();
        Assert.Empty(store.ListOpenIntents());
        AssertSingleOwnerLayout(factory);
        // Two old batches and one successor: the losing planner closed nothing.
        Assert.Equal(3, store.ListManifests().Count);
    }

    [Fact]
    public async Task Concurrent_planner_ticks_close_one_batch_for_the_same_queued_members()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        SeedMember(factory, "DOC-INTENT-A", "docs/intent-a.md");
        SeedMember(factory, "DOC-INTENT-B", "docs/intent-b.md");
        var planners = new[] { Pilot(factory), ActivatorUtilities.CreateInstance<BatchGatePilotService>(factory.Services) };
        using var admission = new Barrier(planners.Length);
        foreach (var planner in planners)
        {
            planner.WaitForFlightsInTick = false;
            // Both planners hold their closed snapshot until the other has one too.
            planner.BeforeFormationAdmissionForTesting = () => admission.SignalAndWait(HangGuard);
            // No flight passes its reservation: the test observes formation only.
            planner.BeforeReservationForTesting = _ => throw new IOException("flight held by the test");
        }

        await Task.WhenAll(planners.Select(planner => Task.Run(() => planner.TickAsync(CancellationToken.None))));
        foreach (var planner in planners)
        {
            // A held flight ends with the hold's exception and leaves its batch formed.
            try { await planner.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None); }
            catch (IOException ex) when (ex.Message == "flight held by the test")
            {
                AgentStudio.Diagnostics.SilentCatch.Note(ex, "The held flight ended");
            }
        }

        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var manifest = Assert.Single(store.ListManifests());
        Assert.Equal(BatchPhase.Formed, store.LatestState(manifest.BatchId).Phase);
        Assert.Equal(["DOC-INTENT-A", "DOC-INTENT-B"], manifest.Members.Select(member => member.TaskKey).Order());
        Assert.All(manifest.Members, member => Assert.Equal(manifest.BatchId, Ownership(member.TaskKey)?.BatchId));
        Assert.Empty(store.ListOpenIntents());
        var plans = Directory.EnumerateFiles(Path.Combine(_workspace, "batch-gates", "plans"), "*.jsonl")
            .Select(File.ReadAllText);
        Assert.Contains(plans, text => text.Contains("formation intent refused: generation-owned", StringComparison.Ordinal));
    }

    [Fact]
    public void Successor_tick_request_survives_a_restart_and_is_served_once()
    {
        using (var factory = BuildFactory())
        {
            _ = factory.CreateClient();
            factory.Services.GetRequiredService<BatchGateStore>()
                .RequestSuccessorTick(ProjectName, "waiting-flight", "replaced by successor", _clock);
        }

        using var restarted = BuildFactory();
        _ = restarted.CreateClient();
        var store = restarted.Services.GetRequiredService<BatchGateStore>();
        Assert.Equal("waiting-flight", Assert.Single(store.ListSuccessorTicks()).BatchId);

        // The flight is gone with the old host, so the first tick serves the request.
        Pilot(restarted).RecoverFormationIntents();

        Assert.Empty(store.ListSuccessorTicks());
        Assert.False(store.TryConsumeSuccessorTick(ProjectName, "waiting-flight"));
    }

    private (BatchGateManifest First, BatchGateManifest Second) ArrangeTwoFormedBatches(
        WebApplicationFactory<Program> factory)
    {
        Configure(factory);
        foreach (var key in Keys)
        {
            SeedMember(factory, key, $"src/{key.ToLowerInvariant()}.cs");
            _clock = _clock.AddSeconds(1);
        }
        var first = CloseBatch(factory, Keys[0], Keys[1]);
        _clock = _clock.AddMinutes(1);
        var second = CloseBatch(factory, Keys[2], Keys[3]);
        _clock = _clock.AddMinutes(1);
        return (first, second);
    }

    private static void Configure(WebApplicationFactory<Program> factory)
    {
        var settings = factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>();
        settings.SetIntegrationBranch(ProjectName, "develop");
        settings.SetBatchGate(ProjectName, new BatchGateFormationOptions(
            Enabled: true, Scope: "code", Planner: "threshold", CloseSize: 6,
            MinimumSize: 2, MaximumSize: 8, DeadlineMinutes: 45,
            PressureSize: 2, Overlap: "separate"));
    }

    private BatchGateManifest CloseBatch(WebApplicationFactory<Program> factory, params string[] keys)
    {
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var pending = store.ListPending().Where(item => keys.Contains(item.Subject.TaskKey)).ToArray();
        var subject = pending[0].Subject;
        var scope = new BatchGateScope(subject.Project, subject.Repository, subject.IntegrationBranch,
            subject.GateProfile, subject.GateProfileDigest, subject.PlatformVersion);
        var manifest = Assert.IsType<BatchGateManifest>(BatchGatePolicy.Close(
            pending.Select(item => item.Subject), scope, RemoteTip(), Settings(factory), keys, _clock).Manifest);
        store.CloseManifest(manifest);
        foreach (var item in pending)
            BatchGateOwnershipStore.Write(item.JobFolderPath,
                new BatchGateOwnership(item.ReviewAttemptId, item.Subject, manifest.BatchId));
        return manifest;
    }

    private static BatchGatePilotService Pilot(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<BatchGatePilotService>();

    /// <summary>
    /// Each queued generation is a member of exactly one non-terminal batch,
    /// that batch is the one formed successor, and the card's ownership names it.
    /// </summary>
    private BatchGateManifest AssertSingleOwnerLayout(WebApplicationFactory<Program> factory)
    {
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var live = store.ListRecoverableManifests();
        foreach (var item in store.ListPending())
        {
            var owners = live.Where(manifest => manifest.Members.Any(member =>
                member.TaskKey == item.Subject.TaskKey && member.RunAttempt == item.Subject.RunAttempt)).ToArray();
            var owner = Assert.Single(owners);
            Assert.Equal(owner.BatchId, Ownership(item.Subject.TaskKey)?.BatchId);
        }
        var successor = Assert.Single(live);
        Assert.Equal(BatchPhase.Formed, store.LatestState(successor.BatchId).Phase);
        Assert.Equal(Keys, successor.Members.Select(member => member.TaskKey).Order(StringComparer.Ordinal));
        return successor;
    }

    /// <summary>Whatever the crash left, a retired predecessor implies a durable successor phase record.</summary>
    private static void AssertNoRetirementBeforeSuccessor(WebApplicationFactory<Program> factory,
        params BatchGateManifest[] predecessors)
    {
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        if (!predecessors.Any(old => store.LatestState(old.BatchId).Phase == BatchPhase.Abandoned)) return;
        var successor = Assert.Single(store.ListManifests(), manifest =>
            predecessors.All(old => old.BatchId != manifest.BatchId));
        Assert.NotNull(store.ReadPhaseRecord(successor.BatchId));
        Assert.Equal(BatchPhase.Formed, store.LatestState(successor.BatchId).Phase);
    }

    private sealed class SimulatedProcessDeath(string path) : Exception($"The host died after writing {path}.");

    /// <summary>The modelled host dies right after write <c>crashAt</c> and performs no later write.</summary>
    private sealed class CrashAfterWrite(int? crashAt)
    {
        private readonly List<string> _writes = [];
        private bool _crashed;

        public IReadOnlyList<string> Writes { get { lock (_writes) return _writes.ToArray(); } }

        public void Observe(string path, BatchGateWriteStage stage)
        {
            lock (_writes)
            {
                if (_crashed) throw new SimulatedProcessDeath(path);
                if (stage == BatchGateWriteStage.Starting) return;
                _writes.Add(path);
                if (_writes.Count != crashAt) return;
                _crashed = true;
            }
            throw new SimulatedProcessDeath(path);
        }
    }
}
