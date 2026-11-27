using System.Text.Json;
using AgentStudio.Projects;
using AgentStudio.Deploy;
using AgentStudio.Secrets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class HostedDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hosted-data-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void DatabaseBinding_UsesEnvironmentSecretReferenceWithoutValue()
    {
        using var document = JsonDocument.Parse("""
            {"data":{"database":{"secretRef":"secret://demo/stage/DATABASE_URL","environmentVariable":"DATABASE_URL"},
                     "volumes":[{"name":"uploads","mountPath":"/app/uploads"}]}}
            """);
        var data = HostedDataDeclaration.Parse(document.RootElement, "container", "stage", "stage.json");
        Assert.Equal("secret://demo/stage/DATABASE_URL", data.Database!.SecretRef);
        Assert.Equal(["EXISTING", "DATABASE_URL"],
            HostedDataBinding.DeclaredSecretNames("demo", "stage", ["EXISTING"], data));
        var workload = new SecretWorkload(SecretWorkload.DeployJob, "run-host", "demo", "stage",
            HostedDataBinding.DeclaredSecretNames("demo", "stage", [], data));
        Assert.True(SecretAccessPolicy.ForWorkload(SecretReference.Parse(data.Database.SecretRef),
            workload, "deploy:database").Allowed);
        Assert.DoesNotContain("password=", JsonSerializer.Serialize(data));
    }

    [Theory]
    [InlineData("secret://other/stage/DATABASE_URL", "stage")]
    [InlineData("secret://demo/production/DATABASE_URL", "stage")]
    public void DatabaseBinding_RejectsWrongProjectOrEnvironment(string reference, string environment)
    {
        var data = new HostedDataDeclaration(new(reference, "DATABASE_URL"), []);
        Assert.Throws<JsonException>(() => HostedDataBinding.DeclaredSecretNames("demo", environment, [], data));
    }

    [Theory]
    [InlineData("/app/../secrets")]
    [InlineData("/app//uploads")]
    public void VolumeDeclaration_RejectsUnsafeMounts(string mount)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            data = new { volumes = new[] { new { name = "uploads", mountPath = mount } } },
        }));
        Assert.Throws<JsonException>(() => HostedDataDeclaration.Parse(document.RootElement, "container", "stage", "stage.json"));
    }

    [Fact]
    public void StaticEnvironmentCannotDeclarePersistentContainerData()
    {
        using var document = JsonDocument.Parse("""{"data":{"volumes":[]}}""");
        Assert.Throws<JsonException>(() => HostedDataDeclaration.Parse(document.RootElement, "static", "stage", "stage.json"));
    }

    [Fact]
    public void ReleaseVersionsChangeForVolumeAndDatabaseBinding()
    {
        var original = new EnvironmentDeclaration("stage", "Stage", "stage", "host", "https://stage.example",
            "container", "8081", "https://stage.example/health", "person", [], 8080);
        var withVolume = original with { Data = new(null, [new("uploads", "/app/uploads")]) };
        var withDatabase = withVolume with
        {
            Data = new(new("secret://demo/stage/DATABASE_URL", "DATABASE_URL"), withVolume.Data.Volumes),
        };
        Assert.NotEqual(ReleaseSnapshot.ProcessVersion(null, [original]),
            ReleaseSnapshot.ProcessVersion(null, [withVolume]));
        Assert.NotEqual(ReleaseSnapshot.VariablesVersion(null, [withVolume]),
            ReleaseSnapshot.VariablesVersion(null, [withDatabase]));
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(13, false, false)]
    [InlineData(8, false, true)]
    [InlineData(11, true, false)]
    public void BackupSchedulePolicy_UsesUtcDayAndOneAttempt(int? hour, bool attemptedToday, bool expected)
    {
        var now = _clock.GetUtcNow();
        var schedule = new HostedVolumeSchedule("demo", "stage", "uploads", hour,
            attemptedToday ? now.AddMinutes(-5) : now.AddDays(-1), null, []);
        Assert.Equal(expected, HostedVolumeSchedulePolicy.IsDue(schedule, now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DueSweep_ContainsUnreadableProject_TracksIncidentUntilRecovery(bool locked)
    {
        string[] projects = ["before", "broken", "after"];
        var settings = new Dictionary<string, string?> { ["TaskRepository"] = Path.Combine(_root, "workspace") };
        for (var i = 0; i < projects.Length; i++)
        {
            var repo = Path.Combine(_root, projects[i]);
            var declarations = Path.Combine(repo, "docs", "deployments", "environments");
            Directory.CreateDirectory(declarations);
            File.WriteAllText(Path.Combine(declarations, "stage.json"), JsonSerializer.Serialize(new
            {
                id = "stage", name = "Stage", kind = "stage", targetHostId = "host",
                address = "https://stage.example", template = "container", destination = "8081",
                healthUrl = "https://stage.example/health", approval = "person", secretRefs = Array.Empty<string>(),
                data = new { volumes = new[] { new { name = "uploads", mountPath = "/app/uploads" } } },
            }));
            settings[$"WatchPaths:{i}:Name"] = projects[i];
            settings[$"WatchPaths:{i}:Path"] = repo;
            settings[$"WatchPaths:{i}:RepositoryPath"] = repo;
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var environments = new ProjectEnvironmentService(config, scanner,
            new GitService(NullLogger<GitService>.Instance, scanner, config),
            new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config), _clock);
        var log = new ScheduleLogger();
        var service = new HostedVolumeBackupService(config, environments, new ScheduleEngine(), _clock, log);
        foreach (var project in projects)
            await service.SetScheduleAsync(project, "stage", "uploads", 8, "owner", CancellationToken.None);
        var brokenFile = Path.Combine(_root, "workspace", "hosted-volume-backups", "broken.json");
        var original = File.ReadAllText(brokenFile);
        using (var fileLock = locked ? new FileStream(brokenFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
        {
            if (!locked) File.WriteAllText(brokenFile, "{ unreadable json");
            var firstFailure = _clock.GetUtcNow();
            for (var sweep = 0; sweep < 3; sweep++)
            {
                await service.RunDueAsync(CancellationToken.None);
                foreach (var project in new[] { "before", "after" })
                {
                    var state = (await service.ReadAsync(project, CancellationToken.None))!;
                    Assert.Equal(sweep + 1, Assert.Single(state.Schedules).Backups.Count);
                    Assert.Equal(_clock.GetUtcNow(), state.Schedules[0].LastAttemptAt);
                    Assert.Null(state.Schedules[0].LastError);
                    Assert.Equal(sweep + 1, state.Audit.Count(item => item.Action == "backup" && item.Outcome == "succeeded"));
                }
                var failure = log.Entries.Last();
                Assert.Equal(LogLevel.Warning, failure.Level);
                Assert.Equal("broken", failure.Fields["Project"]);
                Assert.Equal(firstFailure, failure.Fields["FirstFailedAt"]);
                Assert.Equal(_clock.GetUtcNow().AddMinutes(5), failure.Fields["RetryAt"]);
                Assert.Equal(sweep * 2 + 1, log.Entries.Count);
                // A five-minute retry leaves the healthy schedules bounded to one attempt per UTC day.
                _clock.Advance(TimeSpan.FromMinutes(5));
                await service.RunDueAsync(CancellationToken.None);
                foreach (var project in new[] { "before", "after" })
                    Assert.Equal(sweep + 1, (await service.ReadAsync(project, CancellationToken.None))!.Schedules[0].Backups.Count);
                Assert.Equal(firstFailure, log.Entries.Last().Fields["FirstFailedAt"]);
                // Both warnings refer to the same continuing incident.
                Assert.Equal((sweep + 1) * 2, log.Entries.Count);
                _clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromMinutes(5));
            }
        }
        File.WriteAllText(brokenFile, original);
        await service.RunDueAsync(CancellationToken.None);
        var recovery = log.Entries.Last();
        Assert.Equal(LogLevel.Information, recovery.Level);
        Assert.Equal("broken", recovery.Fields["Project"]);
        Assert.Equal(_clock.GetUtcNow(), recovery.Fields["RecoveredAt"]);
        Assert.Equal(_clock.GetUtcNow().AddDays(-3), recovery.Fields["FirstFailedAt"]);
        Assert.Single((await service.ReadAsync("broken", CancellationToken.None))!.Schedules[0].Backups);
        var logCount = log.Entries.Count;
        await service.RunDueAsync(CancellationToken.None);
        Assert.Equal(logCount, log.Entries.Count);
        File.WriteAllText(brokenFile, "{ broken again");
        _clock.Advance(TimeSpan.FromMinutes(5));
        await service.RunDueAsync(CancellationToken.None);
        Assert.Equal(_clock.GetUtcNow(), log.Entries.Last().Fields["FirstFailedAt"]);
    }

    private sealed class ScheduleEngine : IHostedVolumeBackupEngine
    {
        public Task<HostedVolumeBackup> BackupAsync(HostedVolumeKey key, DateTimeOffset now, CancellationToken cancellationToken)
            => Task.FromResult(new HostedVolumeBackup(Guid.NewGuid().ToString("N"), key, now, "test-digest", 0));
        public Task RestoreAsync(HostedVolumeBackup backup, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PruneBackupsAsync(HostedVolumeKey key, IReadOnlySet<string> retainedIds, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class ScheduleLogger : ILogger<HostedVolumeBackupService>
    {
        public List<(LogLevel Level, Dictionary<string, object?> Fields)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary()));
    }

    [Fact]
    public async Task VolumeIdentitySurvivesReleases_AndBackupRestoresExactBytes()
    {
        var engine = new LocalHostedVolumeBackupEngine(Path.Combine(_root, "volumes"), Path.Combine(_root, "archives"));
        var key = new HostedVolumeKey("demo", "stage", "uploads");
        var firstReleasePath = engine.VolumePath(key);
        Directory.CreateDirectory(firstReleasePath);
        File.WriteAllText(Path.Combine(firstReleasePath, "image.txt"), "release-one-user-data");
        var backup = await engine.BackupAsync(key, _clock.GetUtcNow(), CancellationToken.None);
        var secondReleasePath = engine.VolumePath(new("demo", "stage", "uploads"));
        Assert.Equal(firstReleasePath, secondReleasePath);
        Assert.Equal("release-one-user-data", File.ReadAllText(Path.Combine(secondReleasePath, "image.txt")));
        File.WriteAllText(Path.Combine(secondReleasePath, "image.txt"), "changed-after-backup");
        await engine.RestoreAsync(backup, CancellationToken.None);
        Assert.Equal("release-one-user-data", File.ReadAllText(Path.Combine(secondReleasePath, "image.txt")));
        Assert.Equal(_clock.GetUtcNow(), backup.CreatedAt);
    }

    [Fact]
    public async Task CorruptBackupCannotReplaceLiveVolume()
    {
        var engine = new LocalHostedVolumeBackupEngine(Path.Combine(_root, "volumes"), Path.Combine(_root, "archives"));
        var key = new HostedVolumeKey("demo", "stage", "uploads");
        var path = engine.VolumePath(key);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "data.txt"), "live");
        var backup = await engine.BackupAsync(key, _clock.GetUtcNow(), CancellationToken.None);
        var archive = Path.Combine(_root, "archives", "demo", "stage", "uploads", backup.Id + ".zip");
        File.AppendAllText(archive, "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.RestoreAsync(backup, CancellationToken.None));
        Assert.Equal("live", File.ReadAllText(Path.Combine(path, "data.txt")));
    }

    [Fact]
    public async Task CleanupFailureAfterReplacementReportsSuccessAndKeepsPreviousData()
    {
        string? previous = null;
        var engine = new LocalHostedVolumeBackupEngine(Path.Combine(_root, "volumes"), Path.Combine(_root, "archives"),
            NullLogger<LocalHostedVolumeBackupEngine>.Instance, path =>
            {
                previous = path;
                throw new IOException("Previous directory is busy.");
            });
        var key = new HostedVolumeKey("demo", "stage", "uploads");
        var path = engine.VolumePath(key);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "data.txt"), "backed-up");
        var backup = await engine.BackupAsync(key, _clock.GetUtcNow(), CancellationToken.None);
        File.WriteAllText(Path.Combine(path, "data.txt"), "live-before-restore");

        await engine.RestoreAsync(backup, CancellationToken.None);

        Assert.Equal("backed-up", File.ReadAllText(Path.Combine(path, "data.txt")));
        Assert.NotNull(previous);
        Assert.Equal("live-before-restore", File.ReadAllText(Path.Combine(previous!, "data.txt")));
        Assert.True(File.Exists(path + ".restore-pending"));

        var restarted = new LocalHostedVolumeBackupEngine(Path.Combine(_root, "volumes"),
            Path.Combine(_root, "archives"), NullLogger<LocalHostedVolumeBackupEngine>.Instance,
            _ => throw new IOException("Previous directory is still busy."));
        await Assert.ThrowsAsync<IOException>(() => restarted.RestoreAsync(backup, CancellationToken.None));
        Assert.Single(Directory.EnumerateDirectories(Path.GetDirectoryName(path)!, "uploads.previous-*"));
        Assert.True(File.Exists(path + ".restore-pending"));

        var recovered = new LocalHostedVolumeBackupEngine(Path.Combine(_root, "volumes"),
            Path.Combine(_root, "archives"));
        Assert.False(File.Exists(path + ".restore-pending"));
        Assert.False(Directory.Exists(previous));
        await recovered.RestoreAsync(backup, CancellationToken.None);
        Assert.Equal("backed-up", File.ReadAllText(Path.Combine(path, "data.txt")));
    }

    [Fact]
    public async Task BackupRejectsFileChangedToSymlinkBeforeOpen()
    {
        if (!OperatingSystem.IsLinux()) return;
        var volumeRoot = Path.Combine(_root, "volumes");
        var enginePath = Path.Combine(volumeRoot, "demo", "stage", "uploads");
        Directory.CreateDirectory(enginePath);
        var file = Path.Combine(enginePath, "data.txt");
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(file, "volume-data");
        File.WriteAllText(outside, "outside-secret");
        var engine = new LocalHostedVolumeBackupEngine(volumeRoot, Path.Combine(_root, "archives"),
            NullLogger<LocalHostedVolumeBackupEngine>.Instance, path => Directory.Delete(path, true), _clock,
            beforeSourceOpen: path =>
            {
                Assert.Equal(file, path);
                File.Delete(file);
                File.CreateSymbolicLink(file, outside);
            });

        await Assert.ThrowsAsync<InvalidDataException>(() => engine.BackupAsync(
            new("demo", "stage", "uploads"), _clock.GetUtcNow(), CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "archives", "demo", "stage", "uploads")));
        Assert.Equal("outside-secret", File.ReadAllText(outside));
    }

    [Fact]
    public void RestartRecoversLiveVolumeAfterInterruptedDirectorySwap()
    {
        var volumeRoot = Path.Combine(_root, "volumes");
        var archiveRoot = Path.Combine(_root, "archives");
        var engine = new LocalHostedVolumeBackupEngine(volumeRoot, archiveRoot, clock: _clock);
        var path = engine.VolumePath(new("demo", "stage", "uploads"));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "data.txt"), "live-before-restore");
        var restoreId = Guid.NewGuid().ToString("N");
        var staging = path + ".restore-" + restoreId;
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "data.txt"), "staged-replacement");

        engine.MoveLiveAside(path, restoreId);
        Assert.False(Directory.Exists(path));
        Assert.True(File.Exists(path + ".restore-pending"));

        _ = new LocalHostedVolumeBackupEngine(volumeRoot, archiveRoot, clock: _clock);
        Assert.Equal("live-before-restore", File.ReadAllText(Path.Combine(path, "data.txt")));
        Assert.False(File.Exists(path + ".restore-pending"));
        Assert.False(Directory.Exists(path + ".previous-" + restoreId));
        Assert.False(Directory.Exists(staging));
    }
}
