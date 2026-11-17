using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3878 managed database care in Studio: the backup policy per
/// environment in the desired state, the host's backup catalogue and the
/// visible last-backup time, overdue and failed status, operations that reach
/// the host only when allowed (a production restore only after an Owner's
/// approval), their expiry, alerts that become cards on the AGT-3223 path, the
/// control-plane liveness check, and size and connection metrics in the
/// AGT-3227 operations intake.
/// </summary>
public sealed class ManagedDatabaseBackupTests : IDisposable
{
    private const string Definition = """
        schemaVersion: 2
        stack: [node]
        toolVersions:
        commands:
          prepare: .agent-studio/prepare
          build: [npm run build]
          test: [npm test]
          lint: []
        testSuites:
        cachePaths: []
        capabilities: [git]
        environment:
        project:
          id: demo
        artifacts:
          - name: app
            type: container
            dockerfile: Dockerfile
            image: ghcr.io/example/app
        deploy:
          - step: container
            artifact: app
            target: run-host
            composeFile: /srv/apps/demo/compose.yml
            service: app
            healthUrl: https://stage.example.com/healthz
            healthTimeoutSeconds: 60
            environments: [staging]
          - step: container
            artifact: app
            target: run-host
            composeFile: /srv/apps/demo/compose.yml
            service: app
            healthUrl: https://www.example.com/healthz
            healthTimeoutSeconds: 60
            environments: [production]

        databases:
          - name: orders
            engine: postgres
            version: "16.4"
            environments: [staging, production]
          - name: audit
            engine: postgres
            version: "16.4"
            variable: AUDIT_DATABASE_URL
            environments: [staging]
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "managed-database-backup-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 16, 2, 0, 0, TimeSpan.Zero));
    private readonly string _artifact = "registry.example/app@sha256:" + new string('a', 64);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public void DesiredState_SendsEachEnvironmentsBackupPolicy_WithKindDefaultsAndDeclaredOverrides()
    {
        var fixture = Build(stagePolicy: """{ "backupIntervalHours": 6, "backupRetentionDays": 14, "backupsKept": 5, "volumeAlertMb": 1024 }""");
        Deploy(fixture, "stage");
        Deploy(fixture, "production");

        var desired = fixture.Service.DesiredState("run-host").Environments.ToDictionary(item => item.EnvironmentId);

        Assert.All(desired["stage"].Databases!, item => Assert.Equal(new RunTargetBackupPolicy(6, 14, 5), item.Backup));
        Assert.Equal(new RunTargetBackupPolicy(24, 30, 7), Assert.Single(desired["production"].Databases!).Backup);
        var stage = fixture.Service.Declarations("demo")!.Single(item => item.Id == "stage").EffectiveDatabasePolicy;
        Assert.Equal(1024L * 1024 * 1024, stage.VolumeAlertFor("large"));
        Assert.Equal(5L * 1024 * 1024 * 1024, ManagedDatabaseEnvironmentPolicy.For("stage").VolumeAlertFor("small"));
    }

    [Theory]
    [InlineData("""{ "backupIntervalHours": 0 }""", "backupIntervalHours must be a whole number from 1 to 168")]
    [InlineData("""{ "backupsKept": 2.5 }""", "backupsKept must be a whole number")]
    [InlineData("""{ "keepForever": true }""", "unknown field 'keepForever'")]
    [InlineData("\"daily\"", "managedDatabases must be an object")]
    public void InvalidPolicies_MakeTheDeclarationUnusable(string policy, string error)
    {
        var fixture = Build(stagePolicy: policy);

        // Next to the broken file, schema 2 lists the stage it would have declared.
        var files = fixture.Service.DeclarationFiles("demo")!.Where(item => item.EnvironmentId == "stage").ToArray();

        Assert.All(files, item => Assert.Null(item.Declaration));
        Assert.Contains(files, item => item.Error!.Contains(error, StringComparison.Ordinal));
    }

    [Fact]
    public void HostReport_ShowsTheLastBackup_ThenOverdue_ThenFailed_AndIsSanitized()
    {
        var fixture = Build();
        Deploy(fixture, "stage");
        Observe(fixture, "orders", backups: []);
        Assert.Equal("pending", View(fixture, "orders").BackupStatus);

        var taken = Now;
        var hostile = new RunTargetBackupRecord("../../etc/passwd", Now, 1, "scheduled");
        var future = new RunTargetBackupRecord("20991231T000000Z-manual", Now.AddYears(70), -4, "manual");
        Observe(fixture, "orders", backups: [new(Id(taken), taken, 4096, "scheduled"), hostile, future]);

        // The future dump is clamped to the time Studio received the report.
        var view = View(fixture, "orders");
        Assert.Equal(("ok", Now), (view.BackupStatus, view.LastBackupAt));
        Assert.Equal(["20991231T000000Z-manual", Id(taken)], view.Backups.Select(item => item.Id));
        Assert.Equal((0L, Now), (view.Backups[0].Bytes, view.Backups[0].TakenAt));
        Assert.Equal(new ManagedDatabaseBackupPolicyView(24, 7, 3), view.BackupPolicy);
        Assert.Equal((true, 3, 100, "stage"), (view.Ready, view.Connections, view.MaxConnections, view.EnvironmentKind));

        _clock.Advance(TimeSpan.FromHours(26));
        Observe(fixture, "orders", backups: [new(Id(taken), taken, 4096, "scheduled")]);
        Assert.Equal("overdue", View(fixture, "orders").BackupStatus);

        Observe(fixture, "orders", backups: [new(Id(taken), taken, 4096, "scheduled")], attempt: Now,
            error: "pg_dump failed:\nno space left on device\u0007");
        var failed = View(fixture, "orders");
        Assert.Equal(("failed", "pg_dump failed: no space left on device"), (failed.BackupStatus, failed.LastBackupError));
    }

    [Fact]
    public void BackupAndVerify_GoToTheHostAtOnce_AndTheHostsAnswerClosesThem()
    {
        var fixture = Build();
        Deploy(fixture, "stage");
        var taken = Now;
        Observe(fixture, "orders", backups: [new(Id(taken), taken, 4096, "scheduled")]);

        var backup = Request(fixture, "stage", "orders", new("backup", null, null, null));
        Assert.Equal(ManagedDatabaseOperationOutcome.Conflict, Request(fixture, "stage", "orders", new("verify", Id(taken), null, null)).Outcome);
        Assert.Equal(new RunTargetDatabaseOperation(backup.Operation!.Id, "demo", "stage", "orders", "backup", null, null),
            Assert.Single(fixture.Service.DesiredState("run-host").DatabaseOperations!));
        Assert.Empty(fixture.Service.DesiredState("other-host").DatabaseOperations!);

        // An answer from another host or for another database changes nothing.
        Answer(fixture, "other-host", backup.Operation.Id, "orders", "succeeded");
        Answer(fixture, "run-host", backup.Operation.Id, "audit", "succeeded");
        Assert.Equal("queued", View(fixture, "orders").Operations[0].State);
        Answer(fixture, "run-host", backup.Operation.Id, "orders", "succeeded", "Backup 20261116T020000Z-manual written (4096 bytes).");
        Assert.Empty(fixture.Service.DesiredState("run-host").DatabaseOperations!);

        _clock.Advance(TimeSpan.FromMinutes(1));
        var verify = Request(fixture, "stage", "orders", new("verify", Id(taken), null, null));
        Assert.Equal("queued", verify.Operation!.State);
        Answer(fixture, "run-host", verify.Operation.Id, "orders", "failed", "The backup could not be restored: pg_restore: error");

        var operations = View(fixture, "orders").Operations;
        Assert.Equal([("verify", "failed"), ("backup", "succeeded")], operations.Select(item => (item.Kind, item.State)));
        Assert.Equal("Backup 20261116T020000Z-manual written (4096 bytes).", operations[1].Message);
        Assert.Equal(ManagedDatabaseOperationOutcome.Conflict,
            Request(fixture, "stage", "orders", new("verify", "20200101T000000Z-manual", null, null)).Outcome);
        Assert.Equal(ManagedDatabaseOperationOutcome.Invalid, Request(fixture, "stage", "orders", new("vacuum", null, null, null)).Outcome);
        Assert.Equal(ManagedDatabaseOperationOutcome.NotFound, Request(fixture, "stage", "billing", new("backup", null, null, null)).Outcome);
    }

    [Fact]
    public void ProductionRestore_ReachesTheHostOnlyAfterAnOwnersApproval()
    {
        var fixture = Build();
        Deploy(fixture, "production");
        var taken = Now;
        Observe(fixture, "orders", "production", [new(Id(taken), taken, 4096, "scheduled")]);

        Assert.Equal(ManagedDatabaseOperationOutcome.Invalid,
            Request(fixture, "production", "orders", new("restore", Id(taken), "orders", "Orders")).Outcome);
        var requested = Request(fixture, "production", "orders", new("restore", Id(taken), "orders", "orders"));

        Assert.Equal(("awaiting-approval", "maintainer-1"), (requested.Operation!.State, requested.Operation.RequestedBy));
        Assert.Empty(fixture.Service.DesiredState("run-host").DatabaseOperations!);
        Assert.Equal(ManagedDatabaseOperationOutcome.Conflict,
            Request(fixture, "production", "orders", new("backup", null, null, null)).Outcome);

        var approved = fixture.Service.DecideDatabaseOperation("demo", "production", "orders", requested.Operation.Id, true, "owner-1");
        Assert.Equal(("queued", "owner-1"), (approved.Operation!.State, approved.Operation.ApprovedBy));
        Assert.Equal(new RunTargetDatabaseOperation(requested.Operation.Id, "demo", "production", "orders", "restore", Id(taken), "orders"),
            Assert.Single(fixture.Service.DesiredState("run-host").DatabaseOperations!));
        Assert.Equal(ManagedDatabaseOperationOutcome.Conflict,
            fixture.Service.DecideDatabaseOperation("demo", "production", "orders", requested.Operation.Id, true, "owner-1").Outcome);

        Answer(fixture, "run-host", requested.Operation.Id, "orders", "succeeded", "Backup restored.", "production");
        var second = Request(fixture, "production", "orders", new("restore", Id(taken), null, "orders"));
        var rejected = fixture.Service.DecideDatabaseOperation("demo", "production", "orders", second.Operation!.Id, false, "owner-1");
        Assert.Equal(("rejected", "Rejected by owner-1."), (rejected.Operation!.State, rejected.Operation.Message));
        Assert.Empty(fixture.Service.DesiredState("run-host").DatabaseOperations!);
        Assert.Equal(ManagedDatabaseOperationOutcome.NotFound,
            fixture.Service.DecideDatabaseOperation("demo", "production", "orders", "unknown", true, "owner-1").Outcome);
    }

    [Fact]
    public void Restore_OutsideProductionIsQueuedAtOnce_IntoTheSameOrAnotherDeclaredDatabase_AndRefusedWhileADeployIsOpen()
    {
        var fixture = Build();
        Deploy(fixture, "stage");
        var taken = Now;
        Observe(fixture, "orders", backups: [new(Id(taken), taken, 4096, "scheduled")]);

        Assert.Contains("Database audit is provisioning",
            Request(fixture, "stage", "orders", new("restore", Id(taken), "audit", "audit")).Error);
        Observe(fixture, "audit", backups: []);
        Assert.Contains("declares no database billing",
            Request(fixture, "stage", "orders", new("restore", Id(taken), "billing", "billing")).Error);

        var into = Request(fixture, "stage", "orders", new("restore", Id(taken), "audit", "audit"));
        Assert.Equal(("queued", "orders", "audit"), (into.Operation!.State, into.Operation.SourceDatabase, into.Operation.TargetDatabase));
        Assert.Equal("audit", Assert.Single(fixture.Service.DesiredState("run-host").DatabaseOperations!).TargetName);
        // The target shows the restore too, and is busy until it ends.
        Assert.Equal(into.Operation.Id, View(fixture, "audit").Operations[0].Id);
        Assert.Equal(ManagedDatabaseOperationOutcome.Conflict, Request(fixture, "stage", "audit", new("backup", null, null, null)).Outcome);
        Answer(fixture, "run-host", into.Operation.Id, "orders", "succeeded");

        Assert.NotNull(fixture.Service.QueueDeployment("demo", Declaration(fixture, "stage"), _artifact, "operator", rollback: false));
        Assert.Contains("deploy of this environment is open",
            Request(fixture, "stage", "orders", new("restore", Id(taken), null, "orders")).Error);
    }

    [Fact]
    public void Operations_WithoutAnApprovalOrAHostAnswer_EndInTime()
    {
        var fixture = Build();
        Deploy(fixture, "stage");
        Deploy(fixture, "production");
        var taken = Now;
        Observe(fixture, "orders", backups: [new(Id(taken), taken, 4096, "scheduled")]);
        Observe(fixture, "orders", "production", [new(Id(taken), taken, 4096, "scheduled")]);
        var backup = Request(fixture, "stage", "orders", new("backup", null, null, null)).Operation!;
        var restore = Request(fixture, "production", "orders", new("restore", Id(taken), null, "orders")).Operation!;

        _clock.Advance(TimeSpan.FromHours(7));
        Assert.Empty(fixture.Service.DesiredState("run-host").DatabaseOperations!);
        Assert.Equal(("failed", "The run host did not report a result within 6 hours."),
            (View(fixture, "orders").Operations[0].State, View(fixture, "orders").Operations[0].Message));
        Answer(fixture, "run-host", backup.Id, "orders", "succeeded");
        Assert.Equal("failed", View(fixture, "orders").Operations[0].State);

        _clock.Advance(TimeSpan.FromDays(7));
        fixture.Service.DesiredState("run-host");
        var expired = View(fixture, "orders", "production").Operations.Single(item => item.Id == restore.Id);
        Assert.Equal(("rejected", "Not approved within 7 days."), (expired.State, expired.Message));
    }

    [Fact]
    public void OverdueBackup_OpensOneCardOnTheHealthPath_AndCommentsWhenItRecovers()
    {
        var fixture = Build();
        Deploy(fixture, "stage");
        var taken = Now;
        Observe(fixture, "orders", backups: [new(Id(taken), taken, 4096, "scheduled")]);
        Observe(fixture, "audit", backups: [new(Id(taken), taken, 4096, "scheduled")]);
        Assert.Equal(0, fixture.Alerts.Run());

        _clock.Advance(TimeSpan.FromHours(25.5));
        Observe(fixture, "orders", backups: [new(Id(taken), taken, 4096, "scheduled")]);
        Observe(fixture, "audit", backups: [new(Id(Now), Now, 4096, "scheduled")]);
        Assert.Equal(1, fixture.Alerts.Run());
        Assert.Equal(0, fixture.Alerts.Run());

        var card = Assert.Single(fixture.Cards());
        Assert.Equal(("Database backup overdue: Stage database orders", TaskStates.Backlog), (card.Title, card.State));
        Assert.Contains(HealthAlerts.Tag, card.Tags);
        var alert = Assert.Single(fixture.Activity(), item => item.Topic == OrchestratorLogTopics.EnvironmentIncidentOpened);
        Assert.Equal((OrchestratorLogKinds.Alert, card.Id), (alert.Kind, alert.JobId));
        Assert.Contains("Backups run every 24 h, but the last backup is from 2026-11-16 02:00 UTC.", alert.Reasoning);

        Observe(fixture, "orders", backups: [new(Id(taken), taken, 4096, "scheduled"), new(Id(Now), Now, 4096, "scheduled")]);
        fixture.Alerts.Run();
        Assert.Contains(fixture.Comments(card), item => item.Summary.StartsWith("Database backup overdue recovered", StringComparison.Ordinal));
        Assert.Single(fixture.Activity(), item => item.Topic == OrchestratorLogTopics.EnvironmentIncidentRecovered);

        // The same condition later comments on the card that is still open instead of creating another.
        var last = Now;
        _clock.Advance(TimeSpan.FromHours(25.5));
        Observe(fixture, "orders", backups: [new(Id(last), last, 4096, "scheduled")]);
        Observe(fixture, "audit", backups: [new(Id(Now), Now, 4096, "scheduled")]);
        fixture.Alerts.Run();
        Assert.Single(fixture.Cards());
        Assert.Contains(fixture.Comments(card), item => item.Summary.StartsWith("Database backup overdue again", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedBackup_AndAFullVolume_RaiseTheirOwnCards()
    {
        var fixture = Build();
        Deploy(fixture, "stage");
        var taken = Now;
        Observe(fixture, "orders", backups: [new(Id(taken), taken, 4096, "scheduled")], attempt: Now.AddMinutes(1),
            error: "pg_dump failed: connection refused", volumeBytes: 6L * 1024 * 1024 * 1024);

        Assert.Equal(2, fixture.Alerts.Run());

        var titles = fixture.Cards().Select(item => item.Title).Order().ToArray();
        Assert.Equal(["Database backup failed: Stage database orders", "Database volume above its alert threshold: Stage database orders"],
            titles);
        Assert.Equal(["backup-failed", "volume-threshold"],
            fixture.Load().Databases.Single(item => item.Name == "orders").Alerts.Select(item => item.Kind).Order());
    }

    [Fact]
    public async Task Liveness_IsAControlPlaneCheckPerDatabase_ThatFailsWhenTheHostsReportsStop()
    {
        var fixture = Build();
        var stage = Declaration(fixture, "stage");
        Assert.Contains(HealthCheckDeclarations.Effective(stage), item => item.Id == "database-orders" && item.Kind == "database"
            && item.Target == "database orders");
        Assert.Equal(HealthSample.NotApplicable, fixture.Service.DatabaseLivenessSample("demo", "stage", "orders", _clock.GetUtcNow()).Error);
        Assert.False(new HealthSample(_clock.GetUtcNow(), false, null, null, HealthSample.NotApplicable).MeasuresAvailability);

        Deploy(fixture, "stage");
        Observe(fixture, "orders", backups: []);
        Assert.True(fixture.Service.DatabaseLivenessSample("demo", "stage", "orders", _clock.GetUtcNow()).Ok);
        Observe(fixture, "orders", backups: [], ready: false, state: "unhealthy");
        Assert.Equal("database-not-ready", fixture.Service.DatabaseLivenessSample("demo", "stage", "orders", _clock.GetUtcNow()).Error);

        // The host goes silent: three stale samples open an AGT-3223 incident with its card.
        Observe(fixture, "orders", backups: []);
        var health = new EnvironmentHealthService(fixture.Scanner, fixture.Service, new HealthCheckStore(fixture.Configuration),
            new UpProbe(_clock), new HealthAlerts(fixture.Scanner, fixture.Mutations, fixture.Timeline, fixture.Log, _clock,
                NullLogger<HealthAlerts>.Instance), fixture.Configuration, _clock, NullLogger<EnvironmentHealthService>.Instance);
        // Fresh until three minutes after the last report, then three stale samples in a row.
        for (var minute = 0; minute < 8; minute++)
        {
            await health.RunDueAsync();
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        var check = health.Read("demo")!.Environments.Single(item => item.EnvironmentId == "stage").Checks
            .Single(item => item.Id == "database-orders");
        Assert.Equal(("down", "database-report-stale"), (check.Status, check.LastSample!.Error));
        var card = Assert.Single(fixture.Cards(), item => item.Id.StartsWith("health-stage-", StringComparison.Ordinal));
        Assert.Contains("database-orders", File.ReadAllText(Path.Combine(card.FolderPath, "prompt.md")));
    }

    [Fact]
    public void HostReports_ReachTheEnvironmentsOperationsIntake_AsGauges()
    {
        var fixture = Build();
        Deploy(fixture, "stage");
        var queue = new TelemetryIntakeQueue(new TelemetryOptions());
        var metrics = new ManagedDatabaseMetrics(queue, _clock, NullLogger<ManagedDatabaseMetrics>.Instance);
        var measured = new List<(string Project, ManagedDatabaseRecord Record)>();
        var taken = Now.AddHours(-2);

        _clock.Advance(TimeSpan.FromMinutes(2));
        fixture.Service.RecordObserved("run-host", new([], [Observation("orders", [new(Id(taken), taken, 8192, "scheduled")])]), measured);
        metrics.Publish(measured);

        Assert.True(queue.TryDequeue(out var batch));
        Assert.Equal(new TelemetryEnvironmentKey("demo", "stage"), batch.Environment);
        var values = batch.Metrics.ToDictionary(item => item.Name, item => item.Value);
        Assert.Equal(2048d * 1024, values[ManagedDatabaseMetrics.VolumeSize]);
        Assert.Equal((3d, 100d, 1d), (values[ManagedDatabaseMetrics.Backends], values[ManagedDatabaseMetrics.ConnectionMax],
            values[ManagedDatabaseMetrics.Ready]));
        Assert.Equal((2 * 3600d + 120, 8192d), (values[ManagedDatabaseMetrics.BackupAge], values[ManagedDatabaseMetrics.BackupSize]));
        Assert.All(batch.Metrics, item => Assert.Equal("orders", item.Attributes.Values["db.namespace"]));

        // An unchanged report within a minute adds nothing.
        measured.Clear();
        _clock.Advance(TimeSpan.FromSeconds(15));
        fixture.Service.RecordObserved("run-host", new([], [Observation("orders", [new(Id(taken), taken, 8192, "scheduled")])]), measured);
        Assert.Empty(measured);
    }

    private static string Id(DateTime at, string kind = "scheduled")
        => at.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture) + "-" + kind;

    private ManagedDatabaseView View(Fixture fixture, string name, string environment = "stage")
        => fixture.Service.ManagedDatabases("demo", environment)!.Single(item => item.Name == name);

    private static ManagedDatabaseOperationDecision Request(Fixture fixture, string environment, string name,
        ManagedDatabaseOperationRequest request)
        => fixture.Service.RequestDatabaseOperation("demo", environment, name, request, "maintainer-1");

    private void Answer(Fixture fixture, string host, string id, string name, string state, string? message = null,
        string environment = "stage")
        => fixture.Service.RecordObserved(host, new([], [], [new(id, "demo", environment, name, state, message, Now)]));

    private RunTargetDatabaseObservation Observation(string name, IReadOnlyList<RunTargetBackupRecord> backups,
        string state = "running", bool ready = true, DateTime? attempt = null, string? error = null, long volumeBytes = 2048 * 1024,
        string environment = "stage")
        => new("demo", environment, name, state, "16.4", volumeBytes, Now, ready, 3, 100, backups, attempt, error);

    private void Observe(Fixture fixture, string name, string environment = "stage", IReadOnlyList<RunTargetBackupRecord>? backups = null,
        string state = "running", bool ready = true, DateTime? attempt = null, string? error = null, long volumeBytes = 2048 * 1024)
    {
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, fixture.Service.RecordObserved("run-host",
            new([], [Observation(name, backups ?? [], state, ready, attempt, error, volumeBytes, environment)])));
    }

    private static EnvironmentDeclaration Declaration(Fixture fixture, string environment)
        => fixture.Service.Declarations("demo")!.Single(item => item.Id == environment);

    private void Deploy(Fixture fixture, string environment)
    {
        Assert.NotNull(fixture.Service.QueueDeployment("demo", Declaration(fixture, environment), _artifact, "operator", rollback: false));
        var spec = fixture.Service.DesiredState("run-host").Environments.Single(item => item.EnvironmentId == environment);
        Assert.True(fixture.Service.Report("demo", spec.JobId!, new(spec.LeaseToken!, true, "health-ok")));
    }

    private Fixture Build(string? stagePolicy = null)
    {
        var watch = Path.Combine(_root, "workspace", "projects", "demo");
        foreach (var lane in TaskStates.All) Directory.CreateDirectory(Path.Combine(watch, lane));
        var repository = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repository, ".agent-studio"));
        Git(repository, "init", "-b", "develop");
        Git(repository, "config", "user.email", "test@example.com");
        Git(repository, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(repository, "initial"), "initial");
        Git(repository, "add", ".");
        Git(repository, "commit", "-m", "initial");
        var declarations = Path.Combine(repository, "docs", "deployments", "environments");
        Directory.CreateDirectory(declarations);
        foreach (var (id, kind, domain) in new[] { ("stage", "stage", "stage.example.com"), ("production", "production", "www.example.com") })
        {
            var text = JsonSerializer.Serialize(new
            {
                id, name = id == "stage" ? "Stage" : "Production", kind, targetHostId = "run-host",
                address = $"https://{domain}", template = "container", destination = "edge",
                healthUrl = $"https://{domain}/healthz", secretRefs = Array.Empty<string>(), domains = new[] { domain },
            });
            if (id == "stage" && stagePolicy is not null) text = text[..^1] + ", \"managedDatabases\": " + stagePolicy + "}";
            File.WriteAllText(Path.Combine(declarations, id + ".json"), text);
        }
        File.WriteAllText(Path.Combine(repository, ".agent-studio", "project.yml"), Definition);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["WatchPaths:0:Name"] = "demo", ["WatchPaths:0:Path"] = watch,
            ["WatchPaths:0:RootPath"] = repository, ["WatchPaths:0:RepositoryPath"] = repository,
        }).Build();
        var scanner = new TaskScannerService(configuration, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);
        settings.SetRunTargetDomainSuffixes("demo", ["example.com"]);
        var service = new ProjectEnvironmentService(configuration, scanner, git, settings, _clock);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var mutations = new TaskMutationService(scanner,
            new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance, timeline: timeline);
        var log = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var alerts = new ManagedDatabaseAlerts(scanner, mutations, timeline, log, service, _clock,
            NullLogger<ManagedDatabaseAlerts>.Instance);
        return new(service, alerts, scanner, mutations, timeline, log, configuration, watch,
            Path.Combine(_root, "workspace", "deployments", "demo.json"));
    }

    private static void Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
    }

    private sealed record Fixture(ProjectEnvironmentService Service, ManagedDatabaseAlerts Alerts, TaskScannerService Scanner,
        TaskMutationService Mutations, TimelineLog Timeline, OrchestratorLog Log, IConfiguration Configuration, string WatchPath,
        string StatePath)
    {
        public IReadOnlyList<TaskInfo> Cards() => Scanner.ScanAllJobs().Where(card => card.Tags.Contains(HealthAlerts.Tag)).ToArray();

        public IReadOnlyList<TimelineEvent> Comments(TaskInfo card) => Timeline.ReadAll(card.FolderPath)
            .Where(item => item.Kind == TimelineEventKinds.EnvironmentHealthUpdated).ToArray();

        public IReadOnlyList<OrchestratorLogEntry> Activity() => Log.Read(WatchPath);

        public ProjectEnvironmentService.State Load()
            => JsonSerializer.Deserialize<ProjectEnvironmentService.State>(File.ReadAllText(StatePath), JsonSerializerOptions.Web)!;
    }

    /// <summary>Every network check answers; only the database check reads the host's reports.</summary>
    private sealed class UpProbe(TimeProvider clock) : IHealthCheckProbe
    {
        public Task<HealthSample> ProbeAsync(HealthCheckDefinition check, CancellationToken cancellationToken)
            => Task.FromResult(new HealthSample(clock.GetUtcNow(), true, 5, 200, null));
    }
}
