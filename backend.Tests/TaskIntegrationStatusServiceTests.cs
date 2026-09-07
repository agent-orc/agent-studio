using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2202: the honest, git-derived integration verdict for an accepted card must
/// resolve the "Accept != Merge" blind spot from target-branch commit membership,
/// not remembered merge attempts. The current immutable review result selects
/// the authoritative delivery generation; without one, the verdict's anchor is
/// the attributed <c>commits[]</c> the card widget shows. Every test drives real
/// git against a throwaway repo so
/// every <see cref="IntegrationStatuses"/> class is exercised end to end:
///   - remembered curated/provenance attempts cannot override missing commits,
///   - integrated via attributed-commit ancestry after an out-of-band merge,
///   - integrated when all attributed commits are in develop even though the branch
///     tip carries further un-integrated WIP commits,
///   - partial (some attributed commits in develop, some not) with the missing SHAs,
///   - pending (accepted work still only on the task branch),
///   - conflict-skipped (a recorded merge-into-develop conflict),
///   - no-branch (nothing to integrate).
/// </summary>
public sealed class TaskIntegrationStatusServiceTests : IDisposable
{
    private readonly string _tempDir;

    public TaskIntegrationStatusServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "task-integration-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public void ParseIntegrationMergeKey_MatchesCuratedMergeSubjects()
    {
        Assert.Equal("AGT-2202", GitService.ParseIntegrationMergeKey("merge(AGT-2202): integrations-sicht"));
        Assert.Equal("AGT-2202", GitService.ParseIntegrationMergeKey("merge-recut(AGT-2202): re-cut after conflict"));
        Assert.Equal("RB-42", GitService.ParseIntegrationMergeKey("merge(rb-42): lower-case key upper-cased"));
        Assert.Null(GitService.ParseIntegrationMergeKey("Merge branch 'task/foo' into develop"));
        Assert.Null(GitService.ParseIntegrationMergeKey("feat: not a merge"));
    }

    [Fact]
    public void BuildLookup_AttemptArtifactsDoNotOverrideMissingCommitPresence()
    {
        // The curated integrator lands the work under a merge(KEY) commit on
        // develop WITHOUT the task's own commit being an ancestor (it rewrites).
        // Simulate: the task commit lives only on the task branch; develop gets a
        // separate commit whose SUBJECT is the curated merge marker.
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/curated");
        File.WriteAllText(Path.Combine(repo, "curated.txt"), "task work");
        Commit(repo, "feat: curated work");
        var anchor = RunGit(repo, "rev-parse task/curated").Out.Trim();
        // Develop advances with a curated marker commit that does NOT contain the
        // task branch (empty-ish commit via --allow-empty, different content).
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "commit -q --allow-empty -m \"merge(AGT-2202): curated integration of curated work\"");
        var curatedSha = RunGit(repo, "rev-parse develop").Out.Trim();

        var svc = BuildService(repo, out var project, out var log);
        var job = Job("curated", "AGT-2202", project, repo, log, commits: new[] { Commit(anchor) },
            prov: Prov(branch: "task/curated", merge: curatedSha));

