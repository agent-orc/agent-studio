using AgentRunner.RunTargets;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3878 managed database care on a fake run host: scheduled dumps outside
/// the volume, also while Studio is unreachable, retention that keeps a
/// minimum, readiness and connection checks, a verification restore into a
/// scratch container without a network, a restore that dumps the current data
/// first and replaces the live database only after the load succeeded, each
/// operation once per id, and deletion of the dumps with the data.
/// </summary>
public sealed class RunTargetDatabaseBackupTests : IDisposable
{
    private static readonly string Artifact = "registry.example/app@sha256:" + new string('a', 64);
    private const string PasswordRef = "secret://demo/stage/MANAGED_DB_ORDERS_PASSWORD";
    private const string AuditRef = "secret://demo/stage/MANAGED_DB_AUDIT_PASSWORD";

    private readonly TempWorkspace _workspace = new("run-target-backups");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 16, 2, 0, 0, TimeSpan.Zero));
    private readonly FakeRunTargetDocker _docker = new();
    private readonly List<string> _log = [];
    private readonly string _backups;
    private readonly RunTargetStateStore _store;
    private readonly RunTargetReconciler _reconciler;

    public RunTargetDatabaseBackupTests()
    {
        _store = new RunTargetStateStore(_workspace.Directory("state"), _log.Add);
        _backups = _workspace.Directory("backups");
        // Built at run time: no credential-shaped literal in the source.
        var password = "pw" + Guid.NewGuid().ToString("N");
        var credentials = new FakeRunTargetCredentials(new()
        {
            [$"demo/stage/{PasswordRef}"] = password, [$"demo/stage/{AuditRef}"] = password + "x",
        });
        _reconciler = new RunTargetReconciler(_docker, new FakeRunTargetSecrets([]),
            new RunTargetSecretFiles(_workspace.Directory("secrets-runtime"), _ => true), _store,
            new RunTargetReconcilerSettings(RunTargetOptions.DefaultEdgeImage, new RunTargetEdgeSettings(null, false, []),
                null, HealthAttempts: 3, BackupDirectory: _backups),
            _clock, (_, _) => Task.CompletedTask, _log.Add, credentials);
    }

    public void Dispose() => _workspace.Dispose();

    private static string AppKey => RunTargetNames.AppKey("demo", "stage");
    private static string OrdersKey => RunTargetNames.DatabaseKey(AppKey, "orders");
    private static string AuditKey => RunTargetNames.DatabaseKey(AppKey, "audit");

    private static RunTargetDatabaseSpec Orders(RunTargetBackupPolicy? policy = null)
        => new("orders", "postgres", "16.4", "small", new RunTargetResources(0.5, 512, 256), "DATABASE_URL", PasswordRef,
            policy ?? new RunTargetBackupPolicy(24, 7, 3));

    private static RunTargetDatabaseSpec Audit()
        => new("audit", "postgres", "16.4", "small", new RunTargetResources(0.5, 512, 256), "AUDIT_DATABASE_URL", AuditRef,
            new RunTargetBackupPolicy(24, 7, 3));

    private static RunTargetEnvironmentSpec Spec(params RunTargetDatabaseSpec[] databases)
        => new("demo", "stage", "stage", Artifact, 8080, "/healthz", ["stage.example.com"], "acme",
            new Dictionary<string, string>(), [], new RunTargetResources(0.5, 256, 128), null, null, false,
            Databases: databases);

    private Task<RunTargetCycleResult> Cycle(RunTargetEnvironmentSpec spec, params RunTargetDatabaseOperation[] operations)
        => Cycle(spec, null, operations);

    private Task<RunTargetCycleResult> Cycle(RunTargetEnvironmentSpec spec, RunTargetDatabaseDeletion[]? deletions,
        params RunTargetDatabaseOperation[] operations)
        => _reconciler.ReconcileAsync(new RunTargetDesiredState("run-host", _clock.GetUtcNow().UtcDateTime, [spec], [], [],
            deletions, operations), CancellationToken.None);

    private static RunTargetDatabaseOperation Operation(string id, string kind, string? backupId = null, string? target = null,
        string name = "orders")
        => new(id, "demo", "stage", name, kind, backupId, target);

    private RunTargetDatabaseState Stored(string key = "") => _store.Load().Databases[key.Length == 0 ? OrdersKey : key];

    [Fact]
    public async Task ANewDatabase_IsDumpedRightAway_IntoTheBackupDirectoryOutsideItsVolume()
    {
        var result = await Cycle(Spec(Orders()));

        var observed = Assert.Single(result.Databases!);
        var backup = Assert.Single(observed.Backups!);
        Assert.Equal(("scheduled", 4096L, _clock.GetUtcNow().UtcDateTime), (backup.Kind, backup.Bytes, backup.TakenAt));
        Assert.Equal("20261116T020000Z-scheduled", backup.Id);
        Assert.Null(observed.LastBackupError);
        Assert.Contains($"/backups/orders/{backup.Id}.dump", _docker.BackupFiles);
        // Written under a partial name, read back, and only then renamed.
        var steps = _docker.Execs.Where(line => line.StartsWith(OrdersKey + " pg_dump", StringComparison.Ordinal)
            || line.StartsWith(OrdersKey + " pg_restore --list", StringComparison.Ordinal)
            || line.StartsWith(OrdersKey + " mv", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, steps.Length);
        Assert.EndsWith($"--format=custom --compress=6 --file /backups/orders/{backup.Id}.dump.partial", steps[0]);
        Assert.EndsWith($"/backups/orders/{backup.Id}.dump.partial /backups/orders/{backup.Id}.dump", steps[2]);
        // The host directory is mounted next to the volume, not inside it.
        var compose = _docker.Ups.Last(item => item.Project == OrdersKey).ComposeText;
        Assert.Contains($"      - \"{Path.Combine(_backups, AppKey)}:/backups\"\n", compose);
        Assert.Contains($"      - \"data:{RunTargetNames.DatabaseDataPath}\"\n", compose);
        Assert.True(Directory.Exists(Path.Combine(_backups, AppKey, "orders")));
        Assert.False(Path.Combine(_backups, AppKey).StartsWith(_store.Directory, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Schedule_WaitsForTheInterval_AndKeepsDumpingWhileStudioIsUnreachable()
    {
        await Cycle(Spec(Orders(new RunTargetBackupPolicy(6, 7, 3))));
        _clock.Advance(TimeSpan.FromHours(5));
        await Cycle(Spec(Orders(new RunTargetBackupPolicy(6, 7, 3))));
        Assert.Single(Stored().Backups);

        _clock.Advance(TimeSpan.FromHours(1));
        await _reconciler.SuperviseOnlyAsync("connection refused", CancellationToken.None);

        Assert.Equal(["20261116T020000Z-scheduled", "20261116T080000Z-scheduled"], Stored().Backups.Select(item => item.Id));
        Assert.Equal(new RunTargetBackupPolicy(6, 7, 3), Stored().Backup);
    }

    [Fact]
    public async Task Retention_PrunesDumpsPastTheirDays_ButAlwaysKeepsTheMinimum()
    {
        var policy = new RunTargetBackupPolicy(24, 2, 2);
        for (var day = 0; day <= 5; day++)
        {
            await Cycle(Spec(Orders(policy)));
            _clock.Advance(TimeSpan.FromDays(1));
        }

        // Days 3, 4 and 5 are within two days of day 5; days 0 to 2 are pruned.
        Assert.Equal(["20261119T020000Z-scheduled", "20261120T020000Z-scheduled", "20261121T020000Z-scheduled"],
            Stored().Backups.Select(item => item.Id));
        Assert.Equal(3, _docker.BackupFiles.Count);
        Assert.Contains(_docker.Execs, line => line == $"{OrdersKey} rm -f /backups/orders/20261116T020000Z-scheduled.dump");

        // A host that cannot dump any more keeps the two newest, however old they get.
        _docker.FailExec = (_, command) => command[0] == "pg_dump";
        _clock.Advance(TimeSpan.FromDays(30));
        await Cycle(Spec(Orders(policy)));
        Assert.Equal(["20261120T020000Z-scheduled", "20261121T020000Z-scheduled"], Stored().Backups.Select(item => item.Id));
    }

    [Fact]
    public async Task AFailedDump_IsReported_LeavesNoPartialFile_AndIsRetriedAfterHalfAnHour()
    {
        _docker.FailExec = (_, command) => command[0] == "pg_dump";

        var failed = Assert.Single((await Cycle(Spec(Orders()))).Databases!);

        Assert.Empty(failed.Backups!);
        Assert.StartsWith("pg_dump failed: pg_dump: error: simulated failure", failed.LastBackupError);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, failed.LastBackupAttemptAt);
        Assert.Contains(_docker.Execs, line => line == $"{OrdersKey} rm -f /backups/orders/20261116T020000Z-scheduled.dump.partial");

        _docker.FailExec = null;
        _clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Empty(Assert.Single((await Cycle(Spec(Orders()))).Databases!).Backups!);
        _clock.Advance(TimeSpan.FromMinutes(10));
        var recovered = Assert.Single((await Cycle(Spec(Orders()))).Databases!);
        Assert.Single(recovered.Backups!);
        Assert.Null(recovered.LastBackupError);
    }

    [Fact]
    public async Task ReadinessCheck_RunsEveryCycle_AndConnectionsAreCountedEveryMinute()
    {
        var first = Assert.Single((await Cycle(Spec(Orders()))).Databases!);
        Assert.Equal((true, 3, 100), (first.Ready, first.Connections, first.MaxConnections));

        _docker.NotReady.Add(OrdersKey);
        _clock.Advance(TimeSpan.FromSeconds(15));
        await _reconciler.SuperviseOnlyAsync("connection refused", CancellationToken.None);
        Assert.Equal(("unhealthy", false), (Stored().Status, Stored().Ready));
        Assert.Contains(_log, line => line.Contains("database orders is not ready", StringComparison.Ordinal));

        _docker.NotReady.Remove(OrdersKey);
        _clock.Advance(TimeSpan.FromSeconds(15));
        var back = Assert.Single((await Cycle(Spec(Orders()))).Databases!);
        Assert.Equal(("running", true), (back.State, back.Ready));
        Assert.Equal(1, _docker.Execs.Count(line => line.Contains("max_connections", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Verify_RestoresTheDumpIntoAScratchContainerWithoutNetwork_AndRemovesIt()
    {
        var backup = Assert.Single(Assert.Single((await Cycle(Spec(Orders()))).Databases!).Backups!);

        var result = await Cycle(Spec(Orders()), Operation("op-1", "verify", backup.Id));

        var answer = Assert.Single(result.Operations!);
        Assert.Equal(("succeeded", $"Backup {backup.Id} restored into a scratch database: 12 tables."),
            (answer.State, answer.Message));
        var scratch = Assert.Single(_docker.Scratches);
        Assert.Equal((RunTargetNames.VerifyContainer(OrdersKey), "postgres:16.4-alpine", Path.Combine(_backups, AppKey, "orders"),
            "/backups/orders"), scratch);
        Assert.Contains(_docker.Execs, line => line == $"{scratch.Container} pg_restore -U postgres -d scratch --no-owner "
            + $"--no-privileges --exit-on-error /backups/orders/{backup.Id}.dump");
        Assert.False(_docker.Containers.ContainsKey(scratch.Container));
        var verified = Assert.Single(Assert.Single(result.Databases!).Backups!);
        Assert.Equal((12, _clock.GetUtcNow().UtcDateTime), (verified.VerifiedTables, verified.VerifiedAt));
        // The scratch server never touches the live database.
        Assert.DoesNotContain(_docker.Execs, line => line.StartsWith(OrdersKey + " pg_restore -U", StringComparison.Ordinal));
        var arguments = DockerCliRunTarget.ScratchArguments("c", "i", "/h", "/m");
        Assert.Equal("none", arguments[Array.IndexOf(arguments, "--network") + 1]);
        Assert.Contains("type=bind,source=/h,target=/m,readonly", arguments);
    }

    [Fact]
    public async Task Verify_OfAnUnreadableDump_Fails_AndStillRemovesTheScratchContainer()
    {
        var backup = Assert.Single(Assert.Single((await Cycle(Spec(Orders()))).Databases!).Backups!);
        _docker.FailExec = (container, command) => container.StartsWith("rt-verify-", StringComparison.Ordinal)
            && command[0] == "pg_restore";

        var answer = Assert.Single((await Cycle(Spec(Orders()), Operation("op-1", "verify", backup.Id))).Operations!);

        Assert.Equal("failed", answer.State);
        Assert.StartsWith("The backup could not be restored", answer.Message);
        Assert.Empty(_docker.Containers.Keys.Where(item => item.StartsWith("rt-verify-", StringComparison.Ordinal)));
        Assert.Null(Assert.Single(Stored().Backups).VerifiedAt);
    }

    [Fact]
    public async Task Restore_DumpsTheCurrentDataFirst_LoadsNextToTheLiveDatabase_ThenSwapsTheNames()
    {
        var backup = Assert.Single(Assert.Single((await Cycle(Spec(Orders()))).Databases!).Backups!);
        _clock.Advance(TimeSpan.FromHours(3));
        _docker.Execs.Clear();

        var result = await Cycle(Spec(Orders()), Operation("op-7", "restore", backup.Id, "orders"));

        var answer = Assert.Single(result.Operations!);
        Assert.Equal("succeeded", answer.State);
        Assert.Equal($"Backup {backup.Id} restored into orders. The data it replaced is in backup 20261116T050000Z-pre-restore.",
            answer.Message);
        var staging = RunTargetNames.RestoreStaging("orders", "op-7");
        var retired = RunTargetNames.RestoreRetired("orders", "op-7");
        var order = new[]
        {
            $"{OrdersKey} pg_dump -U app -d orders --format=custom --compress=6 --file /backups/orders/20261116T050000Z-pre-restore.dump.partial",
            $"{OrdersKey} psql -U app -d postgres -v ON_ERROR_STOP=1 -tAq -c CREATE DATABASE \"{staging}\"",
            $"{OrdersKey} pg_restore -U app -d {staging} --no-owner --exit-on-error /backups/orders/{backup.Id}.dump",
            $"{OrdersKey} psql -U app -d postgres -v ON_ERROR_STOP=1 -tAq -c ALTER DATABASE \"orders\" WITH ALLOW_CONNECTIONS false",
            $"{OrdersKey} psql -U app -d postgres -v ON_ERROR_STOP=1 -tAq -c DROP DATABASE \"{retired}\" WITH (FORCE)",
        };
        var positions = order.Select(line => _docker.Execs.FindIndex(item => item.StartsWith(line, StringComparison.Ordinal))).ToArray();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);
        var swap = _docker.Execs.Single(item => item.Contains("ALLOW_CONNECTIONS false", StringComparison.Ordinal));
        Assert.Contains($"-c ALTER DATABASE \"orders\" RENAME TO \"{retired}\" -c ALTER DATABASE \"{staging}\" RENAME TO \"orders\"", swap);
        Assert.Contains("pg_terminate_backend", swap);
        Assert.Equal(["scheduled", "pre-restore"], Stored().Backups.Select(item => item.Kind));
    }

    [Fact]
    public async Task Restore_ThatCannotLoadTheDump_LeavesTheLiveDatabaseAlone()
    {
        var backup = Assert.Single(Assert.Single((await Cycle(Spec(Orders()))).Databases!).Backups!);
        _docker.FailExec = (_, command) => command[0] == "pg_restore" && command.Contains("-d");

        var answer = Assert.Single((await Cycle(Spec(Orders()), Operation("op-7", "restore", backup.Id, "orders"))).Operations!);

        Assert.Equal("failed", answer.State);
        Assert.StartsWith("The backup could not be loaded; the data of orders is unchanged", answer.Message);
        Assert.DoesNotContain(_docker.Execs, line => line.Contains("RENAME", StringComparison.Ordinal)
            || line.Contains("ALLOW_CONNECTIONS", StringComparison.Ordinal));
        Assert.Contains(_docker.Execs, line => line.EndsWith(
            $"DROP DATABASE IF EXISTS \"{RunTargetNames.RestoreStaging("orders", "op-7")}\" WITH (FORCE)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restore_RefusesWhenTheCurrentDataCannotBeDumpedFirst()
    {
        var backup = Assert.Single(Assert.Single((await Cycle(Spec(Orders()))).Databases!).Backups!);
        _docker.FailExec = (_, command) => command[0] == "pg_dump";

        var answer = Assert.Single((await Cycle(Spec(Orders()), Operation("op-7", "restore", backup.Id))).Operations!);

        Assert.Equal("failed", answer.State);
        Assert.StartsWith("The current data of orders could not be backed up first, so nothing was restored", answer.Message);
        Assert.DoesNotContain(_docker.Execs, line => line.Contains("CREATE DATABASE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restore_IntoAnotherDatabaseOfTheEnvironment_ReadsTheSourceDumpFromTheSharedDirectory()
    {
        var first = await Cycle(Spec(Orders(), Audit()));
        var backup = Assert.Single(first.Databases!.Single(item => item.Name == "orders").Backups!);

        var answer = Assert.Single((await Cycle(Spec(Orders(), Audit()), Operation("op-8", "restore", backup.Id, "audit"))).Operations!);

        Assert.Equal("succeeded", answer.State);
        Assert.StartsWith($"Backup {backup.Id} of orders restored into audit.", answer.Message);
        Assert.Contains(_docker.Execs, line => line.StartsWith($"{AuditKey} pg_restore -U app -d audit_restore_", StringComparison.Ordinal)
            && line.EndsWith($"/backups/orders/{backup.Id}.dump", StringComparison.Ordinal));
        Assert.Contains(Stored(AuditKey).Backups, item => item.Kind == "pre-restore");
        Assert.DoesNotContain(_docker.Execs, line => line.StartsWith(OrdersKey + " psql -U app -d postgres", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnOperation_RunsOncePerId_AndItsResultIsSentAgainUntilStudioStopsAsking()
    {
        await Cycle(Spec(Orders()));
        _clock.Advance(TimeSpan.FromMinutes(5));
        var backup = Operation("op-2", "backup");

        var first = Assert.Single((await Cycle(Spec(Orders()), backup)).Operations!);
        var again = Assert.Single((await Cycle(Spec(Orders()), backup)).Operations!);

        Assert.Equal(first, again);
        Assert.Equal(("succeeded", "20261116T020500Z-manual"), (first.State, first.BackupId));
        Assert.Single(_docker.Execs, line => line.Contains(" pg_dump ", StringComparison.Ordinal) && line.Contains("-manual", StringComparison.Ordinal));
        Assert.Empty((await Cycle(Spec(Orders()))).Operations!);
        var unknown = Assert.Single((await Cycle(Spec(Orders()), Operation("op-3", "verify", "20200101T000000Z-manual"))).Operations!);
        Assert.Equal(("failed", "Backup 20200101T000000Z-manual is not on the run host."), (unknown.State, unknown.Message));
        var stranger = Assert.Single((await Cycle(Spec(Orders()), Operation("op-4", "backup", name: "billing"))).Operations!);
        Assert.Equal(("failed", "The run host has no such database."), (stranger.State, stranger.Message));
    }

    [Fact]
    public async Task Deletion_RemovesTheDumpsBeforeTheVolume()
    {
        await Cycle(Spec(Orders()));
        await Cycle(Spec());
        Assert.Equal("retained", Stored().Status);

        var result = await Cycle(Spec(), [new RunTargetDatabaseDeletion("demo", "stage", "orders")]);

        Assert.Contains(result.Databases!, item => item.Name == "orders" && item.State == "deleted");
        var removeDumps = _docker.Calls.FindIndex(call => call == $"run-once postgres:16.4-alpine {Path.Combine(_backups, AppKey)} rm -rf /backups/orders");
        var removeVolume = _docker.Calls.FindIndex(call => call == "volume-rm " + RunTargetNames.DatabaseVolume(OrdersKey));
        Assert.InRange(removeDumps, 0, removeVolume - 1);
        Assert.Empty(_docker.BackupFiles);
    }

    [Fact]
    public void Options_ReadTheBackupDirectory_WhichMustBeAbsoluteAndOutsideTheState()
    {
        var runner = new RunnerOptions
        {
            ServerUrl = "http://studio", RunnerId = "run-host", RunnerName = "run-host", Hostname = "run-host",
            BackendName = "test", WorkDir = "/tmp/run-host", BaseBranch = "main", StateDir = "/var/lib/agent-host",
        };

        Assert.Equal("/var/lib/agent-host/run-target-backups", RunTargetOptions.From(runner, _ => null).Reconciler.BackupDirectory);
        Assert.Equal("/mnt/backups", RunTargetOptions.From(runner,
            name => name == "RUN_TARGET_BACKUP_DIR" ? "/mnt/backups" : null).Reconciler.BackupDirectory);
        foreach (var invalid in new[] { "relative/backups", "/var/lib/agent-host/run-target/backups", "/mnt/a,b" })
            Assert.Throws<ArgumentException>(() => RunTargetOptions.From(runner,
                name => name == "RUN_TARGET_BACKUP_DIR" ? invalid : null));
    }

    [Fact]
    public async Task ARefusedMajorUpgrade_KeepsTheOldServersDataDumped()
    {
        await Cycle(Spec(Orders()));
        await Cycle(Spec(Orders() with { Version = "17" }));
        Assert.Equal("upgrade-refused", Stored().Status);

        _clock.Advance(TimeSpan.FromDays(1));
        await Cycle(Spec(Orders() with { Version = "17" }));

        Assert.Equal(2, Stored().Backups.Count(item => item.Kind == "scheduled"));
    }

    [Fact]
    public void Schedule_ClampsStudiosPolicy()
    {
        Assert.Equal(RunTargetBackupPolicy.Default, RunTargetBackupSchedule.Sanitize(null));
        Assert.Equal(new RunTargetBackupPolicy(1, 1, 1), RunTargetBackupSchedule.Sanitize(new(0, -3, 0)));
        Assert.Equal(new RunTargetBackupPolicy(168, 3650, 100), RunTargetBackupSchedule.Sanitize(new(1000, 10000, 1000)));
    }
}
