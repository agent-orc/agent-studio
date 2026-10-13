using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3095: release 0.9.4 on agent-runner-01 settled each RunAttempt before
/// uploading its result files, and the Task Server refused every upload with a
/// silent 409. Each card got its own partial event and nothing raised one fleet
/// signal. Three distinct attempts of one runner refused within an hour now raise
/// one <c>artifact-upload-after-settlement</c> alarm, and the runner's next
/// accepted artifact write clears it.
/// </summary>
public sealed class ArtifactUploadAfterSettlementAlarmTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 18, 20, 22, TimeSpan.Zero);
    private const string Runner = "agent-runner-01";
    private const string Release = "20260928T1211Z-v0.9.4-6759fe303";

    // ---- Tracker matrix ------------------------------------------------------

    [Fact]
    public void Two_attempts_with_five_rejections_each_do_not_alarm()
    {
        var tracker = new ArtifactUploadAfterSettlementTracker();
        var now = T0.UtcDateTime;

        for (var write = 0; write < 5; write++)
        {
            Assert.Equal(ArtifactUploadAfterSettlementSignal.None,
                tracker.ObserveRejected(Runner, "run-1", "AGT-1", now.AddSeconds(write)).Signal);
            Assert.Equal(ArtifactUploadAfterSettlementSignal.None,
                tracker.ObserveRejected(Runner, "run-2", "AGT-2", now.AddMinutes(10).AddSeconds(write)).Signal);
        }
    }

    [Fact]
    public void Third_attempt_alarms_once_and_a_fourth_does_not_alarm_again()
    {
        var tracker = new ArtifactUploadAfterSettlementTracker();
        var now = T0.UtcDateTime;
        tracker.ObserveRejected(Runner, "run-1", "AGT-1", now);
        tracker.ObserveRejected(Runner, "run-1", "AGT-1", now.AddSeconds(1));
        tracker.ObserveRejected(Runner, "run-2", "AGT-2", now.AddMinutes(20));

        var (signal, attempts) = tracker.ObserveRejected(Runner, "run-3", "AGT-3", now.AddMinutes(40));

        Assert.Equal(ArtifactUploadAfterSettlementSignal.Alarm, signal);
        Assert.Equal(["AGT-1", "AGT-2", "AGT-3"], attempts.Select(attempt => attempt.TaskKey));
        Assert.Equal(2, attempts[0].RejectedWrites);
        Assert.Equal(ArtifactUploadAfterSettlementSignal.None,
            tracker.ObserveRejected(Runner, "run-3", "AGT-3", now.AddMinutes(41)).Signal);
        Assert.Equal(ArtifactUploadAfterSettlementSignal.None,
            tracker.ObserveRejected(Runner, "run-4", "AGT-4", now.AddMinutes(50)).Signal);
    }

    [Fact]
    public void Accepted_write_clears_the_alarm_and_re_arms_the_tracker()
    {
        var tracker = new ArtifactUploadAfterSettlementTracker();
        var now = T0.UtcDateTime;
        Assert.Equal(ArtifactUploadAfterSettlementSignal.None, tracker.ObserveAccepted(Runner));
        tracker.ObserveRejected(Runner, "run-1", "AGT-1", now);
        tracker.ObserveRejected(Runner, "run-2", "AGT-2", now.AddMinutes(1));
        tracker.ObserveRejected(Runner, "run-3", "AGT-3", now.AddMinutes(2));

        Assert.Equal(ArtifactUploadAfterSettlementSignal.Recovered, tracker.ObserveAccepted(Runner));
        Assert.Equal(ArtifactUploadAfterSettlementSignal.None, tracker.ObserveAccepted(Runner));

        // Re-armed and counted from zero: the old attempts no longer add up.
        Assert.Equal(ArtifactUploadAfterSettlementSignal.None,
            tracker.ObserveRejected(Runner, "run-4", "AGT-4", now.AddMinutes(3)).Signal);
        Assert.Equal(ArtifactUploadAfterSettlementSignal.None,
            tracker.ObserveRejected(Runner, "run-5", "AGT-5", now.AddMinutes(4)).Signal);
        Assert.Equal(ArtifactUploadAfterSettlementSignal.Alarm,
            tracker.ObserveRejected(Runner, "run-6", "AGT-6", now.AddMinutes(5)).Signal);
    }

    [Fact]
    public void Attempts_rejected_more_than_an_hour_apart_do_not_add_up()
    {
        var tracker = new ArtifactUploadAfterSettlementTracker();
        var now = T0.UtcDateTime;
        tracker.ObserveRejected(Runner, "run-1", "AGT-1", now);
        tracker.ObserveRejected(Runner, "run-2", "AGT-2", now.AddMinutes(61));

        Assert.Equal(ArtifactUploadAfterSettlementSignal.None,
            tracker.ObserveRejected(Runner, "run-3", "AGT-3", now.AddMinutes(122)).Signal);
    }

    [Fact]
    public void Runners_are_tracked_independently()
    {
        var tracker = new ArtifactUploadAfterSettlementTracker();
        var now = T0.UtcDateTime;
        tracker.ObserveRejected(Runner, "run-1", "AGT-1", now);
        tracker.ObserveRejected(Runner, "run-2", "AGT-2", now);

        Assert.Equal(ArtifactUploadAfterSettlementSignal.None,
            tracker.ObserveRejected("kori-review", "run-3", "AGT-3", now).Signal);
        Assert.Equal(ArtifactUploadAfterSettlementSignal.None, tracker.ObserveAccepted("kori-review"));
        Assert.Equal(ArtifactUploadAfterSettlementSignal.Alarm,
            tracker.ObserveRejected(Runner, "run-4", "AGT-4", now).Signal);
    }

    // ---- Sensor wiring -------------------------------------------------------

    [Fact]
    public void Sensor_names_the_runner_release_and_project_of_the_crossing_attempt()
    {
        var time = new FakeTimeProvider(T0);
        var alarm = new RecordingAlarm();
        var registry = new V1ReviewExecutorRegistry(time);
        registry.Register(Runner, new Contract.RegisterRunnerRequest(
            Runner,
            Runner,
            "instance-1",
            "0.9.4",
            Contract.TaskServerProtocol.Current,
            [Contract.ReviewCapabilities.CodingExecutor],
            Release: new Contract.RunnerReleaseIdentityDto(Release, "0.9.4", "sha")));
        var sensor = new ArtifactUploadAfterSettlementSensor(time, alarm, registry);

        sensor.Rejected(Runner, "run-1", "AGT-1", "alpha", "/srv/alpha");
        time.Advance(TimeSpan.FromMinutes(5));
        sensor.Rejected(Runner, "run-2", "AGT-2", "alpha", "/srv/alpha");
        Assert.Empty(alarm.Raised);

        time.Advance(TimeSpan.FromMinutes(5));
        sensor.Rejected(Runner, "run-3", "AGT-3", "beta", "/srv/beta");
        sensor.Rejected(Runner, "run-4", "AGT-4", "beta", "/srv/beta");

        var raised = Assert.Single(alarm.Raised);
        Assert.Equal(Runner, raised.RunnerId);
        Assert.Equal(Release, raised.ReleaseId);
        Assert.Equal("beta", raised.Project);
        Assert.Equal("/srv/beta", raised.WatchPath);
        Assert.Equal(time.GetUtcNow().UtcDateTime, raised.DetectedAtUtc);
        Assert.Equal(["AGT-1", "AGT-2", "AGT-3"], raised.Attempts.Select(attempt => attempt.TaskKey));

        sensor.Accepted(Runner);
        Assert.Equal([Runner], alarm.Recovered);
    }

    [Fact]
    public void Sensor_without_a_registered_release_leaves_the_release_unknown()
    {
        var time = new FakeTimeProvider(T0);
        var alarm = new RecordingAlarm();
        var sensor = new ArtifactUploadAfterSettlementSensor(time, alarm, new V1ReviewExecutorRegistry(time));

        sensor.Rejected(Runner, "run-1", "AGT-1", "alpha", null);
        sensor.Rejected(Runner, "run-2", "AGT-2", "alpha", null);
        sensor.Rejected(Runner, "run-3", "AGT-3", "alpha", null);

        Assert.Null(Assert.Single(alarm.Raised).ReleaseId);
    }

    // ---- Pipeline health surface --------------------------------------------

    [Fact]
    public void Rejections_raise_the_alarm_in_the_feed_and_snapshot_until_an_accepted_write()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "artifact-settled-" + Guid.NewGuid().ToString("N"));
        var watchPath = Path.Combine(workspace, "projects", "agent-taskboard");
        Directory.CreateDirectory(watchPath);
        try
        {
            var time = new FakeTimeProvider(T0);
            var (service, orchestratorLog) = HealthService(watchPath);
            var sensor = new ArtifactUploadAfterSettlementSensor(time, service);

            foreach (var (attempt, taskKey) in new[] { ("run-1", "AGT-2951"), ("run-2", "AGT-3074"), ("run-3", "AGT-3080") })
            {
                for (var write = 0; write < 4; write++)
                    sensor.Rejected(Runner, attempt, taskKey, "agent-taskboard", watchPath);
                time.Advance(TimeSpan.FromMinutes(10));
            }
            sensor.Rejected(Runner, "run-4", "AGT-3081", "agent-taskboard", watchPath);

            var entry = Assert.Single(orchestratorLog.Read(watchPath));
            Assert.Equal(OrchestratorLogKinds.Alert, entry.Kind);
            Assert.Equal(OrchestratorLogTopics.PipelineHealth, entry.Topic);
            Assert.Contains(Runner, entry.Summary);
            Assert.Contains("AGT-2951, AGT-3074, AGT-3080", entry.Summary);
            Assert.Contains("release unknown", entry.Reasoning);
            Assert.Contains("run-1 (4 write(s))", entry.Reasoning);

            var now = time.GetUtcNow().UtcDateTime;
            var snapshot = service.Snapshot("agent-taskboard", now);
            Assert.NotNull(snapshot);
            Assert.Equal("alarm", snapshot.Status);
            var alert = Assert.Single(snapshot.Alerts);
            Assert.Equal("artifact-upload-after-settlement", alert.Kind);
            Assert.Equal(PipelineHealthConventions.ArtifactUploadAfterSettlementKind, alert.Kind);
            Assert.Equal("AGT-3080", alert.JobId);

            sensor.Accepted(Runner);

            var recovered = service.Snapshot("agent-taskboard", now);
            Assert.NotNull(recovered);
            Assert.Empty(recovered.Alerts);
            Assert.Equal("healthy", recovered.Status);

            // Re-armed: the next regression alarms again without waiting out the cooldown.
            foreach (var attempt in new[] { "run-5", "run-6", "run-7" })
                sensor.Rejected(Runner, attempt, "AGT-" + attempt, "agent-taskboard", watchPath);
            Assert.Equal(2, orchestratorLog.Read(watchPath).Count);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    // ---- helpers ------------------------------------------------------------

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

    private sealed class RecordingAlarm : IArtifactUploadAfterSettlementAlarm
    {
        public List<ArtifactUploadAfterSettlement> Raised { get; } = [];
        public List<string> Recovered { get; } = [];

        public void ArtifactUploadsRejectedAfterSettlement(ArtifactUploadAfterSettlement rejection)
            => Raised.Add(rejection);

        public void ArtifactUploadsRecovered(string runnerId, DateTime recoveredAtUtc) => Recovered.Add(runnerId);
    }
}