        var status = svc.BuildLookup(new[] { job })[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Pending, status.Status);
        Assert.Null(status.Sha);
        // Ground-truth cross-check: neither the curated subject nor the recorded
        // merge attempt can replace the missing attributed commit.
        var svcGit = new GitService(NullLogger<GitService>.Instance,
            new TaskScannerService(EmptyConfig(), NullLogger<TaskScannerService>.Instance,
                new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, EmptyConfig())), EmptyConfig());
        Assert.False(svcGit.IsAncestor(repo, anchor, "develop"));
    }

    [Fact]
    public void BuildLookup_OutOfBandMergeWithoutOwnAttempt_IsIntegrated()
    {
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/merged");
        File.WriteAllText(Path.Combine(repo, "merged.txt"), "dev work");
        Commit(repo, "feat: dev work");
        var anchor = RunGit(repo, "rev-parse task/merged").Out.Trim();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge --no-ff --no-edit task/merged");

        var svc = BuildService(repo, out var project, out var log);
        var job = Job("merged", "AGT-3000", project, repo, log, commits: new[] { Commit(anchor) },
            prov: Prov(branch: "task/merged"));
        Assert.Null(log.Read(job.FolderPath));
        Assert.Null(job.Provenance?.Merge);

        var status = svc.BuildLookup(new[] { job })[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Integrated, status.Status);
        Assert.Equal(anchor[..7], status.Sha);
        Assert.Equal("anchor-ancestor", status.Detail);
    }

    [Fact]
    public void BuildLookup_RebasedReviewedResultWithMarkedHistoricalAttempt_IsIntegrated()
    {
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/original-delivery");
        File.WriteAllText(Path.Combine(repo, "rebased.txt"), "reviewed work");
        Commit(repo, "feat: reviewed work");
        var historicalSha = RunGit(repo, "rev-parse task/original-delivery").Out.Trim();

        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "main-advance.txt"), "main advanced");
        Commit(repo, "chore: advance integration branch");
        RunGit(repo, $"cherry-pick {historicalSha}");
        var rebasedSha = RunGit(repo, "rev-parse develop").Out.Trim();
        Assert.NotEqual(historicalSha, rebasedSha);

        var svc = BuildService(repo, out var project, out var log);
        var job = Job(
            "rebased-delivery",
            "TE-38",
            project,
            repo,
            log,
            commits:
            [
                Commit(historicalSha) with
                {
                    RunAttemptId = "run-original",
                    SupersededByAttempt = "run-recovery",
                },
                Commit(rebasedSha) with { RunAttemptId = "run-recovery" },
            ]);
        ReviewSubjectStore.Write(job.FolderPath, new ReviewSubjectRecord
        {
            TaskKey = "TE-38",
            RunAttemptId = "run-recovery",
            Project = project,
            Repository = repo,
            ResultSha = rebasedSha,
            AttemptChainId = "chain-recovery",
            ResultRef = "refs/heads/agent-studio/results/recovery",
        });

        var status = svc.BuildLookup([job])[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Integrated, status.Status);
        Assert.Equal(rebasedSha[..7], status.Sha);
        Assert.Equal("anchor-ancestor", status.Detail);
    }

    [Fact]
    public void BuildLookup_AbbreviatedAttributedShaOnDevelop_IsIntegrated()
    {
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "short-sha.txt"), "delivered");
        Commit(repo, "feat: delivered under a full git object id");
        var fullSha = RunGit(repo, "rev-parse develop").Out.Trim();

        var svc = BuildService(repo, out var project, out var log);
        var job = Job("short-sha", "TE-1", project, repo, log,
            commits: [Commit(fullSha[..7])]);

        var status = svc.BuildLookup([job])[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Integrated, status.Status);
        Assert.Equal(fullSha[..7], status.Sha);
        Assert.Equal("anchor-ancestor", status.Detail);
    }

    [Fact]
    public void BuildLookup_ConfiguredProjectBranchWinsOverRecordedRunSnapshot()
    {
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q main");
        File.WriteAllText(Path.Combine(repo, "main-only.txt"), "main work");
        Commit(repo, "feat: main-line work");
        var anchor = RunGit(repo, "rev-parse main").Out.Trim();

        var svc = BuildService(repo, out var project, out var log);
        var job = Job("main-line", "AGT-2400", project, repo, log, commits: new[] { Commit(anchor) })
            with { IntegrationBranch = "refs/heads/main" };

        var status = svc.BuildLookup([job])[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Pending, status.Status);
        Assert.Equal("develop", status.IntegrationBranch);
    }

    [Fact]
    public void BuildLookup_AllAttributedCommitsInDevelop_ButBranchTipHasWip_IsIntegrated()
    {
        // The attributed commits[] the card widget shows are all folded into
        // develop, but the task branch tip carries further un-integrated WIP
        // commits. Only the attributed set participates in the verdict.
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/wiptip");
        File.WriteAllText(Path.Combine(repo, "attributed.txt"), "attributed work");
        Commit(repo, "feat: attributed work");
        var attributed = RunGit(repo, "rev-parse task/wiptip").Out.Trim();
        // The attributed commit lands in develop via a plain merge.
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge --no-ff --no-edit task/wiptip");
        // The branch then accrues two WIP commits that are NOT in develop.
        RunGit(repo, "checkout -q task/wiptip");
        File.WriteAllText(Path.Combine(repo, "wip1.txt"), "wip one");
        Commit(repo, "wip: snapshot one");
        File.WriteAllText(Path.Combine(repo, "wip2.txt"), "wip two");
        Commit(repo, "wip: snapshot two");
        var tip = RunGit(repo, "rev-parse task/wiptip").Out.Trim();

        var svc = BuildService(repo, out var project, out var log);
        var job = Job("wiptip", "AGT-2171", project, repo, log, commits: new[] { Commit(attributed) },
            prov: Prov(branch: "task/wiptip", tip: tip));

        var status = svc.BuildLookup(new[] { job })[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Integrated, status.Status);
        Assert.Equal(attributed[..7], status.Sha);
        Assert.Equal("anchor-ancestor", status.Detail);
        // Ground-truth cross-check: the branch tip really is NOT an ancestor of develop.
        var svcGit = new GitService(NullLogger<GitService>.Instance,
            new TaskScannerService(EmptyConfig(), NullLogger<TaskScannerService>.Instance,
                new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, EmptyConfig())), EmptyConfig());
        Assert.False(svcGit.IsAncestor(repo, tip, "develop"));
    }

    [Fact]
    public void BuildLookup_SomeAttributedCommitsInDevelop_IsPartialWithMissingShas()
    {
        // Mixed case: one attributed commit is folded into develop, another is not.
        // The verdict is partial and the detail names the missing short-SHA.
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/partial");
        File.WriteAllText(Path.Combine(repo, "landed.txt"), "landed work");
        Commit(repo, "feat: landed work");
        var landed = RunGit(repo, "rev-parse task/partial").Out.Trim();
        // Land only the first commit into develop.
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge --no-ff --no-edit task/partial");
        // A second attributed commit stays on the branch, never merged.
        RunGit(repo, "checkout -q task/partial");
        File.WriteAllText(Path.Combine(repo, "not-landed.txt"), "not landed");
        Commit(repo, "feat: not landed work");
        var notLanded = RunGit(repo, "rev-parse task/partial").Out.Trim();

        var svc = BuildService(repo, out var project, out var log);
        var job = Job("partial", "AGT-3006", project, repo, log,
            commits: new[] { Commit(landed), Commit(notLanded) },
            prov: Prov(branch: "task/partial"));
        ReviewSubjectStore.Write(job.FolderPath, new ReviewSubjectRecord
        {
            TaskKey = "AGT-3006",
            RunAttemptId = "run-current",
            Project = project,
            Repository = repo,
            ResultSha = landed,
            AttemptChainId = "chain-current",
            ResultRef = "refs/heads/agent-studio/results/current",
        });

        var status = svc.BuildLookup(new[] { job })[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Partial, status.Status);
        Assert.Contains(notLanded[..7], status.Detail);
        Assert.Contains("1/2", status.Detail!);
        Assert.DoesNotContain(landed[..7], status.Detail!);
    }

    [Fact]
    public void BuildLookup_Agt2307StyleRepositories_AreEvaluatedInTheirOwnGraphs()
    {
        var studio = SeedDevelopMainRepo();
        RunGit(studio, "checkout -q develop");
        File.WriteAllText(Path.Combine(studio, "studio.txt"), "studio");
        Commit(studio, "feat: studio delivery");
        var studioSha = RunGit(studio, "rev-parse develop").Out.Trim();
        File.WriteAllText(Path.Combine(studio, "studio-follow-up.txt"), "studio follow-up");
        Commit(studio, "feat: studio follow-up");
        var studioFollowUpSha = RunGit(studio, "rev-parse develop").Out.Trim();

        var runner = SeedDevelopMainRepo();
        RunGit(runner, "checkout -q main");
        File.WriteAllText(Path.Combine(runner, "runner.txt"), "runner");
        Commit(runner, "feat: runner delivery");
        var runnerSha = RunGit(runner, "rev-parse main").Out.Trim();

        var (service, project, log) = BuildMultiRepositoryService(studio, runner);
        var job = Job("externalization", "AGT-2307", project, studio, log, commits:
        [
            Commit(studioSha) with { Repository = "agent-studio", Branch = "develop", FilesChanged = 1 },
            Commit(studioFollowUpSha) with
            {
                Repository = "https://github.com/example/agent-studio.git",
                Branch = "develop",
                FilesChanged = 1,
            },
            Commit(runnerSha) with { Repository = "runner", Branch = "main", FilesChanged = 1 },
        ]);

        var integrated = service.BuildLookup([job])[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Integrated, integrated.Status);
        Assert.Equal(2, integrated.Repositories.Count);
        Assert.Equal(
            ["agent-studio", "runner"],
            integrated.Repositories.Select(repository => repository.Repository));
        Assert.Equal(2, integrated.Repositories[0].Commits.Count);
        Assert.All(integrated.Repositories, repository => Assert.True(repository.OnIntegrationBranch));

        RunGit(runner, "checkout -q -b direct/pending");
        File.WriteAllText(Path.Combine(runner, "pending.txt"), "pending");
        Commit(runner, "feat: runner follow-up");
        var pendingSha = RunGit(runner, "rev-parse HEAD").Out.Trim();
        var partialJob = job with
        {
            Commits = [.. job.Commits, Commit(pendingSha) with
            {
                Repository = "runner",
                Branch = "main",
                FilesChanged = 1,
            }],
        };

        var partial = service.BuildLookup([partialJob])[partialJob.TaskKey];

        Assert.Equal(IntegrationStatuses.Partial, partial.Status);
        Assert.Contains("runner:", partial.Detail);
        Assert.Contains(pendingSha[..7], partial.Detail);
        Assert.DoesNotContain(studioSha[..7], partial.Detail);
    }

    [Fact]
    public void BuildLookup_SupersededConflictRoundMissingButReplacementLanded_IsIntegrated()
    {
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/conflicted-round");
        File.WriteAllText(Path.Combine(repo, "replacement.txt"), "complete delivery");
        Commit(repo, "wip(runner): salvage before teardown - outcome Done");
        var superseded = RunGit(repo, "rev-parse task/conflicted-round").Out.Trim();

        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "integration-advance.txt"), "advance");
        Commit(repo, "chore: advance integration branch");
        RunGit(repo, $"cherry-pick {superseded}");
        var replacement = RunGit(repo, "rev-parse develop").Out.Trim();
        Assert.NotEqual(superseded, replacement);

        var svc = BuildService(repo, out var project, out var log);
        var job = Job(
            "replacement-round",
            "AGT-2533",
            project,
            repo,
            log,
            commits:
            [
                Commit(superseded) with
                {
                    RunAttemptId = "round-1",
                    SupersededByAttempt = "round-2",
                },
                Commit(replacement) with { RunAttemptId = "round-2" },
            ]);

        var status = svc.BuildLookup([job])[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Integrated, status.Status);
        Assert.Equal(replacement[..7], status.Sha);
        Assert.Equal("anchor-ancestor", status.Detail);
    }

    [Fact]
    public void AttributedCommits_SupersededSha_IsReplacedWithoutPartialNoise()
    {
        const string original = "4444444444444444444444444444444444444444";
        const string replacement = "5555555555555555555555555555555555555555";
        var job = new TaskInfo
        {
            Commits =
            [
                Commit(original) with { SupersededBySha = replacement },
                Commit(replacement),
            ],
        };

        Assert.Equal([replacement], TaskIntegrationStatusService.AttributedCommits(job));
    }

    [Fact]
    public void BuildLookup_MissingZeroFileLifecycleMarkers_DoNotMakeDeliveredWorkPartial()
    {
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "delivered.txt"), "delivered");
        Commit(repo, "feat: real deliverable");
        var delivered = RunGit(repo, "rev-parse develop").Out.Trim();

        var svc = BuildService(repo, out var project, out var log);
        var job = Job("marker-noise", "AGT-2302", project, repo, log,
            commits:
            [
                Commit(delivered),
                Commit("1111111111111111111111111111111111111111") with
                {
                    Message = "wip(runner): salvage before teardown - outcome Unknown",
                    FilesChanged = 0,
                    Files = [],
                },
                Commit("2222222222222222222222222222222222222222") with
                {
                    Message = "chore: snapshot for review",
                    FilesChanged = 0,
                    Files = [],
                },
            ]);

        var status = svc.BuildLookup([job])[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Integrated, status.Status);
        Assert.Equal(delivered[..7], status.Sha);
        Assert.Equal("anchor-ancestor", status.Detail);
    }

    [Fact]
    public void AttributedCommits_SalvageMarkerWithChangedFiles_RemainsWork()
    {
        const string sha = "3333333333333333333333333333333333333333";
        var job = new TaskInfo
        {
            Commits =
            [
                Commit(sha) with
                {
                    Message = "wip(runner): salvage before teardown - outcome Done",
                    FilesChanged = 2,
                    Files = ["backend/a.cs", "backend/b.cs"],
                },
            ],
        };

        Assert.Equal([sha], TaskIntegrationStatusService.AttributedCommits(job));
    }

    [Fact]
    public void BuildLookup_AncestorProofDominatesStaleZeroFileMarkerMetadata()
    {
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "salvaged.txt"), "delivered");
        Commit(repo, "wip(runner): salvage before teardown - outcome Done");
        var salvage = RunGit(repo, "rev-parse develop").Out.Trim();

        var svc = BuildService(repo, out var project, out var log);
        var job = Job("salvage-ancestor", "AGT-2489", project, repo, log,
            commits:
            [
                Commit(salvage) with
                {
                    Message = "wip(runner): salvage before teardown - outcome Done",
                    FilesChanged = 0,
                    Files = [],
                },
            ]);

        var status = svc.BuildLookup([job])[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Integrated, status.Status);
        Assert.Equal(salvage[..7], status.Sha);
        Assert.Equal("anchor-ancestor", status.Detail);
    }

    [Fact]
    public void BuildLookup_MissingSnapshotCommitWithChangedFiles_RemainsPartial()
    {
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "delivered.txt"), "delivered");
        Commit(repo, "feat: real deliverable");
        var delivered = RunGit(repo, "rev-parse develop").Out.Trim();
        const string missing = "3333333333333333333333333333333333333333";

        var svc = BuildService(repo, out var project, out var log);
        var job = Job("real-snapshot", "AGT-2303", project, repo, log,
            commits:
            [
                Commit(delivered),
                Commit(missing) with
                {
                    Message = "chore: snapshot for review (1 file changed)",
                    FilesChanged = 1,
                    Files = ["backend/real-deliverable.cs"],
                },
            ]);

        var status = svc.BuildLookup([job])[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Partial, status.Status);
        Assert.Contains("1/2", status.Detail);
        Assert.Contains(missing[..7], status.Detail);
    }

    [Fact]
    public void BuildLookup_AcceptedWorkOnlyOnTaskBranch_IsPending()
    {
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/pending");
        File.WriteAllText(Path.Combine(repo, "pending.txt"), "wip");
        Commit(repo, "feat: pending wip");
        var anchor = RunGit(repo, "rev-parse task/pending").Out.Trim();
        // Never merged into develop.

        var svc = BuildService(repo, out var project, out var log);
        var job = Job("pending", "AGT-3001", project, repo, log, commits: new[] { Commit(anchor) },
            prov: Prov(branch: "task/pending"));

        var status = svc.BuildLookup(new[] { job })[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Pending, status.Status);
        Assert.Null(status.Sha);
    }

    [Fact]
    public void BuildLookup_RemoteDeliveryRefWithoutAttributedCommit_IsPendingAndProjectsRef()
    {
        var repo = SeedDevelopMainRepo();
        var svc = BuildService(repo, out var project, out var log);
        var job = Job("remote-delivery", "AGT-2220", project, repo, log);
        ReviewSubjectStore.Write(job.FolderPath, new ReviewSubjectRecord
        {
            TaskKey = job.Key!,
            RunAttemptId = "run-agt-2220",
            Project = project,
            Repository = repo,
            ResultSha = new string('7', 40),
            AttemptChainId = "attempt-agt-2220",
            Executor = "agent-runner-01",
            LeaseId = "lease-agt-2220",
            FencingToken = 1,
            ImmutableResultRef = "origin/runner/agent-runner-01/AGT-2220",
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });

        var status = svc.BuildLookup([job])[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Pending, status.Status);
        Assert.Equal("runner/agent-runner-01/AGT-2220", status.DeliveryRef);
        Assert.Contains("runner/agent-runner-01/AGT-2220", status.Detail);
        Assert.Null(status.Sha);
    }

    [Fact]
    public void BuildLookup_EvidencedLocalTaskBranchWithoutAttributedCommit_ProjectsTaskRef()
    {
        var repo = SeedDevelopMainRepo();
        var svc = BuildService(repo, out var project, out var log);
        var job = Job(
            "local-delivery",
            "AGT-2434",
            project,
            repo,
            log,
            prov: Prov(branch: "task/local-delivery", tip: new string('8', 40)));

        var status = svc.BuildLookup([job])[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Pending, status.Status);
        Assert.Equal("task/local-delivery", status.DeliveryRef);
    }

    [Fact]
    public void BuildLookup_TargetHeadMoveInvalidatesCachedStatusImmediately()
    {
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/out-of-band");
        File.WriteAllText(Path.Combine(repo, "out-of-band.txt"), "work");
        Commit(repo, "feat: out-of-band work");
        var anchor = RunGit(repo, "rev-parse task/out-of-band").Out.Trim();

        var svc = BuildService(repo, out var project, out var log);
        var job = Job(
            "out-of-band",
            "AGT-2426",
            project,
            repo,
            log,
            commits: [Commit(anchor)]);

        Assert.Equal(IntegrationStatuses.Pending, svc.BuildLookup([job])[job.TaskKey].Status);
        Assert.Equal(1, svc.ComputationCount);

        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge --no-ff --no-edit task/out-of-band");

        Assert.Equal(IntegrationStatuses.Integrated, svc.BuildLookup([job])[job.TaskKey].Status);
        Assert.Equal(2, svc.ComputationCount);
    }

    [Fact]
    public void BuildLookup_RecordedMergeConflict_IsConflictSkipped()
    {
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/conflict");
        File.WriteAllText(Path.Combine(repo, "conflict.txt"), "wip");
        Commit(repo, "feat: conflict wip");
        var anchor = RunGit(repo, "rev-parse task/conflict").Out.Trim();

        var svc = BuildService(repo, out var project, out var log);
        var job = Job("conflict", "AGT-3002", project, repo, log, commits: new[] { Commit(anchor) },
            prov: Prov(branch: "task/conflict"));

        // Seed a recorded merge-into-develop conflict in the job's pipeline record.
        log.EnsureRun(job.FolderPath, PipelineCatalogue.Standard, project, job.Id);
        log.RecordStep(job.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            Verdict = "conflict",
            VerdictSummary = "Conflicted: conflict.txt",
            Reason = "Merge conflict in 1 file(s); merge aborted.",
        });

        var status = svc.BuildLookup(new[] { job })[job.TaskKey];

        Assert.Equal(IntegrationStatuses.ConflictSkipped, status.Status);
        Assert.Contains("conflict.txt", status.Detail);
        Assert.Equal(AcceptedIntegrationFailureCodes.MergeConflict, status.Failure?.Code);
        Assert.True(status.Failure?.RebaseRecoveryAvailable);
    }

    [Theory]
    [InlineData(
        "Release source 'origin/result' must be rebased onto 'main' before the full-suite gate.",
        AcceptedIntegrationFailureCodes.SourceNeedsRebase,
        "Rebase required",
        true)]
    [InlineData(
        "The accepted task has no stable key for review-subject validation.",
        AcceptedIntegrationFailureCodes.ReviewSubjectTaskKeyUnavailable,
        "Task key unavailable",
        false)]
    public void BuildLookup_RecordedIntegrationError_ProjectsTypedCardFailure(
        string pipelineReason,
        string expectedCode,
        string expectedLabel,
        bool recoveryAvailable)
    {
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/typed-failure");
        File.WriteAllText(Path.Combine(repo, "typed-failure.txt"), "wip");
        Commit(repo, "feat: typed integration failure");
        var anchor = RunGit(repo, "rev-parse task/typed-failure").Out.Trim();

        var svc = BuildService(repo, out var project, out var log);
        var job = Job(
            "typed-failure-" + expectedCode,
            "AGT-2532",
            project,
            repo,
            log,
            commits: [Commit(anchor)],
            prov: Prov(branch: "task/typed-failure"));
        log.EnsureRun(job.FolderPath, PipelineCatalogue.Standard, project, job.Id);
        log.RecordStep(job.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            Verdict = "error",
            Reason = pipelineReason,
        });

        var status = svc.BuildLookup([job])[job.TaskKey];

        Assert.Equal(IntegrationStatuses.ConflictSkipped, status.Status);
        Assert.Equal(expectedCode, status.Failure?.Code);
        Assert.Equal(expectedLabel, status.Failure?.Label);
        Assert.Equal(recoveryAvailable, status.Failure?.RebaseRecoveryAvailable);
        Assert.DoesNotContain("review-subject", status.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildLookup_MergePassedButPushBlocked_IsConflictSkippedNotPending()
    {
        // AGT-2688: the merge into develop succeeded (Passed), but the deferred
        // push recorded a lineage block. The attributed commit is therefore not
        // yet reachable from this reader's develop ancestry either. That must
        // surface as a distinct, typed integration-push-blocked failure - never
        // as plain "pending", which is what let the accepted-integration
        // backstop loop forever reading a merged-but-unpublished card as still
        // in flight.
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/push-blocked");
        File.WriteAllText(Path.Combine(repo, "push-blocked.txt"), "wip");
        Commit(repo, "feat: push blocked wip");
        var anchor = RunGit(repo, "rev-parse task/push-blocked").Out.Trim();
        RunGit(repo, "checkout -q develop");

        var svc = BuildService(repo, out var project, out var log);
        var job = Job("push-blocked", "AGT-3010", project, repo, log, commits: new[] { Commit(anchor) },
            prov: Prov(branch: "task/push-blocked"));

        log.EnsureRun(job.FolderPath, PipelineCatalogue.Standard, project, job.Id);
        log.RecordStep(job.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Passed,
            Verdict = "merged",
        });
        log.RecordStep(job.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopPushStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            Verdict = "lineage-blocked",
            Reason = "Integration push blocked: main is not an ancestor of develop yet.",
            FailureCode = AcceptedIntegrationFailureCodes.IntegrationPushBlocked,
        });

        var status = svc.BuildLookup(new[] { job })[job.TaskKey];

        Assert.Equal(IntegrationStatuses.ConflictSkipped, status.Status);
        Assert.Equal(AcceptedIntegrationFailureCodes.IntegrationPushBlocked, status.Failure?.Code);
        Assert.False(status.Failure?.RebaseRecoveryAvailable);

        // The accepted-integration backstop must not replay the merge for this
        // card: the merge already succeeded, only the push is blocked, and that
        // is the push backstop's job, not a full re-claim.
        var recovery = svc.ResolveAcceptedIntegrationRecovery(job, status);
        Assert.Equal(AcceptedIntegrationRecoveryAction.Ignore, recovery.Action);
    }

    [Fact]
    public void BuildLookup_NoCommitAndNoBranch_IsNoBranch()
    {
        var repo = SeedDevelopMainRepo();
        var svc = BuildService(repo, out var project, out var log);
        // A read-only / no-code accepted card: no anchor commit, no branch tip.
        var job = Job("nobranch", "AGT-3003", project, repo, log);

        var status = svc.BuildLookup(new[] { job })[job.TaskKey];

        Assert.Equal(IntegrationStatuses.NoBranch, status.Status);
        Assert.Null(status.DeliveryRef);
        Assert.Null(status.Sha);
    }

    [Fact]
    public void BuildLookup_OnlyDeliveredLanes_GetAVerdict()
    {
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "x.txt"), "x");
        Commit(repo, "feat: x");
        var sha = RunGit(repo, "rev-parse develop").Out.Trim();

        var svc = BuildService(repo, out var project, out var log);
        var inProgress = Job("wip", "AGT-3004", project, repo, log, commits: new[] { Commit(sha) }) with { State = TaskStates.Progress };
        var autoReview = Job("reviewing", "AGT-3006", project, repo, log, commits: new[] { Commit(sha) }) with { State = TaskStates.AutoReview };
        var completed = Job("done", "AGT-3005", project, repo, log, commits: new[] { Commit(sha) }) with { State = TaskStates.Completed };

        var lookup = svc.BuildLookup(new[] { inProgress, autoReview, completed });

        Assert.False(lookup.ContainsKey(inProgress.TaskKey));
        Assert.Equal(IntegrationStatuses.Integrated, lookup[autoReview.TaskKey].Status);
        Assert.True(lookup.ContainsKey(completed.TaskKey));
        Assert.Equal(IntegrationStatuses.Integrated, lookup[completed.TaskKey].Status);
    }

    // --- helpers -----------------------------------------------------------

    private TaskIntegrationStatusService BuildService(string repo, out string projectName, out PipelineExecutionLog log)
    {
        projectName = "Fixture";
        var config = ConfigFor(repo, projectName);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetIntegrationBranch(projectName, "develop");
        log = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        return new TaskIntegrationStatusService(
            git, settings, log, NullLogger<TaskIntegrationStatusService>.Instance);
    }

    private (TaskIntegrationStatusService Service, string Project, PipelineExecutionLog Log)
        BuildMultiRepositoryService(string studio, string runner)
    {
        const string projectName = "Agent Studio";
        var taskRepository = Path.Combine(_tempDir, "task-store-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(taskRepository, ".metadata"));
        var projects = new ProjectsFile
        {
            NextProjectIdSeq = 3,
            Projects =
            [
                new ProjectRecord
                {
                    Id = "PROJ-001", DisplayName = projectName, ShortCode = "AGT",
                    WorkspaceId = DefaultWorkspace.Id, StorageLocation = Path.Combine(taskRepository, "studio"),
                    RepositoryPath = studio,
                    Urls = [new ProjectUrlRecord { Id = "repo", Label = "Repository", Url = "https://github.com/example/agent-studio.git" }],
                },
                new ProjectRecord
                {
                    Id = "PROJ-002", DisplayName = "Runner", ShortCode = "RUN",
                    WorkspaceId = DefaultWorkspace.Id, StorageLocation = Path.Combine(taskRepository, "runner"),
                    RepositoryPath = runner,
                    Urls = [new ProjectUrlRecord { Id = "repo", Label = "Repository", Url = "https://github.com/example/runner.git" }],
                },
            ],
        };
        File.WriteAllText(
            Path.Combine(taskRepository, ".metadata", "projects.json"),
            JsonSerializer.Serialize(projects));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = taskRepository,
            ["WatchPaths:0:Name"] = projectName,
            ["WatchPaths:0:RootPath"] = studio,
            ["WatchPaths:0:RepositoryPath"] = studio,
            ["WatchPaths:0:Path"] = Path.Combine(studio, ".orchestrator", "jobs"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetIntegrationBranch(projectName, "develop");
        settings.SetIntegrationBranch("Runner", "main");
        var log = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        return (new TaskIntegrationStatusService(
            git, settings, log, NullLogger<TaskIntegrationStatusService>.Instance, registry), projectName, log);
    }

    private static IConfiguration ConfigFor(string repo, string projectName)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = projectName,
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
            ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
        }).Build();

    private static IConfiguration EmptyConfig()
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private string SeedDevelopMainRepo()
    {
        var repo = Path.Combine(_tempDir, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q main");
        return repo;
    }

    private static TaskProvenance Prov(string branch, string? merge = null, string? tip = null)
        => new()
        {
            Branch = branch,
            Merge = merge is null ? null : new TaskProvenanceMerge { MergeCommit = merge },
            Transitions = tip is null
                ? []
                : [new TaskProvenanceTransition { Lane = TaskStates.Completed, BranchTip = tip }],
        };

    private static TaskCommitInfo Commit(string sha)
        => new() { Sha = sha, ShortSha = sha.Length > 7 ? sha[..7] : sha, Message = "commit " + sha };

    private TaskInfo Job(
        string id,
        string key,
        string project,
        string repo,
        PipelineExecutionLog log,
        TaskProvenance? prov = null,
        TaskCommitInfo[]? commits = null)
    {
        // Give the card a real on-disk folder so the pipeline-execution.json read
        // path works for the conflict-skipped classification.
        var folder = Path.Combine(_tempDir, "jobs", id);
        Directory.CreateDirectory(folder);
        return new TaskInfo
        {
            Id = id,
            Key = key,
            TaskKey = repo + "::" + id,
            State = TaskStates.Completed,
            ProjectName = project,
            WatchPath = repo,
            FolderPath = folder,
            Provenance = prov,
            Commits = (commits ?? Array.Empty<TaskCommitInfo>()).ToList(),
        };
    }

    private static (string Out, string Err, int Code) RunGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (so, se, p.ExitCode);
    }

    private static void Commit(string cwd, string message)
    {
        RunGit(cwd, "add -A");
        RunGit(cwd, $"commit -q -m \"{message}\"");
    }
}
