using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Tests for restart visibility in <c>pipeline-execution.json</c>. A
/// re-run / re-issue must surface as a fresh record with an incremented
/// <see cref="PipelineExecutionRecord.Attempt"/>, and the previous run's
/// steps must survive in <see cref="PipelineExecutionRecord.PreviousAttempts"/>
/// so an operator can still tell old step runs apart from the current ones.
///
/// This is the acceptance criterion for "Pipeline-Neustart sichtbar machen":
/// a re-run is clearly a new run, and old vs. new step runs are
/// distinguishable.
/// </summary>
public class PipelineExecutionRestartTests : IDisposable
{
    private readonly string _jobFolder;
    private readonly PipelineExecutionLog _log;

    public PipelineExecutionRestartTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "pipeline-restart-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
        _log = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void FirstRun_IsAttemptOne_WithNoPreviousAttempts()
    {
        var record = _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        Assert.Equal(1, record.Attempt);
        Assert.Empty(record.PreviousAttempts);
    }

    [Fact]
    public void CompletedRun_ThenEnsureRun_StartsAttemptTwo_ArchivingPriorSteps()
    {
        // First run: mark a step done, complete it.
        _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");
        var doneAt = DateTime.UtcNow;
        _log.RecordStep(_jobFolder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.CoreAgentRunStepId,
            Kind = StepKind.Core,
            Model = "claude-opus-4-8",
            Status = PipelineStepStatus.Passed,
            StartedAt = doneAt.AddSeconds(-5),
            CompletedAt = doneAt,
            DurationMs = 5000,
        });
        _log.Complete(_jobFolder);

