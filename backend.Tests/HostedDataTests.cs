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
    }

    [Fact]
    public async Task ConcurrentFileSwapCannotReadOutsideDeclaredVolume()
    {
        if (!OperatingSystem.IsLinux()) return;
        var volumeRoot = Path.Combine(_root, "volumes");
        var archiveRoot = Path.Combine(_root, "archives");
        var key = new HostedVolumeKey("demo", "stage", "uploads");
        var path = Path.Combine(volumeRoot, "demo", "stage", "uploads");
        Directory.CreateDirectory(path);
        var original = Path.Combine(path, "data.txt");
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(original, "inside");
        File.WriteAllText(outside, "outside-secret");
        var swapped = false;
        var engine = new LocalHostedVolumeBackupEngine(volumeRoot, archiveRoot,
            NullLogger<LocalHostedVolumeBackupEngine>.Instance, directory => Directory.Delete(directory, true),
            _clock, beforeOpenSource: relative =>
            {
                if (relative != "data.txt") return;
                File.Move(original, original + ".original");
                File.CreateSymbolicLink(original, outside);
                swapped = true;
            });

        await Assert.ThrowsAsync<IOException>(() => engine.BackupAsync(key, _clock.GetUtcNow(), CancellationToken.None));
        Assert.True(swapped);
        Assert.Equal("outside-secret", File.ReadAllText(outside));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(archiveRoot, "demo", "stage", "uploads"), "*.zip"));
    }

    [Fact]
    public async Task CleanupMarkerSurvivesRestartAndBlocksRepeatedRestoresUntilCleanupSucceeds()
    {
        var volumeRoot = Path.Combine(_root, "volumes");
        var archiveRoot = Path.Combine(_root, "archives");
        var key = new HostedVolumeKey("demo", "stage", "uploads");
        var engine = new LocalHostedVolumeBackupEngine(volumeRoot, archiveRoot,
            NullLogger<LocalHostedVolumeBackupEngine>.Instance,
            _ => throw new IOException("busy"), _clock);
        var path = engine.VolumePath(key);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "data.txt"), "backup");
        var backup = await engine.BackupAsync(key, _clock.GetUtcNow(), CancellationToken.None);
        File.WriteAllText(Path.Combine(path, "data.txt"), "old-live");
        await engine.RestoreAsync(backup, CancellationToken.None);
        Assert.True(File.Exists(path + ".restore-pending"));
        Assert.Single(Directory.EnumerateDirectories(Path.GetDirectoryName(path)!, "uploads.previous-*"));

        var restarted = new LocalHostedVolumeBackupEngine(volumeRoot, archiveRoot,
            NullLogger<LocalHostedVolumeBackupEngine>.Instance,
            _ => throw new IOException("still busy"), _clock);
        await Assert.ThrowsAsync<IOException>(() => restarted.RestoreAsync(backup, CancellationToken.None));
        Assert.Single(Directory.EnumerateDirectories(Path.GetDirectoryName(path)!, "uploads.previous-*"));
        Assert.Equal("backup", File.ReadAllText(Path.Combine(path, "data.txt")));

        var recovered = new LocalHostedVolumeBackupEngine(volumeRoot, archiveRoot, clock: _clock);
        Assert.False(File.Exists(path + ".restore-pending"));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(path)!, "uploads.previous-*"));
        await recovered.RestoreAsync(backup, CancellationToken.None);
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(path)!, "uploads.previous-*"));
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
