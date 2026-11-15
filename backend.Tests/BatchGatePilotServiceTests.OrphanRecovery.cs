using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGatePilotOrphanRecoveryTests : BatchGatePilotServiceTests
{
    [Fact]
    public async Task Restart_resumes_a_durable_fallback_handoff_after_the_batch_retired()
    {
        using (var original = BuildFactory())
        {
            _ = original.CreateClient();
            EnableBatchGate(original, closeSize: 2);
            SeedMember(original, "DOC-HANDOFF-A", "docs/handoff-a.md");
            SeedMember(original, "DOC-HANDOFF-B", "docs/handoff-b.md");
            var manifest = ClosePendingBatch(original);
            var store = original.Services.GetRequiredService<BatchGateStore>();
            foreach (var item in store.ListPending())
            {
                store.RecordFallbackCause(new BatchGateFallbackCause(ProjectName,
                    item.Subject.TaskKey, item.ReviewAttemptId, item.Subject.RunAttempt,
                    PerTaskFallbackRules.BatchPaused, "paused batch handoff", manifest.BatchId, _clock));
                BatchGateOwnershipStore.Write(item.JobFolderPath,
                    new BatchGateOwnership(item.ReviewAttemptId,
                        item.Subject with { OwningBatchId = null }));
            }
            store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
                BatchPhase.Paused, null, 1, _clock, "gate interrupted"));
            store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
                BatchPhase.Abandoned, null, 1, _clock, "paused-to-per-task-gate"));
        }

        using var restarted = BuildFactory();
        _ = restarted.CreateClient();
        EnableBatchGate(restarted, closeSize: 2);
        _gate.Fallback = request => Green(request.ExpectedSha);
        var pilot = restarted.Services.GetRequiredService<BatchGatePilotService>();
        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        AssertLane("DOC-HANDOFF-A", TaskStates.HumanReview);
        AssertLane("DOC-HANDOFF-B", TaskStates.HumanReview);
        Assert.True(_gate.FallbackRuns > 0);
    }

    [Fact]
    public async Task Reconciler_restores_a_missing_pending_marker()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 3);
        var member = SeedMember(factory, "DOC-MISSING-MARKER", "docs/missing-marker.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var item = Assert.Single(store.ListPending());
        File.Delete(Path.Combine(TaskPaths.LogsDir(item.JobFolderPath),
            "batch-gate-ownership.json"));
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var missing = Assert.Single((await pilot.CheckInvariantsAsync(ProjectName, CancellationToken.None)).Findings);
        Assert.Equal("restore-queued-marker", missing.Repair);
        Assert.Equal(member.ReviewAttemptId, missing.ReviewAttemptId);

        await pilot.TickAsync(CancellationToken.None);
        Assert.Equal(member.ReviewAttemptId,
            BatchGateOwnershipStore.Read(item.JobFolderPath)?.ReviewAttemptId);
    }

    [Fact]
    public async Task Invariant_report_names_the_age_of_an_overdue_unowned_generation()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 3);
        SeedMember(factory, "DOC-AGED-PENDING", "docs/aged-pending.md");
        _clock = _clock.AddMinutes(Settings(factory).MaximumWaitMinutes + 1);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var aged = Assert.Single((await pilot.CheckInvariantsAsync(ProjectName, CancellationToken.None)).Findings);
        Assert.Equal("retry-planner", aged.Repair);
        Assert.True(aged.AgeMs >= TimeSpan.FromMinutes(Settings(factory).MaximumWaitMinutes).TotalMilliseconds);
    }

    [Fact]
    public async Task Unreadable_manifest_does_not_release_its_member_to_a_new_batch()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var a = SeedMember(factory, "DOC-QUARANTINED-A", "docs/quarantined-a.md");
        SeedMember(factory, "DOC-QUARANTINED-B", "docs/quarantined-b.md");
        var manifest = ClosePendingBatch(factory);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        File.WriteAllText(Path.Combine(Assert.Single(store.ListManifestFolders()), "manifest.json"), "{");
        Assert.True(store.HasManifest(manifest.BatchId));

        await factory.Services.GetRequiredService<BatchGatePilotService>()
            .TickAsync(CancellationToken.None);

        Assert.Equal(manifest.BatchId, Ownership(a.Key)?.BatchId);
        Assert.Single(store.ListManifestFolders());
        Assert.Empty(store.ListManifests());
    }

    [Fact]
    public async Task Tick_recovers_a_formed_manifest_missing_from_the_pilots_flights()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2, scope: "code");
        var members = new[]
        {
            SeedMember(factory, "CODE-LOST-A", "src/lost-a.cs"),
            SeedMember(factory, "CODE-LOST-B", "src/lost-b.cs"),
        };
        var formed = ClosePendingBatch(factory);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        AssertPhase(store, formed.BatchId, BatchPhase.Formed);
        Assert.Empty(store.ListRuns(formed.BatchId));
        _gate.Batch = request => Green(request.ExpectedSha);

        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        using (var before = System.Text.Json.JsonDocument.Parse(
                   System.Text.Json.JsonSerializer.Serialize(pilot.State(ProjectName, history: 0))))
        {
            var batch = Assert.Single(before.RootElement.GetProperty("batches").EnumerateArray());
            Assert.Equal(formed.BatchId, batch.GetProperty("BatchId").GetString());
            Assert.NotEqual(System.Text.Json.JsonValueKind.Null, batch.GetProperty("nextAction").ValueKind);
        }
        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        AssertPhase(store, formed.BatchId, BatchPhase.Published);
        Assert.Equal(1, _gate.BatchRuns);
        Assert.All(members, member => AssertLane(member.Key, TaskStates.HumanReview));
    }

    [Fact]
    public async Task Formed_manifest_starts_even_when_later_planning_fails()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2, scope: "code");
        SeedMember(factory, "CODE-RECOVER-A", "src/recover-a.cs");
        SeedMember(factory, "CODE-RECOVER-B", "src/recover-b.cs");
        var formed = ClosePendingBatch(factory);
        SeedMember(factory, "CODE-NEXT-A", "src/next-a.cs");
        SeedMember(factory, "CODE-NEXT-B", "src/next-b.cs");
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.BeforeFormationAdmissionForTesting = () => throw new InvalidOperationException("planner interrupted");

        await Assert.ThrowsAsync<InvalidOperationException>(() => pilot.TickAsync(CancellationToken.None));
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        var store = factory.Services.GetRequiredService<BatchGateStore>();
        AssertPhase(store, formed.BatchId, BatchPhase.Published);
        Assert.Equal(1, _gate.BatchRuns);
    }

    [Fact]
    public async Task Tick_starts_a_formed_batch_past_its_maximum_wait_before_a_later_larger_batch()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 3, scope: "code");
        var olderMembers = new[]
        {
            SeedMember(factory, "CODE-OLD-A", "src/old-a.cs"),
            SeedMember(factory, "CODE-OLD-B", "src/old-b.cs"),
        };
        var older = ClosePendingBatch(factory);
        // AGT-3710: formed batches start first in, first out, so the older
        // batch starts before the later, larger one.
        _clock = _clock.AddMinutes(Settings(factory).MaximumWaitMinutes + 1);
        var newerMembers = new[]
        {
            SeedMember(factory, "CODE-NEW-A", "src/new-a.cs"),
            SeedMember(factory, "CODE-NEW-B", "src/new-b.cs"),
            SeedMember(factory, "CODE-NEW-C", "src/new-c.cs"),
        };
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var newPending = store.ListPending().Where(item => newerMembers.Any(member =>
            member.Key == item.Subject.TaskKey)).ToArray();
        var close = BatchGatePolicy.Close(newPending.Select(item => item.Subject), older.Scope,
            older.BaseSha, Settings(factory), newerMembers.Select(member => member.Key).ToArray(), _clock);
        var newer = Assert.IsType<BatchGateManifest>(close.Manifest);
        store.CloseManifest(newer);
        foreach (var item in newPending)
            BatchGateOwnershipStore.Write(item.JobFolderPath,
                new BatchGateOwnership(item.ReviewAttemptId, item.Subject, newer.BatchId));
        // Keep both closed snapshots distinct while testing the start queue.
        var settings = factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>();
        settings.SetBatchGate(ProjectName, Settings(factory) with { Planner = "look-ahead" });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.BatchAsync = async (request, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Green(request.ExpectedSha);
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        try
        {
            await pilot.TickAsync(CancellationToken.None);
            await _gate.FirstBatchStarted.WaitAsync(TimeSpan.FromSeconds(45));

            AssertPhase(store, older.BatchId, BatchPhase.Running);
            AssertPhase(store, newer.BatchId, BatchPhase.Formed);
        }
        finally
        {
            release.TrySetResult();
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        }

        // The finished flight started the newer batch at once; it re-formed on
        // the moved base and its successor started at once as well.
        var successor = Assert.Single(store.ListManifests(), item =>
            item.ReformedFromBatchId == newer.BatchId);
        AssertPhase(store, newer.BatchId, BatchPhase.Abandoned);

        AssertPhase(store, older.BatchId, BatchPhase.Published);
        AssertPhase(store, successor.BatchId, BatchPhase.Published);
        Assert.All(olderMembers.Concat(newerMembers), member =>
            AssertLane(member.Key, TaskStates.HumanReview));
    }

    [Fact]
    public void State_keeps_an_old_formed_batch_visible_and_explains_overdue_members()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 3);
        var members = new[]
        {
            SeedMember(factory, "DOC-ORPHAN-A", "docs/orphan-a.md"),
            SeedMember(factory, "DOC-ORPHAN-B", "docs/orphan-b.md"),
        };
        var formed = ClosePendingBatch(factory);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        store.AppendPlanDecision(ProjectName, new BatchPlanDecision("idle",
            "waiting for threshold or deadline", _clock.AddMinutes(20), [], 3, []));
        _clock = _clock.AddMinutes(150);

        using var state = JsonDocument.Parse(JsonSerializer.Serialize(
            factory.Services.GetRequiredService<BatchGatePilotService>().State(ProjectName, history: 0),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var plan = state.RootElement.GetProperty("plan");
        Assert.Equal("wait", plan.GetProperty("decision").GetString());
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("closeAtUtc").ValueKind);
        foreach (var member in members)
            Assert.Contains(member.Key + ": batch " + formed.BatchId,
                plan.GetProperty("reason").GetString());
        Assert.Contains(state.RootElement.GetProperty("batches").EnumerateArray(), item =>
            item.GetProperty("batchId").GetString() == formed.BatchId);
    }

    [Fact]
    public void State_never_reports_idle_for_an_overdue_unowned_pending_member()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 3);
        var member = SeedMember(factory, "DOC-OVERDUE", "docs/overdue.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        store.AppendPlanDecision(ProjectName, new BatchPlanDecision("idle",
            "waiting for threshold or deadline", _clock.AddMinutes(20), [], 3, []));
        _clock = _clock.AddMinutes(150);

        using var state = JsonDocument.Parse(JsonSerializer.Serialize(
            factory.Services.GetRequiredService<BatchGatePilotService>().State(ProjectName),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var plan = state.RootElement.GetProperty("plan");
        Assert.Equal("wait", plan.GetProperty("decision").GetString());
        Assert.Contains(member.Key + ": pending member awaits the next planner tick",
            plan.GetProperty("reason").GetString());
        Assert.Contains(plan.GetProperty("proposedMembers").EnumerateArray(), key =>
            key.GetString() == member.Key);
    }
}
