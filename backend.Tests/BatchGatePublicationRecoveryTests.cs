using System.Reflection;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class BatchGatePublicationRecoveryTests : BatchGatePilotServiceTests
{
    private static Task Recover(BatchGatePilotService pilot)
        => (Task)typeof(BatchGatePilotService).GetMethod("RecoverPublishedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pilot, [CancellationToken.None])!;

    private void Complete(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string folder)
    {
        var destination = Path.Combine(_watchPath, TaskStates.Completed, Path.GetFileName(folder));
        Directory.Move(folder, destination);
        var path = Path.Combine(destination, "task.json");
        var task = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        task["state"] = TaskStates.Completed;
        File.WriteAllText(path, task.ToJsonString());
        factory.Services.GetRequiredService<TaskScannerService>().InvalidateCache();
    }

    [Fact]
    public async Task Missing_evidence_is_settled_and_logged_once_across_restarts()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, 2);
        SeedMember(factory, "RECOVERY-A", "docs/a.md");
        SeedMember(factory, "RECOVERY-B", "docs/b.md");
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.AfterRemotePushForTesting = () => throw new IOException("Interrupted after push.");
        try { await pilot.TickAsync(CancellationToken.None); }
        catch (IOException ex) { SilentCatch.Note(ex, "Expected simulated publication interruption."); }
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var manifest = Assert.Single(store.ListManifests());
        var run = Assert.Single(store.ListRuns(manifest.BatchId));
        File.Delete(run.EvidencePath);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await Recover(pilot);
            var recovery = Assert.IsType<BatchPublicationRecovery>(store.ReadPublicationRecovery(manifest.BatchId));
            Assert.Equal(attempt, recovery.Attempts);
            Assert.Equal(attempt == 3, recovery.Settled);
            var held = AssertPhase(store, manifest.BatchId, BatchPhase.Publishing);
            Assert.Equal("publication-remote-proof-ambiguous", held.Reason);
            Assert.Equal(run.BatchRunId, held.BatchRunId);
            Assert.Equal(run.CandidateSha, held.CandidateSha);
            Assert.Equal(2, store.ListPending().Count);
            Assert.Equal(0, _gate.FallbackRuns);
            if (attempt < 3) Assert.DoesNotContain("publication-recovery-settled", _errors.ToString());
        }
        var first = _errors.ToString();
        Assert.Contains("publication-recovery-settled", first);
        for (var i = 0; i < 5; i++) await Recover(pilot);
        Assert.Equal(first, _errors.ToString());
        using var restarted = BuildFactory();
        _ = restarted.CreateClient();
        await Recover(restarted.Services.GetRequiredService<BatchGatePilotService>());
        Assert.Equal(first, _errors.ToString());
    }

    [Fact]
    public async Task Paused_batch_is_visible_and_does_not_permanently_disable_publication_recovery()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, 3);
        SeedMember(factory, "PAUSED-A", "docs/a.md");
        SeedMember(factory, "PAUSED-B", "docs/b.md");
        var manifest = ClosePendingBatch(factory);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        foreach (var phase in new[] { BatchPhase.Assembling, BatchPhase.Paused })
            store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
                phase, null, 1, _clock, "recoverable pause"));
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        await Recover(pilot);
        Assert.Null(store.ReadPublicationRecovery(manifest.BatchId));
        foreach (var item in store.ListPending()) Complete(factory, item.JobFolderPath);
        using var state = JsonDocument.Parse(JsonSerializer.Serialize(pilot.State(ProjectName)));
        Assert.Single(state.RootElement.GetProperty("batches").EnumerateArray());
    }

    [Fact]
    public async Task Transient_recovery_failure_has_three_durable_attempts_and_one_log()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, 2);
        SeedMember(factory, "TRANSIENT-A", "docs/a.md");
        SeedMember(factory, "TRANSIENT-B", "docs/b.md");
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.AfterRemotePushForTesting = () => throw new IOException("Interrupted after push.");
        try { await pilot.TickAsync(CancellationToken.None); }
        catch (IOException ex) { SilentCatch.Note(ex, "Expected simulated publication interruption."); }
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var manifest = Assert.Single(store.ListManifests());
        var writes = 0;
        BatchGateDurableWrites.ObserverForTesting = (path, stage) =>
        {
            if (Path.GetFileName(path) == "remote-verified.json" && stage == BatchGateWriteStage.Starting)
            {
                writes++;
                throw new IOException("Temporary storage outage.");
            }
        };
        try
        {
            for (var i = 0; i < 6; i++) await Recover(pilot);
        }
        finally { BatchGateDurableWrites.ObserverForTesting = null; }
        Assert.Equal(3, writes);
        Assert.Equal(3, store.ReadPublicationRecovery(manifest.BatchId)!.Attempts);
        Assert.True(store.ReadPublicationRecovery(manifest.BatchId)!.Settled);
        Assert.Equal(1, _errors.ToString().Split("publication-recovery-settled").Length - 1);
    }

    [Theory]
    [InlineData(BatchPhase.Abandoned)]
    [InlineData(BatchPhase.Red)]
    [InlineData(BatchPhase.Published)]
    public async Task Terminal_batches_with_completed_members_leave_state_but_keep_the_ledger(BatchPhase phase)
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, 3);
        SeedMember(factory, "SETTLED-A", "docs/a.md");
        SeedMember(factory, "SETTLED-B", "docs/b.md");
        var manifest = ClosePendingBatch(factory);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        // Seed the reported legacy shape without invoking integration.
        foreach (var item in store.ListPending())
            Complete(factory, item.JobFolderPath);
        var path = phase == BatchPhase.Abandoned ? new[] { BatchPhase.Abandoned }
            : phase == BatchPhase.Red
                ? new[] { BatchPhase.Assembling, BatchPhase.Assembled, BatchPhase.Running, BatchPhase.Red }
                : new[] { BatchPhase.Assembling, BatchPhase.Assembled, BatchPhase.Running,
                    BatchPhase.Green, BatchPhase.Publishing, BatchPhase.Published };
        foreach (var next in path)
            store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
                next, null, 1, _clock, "legacy terminal batch"));
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        using var state = JsonDocument.Parse(JsonSerializer.Serialize(pilot.State(ProjectName)));
        Assert.Empty(state.RootElement.GetProperty("batches").EnumerateArray());
        Assert.Empty(state.RootElement.GetProperty("pending").EnumerateArray());
        Assert.Empty(state.RootElement.GetProperty("members").EnumerateObject());
        await Recover(pilot);
        var settlement = store.ReadPublicationRecovery(manifest.BatchId);
        Assert.NotNull(settlement);
        await Recover(pilot);
        Assert.Equal(settlement, store.ReadPublicationRecovery(manifest.BatchId));
        await pilot.TickAsync(CancellationToken.None);
        Assert.Empty(store.ListPending());
        Assert.Single(store.ListManifests());
        Assert.DoesNotContain("publication-recovery-failed", _errors.ToString());
    }

    [Fact]
    public void Completed_card_is_removed_from_stored_exclusions_and_fast_state_names_the_strategy()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, 3);
        SeedMember(factory, "COMPLETED-EXCLUDED", "docs/a.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var pending = Assert.Single(store.ListPending());
        Complete(factory, pending.JobFolderPath);
        store.AppendPlanDecision(ProjectName, new BatchPlanDecision("wait", "all pending members excluded",
            null, [], 0, [], ExcludedMembers: [new("COMPLETED-EXCLUDED", "pending-continue-intent")]));
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        using var strict = JsonDocument.Parse(JsonSerializer.Serialize(pilot.State(ProjectName)));
        Assert.Empty(strict.RootElement.GetProperty("plan").GetProperty("ExcludedMembers").EnumerateArray());
        factory.Services.GetRequiredService<ProjectSettingsService>().SetTestingStrategy(ProjectName, "fast");
        using var fast = JsonDocument.Parse(JsonSerializer.Serialize(pilot.State(ProjectName)));
        Assert.Contains("post-merge", fast.RootElement.GetProperty("plan").GetProperty("Reason").GetString());
    }
}