        // Re-issue: EnsureRun sees a complete record and begins a fresh run.
        var second = _log.EnsureRun(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        Assert.Equal(2, second.Attempt);
        Assert.Single(second.PreviousAttempts);

        // The archived prior run keeps its step outcome so old vs. new is clear.
        var archived = second.PreviousAttempts[0];
        Assert.Equal(1, archived.Attempt);
        Assert.True(archived.IsComplete);
        var archivedCore = archived.Steps.First(s =>
            string.Equals(s.StepId, PipelineCatalogue.CoreAgentRunStepId, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PipelineStepStatus.Passed, archivedCore.Status);
        Assert.Equal(5000, archivedCore.DurationMs);

        // The fresh run's own steps are reset to Pending/Planned.
        var freshCore = second.Steps.First(s =>
            string.Equals(s.StepId, PipelineCatalogue.CoreAgentRunStepId, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PipelineStepStatus.Pending, freshCore.Status);
        Assert.Null(second.CompletedAt);
    }

    [Fact]
    public void Complete_TerminalizesUnreachedSteps_ButPreservesDeferredAndPlannedSlots()
    {
        var runningAt = new DateTime(2026, 7, 13, 10, 0, 0, DateTimeKind.Utc);
        var completedAt = runningAt.AddSeconds(5);
        _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");
        _log.RecordStep(_jobFolder, new PipelineStepExecution
        {
            StepId = "aspect-code-quality",
            Kind = StepKind.Aspect,
            Status = PipelineStepStatus.Running,
            StartedAt = runningAt,
        });

        const string stopReason = "Not run because the build/test gate failed: dotnet test exit 1.";
        _log.Complete(_jobFolder, nowUtc: completedAt, pendingStepReason: stopReason);

        var completed = _log.Read(_jobFolder);
        Assert.NotNull(completed);
        Assert.True(completed!.IsComplete);

        var grade = completed.Steps.Single(s => s.StepId == PipelineCatalogue.CodeReviewGradeStepId);
        Assert.Equal(PipelineStepStatus.Skipped, grade.Status);
        Assert.Equal(stopReason, grade.Reason);
        Assert.NotNull(grade.CompletedAt);

        var interrupted = completed.Steps.Single(s => s.StepId == "aspect-code-quality");
        Assert.Equal(PipelineStepStatus.Failed, interrupted.Status);
        Assert.Equal("Pipeline attempt ended while this step was still running.", interrupted.Reason);
        Assert.NotNull(interrupted.CompletedAt);
        Assert.Equal(5000, interrupted.DurationMs);

        // Deferred operator-triggered delivery steps deliberately remain
        // pending after the automatic pipeline bracket ends.
        Assert.Equal(PipelineStepStatus.Pending,
            completed.Steps.Single(s => s.StepId == PipelineCatalogue.MergeIntoDevelopStepId).Status);
        Assert.Equal(PipelineStepStatus.Pending,
            completed.Steps.Single(s => s.StepId == PipelineCatalogue.MergeIntoDevelopPushStepId).Status);

        // Catalogue stubs remain planned rather than being rewritten as if
        // their unimplemented work had executed.
        Assert.Equal(PipelineStepStatus.Planned,
            completed.Steps.Single(s => s.StepId == PipelineCatalogue.GitCommitAttributionStepId).Status);
    }

    [Fact]
    public void Read_NormalizesLegacyCompletedRecord_WithPendingGrade()
    {
        var pending = _log.Begin(
            _jobFolder,
            PipelineCatalogue.Standard,
            project: "demo",
            jobId: "job-1");
        var completedAt = DateTime.UtcNow;
        pending = pending with
        {
            Steps = pending.Steps.Select(step => step.StepId == "aspect-code-quality"
                ? step with
                {
                    Status = PipelineStepStatus.Running,
                    StartedAt = completedAt.AddSeconds(-3),
                }
                : step).ToList(),
        };

        // Reproduce an on-disk record written before completion terminalized
        // unreached rows: CompletedAt is present while the grade is Pending.
        // Write it directly so this test exercises the read-time compatibility
        // projection instead of the new Complete() path.
        var json = System.Text.Json.JsonSerializer.Serialize(
            pending with { CompletedAt = completedAt },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            });
        File.WriteAllText(Path.Combine(_jobFolder, PipelineExecutionLog.FileName), json);

        var normalized = _log.Read(_jobFolder);
        Assert.NotNull(normalized);

        var grade = normalized!.Steps.Single(s => s.StepId == PipelineCatalogue.CodeReviewGradeStepId);
        Assert.Equal(PipelineStepStatus.Skipped, grade.Status);
        Assert.Contains("pipeline attempt ended", grade.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(completedAt, grade.CompletedAt);

        var interrupted = normalized.Steps.Single(s => s.StepId == "aspect-code-quality");
        Assert.Equal(PipelineStepStatus.Failed, interrupted.Status);
        Assert.Equal("Pipeline attempt ended while this step was still running.", interrupted.Reason);
        Assert.Equal(completedAt, interrupted.CompletedAt);
        Assert.Equal(3000, interrupted.DurationMs);

        Assert.Equal(PipelineStepStatus.Pending,
            normalized.Steps.Single(s => s.StepId == PipelineCatalogue.MergeIntoDevelopStepId).Status);
        Assert.Equal(PipelineStepStatus.Planned,
            normalized.Steps.Single(s => s.StepId == PipelineCatalogue.GitCommitAttributionStepId).Status);
    }

    [Fact]
    public void Begin_OnExistingSameJobRun_ArchivesAndIncrementsAttempt()
    {
        _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");
        var second = _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        Assert.Equal(2, second.Attempt);
        Assert.Single(second.PreviousAttempts);
        Assert.Equal(1, second.PreviousAttempts[0].Attempt);
    }

    [Fact]
    public void EnsureRun_WhileInFlight_ReturnsSameRecord_NoAttemptBump()
    {
        var first = _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");
        // No Complete() -> the run is still in flight.
        var same = _log.EnsureRun(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        Assert.Equal(1, same.Attempt);
        Assert.Empty(same.PreviousAttempts);
        Assert.Equal(first.StartedAt, same.StartedAt);
    }

    [Fact]
    public void EnsureAgentRunStart_CoreAlreadyTouchedButIncomplete_StartsNewAttempt()
    {
        _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");
        var startedAt = DateTime.UtcNow.AddMinutes(-10);
        _log.RecordStep(_jobFolder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.CoreAgentRunStepId,
            Kind = StepKind.Core,
            Status = PipelineStepStatus.Passed,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddMinutes(2),
            DurationMs = 120_000,
        });
        // Deliberately no Complete(): this is the re-open bug shape. Some
        // short-circuit reissue paths move the card back to Ready before the
        // post bracket stamps CompletedAt.

        var second = _log.EnsureAgentRunStart(
            _jobFolder,
            PipelineCatalogue.Standard,
            project: "demo",
            jobId: "job-1");

        Assert.Equal(2, second.Attempt);
        var archived = Assert.Single(second.PreviousAttempts);
        Assert.Equal(1, archived.Attempt);
        Assert.Null(archived.CompletedAt);
        Assert.Equal(PipelineStepStatus.Passed,
            archived.Steps.First(s => s.StepId == PipelineCatalogue.CoreAgentRunStepId).Status);
        Assert.Equal(PipelineStepStatus.Pending,
            second.Steps.First(s => s.StepId == PipelineCatalogue.CoreAgentRunStepId).Status);
    }

    [Fact]
    public void EnsureAgentRunStart_PreOnlyRecord_ReusesSameAttempt()
    {
        var first = _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");
        var now = DateTime.UtcNow;
        _log.RecordStep(_jobFolder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.LoopGuardStepId,
            Kind = StepKind.Module,
            Status = PipelineStepStatus.Passed,
            StartedAt = now,
            CompletedAt = now,
        });

        var same = _log.EnsureAgentRunStart(
            _jobFolder,
            PipelineCatalogue.Standard,
            project: "demo",
            jobId: "job-1");

        Assert.Equal(1, same.Attempt);
        Assert.Empty(same.PreviousAttempts);
        Assert.Equal(first.StartedAt, same.StartedAt);
    }

    [Fact]
    public void MultipleRestarts_KeepNewestFirstOrder_AndFlattenHistory()
    {
        // Run three times, completing each, so the third run carries two
        // archived attempts ordered most-recent-first.
        for (var i = 0; i < 3; i++)
        {
            _log.EnsureRun(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");
            _log.Complete(_jobFolder);
        }
        var fourth = _log.EnsureRun(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        Assert.Equal(4, fourth.Attempt);
        Assert.Equal(3, fourth.PreviousAttempts.Count);

        // Newest-first: the immediately prior run (attempt 3) leads the list.
        Assert.Equal(3, fourth.PreviousAttempts[0].Attempt);
        Assert.Equal(2, fourth.PreviousAttempts[1].Attempt);
        Assert.Equal(1, fourth.PreviousAttempts[2].Attempt);

        // Flattened: archived entries carry no nested history of their own.
        Assert.All(fourth.PreviousAttempts, a => Assert.Empty(a.PreviousAttempts));
    }

    [Fact]
    public void ArchivedAttempts_AreBounded()
    {
        // Drive many more restarts than the archive cap and confirm the
        // PreviousAttempts list stays bounded while the attempt counter keeps
        // climbing.
        const int runs = 15;
        for (var i = 0; i < runs; i++)
        {
            _log.EnsureRun(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");
            _log.Complete(_jobFolder);
        }
        var latest = _log.EnsureRun(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        Assert.Equal(runs + 1, latest.Attempt);
        Assert.True(latest.PreviousAttempts.Count <= 10,
            $"expected archived attempts bounded to 10, got {latest.PreviousAttempts.Count}");
        // The most recent archived run is the one just before this attempt.
        Assert.Equal(runs, latest.PreviousAttempts[0].Attempt);
    }

    [Fact]
    public void LeftoverRecordFromDifferentJob_IsNotTreatedAsRestart()
    {
        // A stale file from another job is not a restart of this one: the new
        // record begins at attempt 1 with no inherited history.
        _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "other-job");
        _log.Complete(_jobFolder);

        var fresh = _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        Assert.Equal(1, fresh.Attempt);
        Assert.Empty(fresh.PreviousAttempts);
    }

    [Fact]
    public void RestartHistory_SurvivesRoundTripThroughDisk()
    {
        _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");
        _log.Complete(_jobFolder);
        _log.EnsureRun(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        // Read back fresh from disk (no in-memory shortcut).
        var reread = _log.Read(_jobFolder);
        Assert.NotNull(reread);
        Assert.Equal(2, reread!.Attempt);
        Assert.Single(reread.PreviousAttempts);
        Assert.Equal(1, reread.PreviousAttempts[0].Attempt);
    }

    [Fact]
    public void NewAttempt_FencesLateStepVerdictAndCompletionFromSupersededAttempt()
    {
        var firstStarted = new DateTime(2026, 7, 24, 18, 0, 0, DateTimeKind.Utc);
        var first = _log.Begin(
            _jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1", nowUtc: firstStarted);

        using var oldAttempt = _log.EnterAttempt(_jobFolder, first.Attempt);
        _log.RecordStep(_jobFolder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.OrchestratorDecisionStepId,
            Kind = StepKind.Orchestrator,
            Status = PipelineStepStatus.Running,
            StartedAt = firstStarted.AddMinutes(1),
        });

        var second = _log.Begin(
            _jobFolder,
            PipelineCatalogue.Standard,
            project: "demo",
            jobId: "job-1",
            nowUtc: firstStarted.AddMinutes(2));

        // The old async flow finishes after the rerun has already opened. Its
        // red verdict and completion stamp must not leak into attempt 2.
        _log.RecordStep(_jobFolder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.OrchestratorDecisionStepId,
            Kind = StepKind.Orchestrator,
            Status = PipelineStepStatus.Failed,
            StartedAt = firstStarted.AddMinutes(1),
            CompletedAt = firstStarted.AddMinutes(3),
            Verdict = "escalate",
        });
        _log.Complete(_jobFolder, nowUtc: firstStarted.AddMinutes(3));

        var current = _log.Read(_jobFolder);
        Assert.NotNull(current);
        Assert.Equal(second.Attempt, current!.Attempt);
        Assert.Null(current.CompletedAt);
        var decision = current.Steps.Single(step => step.StepId == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.Equal(PipelineStepStatus.Pending, decision.Status);
        Assert.Null(decision.Verdict);
        Assert.Equal(second.Attempt, decision.Attempt);
    }

    [Fact]
    public void NewAttempt_RejectsLegacyUnscopedStepThatStartedBeforeEpoch()
    {
        var firstStarted = new DateTime(2026, 7, 24, 18, 0, 0, DateTimeKind.Utc);
        _log.Begin(
            _jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1", nowUtc: firstStarted);
        var second = _log.Begin(
            _jobFolder,
            PipelineCatalogue.Standard,
            project: "demo",
            jobId: "job-1",
            nowUtc: firstStarted.AddMinutes(2));

        _log.RecordStep(_jobFolder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.OrchestratorDecisionStepId,
            Kind = StepKind.Orchestrator,
            Status = PipelineStepStatus.Failed,
            StartedAt = firstStarted.AddMinutes(1),
            CompletedAt = firstStarted.AddMinutes(3),
            Verdict = "escalate",
        });

        var decision = _log.Read(_jobFolder)!.Steps.Single(
            step => step.StepId == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.Equal(second.Attempt, decision.Attempt);
        Assert.Equal(PipelineStepStatus.Pending, decision.Status);
        Assert.Null(decision.Verdict);
    }

    [Fact]
    public void BackendRestart_ResumesPostStepChain_FromFirstUnfinishedStep()
    {
        var firstHost = _log.Begin(
            _jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");
        using (var attempt = _log.EnterAttempt(_jobFolder, firstHost.Attempt))
        {
            var completedAt = DateTime.UtcNow;
            _log.RecordStep(_jobFolder, new PipelineStepExecution
            {
                StepId = PipelineCatalogue.BuildTestGateStepId,
                Kind = StepKind.Tool,
                Status = PipelineStepStatus.Passed,
                StartedAt = completedAt.AddSeconds(-1),
                CompletedAt = completedAt,
                Verdict = "ok",
            });
            PostStepCheckpointStore.Write(
                _log,
                _jobFolder,
                PipelineCatalogue.BuildTestGateStepId,
                new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 1000, "green", "passed", true, false));
            _log.RecordStep(_jobFolder, new PipelineStepExecution
            {
                StepId = PipelineCatalogue.LintScssStepId,
                Kind = StepKind.Tool,
                Status = PipelineStepStatus.Running,
                StartedAt = completedAt,
            });
        }

        // A replacement host gets a new in-memory logger instance but reads the
        // same attempt authority and payload evidence from the task folder.
        var replacement = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var buildInvocations = 0;
        var lintInvocations = 0;
        var radarInvocations = 0;
        using (replacement.EnterAttempt(_jobFolder, firstHost.Attempt))
        {
            if (!PostStepCheckpointStore.TryRead<BuildTestGateResult>(
                    replacement, _jobFolder, PipelineCatalogue.BuildTestGateStepId, out var build))
            {
                buildInvocations++;
            }
            Assert.Equal(BuildTestGateVerdict.Ok, build!.Verdict);

            Assert.False(PostStepCheckpointStore.TryRead<LintScssResult>(
                replacement, _jobFolder, PipelineCatalogue.LintScssStepId, out _));
            lintInvocations++;
            replacement.RecordStep(_jobFolder, new PipelineStepExecution
            {
                StepId = PipelineCatalogue.LintScssStepId,
                Kind = StepKind.Tool,
                Status = PipelineStepStatus.Passed,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                Verdict = "ok",
            });

            radarInvocations++;
            replacement.RecordStep(_jobFolder, new PipelineStepExecution
            {
                StepId = PipelineCatalogue.RegressionRadarStepId,
                Kind = StepKind.Tool,
                Status = PipelineStepStatus.Passed,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                Verdict = "clean",
            });
        }

        Assert.Equal(0, buildInvocations);
        Assert.Equal(1, lintInvocations);
        Assert.Equal(1, radarInvocations);
        Assert.Equal(firstHost.Attempt, replacement.Read(_jobFolder)!.Attempt);
    }
}
