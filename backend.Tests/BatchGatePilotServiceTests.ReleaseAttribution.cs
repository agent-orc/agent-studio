using System.Text.Json.Nodes;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3717: a published batch member whose rebased commits cannot be mapped
/// onto its attributed chain, and the repair of the runner salvage subjects
/// that left such chains empty.
/// </summary>
public sealed class BatchGatePilotReleaseAttributionTests : BatchGatePilotServiceTests
{
    [Fact]
    public async Task Unattributable_rebased_member_is_escalated_and_the_other_members_are_released()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 3);
        // Each delivery was cut from the same base, so every member after the
        // first is rebased onto the candidate and rewrites its commit.
        var members = new[]
        {
            SeedMember(factory, "DOC-RELEASE-1", "docs/release-1.md"),
            SeedMember(factory, "DOC-RELEASE-2", "docs/release-2.md"),
            SeedMember(factory, "DOC-RELEASE-3", "docs/release-3.md"),
        };
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var manifest = ClosePendingBatch(factory);
        // The middle member lost its attribution, as a rejected salvage subject
        // left it: the release must not stop at it.
        var unattributed = manifest.Members[1].TaskKey;
        ClearCommits(factory, unattributed);
        _gate.Batch = request => Green(request.ExpectedSha);
        _gate.Fallback = _ => throw new InvalidOperationException("A published member ran a per-task gate.");

        await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);

        AssertPhase(store, manifest.BatchId, BatchPhase.Published);
        Assert.Contains(store.ReadReplay(manifest.BatchId, unattributed).Replacements,
            item => !string.Equals(item.OriginalSha, item.RebasedSha, StringComparison.OrdinalIgnoreCase));
        foreach (var member in members.Where(member => member.Key != unattributed))
            AssertLane(member.Key, TaskStates.HumanReview);
        AssertLane(unattributed, TaskStates.Escalated);
        var status = File.ReadAllText(Path.Combine(_watchPath, TaskStates.Escalated, unattributed, "status.md"));
        Assert.Contains("rebased commit(s) could not be recorded", status, StringComparison.Ordinal);
        Assert.Contains(Assert.Single(store.ListRuns(manifest.BatchId)).CandidateSha[..8], status,
            StringComparison.Ordinal);
        Assert.Empty(store.ListPending());
        Assert.Equal(0, _gate.FallbackRuns);
        Assert.Equal(Assert.Single(store.ListRuns(manifest.BatchId)).CandidateSha, RemoteTip());
    }

    [Fact]
    public async Task Salvage_subject_repair_reattributes_the_rejected_generation_and_the_batch_releases_it()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var baseSha = RemoteTip();
        var first = SeedMember(factory, "DOC-SALVAGE-1", "docs/salvage-1.md");
        // The Deploy 38 runner wrote the card title, naming a related card,
        // into the subject of its own salvage commit.
        var salvaged = SeedMember(factory, "DOC-SALVAGE-2", "docs/salvage-2.md",
            commitMessage: SalvageMessage("DOC-SALVAGE-2", "CP-9 · Fixture slice"));
        // An agent commit that names another card stays rejected.
        var foreign = SeedMember(factory, "DOC-SALVAGE-3", "docs/salvage-3.md", settleReview: false,
            commitMessage: "docs(CP-9): touch another card");
        foreach (var member in new[] { salvaged, foreign })
        {
            ClearCommits(factory, member.Key);
            var folder = Path.Combine(_watchPath, TaskStates.AutoReview, member.Key);
            ReviewSubjectStore.Write(folder, ReviewSubjectStore.Read(folder)! with
            {
                BaseSha = baseSha,
                IntegrationBranch = "refs/heads/develop",
            });
        }
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        scanner.InvalidateCache();
        var sweep = new RemoteCompletionAttributionSweep(
            scanner,
            factory.Services.GetRequiredService<AgentStudio.Git.GitService>(),
            factory.Services.GetRequiredService<TaskMutationService>(),
            factory.Services.GetRequiredService<RemoteTokenReceiptService>(),
            Path.Combine(_workspace, "migrations", RemoteCompletionAttributionSweep.ReportFileName),
            _clock,
            NullLogger<RemoteCompletionAttributionSweep>.Instance);

        var report = sweep.RunSalvageSubjectRepairOnce();

        var repaired = Assert.Single(report.Tasks, row => row.Commits > 0);
        Assert.Equal(salvaged.Key, repaired.TaskKey);
        Assert.Equal(0, report.UnresolvedTasks);
        Assert.Equal(salvaged.ResultSha, Assert.Single(scanner.FindJob(salvaged.Key, _watchPath)!.Commits).Sha);
        Assert.Empty(scanner.FindJob(foreign.Key, _watchPath)!.Commits);
        Assert.True(File.Exists(Path.Combine(_workspace, "migrations",
            RemoteCompletionAttributionSweep.SalvageSubjectReportFileName)));
        Assert.True(sweep.RunSalvageSubjectRepairOnce().AlreadyCompleted);

        _gate.Batch = request => Green(request.ExpectedSha);
        _gate.Fallback = _ => throw new InvalidOperationException("A published member ran a per-task gate.");
        await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);

        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var manifest = Assert.Single(store.ListManifests());
        AssertPhase(store, manifest.BatchId, BatchPhase.Published);
        AssertLane(first.Key, TaskStates.HumanReview);
        AssertLane(salvaged.Key, TaskStates.HumanReview);
        Assert.Empty(store.ListPending());
    }

    private static string SalvageMessage(string key, string title)
        => $"{key}: {title}\n\nRunner committed changes the agent left uncommitted (outcome Done).\n\n"
            + $"Agent-Studio-Task: {key}\nAgent-Studio-Run: run_fixture\n"
            + "Agent-Studio-Salvage: teardown\nAgent-Studio-Outcome: Done";

    private void ClearCommits(WebApplicationFactory<Program> factory, string key)
    {
        var path = Path.Combine(_watchPath, TaskStates.AutoReview, key, "task.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["commits"] = new JsonArray();
        File.WriteAllText(path, json.ToJsonString());
        factory.Services.GetRequiredService<TaskScannerService>().InvalidateCache();
    }
}
