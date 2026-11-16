using System.Text.Json.Nodes;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3717: a published batch member whose rebased commits cannot be mapped
/// onto its attributed chain, and the repair of the runner salvage subjects
/// that left such chains empty.
/// </summary>
public sealed class BatchGatePilotReleaseAttributionTests : BatchGatePilotServiceTests
{
    [Fact]
    public async Task Merge_only_recovery_is_reattributed_to_its_run_and_enters_the_batch()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var baseSha = RemoteTip();
        var member = SeedMember(factory, "DOC-3787", "docs/original.md",
            settleReview: false, initialState: TaskStates.Escalated);

        Git(_repo, "checkout", "-q", "develop");
        File.WriteAllText(Path.Combine(_repo, "integrated-one.txt"), "one");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-qm", "fix(DOC-OTHER-1): integrated one");
        Git(_repo, "push", "-q", "origin", "develop");
        Git(_repo, "checkout", "-q", "--detach", member.ResultSha);
        Git(_repo, "merge", "-q", "--no-ff", "--no-edit", "origin/develop");
        var firstMerge = Git(_repo, "rev-parse", "HEAD");

        Git(_repo, "checkout", "-q", "develop");
        File.WriteAllText(Path.Combine(_repo, "integrated-two.txt"), "two");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-qm", "fix(DOC-OTHER-2): integrated two");
        Git(_repo, "push", "-q", "origin", "develop");
        Git(_repo, "checkout", "-q", "--detach", firstMerge);
        Git(_repo, "merge", "-q", "--no-ff", "--no-edit", "origin/develop");
        var resultSha = Git(_repo, "rev-parse", "HEAD");
        PublishResult(member.Key, resultSha);

        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var run = authority.AcquireRun(member.Key, RepositoryId, null, "coding-runner",
            "coding-host", 600, "merge-only-run").RunAttempt!;
        var resultRef = "refs/heads/agent-studio/results/" + member.Key;
        var settled = authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch,
                "merge-only-settle"),
            Outcome = "done",
            ResultSha = resultSha,
            ResultEnvelope = new Contract.ImmutableResultEnvelope(RepositoryId, run.AttemptId,
                baseSha, resultSha, resultRef, null, new string('b', 64)),
        });
        Assert.True(settled.Accepted, settled.Message);
        var priorPlan = authority.GetReview(member.ReviewAttemptId)!.Subject.Plan;
        var created = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            member.Key, RepositoryId, resultSha, run.AttemptId, "requirements", "policy", [],
            "merge-only-review", ResultRef: resultRef, Plan: priorPlan));
        Assert.True(created.Accepted, created.Message);
        var claimed = authority.ClaimReview(created.ReviewAttempt!.AttemptId, "review-executor",
            "review-host", 600, "merge-only-review-claim");
        Assert.True(claimed.Accepted, claimed.Message);
        var review = authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(claimed.ReviewAttempt!.AttemptId,
                claimed.ReviewAttempt.LastFence, claimed.ReviewAttempt.AuthorityEpoch,
                "merge-only-review-pass"), resultSha, ReviewTerminalOutcome.Pass));
        Assert.True(review.Accepted, review.Message);

        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var folder = scanner.FindJob(member.Key, _watchPath)!.FolderPath;
        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = member.Key,
            RunAttemptId = run.AttemptId,
            AttemptChainId = "merge-only-chain",
            Project = ProjectName,
            Repository = RepositoryId,
            ResultSha = resultSha,
            BaseSha = baseSha,
            ImmutableResultRef = resultRef,
            IntegrationBranch = "refs/heads/develop",
            CompletedAtUtc = _clock,
        });
        scanner.InvalidateCache();
        var timeline = factory.Services.GetRequiredService<TimelineLog>();
        var sweep = new RemoteCompletionAttributionSweep(scanner,
            factory.Services.GetRequiredService<AgentStudio.Git.GitService>(),
            factory.Services.GetRequiredService<TaskMutationService>(),
            factory.Services.GetRequiredService<RemoteTokenReceiptService>(),
            Path.Combine(_workspace, "merge-only-migrations", RemoteCompletionAttributionSweep.ReportFileName),
            _clock, NullLogger<RemoteCompletionAttributionSweep>.Instance,
            factory.Services.GetRequiredService<TaskStateMachine>(), timeline, authority);

        var report = sweep.RunIntegrationHistoryRepairOnce();

        Assert.Equal(2, Assert.Single(report.Tasks).Commits);
        var repaired = scanner.FindJob(member.Key, _watchPath)!;
        Assert.Equal(TaskStates.AutoReview, repaired.State);
        Assert.Contains(repaired.Commits, commit => commit.Sha == member.ResultSha
            && commit.RunAttemptId == member.RunAttemptId);
        Assert.Contains(repaired.Commits, commit => commit.Sha == firstMerge
            && commit.RunAttemptId == run.AttemptId && commit.ResultSha == resultSha);
        Assert.Contains(repaired.Commits, commit => commit.Sha == resultSha
            && commit.RunAttemptId == run.AttemptId && commit.ResultSha == resultSha);
        Assert.Contains(timeline.ReadAll(repaired.FolderPath), evt =>
            evt.Kind == TimelineEventKinds.IntegrationHistoryAttributionRepaired
            && evt.Details?.GetValueOrDefault("reason") == "merge-only-recovery-delivery");

        await factory.Services.GetRequiredService<BatchGatePilotService>().EnqueueAsync(
            repaired, review.ReviewAttempt!, authority.GetRun(run.AttemptId)!, _clock,
            CancellationToken.None);
        Assert.Contains(factory.Services.GetRequiredService<BatchGateStore>().ListPending(), pending =>
            pending.Subject.TaskKey == member.Key);
    }

    [Fact]
    public void Escalated_passing_review_recovers_imported_integration_history_without_agent_round()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var baseSha = RemoteTip();
        Git(_repo, "checkout", "-q", "develop");
        File.WriteAllText(Path.Combine(_repo, "foreign-history.txt"), "integrated");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-qm", "fix(DOC-9999): integrated change");
        var foreignSha = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "push", "-q", "origin", "develop");
        var member = SeedMember(factory, "DOC-3789", "docs/history.md",
            parentSha: baseSha, mergeFrom: "origin/develop");
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, member.Key);
        ReviewSubjectStore.Write(folder, ReviewSubjectStore.Read(folder)! with
        {
            BaseSha = baseSha,
            IntegrationBranch = "refs/heads/develop",
        });
        ClearCommits(factory, member.Key);
        var timeline = factory.Services.GetRequiredService<TimelineLog>();
        timeline.Append(folder, TimelineEventKinds.AgentRunFinished, TimelineActors.Agent,
            "remote run done", member.RunAttemptId, details: new Dictionary<string, string>
            {
                ["commitAttributionWarning"] = $"Remote commit attribution rejected for '{member.Key}': commit {foreignSha[..10]} names foreign task 'DOC-9999'.",
            });
        var moved = factory.Services.GetRequiredService<TaskStateMachine>().MoveJob(
            member.Key, TaskStates.Escalated, _watchPath,
            expectedSourceState: TaskStates.AutoReview);
        Assert.Equal(MoveJobStatus.Success, moved.Status);
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        scanner.InvalidateCache();
        var sweep = new RemoteCompletionAttributionSweep(scanner,
            factory.Services.GetRequiredService<AgentStudio.Git.GitService>(),
            factory.Services.GetRequiredService<TaskMutationService>(),
            factory.Services.GetRequiredService<RemoteTokenReceiptService>(),
            Path.Combine(_workspace, "migrations", RemoteCompletionAttributionSweep.ReportFileName),
            _clock, NullLogger<RemoteCompletionAttributionSweep>.Instance,
            factory.Services.GetRequiredService<TaskStateMachine>(), timeline,
            factory.Services.GetRequiredService<AttemptAuthorityService>());

        var report = sweep.RunIntegrationHistoryRepairOnce();

        Assert.Equal(member.Key, Assert.Single(report.Tasks, row => row.Commits > 0).TaskKey);
        Assert.Equal(TaskStates.AutoReview, scanner.FindJob(member.Key, _watchPath)!.State);
        Assert.Contains(scanner.FindJob(member.Key, _watchPath)!.Commits,
            commit => commit.ResultSha == member.ResultSha);
        Assert.DoesNotContain(scanner.FindJob(member.Key, _watchPath)!.Commits,
            commit => commit.Sha == foreignSha);
        Assert.Contains(timeline.ReadAll(scanner.FindJob(member.Key, _watchPath)!.FolderPath),
            evt => evt.Kind == TimelineEventKinds.IntegrationHistoryAttributionRepaired);
        Assert.True(sweep.RunIntegrationHistoryRepairOnce().AlreadyCompleted);
    }

    /// <summary>
    /// AGT-3854: AGT-3808 escalated because its finding-id subjects were read
    /// as foreign work. The repair restores it; a subject naming only an
    /// unreferenced foreign key keeps its escalation.
    /// </summary>
    [Fact]
    public void Escalated_delivery_rejected_for_a_finding_id_subject_is_repaired_once()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 3);
        var baseSha = RemoteTip();
        var finding = SeedMember(factory, "DOC-3808", "docs/finding.md", parentSha: baseSha,
            commitMessage: "DOC-3794-F13: keep per-run tokens and cost in the narrow Overview pane");
        var foreign = SeedMember(factory, "DOC-3809", "docs/foreign.md", parentSha: baseSha,
            commitMessage: "fix(DOC-9998): unrelated work");
        var timeline = factory.Services.GetRequiredService<TimelineLog>();
        foreach (var (member, named) in new[] { (finding, "DOC-3794"), (foreign, "DOC-9998") })
        {
            var folder = Path.Combine(_watchPath, TaskStates.AutoReview, member.Key);
            ReviewSubjectStore.Write(folder, ReviewSubjectStore.Read(folder)! with
            {
                BaseSha = baseSha,
                IntegrationBranch = "refs/heads/develop",
            });
            ClearCommits(factory, member.Key);
            timeline.Append(folder, TimelineEventKinds.AgentRunFinished, TimelineActors.Agent,
                "remote run done", member.RunAttemptId, details: new Dictionary<string, string>
                {
                    ["commitAttributionWarning"] = $"Remote commit attribution rejected for '{member.Key}': commit {member.ResultSha[..10]} names foreign task '{named}'.",
                });
            Assert.Equal(MoveJobStatus.Success, factory.Services.GetRequiredService<TaskStateMachine>()
                .MoveJob(member.Key, TaskStates.Escalated, _watchPath,
                    expectedSourceState: TaskStates.AutoReview).Status);
        }
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        scanner.InvalidateCache();
        var sweep = new RemoteCompletionAttributionSweep(scanner,
            factory.Services.GetRequiredService<AgentStudio.Git.GitService>(),
            factory.Services.GetRequiredService<TaskMutationService>(),
            factory.Services.GetRequiredService<RemoteTokenReceiptService>(),
            Path.Combine(_workspace, "migrations", RemoteCompletionAttributionSweep.ReportFileName),
            _clock, NullLogger<RemoteCompletionAttributionSweep>.Instance,
            factory.Services.GetRequiredService<TaskStateMachine>(), timeline,
            factory.Services.GetRequiredService<AttemptAuthorityService>());

        var report = sweep.RunCommitSubjectKeyRepairOnce();

        var row = Assert.Single(report.Tasks);
        Assert.Equal(finding.Key, row.TaskKey);
        Assert.Equal(1, row.Commits);
        Assert.Equal(0, report.UnresolvedTasks);
        var repaired = scanner.FindJob(finding.Key, _watchPath)!;
        Assert.Equal(TaskStates.AutoReview, repaired.State);
        Assert.Contains(repaired.Commits, commit => commit.Sha == finding.ResultSha
            && commit.RunAttemptId == finding.RunAttemptId);
        Assert.Contains(timeline.ReadAll(repaired.FolderPath), evt =>
            evt.Kind == TimelineEventKinds.CommitSubjectKeyAttributionRepaired
            && evt.Details?.GetValueOrDefault("reason") == "commit-subject-key-allowed"
            && evt.Details?.GetValueOrDefault("namedKey") == "DOC-3794");
        var stillForeign = scanner.FindJob(foreign.Key, _watchPath)!;
        Assert.Equal(TaskStates.Escalated, stillForeign.State);
        Assert.Empty(stillForeign.Commits);
        Assert.True(sweep.RunCommitSubjectKeyRepairOnce().AlreadyCompleted);
    }

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

    [Fact]
    public async Task Published_unattributed_member_is_repaired_from_its_batch_receipt()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        SeedMember(factory, "DOC-REPAIR-1", "docs/repair-1.md");
        var repaired = SeedMember(factory, "DOC-REPAIR-2", "docs/repair-2.md",
            commitMessage: "docs: repair second card\n\nAgent-Studio-Task: DOC-REPAIR-2");
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, repaired.Key);
        ReviewSubjectStore.Write(folder, ReviewSubjectStore.Read(folder)! with
        {
            BaseSha = new string('f', 40),
            IntegrationBranch = "refs/heads/develop",
        });
        var manifest = ClosePendingBatch(factory);
        ClearCommits(factory, repaired.Key);
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);
        AssertLane(repaired.Key, TaskStates.Escalated);
        Assert.Equal("release-unattributed", factory.Services.GetRequiredService<BatchGateStore>()
            .ReadPendingOutcome(repaired.ReviewAttemptId));

        await pilot.TickAsync(CancellationToken.None);

        AssertLane(repaired.Key, TaskStates.HumanReview);
        var task = factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(repaired.Key, _watchPath)!;
        Assert.Contains(task.Commits, commit => commit.Sha == repaired.ResultSha
            && !string.IsNullOrWhiteSpace(commit.SupersededBySha));
        Assert.Contains(task.IntegrationRecords, record => record.Id.StartsWith(
            $"batch-map:{manifest.BatchId}:", StringComparison.Ordinal));
        Assert.Contains(task.IntegrationRecords, record => record.Id.StartsWith(
            $"batch-gate:{manifest.BatchId}:", StringComparison.Ordinal));
        Assert.Equal(manifest.BatchId, Ownership(repaired.Key)?.BatchId);
        Assert.Equal(1, _gate.BatchRuns);
    }

    [Fact]
    public async Task Rebased_delivery_without_fenced_base_is_attributed_before_batch_release()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var first = SeedMember(factory, "DOC-BASE-1", "docs/base-1.md");
        var rebased = SeedMember(factory, "DOC-BASE-2", "docs/base-2.md",
            commitMessage: "docs: rebased delivery\n\nAgent-Studio-Task: DOC-BASE-2");
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, rebased.Key);
        ReviewSubjectStore.Write(folder, ReviewSubjectStore.Read(folder)! with
        {
            BaseSha = new string('f', 40),
            IntegrationBranch = "refs/heads/develop",
        });
        ClearCommits(factory, rebased.Key);
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var sweep = new RemoteCompletionAttributionSweep(scanner,
            factory.Services.GetRequiredService<AgentStudio.Git.GitService>(),
            factory.Services.GetRequiredService<TaskMutationService>(),
            factory.Services.GetRequiredService<RemoteTokenReceiptService>(),
            Path.Combine(_workspace, "migrations", RemoteCompletionAttributionSweep.ReportFileName),
            _clock, NullLogger<RemoteCompletionAttributionSweep>.Instance);

        var report = sweep.RunOnce();
        Assert.Equal(rebased.Key, Assert.Single(report.Tasks, row => row.Commits > 0).TaskKey);
        Assert.Equal(rebased.ResultSha,
            Assert.Single(scanner.FindJob(rebased.Key, _watchPath)!.Commits).Sha);
        _gate.Batch = request => Green(request.ExpectedSha);
        await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);

        AssertLane(first.Key, TaskStates.HumanReview);
        AssertLane(rebased.Key, TaskStates.HumanReview);
        Assert.Equal(1, _gate.BatchRuns);
    }

    [Fact]
    public async Task Missing_current_attribution_is_shown_and_refused_before_a_batch_gate()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-NO-ATTRIBUTION", "docs/no-attribution.md",
            settleReview: false);
        ClearCommits(factory, member.Key);
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var claim = authority.ClaimReview(member.ReviewAttemptId, "review-executor",
            "review-host", 600, "claim-no-attribution");
        Assert.True(claim.Accepted, claim.Message);
        var leased = claim.ReviewAttempt!;
        var passed = authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(member.ReviewAttemptId, leased.LastFence,
                leased.AuthorityEpoch, "pass-no-attribution"),
            member.ResultSha, ReviewTerminalOutcome.Pass));
        Assert.True(passed.Accepted, passed.Message);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var task = factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(member.Key, _watchPath)!;
        var source = authority.GetRun(member.RunAttemptId)!;

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pilot.EnqueueAsync(task, passed.ReviewAttempt!, source, _clock, CancellationToken.None));
        Assert.Contains("Commit attribution is missing", refusal.Message);
        await pilot.RunEmergencyFallbackAsync(task, passed.ReviewAttempt!, source,
            _clock, CancellationToken.None);

        AssertLane(member.Key, TaskStates.Escalated);
        var status = File.ReadAllText(Path.Combine(_watchPath, TaskStates.Escalated,
            member.Key, "status.md"));
        Assert.Contains("Commit attribution is missing", status);
        Assert.Equal(0, _gate.BatchRuns);
        Assert.Equal(0, _gate.FallbackRuns);
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
