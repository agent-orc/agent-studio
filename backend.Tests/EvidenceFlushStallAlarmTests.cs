using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3000: 36 hours of <c>workspace-evidence-flush-failed</c> warnings raised
/// no alarm. A flush failure that repeats for more than 15 minutes now raises
/// the <c>evidence-flush-stalled</c> pipeline health alarm with the repository
/// and the error, and the next successful flush clears it.
/// </summary>
public sealed class EvidenceFlushStallAlarmTests
{
    private static readonly DateTime T0 = new(2026, 9, 27, 15, 32, 0, DateTimeKind.Utc);
    private const string Repo = "/srv/agent-taskboard-workspace";
    private const string LockError =
        "git-add: fatal: Unable to create '/srv/agent-taskboard-workspace/.git/index.lock': File exists.";

    // ---- Tracker matrix ------------------------------------------------------

    [Fact]
    public void Failures_within_fifteen_minutes_do_not_alarm()
    {
        var tracker = new EvidenceFlushStallTracker();

        Assert.Equal(EvidenceFlushStallSignal.None, tracker.Observe(Repo, false, LockError, T0).Signal);
        Assert.Equal(EvidenceFlushStallSignal.None, tracker.Observe(Repo, false, LockError, T0.AddMinutes(7)).Signal);
        Assert.Equal(EvidenceFlushStallSignal.None, tracker.Observe(Repo, false, LockError, T0.AddMinutes(15)).Signal);
    }

    [Fact]
    public void Failure_repeating_past_fifteen_minutes_alarms_with_repository_and_error()
    {
        var tracker = new EvidenceFlushStallTracker();
        tracker.Observe(Repo, false, "git-add: earlier", T0);
        tracker.Observe(Repo, false, "git-add: earlier", T0.AddMinutes(8));

        var (signal, stall) = tracker.Observe(Repo, false, LockError, T0.AddMinutes(16));

        Assert.Equal(EvidenceFlushStallSignal.Stalled, signal);
        Assert.NotNull(stall);
        Assert.Equal(Repo, stall.GitRoot);
        Assert.Equal(LockError, stall.Error);
        Assert.Equal(T0, stall.FirstFailedAtUtc);
        Assert.Equal(3, stall.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromMinutes(16), stall.FailingFor);
    }

    [Fact]
    public void Success_resets_the_window_and_reports_recovery_only_after_an_alarm()
    {
        var tracker = new EvidenceFlushStallTracker();
        tracker.Observe(Repo, false, LockError, T0);
        Assert.Equal(EvidenceFlushStallSignal.None, tracker.Observe(Repo, true, null, T0.AddMinutes(5)).Signal);

        // The window restarted at the next failure, not at T0.
        tracker.Observe(Repo, false, LockError, T0.AddMinutes(10));
        Assert.Equal(EvidenceFlushStallSignal.None, tracker.Observe(Repo, false, LockError, T0.AddMinutes(20)).Signal);
        Assert.Equal(EvidenceFlushStallSignal.Stalled, tracker.Observe(Repo, false, LockError, T0.AddMinutes(26)).Signal);

        Assert.Equal(EvidenceFlushStallSignal.Recovered, tracker.Observe(Repo, true, null, T0.AddMinutes(27)).Signal);
        Assert.Equal(EvidenceFlushStallSignal.None, tracker.Observe(Repo, true, null, T0.AddMinutes(28)).Signal);
    }

    [Fact]
    public void Repositories_are_tracked_independently()
    {
        var tracker = new EvidenceFlushStallTracker();
        tracker.Observe(Repo, false, LockError, T0);
        tracker.Observe("/srv/other", false, LockError, T0.AddMinutes(10));

        Assert.Equal(EvidenceFlushStallSignal.Stalled, tracker.Observe(Repo, false, LockError, T0.AddMinutes(16)).Signal);
        Assert.Equal(EvidenceFlushStallSignal.None, tracker.Observe("/srv/other", false, LockError, T0.AddMinutes(16)).Signal);
    }

    // ---- Worker wiring -------------------------------------------------------

    [Fact]
    public void Worker_forwards_a_stalled_flush_and_its_recovery_to_the_alarm()
    {
        var time = new FakeTimeProvider(T0);
        var alarm = new RecordingAlarm();
        var worker = Worker(time, alarm);

        worker.ObserveStall(Failed());
        time.Advance(TimeSpan.FromMinutes(10));
        worker.ObserveStall(Failed());
        Assert.Empty(alarm.Stalls);

        time.Advance(TimeSpan.FromMinutes(6));
        worker.ObserveStall(Failed());
        var stall = Assert.Single(alarm.Stalls);
        Assert.Equal(Repo, stall.GitRoot);
        Assert.Contains("index.lock", stall.Error);

        time.Advance(TimeSpan.FromMinutes(1));
        worker.ObserveStall(new WorkspaceEvidenceFlushResult(Repo, 1, WorkspaceArtifactCommitResult.Committed("abc1234", 0, "evidence")));
        Assert.Equal([Repo], alarm.Recovered);
    }

