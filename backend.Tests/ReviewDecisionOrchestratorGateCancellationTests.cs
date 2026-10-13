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
    [Theory]
    [InlineData("delivery")]
    [InlineData("lane")]
    [InlineData("continue")]
    public async Task Cancelled_superseded_gate_does_not_escalate(string change)
    {
        const string slug = "cancel-superseded";
        SeedReviewJobWithDone(slug, "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        WriteRemoteSubject(slug, new string('a', 40), "old");
        var transport = new CancellationTransport(request =>
        {
            if (change == "delivery") WriteRemoteSubject(slug, new string('b', 40), "new");
            else if (change == "lane") Assert.Equal(MoveJobStatus.Success,
                _cancellationStateMachine!.MoveJob(slug, TaskStates.Ready, _watchPath).Status);
            else Assert.NotNull(_cancellationMutations!.SavePendingIntent(slug,
                ContinueModes.Continue, "Continue the delivery", "operator", null, _watchPath));
            throw new OperationCanceledException();
        });
        var diagnostics = new CancellationDiagnostics();
        var orchestrator = BuildOrchestrator(new CountingAspect().Cli, 3,
            new RemoteBuildTestGateRunner(transport, NullLogger<RemoteBuildTestGateRunner>.Instance),
            new CancellationClock(), diagnostics);
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

    [Fact]
    public async Task Cancelled_operator_gate_still_parks_without_retry()
    {
        const string slug = "cancel-operator";
        SeedReviewJobWithDone(slug, "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        WriteRemoteSubject(slug, new string('a', 40), "operator");
        var transport = new CancellationTransport(request => RemoteBuildTestGateRunner.Failure(request,
            BuildTestGateFailureKind.Cancellation, "Operator stopped verification.")
            with { CancellationReason = GateCancellationReasons.Operator });
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
