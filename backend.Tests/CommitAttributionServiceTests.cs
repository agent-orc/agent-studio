

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the deterministic commit-attribution rule engine (ADR
/// "Commit-Attribution-Regel"). The service is pure - no git, no filesystem,
/// no clock - so every rule can be exercised in isolation with hand-built
/// candidates. The cases mirror the acceptance list: no commits, only the
/// task's own crash-recovery, a crash-recovery for another task, commits
/// before the task window, time-window-overlap separated by author and by
/// file path, plus the release-stream exclusions (update-stable bump, merge)
/// and the platform-stamp fast path. The final case proves idempotency: the
/// same input twice yields byte-identical output, which is what makes the
/// post-step safe to re-run.
/// </summary>
public class CommitAttributionServiceTests
{
    private const string TaskId = "feature-alpha";

    private static AttributionCandidate Candidate(
        string sha, string subject, string author = "dev", string? message = null,
        DateTime? at = null, IReadOnlyList<string>? files = null) => new()
    {
        Sha = sha.PadRight(40, '0'),
        ShortSha = sha,
        Author = author,
        Subject = subject,
        Message = message ?? subject,
        AuthorDateUtc = at ?? new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc),
        FilesChanged = 3,
        Files = files ?? [],
    };

    [Fact]
    public void NoCommits_ProducesEmptyResult()
    {
        var result = CommitAttributionService.Attribute(new AttributionInput { TaskId = TaskId });
        Assert.Empty(result.Attributed);
        Assert.Empty(result.Excluded);
    }

    [Fact]
    public void Attribution_StampsRepositoryAndBranchForEveryCommit()
    {
        var result = CommitAttributionService.Attribute(new AttributionInput
        {
            TaskId = TaskId,
            Repository = "repo_agent_studio",
            TaskBranch = "develop",
            Candidates = [Candidate("a101", "feat: direct delivery")],
        });

        var commit = Assert.Single(result.Attributed);
        Assert.Equal("repo_agent_studio", commit.Repository);
        Assert.Equal("develop", commit.Branch);
    }

    [Fact]
    public void LegacyRepositoryPrefix_IsParsedWithoutChangingMessage()
    {
        var legacy = new TaskCommitInfo
        {
            Sha = "a".PadRight(40, '0'),
            Message = "[runner] feat: publish runner",
        };

        var migrated = TaskCommitRepository.NormalizeLegacy(legacy);

        Assert.Equal("runner", migrated.Repository);
        Assert.Equal(legacy.Message, migrated.Message);
        Assert.Same(migrated, TaskCommitRepository.NormalizeLegacy(migrated));
    }

    [Fact]
    public void OwnCrashRecovery_IsAttributed_NotExcluded()
    {
        var result = CommitAttributionService.Attribute(new AttributionInput
        {
            TaskId = TaskId,
            Candidates = [Candidate("aaa1", $"chore(crash-recovery): rescue orphan changes for {TaskId}")],
        });

        var c = Assert.Single(result.Attributed);
        Assert.Equal(CommitAttributionKinds.Automatic, c.Attribution);
        Assert.Empty(result.Excluded);
    }

    [Fact]
    public void CrashRecoveryOfOtherTask_IsExcluded()
    {
        var result = CommitAttributionService.Attribute(new AttributionInput
        {
            TaskId = TaskId,
            Candidates = [Candidate("bbb2", "chore(crash-recovery): rescue orphan changes for some-other-task")],
        });

        Assert.Empty(result.Attributed);
        var ex = Assert.Single(result.Excluded);
        Assert.Equal(CommitExclusionReasons.CrashRecoveryOfOtherTask, ex.Reason);
    }

    [Fact]
    public void CrashRecoveryForProject_IsExcluded_NeverNamesASingleTask()
    {
        var result = CommitAttributionService.Attribute(new AttributionInput
        {
            TaskId = TaskId,
            Candidates = [Candidate("bbb3", "chore(crash-recovery): rescue orphan changes for project demo")],
        });

        Assert.Empty(result.Attributed);
        Assert.Equal(CommitExclusionReasons.CrashRecoveryOfOtherTask, Assert.Single(result.Excluded).Reason);
    }

    [Fact]
    public void CommitBeforeTaskStart_IsExcludedAsOutsideWindow()
    {
        var start = new DateTime(2026, 5, 20, 9, 0, 0, DateTimeKind.Utc);
        var result = CommitAttributionService.Attribute(new AttributionInput
        {
            TaskId = TaskId,
            WindowStartUtc = start,
            Candidates =
            [
                Candidate("ccc4", "feat: pre-window", at: start.AddMinutes(-30)),
                Candidate("ccc5", "feat: in-window", at: start.AddMinutes(30)),
            ],
        });

        Assert.Equal("ccc5", Assert.Single(result.Attributed).ShortSha);
        var ex = Assert.Single(result.Excluded);
        Assert.Equal(CommitExclusionReasons.OutsideTaskWindow, ex.Reason);
        Assert.Equal("ccc4", ex.ShortSha);
    }

    [Fact]
    public void UpdateStableBump_IsExcluded()
    {
        var result = CommitAttributionService.Attribute(new AttributionInput
        {
            TaskId = TaskId,
            Candidates = [Candidate("ddd6", "chore(submodules): bump dev to abc123")],
        });

        Assert.Empty(result.Attributed);
        Assert.Equal(CommitExclusionReasons.UpdateStableBump, Assert.Single(result.Excluded).Reason);
    }

    [Fact]
    public void MergeCommit_IsExcluded()
    {
        var result = CommitAttributionService.Attribute(new AttributionInput
        {
            TaskId = TaskId,
            Candidates = [Candidate("eee7", "Merge branch 'main' into update-stable")],
        });

        Assert.Empty(result.Attributed);
        Assert.Equal(CommitExclusionReasons.MergeCommit, Assert.Single(result.Excluded).Reason);
    }

    [Fact]
    public void MergeByParentCount_IsExcluded_EvenWithOrdinarySubject()
    {
        var result = CommitAttributionService.Attribute(new AttributionInput
        {
            TaskId = TaskId,
            Candidates = [Candidate("eee8", "feat: looks normal") with { IsMerge = true }],
        });

        Assert.Empty(result.Attributed);
        Assert.Equal(CommitExclusionReasons.MergeCommit, Assert.Single(result.Excluded).Reason);
    }

    [Fact]
    public void OverlappingWindow_SeparatedByAuthor_DrivesConfidence()
    {
        // Two commits in the same window: one authored by the agent, one by a
        // human operator. Both are attributed (single-branch, in-window) but
        // the agent commit carries higher confidence so the UI can flag the
        // weaker call.
        var result = CommitAttributionService.Attribute(new AttributionInput
        {
            TaskId = TaskId,
            AgentMarker = "Claude",
            Candidates =
            [
                Candidate("f001", "feat: agent work", author: "Claude"),
                Candidate("f002", "fix: operator tweak", author: "Robert"),
            ],
        });

        Assert.Equal(2, result.Attributed.Count);
        var agent = result.Attributed.Single(c => c.ShortSha == "f001");
        var human = result.Attributed.Single(c => c.ShortSha == "f002");
        Assert.True(agent.Confidence > human.Confidence,
            $"agent confidence {agent.Confidence} should exceed operator {human.Confidence}");
    }

    [Fact]
    public void CoAuthorTrailer_CountsAsAgentSignal()
    {
        var result = CommitAttributionService.Attribute(new AttributionInput
        {
            TaskId = TaskId,
            AgentMarker = "Claude",
            Candidates =
            [
                Candidate("f010", "feat: work", author: "Robert",
                    message: "feat: work\n\nCo-Authored-By: Claude <noreply@anthropic.com>"),
            ],
        });

        Assert.Equal(0.9, Assert.Single(result.Attributed).Confidence);
    }

    [Fact]
    public void WorkingDirPrefix_ExcludesCommitsOutsideDevCheckout()
    {
        var result = CommitAttributionService.Attribute(new AttributionInput
        {
            TaskId = TaskId,
            WorkingDirPrefix = "agent-taskboard-dev/",
            Candidates =
            [
                Candidate("f020", "feat: in dev", files: ["agent-taskboard-dev/backend/X.cs"]),
                Candidate("f021", "feat: elsewhere", files: ["other-repo/Y.cs"]),
            ],
        });

        Assert.Equal("f020", Assert.Single(result.Attributed).ShortSha);
        Assert.Equal("f021", Assert.Single(result.Excluded).ShortSha);
    }

    [Fact]
    public void PlatformStamp_IsAttributedFullConfidence_BypassingExclusion()
    {
        // A platform-stamped commit whose message looks like a crash-recovery
        // for another task must still be attributed: the platform committed
        // the accepted work deliberately, so the message pattern does not win.
        var stamped = Candidate("f030", "chore(crash-recovery): rescue orphan changes for some-other-task");
        var result = CommitAttributionService.Attribute(new AttributionInput
        {
            TaskId = TaskId,
            PlatformStampShas = [stamped.Sha],
            Candidates = [stamped],
        });

        var c = Assert.Single(result.Attributed);
        Assert.Equal(1.0, c.Confidence);
        Assert.Equal(CommitAttributionKinds.Automatic, c.Attribution);
        Assert.Empty(result.Excluded);
    }

    [Fact]
    public void MisAttributionReproducer_TodaysSymptom_OnlyOwnWorkIsAttributed()
    {
        // Faithful reproducer of the 2026-05-28 symptom: opening this very task
        // surfaced a crash-recovery commit that belonged to the *lint-scss*
        // task (real SHA 9b8892e in this repo), plus release-stream noise. The
        // operator could no longer tell which changes belonged to the decision
        // under review. The shapes below mirror the actual git log of that day.
        const string thisTask = "feature-post-step-git-commit-attribution-deterministic-commit-to-task-binding";
        const string otherTask = "pipeline-lintscss-as-configurable-post-step--auto-reissue-on-fail--bring-repo-to-0-errors";

        var window = new DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc);
        var result = CommitAttributionService.Attribute(new AttributionInput
        {
            TaskId = thisTask,
            AgentMarker = "Claude",
            WorkingDirPrefix = "agent-taskboard-dev/",
            WindowStartUtc = window,
            Candidates =
            [
                // The task's own work - the only thing that should be attributed.
                Candidate("ownwork1", "feat(attribution): deterministic commit-to-task binding",
                    author: "Claude", at: window.AddMinutes(20),
                    files: ["agent-taskboard-dev/backend/Services/Jobs/CommitAttributionService.cs"]),
                // THE BUG: a crash-recovery commit for a *different* task.
                Candidate("9b8892e", $"chore(crash-recovery): rescue orphan changes for {otherTask}",
                    author: "Crash Recovery", at: window.AddMinutes(25),
                    files: ["agent-taskboard-dev/frontend/src/x.scss"]),
                // Release-stream noise that belongs to no single task.
                Candidate("bumpdev1", "chore(submodules): bump dev to deadbee",
                    at: window.AddMinutes(30)),
                Candidate("mergez01", "Merge branch 'main' into update-stable",
                    at: window.AddMinutes(35)) with { IsMerge = true },
            ],
        });

        // Only the task's own work is attributed - the heart of the fix.
        var attributed = Assert.Single(result.Attributed);
        Assert.Equal("ownwork1", attributed.ShortSha);

        // The cross-task crash-recovery commit is excluded with the right reason
        // (this is the exact line item the operator saw mis-attributed).
        var crossTask = result.Excluded.Single(e => e.ShortSha == "9b8892e");
        Assert.Equal(CommitExclusionReasons.CrashRecoveryOfOtherTask, crossTask.Reason);

        // Release-stream noise is excluded too, never attributed.
        Assert.Equal(CommitExclusionReasons.UpdateStableBump,
            result.Excluded.Single(e => e.ShortSha == "bumpdev1").Reason);
        Assert.Equal(CommitExclusionReasons.MergeCommit,
            result.Excluded.Single(e => e.ShortSha == "mergez01").Reason);
        Assert.DoesNotContain(result.Attributed, c => c.ShortSha != "ownwork1");
    }

    [Fact]
    public void Attribution_IsDeterministic_SameInputSameOutput()
    {
        var input = new AttributionInput
        {
            TaskId = TaskId,
            AgentMarker = "Claude",
            Candidates =
            [
                Candidate("f100", "feat: work", author: "Claude"),
                Candidate("f101", "chore(crash-recovery): rescue orphan changes for other"),
                Candidate("f102", "chore(submodules): bump dev"),
            ],
        };

        var a = CommitAttributionService.Attribute(input);
        var b = CommitAttributionService.Attribute(input);

        Assert.Equal(
            a.Attributed.Select(c => (c.Sha, c.Attribution, c.Confidence)),
            b.Attributed.Select(c => (c.Sha, c.Attribution, c.Confidence)));
        Assert.Equal(
            a.Excluded.Select(e => (e.Sha, e.Reason)),
            b.Excluded.Select(e => (e.Sha, e.Reason)));
        Assert.Single(a.Attributed);
        Assert.Equal(2, a.Excluded.Count);
    }
}