    // ---- Pipeline health surface --------------------------------------------

    [Fact]
    public void Stalled_flush_raises_the_alarm_in_the_feed_and_snapshot_until_recovery()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "evidence-stall-" + Guid.NewGuid().ToString("N"));
        var watchPath = Path.Combine(workspace, "projects", "agent-taskboard");
        Directory.CreateDirectory(watchPath);
        try
        {
            var (service, orchestratorLog) = HealthService(watchPath);
            var now = new FakeTimeProvider(new DateTimeOffset(T0)).GetUtcNow().UtcDateTime;
            var stall = new EvidenceFlushStall(workspace, LockError, now.AddMinutes(-16), now, 9);

            service.EvidenceFlushStalled(stall);
            service.EvidenceFlushStalled(stall with { ConsecutiveFailures = 10 });

            var entry = Assert.Single(orchestratorLog.Read(watchPath));
            Assert.Equal(OrchestratorLogKinds.Alert, entry.Kind);
            Assert.Equal(OrchestratorLogTopics.PipelineHealth, entry.Topic);
            Assert.Contains("evidence flush failing for 16 min", entry.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(workspace, entry.Reasoning);
            Assert.Contains("index.lock", entry.Reasoning);

            var snapshot = service.Snapshot("agent-taskboard", now);
            Assert.NotNull(snapshot);
            Assert.Equal("alarm", snapshot.Status);
            var alert = Assert.Single(snapshot.Alerts);
            Assert.Equal(PipelineHealthConventions.EvidenceFlushStalledKind, alert.Kind);
            Assert.Equal("evidence-flush-stalled", alert.Kind);
            Assert.Contains("10 consecutive", alert.Detail);
            Assert.Equal(workspace, alert.Repository);

            service.EvidenceFlushRecovered(workspace, now.AddMinutes(1));

            var recovered = service.Snapshot("agent-taskboard", now.AddMinutes(1));
            Assert.NotNull(recovered);
            Assert.Empty(recovered.Alerts);
            Assert.Equal("healthy", recovered.Status);

            // Re-armed: the next stall alarms again without waiting out the cooldown.
            service.EvidenceFlushStalled(stall with { LastFailedAtUtc = now.AddMinutes(20) });
            Assert.Equal(2, orchestratorLog.Read(watchPath).Count);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Stall_in_a_repository_without_projects_does_not_alarm_unrelated_projects()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "evidence-stall-" + Guid.NewGuid().ToString("N"));
        var watchPath = Path.Combine(workspace, "projects", "demo");
        Directory.CreateDirectory(watchPath);
        try
        {
            var (service, orchestratorLog) = HealthService(watchPath);
            var now = new FakeTimeProvider(new DateTimeOffset(T0)).GetUtcNow().UtcDateTime;

            service.EvidenceFlushStalled(new EvidenceFlushStall(workspace + "-elsewhere", LockError, now.AddMinutes(-20), now, 4));

            Assert.Empty(orchestratorLog.Read(watchPath));
            Assert.Empty(service.Snapshot("demo", now)!.Alerts);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    // ---- helpers ------------------------------------------------------------

    private static WorkspaceEvidenceFlushResult Failed()
        => new(Repo, 1, WorkspaceArtifactCommitResult.Failed(
            "git-add",
            "fatal: Unable to create '/srv/agent-taskboard-workspace/.git/index.lock': File exists."));

    private static WorkspaceEvidenceWorker Worker(FakeTimeProvider time, IEvidenceFlushAlarm alarm)
    {
        var config = new ConfigurationBuilder().Build();
        var commit = new WorkspaceArtifactCommitService(config, NullLogger<WorkspaceArtifactCommitService>.Instance);
        var scanner = new TaskScannerService(
            config,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        return new WorkspaceEvidenceWorker(
            new WorkspaceEvidenceQueue(),
            new WorkspaceEvidenceBatcher(commit, config, NullLogger.Instance, time),
            scanner,
            config,
            NullLogger<WorkspaceEvidenceWorker>.Instance,
            time,
            alarm);
    }

    private static (PipelineHealthService Service, OrchestratorLog Log) HealthService(string watchPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = Path.GetFileName(watchPath),
                ["WatchPaths:0:Path"] = watchPath,
                ["WatchPaths:0:RootPath"] = watchPath,
            })
            .Build();
        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var service = new PipelineHealthService(
            new PipelineHealthDetector(),
            scanner,
            new TimelineLog(NullLogger<TimelineLog>.Instance),
            orchestratorLog,
            NullLogger<PipelineHealthService>.Instance);
        return (service, orchestratorLog);
    }

    private sealed class RecordingAlarm : IEvidenceFlushAlarm
    {
        public List<EvidenceFlushStall> Stalls { get; } = [];
        public List<string> Recovered { get; } = [];

        public void EvidenceFlushStalled(EvidenceFlushStall stall) => Stalls.Add(stall);

        public void EvidenceFlushRecovered(string gitRoot, DateTime recoveredAtUtc) => Recovered.Add(gitRoot);
    }
}
