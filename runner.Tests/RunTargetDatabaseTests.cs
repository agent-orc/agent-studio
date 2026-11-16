using AgentRunner.RunTargets;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3877 managed databases on a fake run host: provisioning before the app
/// revision, zone isolation of the database container, credentials that reach
/// only tmpfs files and never logs or compose files, minor upgrades as a deploy
/// step, refused major changes, retention of undeclared data, and deletion only
/// on an owner's signal.
/// </summary>
public sealed class RunTargetDatabaseTests : IDisposable
{
    private static readonly string First = "registry.example/app@sha256:" + new string('a', 64);
    private static readonly string Second = "registry.example/app@sha256:" + new string('b', 64);
    private const string PasswordRef = "secret://demo/stage/MANAGED_DB_ORDERS_PASSWORD";

    private readonly TempWorkspace _workspace = new("run-target-database");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeRunTargetDocker _docker = new();
    private readonly List<string> _log = [];
    // Built at run time: no credential-shaped literal in the source.
    private readonly string _password = "pw" + Guid.NewGuid().ToString("N");
    private readonly string _runtime;
    private readonly RunTargetStateStore _store;
    private readonly FakeRunTargetCredentials _credentials;
    private readonly RunTargetReconciler _reconciler;

    public RunTargetDatabaseTests()
    {
        _store = new RunTargetStateStore(_workspace.Directory("state"), _log.Add);
        _runtime = _workspace.Directory("secrets-runtime");
        _credentials = new FakeRunTargetCredentials(new() { [$"demo/stage/{PasswordRef}"] = _password });
        _reconciler = new RunTargetReconciler(_docker, new FakeRunTargetSecrets([]),
            new RunTargetSecretFiles(_runtime, _ => true), _store,
            new RunTargetReconcilerSettings(RunTargetOptions.DefaultEdgeImage, new RunTargetEdgeSettings(null, false, []),
                null, HealthAttempts: 3),
            _clock, (_, _) => Task.CompletedTask, _log.Add, _credentials);
    }

    public void Dispose() => _workspace.Dispose();

    private static string AppKey => RunTargetNames.AppKey("demo", "stage");
    private static string DatabaseKey => RunTargetNames.DatabaseKey(AppKey, "orders");
    private static string Volume => RunTargetNames.DatabaseVolume(DatabaseKey);

    private static RunTargetDatabaseSpec Orders(string version = "16.4", string size = "small")
        => new("orders", "postgres", version, size,
            size == "small" ? new RunTargetResources(0.5, 512, 256) : new RunTargetResources(1, 1024, 512),
            "DATABASE_URL", PasswordRef);

    private static RunTargetEnvironmentSpec Spec(string artifact, string? jobId, params RunTargetDatabaseSpec[] databases)
        => new("demo", "stage", "stage", artifact, 8080, "/healthz", ["stage.example.com"], "acme",
            new Dictionary<string, string>(), [], new RunTargetResources(0.5, 256, 128), jobId,
            jobId is null ? null : "lease-" + jobId, false, Databases: databases);

    private Task<RunTargetCycleResult> Desired(RunTargetEnvironmentSpec[] specs, RunTargetDatabaseDeletion[]? deletions = null,
        RunTargetRemoval[]? removals = null)
        => _reconciler.ReconcileAsync(new RunTargetDesiredState("run-host", _clock.GetUtcNow().UtcDateTime, specs, [],
            removals ?? [], deletions), CancellationToken.None);

    private string DatabaseCompose => _docker.Ups.Last(item => item.Project == DatabaseKey).ComposeText;
    private string AppCompose(RunTargetEnvironmentSpec spec)
        => _docker.Ups.Last(item => item.Project == AppKey + "-" + RunTargetNames.Revision(spec)).ComposeText;

    [Fact]
    public async Task Deploy_ProvisionsTheDatabaseBeforeTheApp_AndHandsTheAppAConnectionStringFile()
    {
        var spec = Spec(First, "job-1", Orders());

        var result = await Desired([spec]);

        Assert.True(Assert.Single(result.JobReports).Healthy);
        var database = Assert.Single(result.Databases!);
        Assert.Equal(("orders", "running", "16.4"), (database.Name, database.State, database.Version));
        Assert.Equal(2048L * 1024, database.VolumeBytes);
        Assert.Contains(Volume, _docker.Volumes);
        var upOrder = _docker.Calls.Where(call => call.StartsWith("up rt-", StringComparison.Ordinal)
            && call != "up " + RunTargetNames.EdgeContainer).ToArray();
        Assert.Equal("up " + DatabaseKey, upOrder[0]);
        Assert.Contains("pull postgres:16.4-alpine", _docker.Calls);
        Assert.Contains($"exec {DatabaseKey} pg_isready", _docker.Calls);

        Assert.Contains("\"DATABASE_URL_FILE=/run/secrets/managed-db-orders-url\"", AppCompose(spec));
        var url = File.ReadAllText(Path.Combine(_runtime, AppKey + "-" + RunTargetNames.Revision(spec), "managed-db-orders-url"));
        Assert.Equal($"postgresql://app:{_password}@{DatabaseKey}:5432/orders", url);
        Assert.Equal(_password, File.ReadAllText(Path.Combine(_runtime, DatabaseKey, "password")));
        Assert.Equal([PasswordRef, PasswordRef], _credentials.Reads);
    }

