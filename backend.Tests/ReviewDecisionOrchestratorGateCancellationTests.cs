using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentStudio.Tests;

public partial class ReviewDecisionOrchestratorCompletionGateTests
{
    private TaskStateMachine? _cancellationStateMachine;
    private TaskMutationService? _cancellationMutations;
    private TaskScannerService? _cancellationScanner;
    private TaskTransitionService? _cancellationTransitions;

    [Fact]
    public async Task Accepted_card_cancels_its_queued_gate()
    {
        const string slug = "accept-cancels-gate";
        SeedReviewJobWithDone(slug, "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        WriteRemoteSubject(slug, new string('a', 40), "accepted");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new WaitingTransport(started);
        var registry = new TaskGateCancellationRegistry();
        var orchestrator = BuildOrchestrator(new CountingAspect().Cli, 0,
            new RemoteBuildTestGateRunner(transport, NullLogger<RemoteBuildTestGateRunner>.Instance, registry),
            new CancellationClock(), gateCancellation: registry);
        var tick = orchestrator.TickOnceAsync(_workspace, CancellationToken.None);
        await started.Task;
        Assert.Equal(MoveJobStatus.Success,
            _cancellationStateMachine!.MoveJob(slug, TaskStates.HumanReview, _watchPath).Status);
        var reviewFolder = Path.Combine(_watchPath, TaskStates.HumanReview, slug);
        TaskJsonFile.UpdateField(reviewFolder, "requiresIntegration", false,
            NullLogger<TaskTransitionService>.Instance);
        _cancellationScanner!.InvalidateCache();
        var accepted = await _cancellationTransitions!.MoveAsync(slug, TaskStates.Completed, _watchPath,
            cause: "operator:test", reason: "Accepted the reviewed result.", operatorOverride: true);
        Assert.Equal(MoveJobStatus.Success, accepted.Status);
        await tick;

        var folder = Path.Combine(_watchPath, TaskStates.Completed, slug);
        Assert.True(Directory.Exists(folder));
        Assert.Equal("superseded", Assert.Single(new TimelineLog(NullLogger<TimelineLog>.Instance)
            .ReadAll(folder), item => item.Kind == "gate-cancelled").Details!["reason"]);
    }

    private sealed class WaitingTransport(TaskCompletionSource started) : IRemoteGateTransport
    {
        public async Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles, AgentStudio.Shared.BuildProfile? profile,
            PostStepMode mode, TimeSpan timeout, CancellationToken ct)
        {
            var cancelled = new TaskCompletionSource<BuildTestGateResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => cancelled.TrySetCanceled(ct));
            started.SetResult();
            return await cancelled.Task;
        }
    }

    [Fact]
    public async Task Completed_aspect_review_for_superseded_delivery_does_not_move_new_run()
    {
        const string slug = "aspect-superseded";
        SeedReviewJobWithDone(slug, "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        WriteRemoteSubject(slug, new string('a', 40), "old");
        var moved = 0;
        Task<string> Aspect(string aspectId, string cli, string model, string prompt,
            TimeSpan timeout, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref moved, 1) == 0)
            {
                Assert.Equal(MoveJobStatus.Success,
                    _cancellationStateMachine!.MoveJob(slug, TaskStates.Ready, _watchPath).Status);
                Assert.Equal(MoveJobStatus.Success,
                    _cancellationStateMachine.MoveJob(slug, TaskStates.Progress, _watchPath).Status);
            }
            return Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");
        }
        var transport = new CancellationTransport(request => new BuildTestGateResult(
            BuildTestGateVerdict.Ok, 0, 10, "", "passed", true, false)
        { ExpectedSha = request.ExpectedSha, TestedSha = request.ExpectedSha, GateId = request.GateId });
        var orchestrator = BuildOrchestrator(Aspect, 3,
            new RemoteBuildTestGateRunner(transport, NullLogger<RemoteBuildTestGateRunner>.Instance),
            new CancellationClock());

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        Assert.True(Directory.Exists(folder));
        Assert.DoesNotContain(ReviewDecisionLog.ReadAll(_workspace, Project), item => item.JobId == slug);
        var receipt = Assert.Single(new TimelineLog(NullLogger<TimelineLog>.Instance)
            .ReadAll(folder), item => item.Kind == "review-result-superseded");
        Assert.Equal(new string('a', 40), receipt.Details!["expectedSha"]);
        Assert.Contains("pass", receipt.Details["verdicts"], StringComparison.OrdinalIgnoreCase);
    }


    [Theory]
    [InlineData("delivery")]
    [InlineData("attempt")]
    [InlineData("progress")]
    [InlineData("continue")]
    public async Task Completed_red_gate_for_superseded_delivery_records_evidence_without_parking(string change)
    {
        const string slug = "red-superseded";
        SeedReviewJobWithDone(slug, "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        var oldSha = new string('a', 40);
        WriteRemoteSubject(slug, oldSha, "old");
        var clock = new CancellationClock();
        var transport = new CancellationTransport(request =>
        {
            if (change == "delivery") WriteRemoteSubject(slug, new string('b', 40), "new");
            if (change == "attempt") WriteRemoteSubject(slug, oldSha, "new");
            if (change == "progress")
            {
                Assert.Equal(MoveJobStatus.Success,
                    _cancellationStateMachine!.MoveJob(slug, TaskStates.Ready, _watchPath).Status);
                Assert.Equal(MoveJobStatus.Success,
                    _cancellationStateMachine.MoveJob(slug, TaskStates.Progress, _watchPath).Status);
            }
            if (change == "continue")
                Assert.NotNull(_cancellationMutations!.SavePendingIntent(slug,
                    ContinueModes.Continue, "Run the queued fix", "operator", null, _watchPath));
            return new BuildTestGateResult(BuildTestGateVerdict.Fail, 1, 12,
                "dotnet test failed", "tests red", true, false)
            { ExpectedSha = request.ExpectedSha, TestedSha = request.ExpectedSha, GateId = request.GateId };
        });
        var orchestrator = BuildOrchestrator(new CountingAspect().Cli, 0,
            new RemoteBuildTestGateRunner(transport, NullLogger<RemoteBuildTestGateRunner>.Instance),
            clock, new CancellationDiagnostics());

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var state = change == "progress" ? TaskStates.Progress : TaskStates.AutoReview;
        var folder = Path.Combine(_watchPath, state, slug);
        Assert.True(Directory.Exists(folder));
        Assert.DoesNotContain(ReviewDecisionLog.ReadAll(_workspace, Project), item => item.JobId == slug);
        var eventRecord = Assert.Single(new TimelineLog(NullLogger<TimelineLog>.Instance)
            .ReadAll(folder), item => item.Kind == "gate-result-superseded");
        Assert.Equal("Fail", eventRecord.Details!["verdict"]);
        Assert.Equal(oldSha, eventRecord.Details["expectedSha"]);
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(folder, "post-steps"), "build-test-gate-*.log"),
            file => File.ReadAllText(file).Contains("dotnet test failed", StringComparison.Ordinal));
        if (change == "continue")
            Assert.Contains("Run the queued fix", File.ReadAllText(Path.Combine(folder, "pending-intent.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_gate_failure_after_operator_acceptance_records_old_result_without_escalation(bool queueTimeout)
    {
        const string slug = "accepted-queue-timeout";
        SeedReviewJobWithDone(slug, "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        var oldSha = new string('a', 40);
        WriteRemoteSubject(slug, oldSha, "accepted-delivery");
        var clock = new CancellationClock();
        var transport = new CancellationTransport(request =>
        {
            Assert.Equal(MoveJobStatus.Success,
                _cancellationStateMachine!.MoveJob(slug, TaskStates.HumanReview, _watchPath).Status);
            Assert.Equal(MoveJobStatus.Success,
                _cancellationStateMachine.MoveJob(slug, TaskStates.Completed, _watchPath).Status);
            return new BuildTestGateResult(BuildTestGateVerdict.Fail, queueTimeout ? null : 1, 0,
                queueTimeout ? "Gate queue wait budget expired" : "dotnet test failed",
                queueTimeout ? "queue wait timeout" : "test failure", false, false)
            {
                ExpectedSha = request.ExpectedSha,
                TestedSha = request.ExpectedSha,
                GateId = request.GateId,
                FailureKind = queueTimeout ? BuildTestGateFailureKind.Timeout : BuildTestGateFailureKind.Code,
                GateQueueWaitMs = queueTimeout ? 60_000 : 0,
            };
        });
        var orchestrator = BuildOrchestrator(new CountingAspect().Cli, 0,
            new RemoteBuildTestGateRunner(transport, NullLogger<RemoteBuildTestGateRunner>.Instance),
            clock);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.Completed, slug);
        Assert.True(Directory.Exists(folder));
        Assert.DoesNotContain(ReviewDecisionLog.ReadAll(_workspace, Project), item => item.JobId == slug);
        var receipt = Assert.Single(new TimelineLog(NullLogger<TimelineLog>.Instance)
            .ReadAll(folder), item => item.Kind == "gate-result-superseded");
        Assert.Equal(oldSha, receipt.Details!["expectedSha"]);
        Assert.Equal("Fail", receipt.Details["verdict"]);
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(folder, "post-steps"), "build-test-gate-*.log"),
            file => File.ReadAllText(file).Contains(
                queueTimeout ? "Gate queue wait budget expired" : "dotnet test failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Supersession_during_failure_decision_does_not_park_active_run(bool intervention)
    {
        const string slug = "failure-decision-superseded";
        SeedReviewJobWithDone(slug, "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        WriteRemoteSubject(slug, new string('a', 40), "old");
        var clock = new CancellationClock();
        var transport = new CancellationTransport(request => new BuildTestGateResult(
            BuildTestGateVerdict.Fail, intervention ? 1 : null, 10,
            intervention ? "dotnet test failed" : "Gate queue wait timed out",
            intervention ? "test failure" : "queue wait timeout", true, false)
        {
            ExpectedSha = request.ExpectedSha,
            TestedSha = request.ExpectedSha,
            GateId = request.GateId,
            FailureKind = intervention ? BuildTestGateFailureKind.Code : BuildTestGateFailureKind.Timeout,
        });
        async Task SupersedeAfterIntervention()
        {
            await Task.Yield();
            Assert.Equal(MoveJobStatus.Success,
                _cancellationStateMachine!.MoveJob(slug, TaskStates.Ready, _watchPath).Status);
            Assert.Equal(MoveJobStatus.Success,
                _cancellationStateMachine.MoveJob(slug, TaskStates.Progress, _watchPath).Status);
        }
        var orchestrator = BuildOrchestrator(new CountingAspect().Cli, 0,
            new RemoteBuildTestGateRunner(transport, NullLogger<RemoteBuildTestGateRunner>.Instance),
            clock, enableFailureIntervention: intervention,
            gateFailureDecisionCheckpoint: intervention ? null : SupersedeAfterIntervention);
        if (intervention)
            orchestrator.GateInterventionApplyCheckpointAsync = SupersedeAfterIntervention;
        orchestrator.BuildTestGateRetryBackoff = _ => TimeSpan.Zero;

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        Assert.True(Directory.Exists(folder));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, slug)));
        Assert.DoesNotContain(ReviewDecisionLog.ReadAll(_workspace, Project), item => item.JobId == slug);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var receipt = Assert.Single(timeline.ReadAll(folder), item => item.Kind == "gate-result-superseded");
        Assert.Equal("Fail", receipt.Details!["verdict"]);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, slug)));
        var live = Assert.Single(_cancellationScanner!.ScanAllJobs());
        Assert.Equal(slug, live.Id);
        Assert.Empty(live.References.BlockedBy);
        Assert.Empty(live.References.RaisedFollowUps);
    }

    [Theory]
    [InlineData("delivery")]
    [InlineData("lane")]
    [InlineData("continue")]
    public async Task Cancelled_superseded_gate_does_not_escalate(string change)
    {
        const string slug = "cancel-superseded";
        SeedReviewJobWithDone(slug, "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        WriteRemoteSubject(slug, new string('a', 40), "old");
        var clock = new CancellationClock();
        var transport = new CancellationTransport(request =>
        {
            if (change == "delivery") WriteRemoteSubject(slug, new string('b', 40), "new");
            else if (change == "lane")
            {
                Assert.Equal(MoveJobStatus.Success,
                    _cancellationStateMachine!.MoveJob(slug, TaskStates.Ready, _watchPath).Status);
                var liveFolder = Path.Combine(_watchPath, TaskStates.Ready, slug);
                var liveLog = new PipelineExecutionLog(TokenTestServices.Pricing(),
                    NullLogger<PipelineExecutionLog>.Instance);
                var prior = Assert.IsType<PipelineExecutionRecord>(liveLog.Read(liveFolder));
                var newAttemptAt = prior.StartedAt.AddTicks(1);
                liveLog.Begin(liveFolder, PipelineCatalogue.Standard, Project, slug, newAttemptAt);
                liveLog.RecordStep(liveFolder, BuildTestGateStepProjection.Create(
                    PipelineStepStatus.Passed, 0, "ok", "newer attempt passed", null,
                    newAttemptAt));
            }
            else Assert.NotNull(_cancellationMutations!.SavePendingIntent(slug,
                ContinueModes.Continue, "Continue the delivery", "operator", null, _watchPath));
            throw new OperationCanceledException();
        });
        var diagnostics = new CancellationDiagnostics();
        var orchestrator = BuildOrchestrator(new CountingAspect().Cli, 3,
            new RemoteBuildTestGateRunner(transport, NullLogger<RemoteBuildTestGateRunner>.Instance),
            clock, diagnostics);
        orchestrator.BuildTestGateRetryBackoff = _ => TimeSpan.Zero;

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(1, transport.Calls);
        Assert.DoesNotContain(diagnostics.Entries, e => e.Contains("escalation-diagnostic", StringComparison.Ordinal));
        Assert.True(Directory.Exists(Path.Combine(_watchPath,
            change == "lane" ? TaskStates.Ready : TaskStates.AutoReview, slug)));
        Assert.DoesNotContain(ReviewDecisionLog.ReadAll(_workspace, Project), item => item.JobId == slug);
        var folder = Path.Combine(_watchPath, change == "lane" ? TaskStates.Ready : TaskStates.AutoReview, slug);
        Assert.Equal("superseded", Assert.Single(new TimelineLog(NullLogger<TimelineLog>.Instance)
            .ReadAll(folder), e => e.Kind == "gate-cancelled").Details!["reason"]);
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(folder, "post-steps"), "build-test-gate-*.log"),
            file => File.ReadAllText(file).Contains("reason=Gate cancelled: superseded.", StringComparison.Ordinal));
        if (change == "lane")
        {
            var liveLog = new PipelineExecutionLog(TokenTestServices.Pricing(),
                NullLogger<PipelineExecutionLog>.Instance);
            var step = Assert.Single(liveLog.Read(folder)!.Steps,
                item => item.StepId == PipelineCatalogue.BuildTestGateStepId);
            Assert.Equal(PipelineStepStatus.Passed, step.Status);
            Assert.Equal("newer attempt passed", step.Reason);
        }
    }

    [Fact]
    public async Task Cancelled_shutdown_gate_schedules_one_infrastructure_retry_across_restart()
    {
        const string slug = "cancel-shutdown";
        SeedReviewJobWithDone(slug, "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        var sha = new string('a', 40);
        WriteRemoteSubject(slug, sha, "shutdown");
        var clock = new CancellationClock();
        using var shutdown = new CancellationTokenSource();
        var transport = new CancellationTransport(request =>
        {
            shutdown.Cancel();
            throw new OperationCanceledException(shutdown.Token);
        });
        var diagnostics = new CancellationDiagnostics();
        var orchestrator = BuildOrchestrator(new CountingAspect().Cli, 3,
            new RemoteBuildTestGateRunner(transport, NullLogger<RemoteBuildTestGateRunner>.Instance), clock, diagnostics);

        await orchestrator.TickOnceAsync(_workspace, shutdown.Token);

        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Assert.True(Directory.Exists(folder));
        Assert.Equal(1, transport.Calls);
        Assert.DoesNotContain(diagnostics.Entries, e => e.Contains("escalation-diagnostic", StringComparison.Ordinal));
        Assert.DoesNotContain(ReviewDecisionLog.ReadAll(_workspace, Project), item => item.JobId == slug);
        var scheduled = Assert.IsType<GateCancellationRetry>(GateCancellationRetry.Read(folder, sha, "shutdown"));
        Assert.Equal(1, scheduled.RetryNumber);
        Assert.Equal(clock.GetUtcNow() + PostProcessingOutcomeTaxonomy.RetryBackoff(1), scheduled.DueAtUtc);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        Assert.Equal("shutdown", Assert.Single(timeline.ReadAll(folder), e => e.Kind == "gate-cancelled").Details!["reason"]);

        var recovered = new CancellationTransport(request => new BuildTestGateResult(
            BuildTestGateVerdict.Ok, 0, 0, "", "passed", true, false)
            { ExpectedSha = request.ExpectedSha, TestedSha = request.ExpectedSha, GateId = request.GateId });
        var restarted = BuildOrchestrator(new CountingAspect().Cli, 3,
            new RemoteBuildTestGateRunner(recovered, NullLogger<RemoteBuildTestGateRunner>.Instance), clock, diagnostics);
        await restarted.TickOnceAsync(_workspace, CancellationToken.None);
        Assert.Equal(0, recovered.Calls);
        clock.Advance(PostProcessingOutcomeTaxonomy.RetryBackoff(1));
        await restarted.TickOnceAsync(_workspace, CancellationToken.None);
        Assert.Equal(1, recovered.Calls);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, slug)));
        Assert.DoesNotContain(ReviewDecisionLog.ReadAll(_workspace, Project),
            item => item.JobId == slug && item.Kind == ReviewDecisionKind.Escalate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_operator_gate_still_parks_without_retry(bool throwFromTransport)
    {
        const string slug = "cancel-operator";
        SeedReviewJobWithDone(slug, "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        WriteRemoteSubject(slug, new string('a', 40), "operator");
        var transport = new CancellationTransport(request =>
        {
            if (throwFromTransport) throw new OperationCanceledException();
            return RemoteBuildTestGateRunner.Failure(request,
                BuildTestGateFailureKind.Cancellation, "Operator stopped verification.")
                with { CancellationReason = GateCancellationReasons.Operator };
        });
        var diagnostics = new CancellationDiagnostics();
        var orchestrator = BuildOrchestrator(new CountingAspect().Cli, 3,
            new RemoteBuildTestGateRunner(transport, NullLogger<RemoteBuildTestGateRunner>.Instance), new CancellationClock(), diagnostics);
        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);
        Assert.Equal(1, transport.Calls);
        var folder = Path.Combine(_watchPath, TaskStates.Escalated, slug);
        Assert.True(Directory.Exists(folder));
        Assert.Equal(ReviewDecisionKind.Escalate,
            Assert.Single(ReviewDecisionLog.ReadAll(_workspace, Project), item => item.JobId == slug).Kind);
        Assert.Equal("operator", Assert.Single(new TimelineLog(NullLogger<TimelineLog>.Instance)
            .ReadAll(folder), e => e.Kind == "gate-cancelled").Details!["reason"]);
    }

    private sealed class CancellationClock : TimeProvider
    {
        private DateTimeOffset _now = new(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class CancellationDiagnostics : ILogger<TaskStateMachine>
    {
        public List<string> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception));
    }

    private sealed class CancellationTransport(Func<BuildTestGateRequest, BuildTestGateResult> run)
        : IRemoteGateTransport
    {
        public int Calls { get; private set; }
        public Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles, AgentStudio.Shared.BuildProfile? profile,
            PostStepMode mode, TimeSpan timeout, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(run(request));
        }
    }
}
