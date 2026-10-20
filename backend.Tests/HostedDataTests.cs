using System.Text.Json;
using AgentStudio.Projects;
using AgentStudio.Deploy;
using AgentStudio.Secrets;
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
}