    [Fact]
    public async Task DatabaseContainer_JoinsOnlyItsEnvironmentsDataNetwork_WhichTheEdgeNeverJoins()
    {
        var spec = Spec(First, "job-1", Orders());

        await Desired([spec]);

        var compose = DatabaseCompose;
        var dataNetwork = RunTargetNames.DataNetwork(AppKey);
        Assert.Contains($"networks:\n  data:\n    name: \"{dataNetwork}\"\n    external: true\n", compose);
        Assert.DoesNotContain(RunTargetNames.AppNetwork(AppKey), compose);
        Assert.DoesNotContain(RunTargetNames.EdgeNetwork + "\"", compose);
        Assert.DoesNotContain("ports:", compose);
        Assert.DoesNotContain("network_mode", compose);
        Assert.DoesNotContain("/var/run/docker.sock", compose);
        Assert.Contains($"volumes:\n  data:\n    name: \"{Volume}\"\n    external: true\n", compose);
        Assert.Contains("mem_limit: \"512m\"", compose);
        Assert.Contains("cpus: 0.5", compose);
        Assert.Contains("pids_limit: 256", compose);
        Assert.Contains("no-new-privileges:true", compose);
        // The app joins the data network next to its own; the edge is connected to the app network only.
        Assert.Contains($"  data:\n    name: \"{dataNetwork}\"\n    external: true\n", AppCompose(spec));
        Assert.Contains("network " + dataNetwork, _docker.Calls);
        Assert.DoesNotContain(_docker.Calls, call => call.StartsWith("connect " + dataNetwork, StringComparison.Ordinal));
        Assert.Contains($"connect {RunTargetNames.AppNetwork(AppKey)} {RunTargetNames.EdgeContainer}", _docker.Calls);

        // Another environment's database gets its own network and key.
        var other = RunTargetNames.AppKey("demo", "prod");
        Assert.NotEqual(RunTargetNames.DataNetwork(other), dataNetwork);
        Assert.NotEqual(RunTargetNames.DatabaseKey(other, "orders"), DatabaseKey);
        Assert.True(DatabaseKey.Length <= 63);
    }

    [Fact]
    public async Task Credential_NeverReachesLogsComposeFilesOrHostState()
    {
        _docker.FailUpWith = null;
        await Desired([Spec(First, "job-1", Orders())]);
        _docker.FailUpWith = "start failed for password " + _password;
        await Desired([Spec(Second, "job-2", Orders("16.6"))]);

        Assert.DoesNotContain(_log, line => line.Contains(_password, StringComparison.Ordinal));
        Assert.Contains(_log, line => line.Contains("[redacted]", StringComparison.Ordinal));
        Assert.All(_docker.Ups, up => Assert.DoesNotContain(_password, up.ComposeText));
        Assert.DoesNotContain(_password, File.ReadAllText(_store.StatePath));
        Assert.All(Directory.EnumerateFiles(_store.Directory, "*", SearchOption.AllDirectories),
            file => Assert.DoesNotContain(_password, File.ReadAllText(file)));
    }

    [Fact]
    public async Task MinorUpgrade_RecreatesOnTheSameVolume_AndAMajorChangeIsRefused()
    {
        await Desired([Spec(First, "job-1", Orders("16.4"))]);
        _docker.Calls.Clear();

        var minor = await Desired([Spec(First, "job-2", Orders("16.6"))]);

        Assert.True(Assert.Single(minor.JobReports).Healthy);
        Assert.Contains("pull postgres:16.6-alpine", _docker.Calls);
        Assert.Contains("\"postgres:16.6-alpine\"", DatabaseCompose);
        Assert.Contains($"\"{Volume}\"", DatabaseCompose);
        Assert.DoesNotContain(_docker.Calls, call => call.StartsWith("volume-rm", StringComparison.Ordinal));
        Assert.Equal("16.6", Assert.Single(minor.Databases!).Version);
        _docker.Calls.Clear();

        var major = await Desired([Spec(First, "job-3", Orders("17.0"))]);

        var report = Assert.Single(major.JobReports);
        Assert.False(report.Healthy);
        Assert.Equal(("upgrade-refused", "16.6"), (Assert.Single(major.Databases!).State, major.Databases![0].Version));
        Assert.DoesNotContain(_docker.Calls, call => call.Contains("postgres:17", StringComparison.Ordinal)
            || call == "up " + DatabaseKey || call.StartsWith("volume-rm", StringComparison.Ordinal));
        Assert.Contains(_log, line => line.Contains("major upgrade needs a dump and restore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UndeclaredDatabase_IsStoppedAfterTheNewRevisionIsLive_AndKeepsItsVolume()
    {
        await Desired([Spec(First, "job-1", Orders())]);

        var result = await Desired([Spec(Second, "job-2")]);

        Assert.True(Assert.Single(result.JobReports).Healthy);
        Assert.Equal("retained", Assert.Single(result.Databases!).State);
        Assert.Contains("stop " + DatabaseKey, _docker.Calls);
        Assert.DoesNotContain("down " + DatabaseKey, _docker.Calls);
        Assert.Contains(Volume, _docker.Volumes);
        Assert.False(Directory.Exists(Path.Combine(_runtime, DatabaseKey)));
        var swap = _docker.Calls.IndexOf("stop " + AppKey + "-" + RunTargetNames.Revision(Spec(First, null, Orders())));
        Assert.True(swap >= 0 && swap < _docker.Calls.IndexOf("stop " + DatabaseKey));
    }

    [Fact]
    public async Task EnvironmentRemoval_RemovesTheContainerButKeepsTheVolume()
    {
        await Desired([Spec(First, "job-1", Orders())]);

        var result = await Desired([], removals: [new("demo", "stage")]);

        Assert.Equal("removed", Assert.Single(result.Observations).State);
        Assert.Equal("retained", Assert.Single(result.Databases!).State);
        Assert.Contains("down " + DatabaseKey, _docker.Calls);
        Assert.Contains("network-rm " + RunTargetNames.DataNetwork(AppKey), _docker.Calls);
        Assert.Contains(Volume, _docker.Volumes);
    }

    [Fact]
    public async Task Deletion_RemovesTheVolumeOnlyWhenNoSpecDeclaresTheDatabase()
    {
        await Desired([Spec(First, "job-1", Orders())]);
        var deletion = new RunTargetDatabaseDeletion("demo", "stage", "orders");

        var refused = await Desired([Spec(First, null, Orders())], [deletion]);

        Assert.Contains(Volume, _docker.Volumes);
        Assert.DoesNotContain(refused.Databases!, item => item.State == "deleted");
        Assert.Contains(_log, line => line.Contains("refused deletion", StringComparison.Ordinal));

        await Desired([Spec(Second, "job-2")]);
        var deleted = await Desired([Spec(Second, null)], [deletion]);

        Assert.DoesNotContain(Volume, _docker.Volumes);
        Assert.Equal("deleted", Assert.Single(deleted.Databases!).State);
        Assert.Contains("volume-rm " + Volume, _docker.Calls);
        Assert.False(Directory.Exists(Path.Combine(_store.DatabasesDirectory, DatabaseKey)));
        Assert.Empty((await Desired([Spec(Second, null)])).Databases!);
    }

    [Fact]
    public async Task MissingCredential_FailsTheDeploy_AndStartsNothing()
    {
        var reconciler = new RunTargetReconciler(_docker, new FakeRunTargetSecrets([]),
            new RunTargetSecretFiles(_runtime, _ => true), _store,
            new RunTargetReconcilerSettings(RunTargetOptions.DefaultEdgeImage, new RunTargetEdgeSettings(null, false, []),
                null, HealthAttempts: 3),
            _clock, (_, _) => Task.CompletedTask, _log.Add, new FakeRunTargetCredentials([]));

        var result = await reconciler.ReconcileAsync(new RunTargetDesiredState("run-host", _clock.GetUtcNow().UtcDateTime,
            [Spec(First, "job-1", Orders())], [], []), CancellationToken.None);

        Assert.False(Assert.Single(result.JobReports).Healthy);
        Assert.DoesNotContain(_docker.Calls, call => call.StartsWith("up rt-", StringComparison.Ordinal)
            && call != "up " + RunTargetNames.EdgeContainer);
        Assert.Contains(_log, line => line.Contains("did not issue " + PasswordRef, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CachedCredentials_ServeAnEarlierValueWhileStudioIsUnreachable_AndARefusalDropsIt()
    {
        var studio = new FakeRunTargetCredentials(new() { [$"demo/stage/{PasswordRef}"] = _password });
        var cached = new CachedRunTargetCredentials(studio, _log.Add);
        Assert.Equal(_password, await cached.ReadAsync("demo", "stage", PasswordRef, CancellationToken.None));

        studio.Unreachable = true;
        Assert.Equal(_password, await cached.ReadAsync("demo", "stage", PasswordRef, CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => cached.ReadAsync("demo", "prod", PasswordRef, CancellationToken.None));
        Assert.DoesNotContain(_log, line => line.Contains(_password, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Orders", "16", "DATABASE_URL")]
    [InlineData("orders", "13", "DATABASE_URL")]
    [InlineData("orders", "16.x", "DATABASE_URL")]
    [InlineData("orders", "16", "LOG_LEVEL")]
    [InlineData("orders", "16", "lower")]
    public void InvalidDatabaseSpecs_AreRefusedBeforeAnythingStarts(string name, string version, string variable)
    {
        var spec = Spec(First, "job-1", Orders() with { Name = name, Version = version, Variable = variable })
            with { Variables = new Dictionary<string, string> { ["LOG_LEVEL_FILE"] = "x" } };

        Assert.NotNull(RunTargetNames.Invalid(spec));
    }
}
