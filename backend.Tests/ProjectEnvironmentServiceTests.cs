using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Pipeline;
using AgentStudio.Deploy;
using AgentStudio.Projects;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectEnvironmentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deploy-env-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly string _artifact = "registry.example/app@sha256:" + new string('a', 64);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void ParsesRepositoryDeclarations_AndNeverExposesSecretValues()
    {
        var (service, _, _) = Build();
        var declarations = service.Declarations("demo");
        Assert.NotNull(declarations);
        Assert.Equal(["stage", "production"], declarations!.Select(item => item.Id));
        Assert.Equal(["DATABASE_URL"], declarations[1].SecretRefs);
        Assert.DoesNotContain("sensitive-value", JsonSerializer.Serialize(service.Read("demo")));
    }

    [Fact]
    public async Task ScheduledVolumeBackup_UsesInjectedClock_AndRestoreIsAudited()
    {
        var (environments, repo, _) = Build();
        File.WriteAllText(Path.Combine(repo, "docs", "deployments", "environments", "stage.json"),
            JsonSerializer.Serialize(new
            {
                id = "stage", name = "Stage", kind = "stage", targetHostId = "stage-host",
                address = "https://stage.example", template = "container", destination = "8081",
                healthUrl = "https://stage.example/health", approval = "person", secretRefs = Array.Empty<string>(),
                data = new { volumes = new[] { new { name = "uploads", mountPath = "/app/uploads" } } },
            }));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
        }).Build();
        var engine = new LocalHostedVolumeBackupEngine(Path.Combine(_root, "volumes"), Path.Combine(_root, "archives"));
        var key = new HostedVolumeKey("demo", "stage", "uploads");
        Directory.CreateDirectory(engine.VolumePath(key));
        File.WriteAllText(Path.Combine(engine.VolumePath(key), "data.txt"), "before");
        var backups = new HostedVolumeBackupService(config, environments, engine, _clock,
            NullLogger<HostedVolumeBackupService>.Instance);
        await backups.SetScheduleAsync("demo", "stage", "uploads", 13, "owner", CancellationToken.None);
        await backups.RunDueAsync(CancellationToken.None);
        Assert.Empty((await backups.ReadAsync("demo", CancellationToken.None))!.Schedules.Single().Backups);
        _clock.Advance(TimeSpan.FromHours(1));
        await backups.RunDueAsync(CancellationToken.None);
        var state = (await backups.ReadAsync("demo", CancellationToken.None))!;
        var backup = Assert.Single(Assert.Single(state.Schedules).Backups);
        File.WriteAllText(Path.Combine(engine.VolumePath(key), "data.txt"), "after");
        await backups.RestoreAsync("demo", "stage", "uploads", backup.Id, "owner", CancellationToken.None);
        Assert.Equal("before", File.ReadAllText(Path.Combine(engine.VolumePath(key), "data.txt")));
        state = (await backups.ReadAsync("demo", CancellationToken.None))!;
        Assert.Contains(state.Audit, entry => entry.Action == "backup" && entry.Outcome == "succeeded");
        Assert.Contains(state.Audit, entry => entry.Action == "restore" && entry.Actor == "owner"
            && entry.BackupId == backup.Id && entry.Outcome == "started");
        Assert.Contains(state.Audit, entry => entry.Action == "restore" && entry.Actor == "owner"
            && entry.BackupId == backup.Id && entry.Outcome == "succeeded");
    }

    [Fact]
    public async Task VolumeBackupRetentionBoundsMetadataAndArchives()
    {
        var (environments, config, key, path) = PrepareVolumeBackups();
        var engine = new LocalHostedVolumeBackupEngine(Path.Combine(_root, "volumes"),
            Path.Combine(_root, "archives"), clock: _clock);
        var service = new HostedVolumeBackupService(config, environments, engine, _clock,
            NullLogger<HostedVolumeBackupService>.Instance);
        var ids = new List<string>();
        for (var index = 0; index < HostedVolumeSchedulePolicy.MaxBackups + 2; index++)
        {
            File.WriteAllText(Path.Combine(path, "data.txt"), index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ids.Add((await service.BackupAsync(key.Project, key.Environment, key.Volume, "owner", CancellationToken.None)).Id);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        var state = (await service.ReadAsync(key.Project, CancellationToken.None))!;
        var retained = Assert.Single(state.Schedules).Backups.Select(item => item.Id).ToArray();
        Assert.Equal(ids.Skip(2), retained);
        var archives = Directory.EnumerateFiles(Path.Combine(_root, "archives", "demo", "stage", "uploads"), "*.zip")
            .Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(retained.Order(StringComparer.Ordinal), archives);
    }

    [Fact]
    public async Task FailedPruneRemainsPendingAcrossRestartAndBlocksFurtherArchives()
    {
        var (environments, config, key, _) = PrepareVolumeBackups();
        var local = new LocalHostedVolumeBackupEngine(Path.Combine(_root, "volumes"),
            Path.Combine(_root, "archives"), clock: _clock);
        var engine = new FailingPruneEngine(local);
        var service = new HostedVolumeBackupService(config, environments, engine, _clock,
            NullLogger<HostedVolumeBackupService>.Instance);
        for (var index = 0; index < HostedVolumeSchedulePolicy.MaxBackups; index++)
            await service.BackupAsync(key.Project, key.Environment, key.Volume, "owner", CancellationToken.None);

        engine.FailPrune = true;
        await service.BackupAsync(key.Project, key.Environment, key.Volume, "owner", CancellationToken.None);
        var archivePath = Path.Combine(_root, "archives", "demo", "stage", "uploads");
        Assert.Equal(HostedVolumeSchedulePolicy.MaxBackups + 1,
            Directory.EnumerateFiles(archivePath, "*.zip").Count());
        var persisted = (await service.ReadAsync(key.Project, CancellationToken.None))!.Schedules.Single();
        Assert.True(persisted.CleanupPending);
        Assert.Contains("busy", persisted.LastError);

        service = new HostedVolumeBackupService(config, environments, engine, _clock,
            NullLogger<HostedVolumeBackupService>.Instance);
        var creations = engine.BackupCount;
        for (var index = 0; index < 3; index++)
            await Assert.ThrowsAsync<IOException>(() => service.BackupAsync(
                key.Project, key.Environment, key.Volume, "owner", CancellationToken.None));
        Assert.Equal(creations, engine.BackupCount);
        Assert.Equal(HostedVolumeSchedulePolicy.MaxBackups + 1,
            Directory.EnumerateFiles(archivePath, "*.zip").Count());

        engine.FailPrune = false;
        await service.BackupAsync(key.Project, key.Environment, key.Volume, "owner", CancellationToken.None);
        Assert.Equal(HostedVolumeSchedulePolicy.MaxBackups,
            Directory.EnumerateFiles(archivePath, "*.zip").Count());
        persisted = (await service.ReadAsync(key.Project, CancellationToken.None))!.Schedules.Single();
        Assert.False(persisted.CleanupPending);
        Assert.Null(persisted.LastError);
        Assert.Equal(HostedVolumeSchedulePolicy.MaxBackups, persisted.Backups.Count);
    }

    [Fact]
    public async Task CancelledBackupRemovesTemporaryArchive()
    {
        var (_, _, key, _) = PrepareVolumeBackups();
        var reader = new BlockingReadStream(_clock);
        var engine = new LocalHostedVolumeBackupEngine(Path.Combine(_root, "volumes"),
            Path.Combine(_root, "archives"), NullLogger<LocalHostedVolumeBackupEngine>.Instance,
            path => Directory.Delete(path, recursive: true), _clock, _ => reader);
        using var cancellation = new CancellationTokenSource();
        var backup = engine.BackupAsync(key, _clock.GetUtcNow(), cancellation.Token);
        await reader.Entered;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backup);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "archives", "demo", "stage", "uploads")));
    }

    [Fact]
    public async Task TimedOutBackupDoesNotBlockOtherVolumeOperations()
    {
        var (environments, config, key, _) = PrepareVolumeBackups();
        var reader = new BlockingReadStream(_clock);
        var engine = new LocalHostedVolumeBackupEngine(Path.Combine(_root, "volumes"),
            Path.Combine(_root, "archives"), NullLogger<LocalHostedVolumeBackupEngine>.Instance,
            path => Directory.Delete(path, recursive: true), _clock,
            path => Path.GetFileName(Path.GetDirectoryName(path)) == key.Volume
                ? reader : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
        var service = new HostedVolumeBackupService(config, environments, engine, _clock,
            NullLogger<HostedVolumeBackupService>.Instance);
        var backup = service.BackupAsync(key.Project, key.Environment, key.Volume, "owner", CancellationToken.None);
        await reader.Entered;

        var other = await service.BackupAsync(key.Project, key.Environment, "logs", "owner", CancellationToken.None);
        Assert.Equal("logs", other.Volume.Volume);

        var schedule = service.SetScheduleAsync(key.Project, key.Environment, key.Volume, 8, "owner", CancellationToken.None);
        Assert.True(schedule.IsCompleted);
        await schedule;
        _clock.Advance(LocalHostedVolumeBackupEngine.BackupTimeout);
        await Assert.ThrowsAsync<TimeoutException>(() => backup);
        var state = (await service.ReadAsync(key.Project, CancellationToken.None))!;
        Assert.Contains("time limit", state.Schedules.Single(item => item.Volume == key.Volume).LastError);
        Assert.Single(state.Schedules.Single(item => item.Volume == "logs").Backups);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "archives", "demo", "stage", "uploads")));
    }

    [Fact]
    public async Task StalledRestoreDoesNotBlockOtherVolumeOrStateOperations()
    {
        var (environments, config, key, _) = PrepareVolumeBackups();
        var engine = new BlockingRestoreEngine();
        var service = new HostedVolumeBackupService(config, environments, engine, _clock,
            NullLogger<HostedVolumeBackupService>.Instance);
        var backup = await service.BackupAsync(key.Project, key.Environment, key.Volume, "owner", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var restore = service.RestoreAsync(key.Project, key.Environment, key.Volume, backup.Id,
            "owner", cancellation.Token);
        await engine.RestoreEntered;

        Task<HostedVolumeBackup>? sameVolume = null;
        try
        {
            var otherVolume = service.BackupAsync(key.Project, key.Environment, "logs", "owner", CancellationToken.None);
            Assert.True(otherVolume.IsCompleted, "A stalled restore held the project state lock.");
            Assert.Equal("logs", (await otherVolume).Volume.Volume);

            var schedule = service.SetScheduleAsync(key.Project, key.Environment, key.Volume, 8,
                "owner", CancellationToken.None);
            Assert.True(schedule.IsCompleted, "A stalled restore blocked schedule changes.");
            await schedule;
            var state = service.ReadAsync(key.Project, CancellationToken.None);
            Assert.True(state.IsCompleted, "A stalled restore blocked state reads.");
            Assert.Contains((await state)!.Audit, entry => entry.Action == "restore" && entry.Outcome == "started");

            sameVolume = service.BackupAsync(key.Project, key.Environment, key.Volume, "owner", CancellationToken.None);
            Assert.False(sameVolume.IsCompleted, "A backup must wait for a restore of the same volume.");
        }
        finally { cancellation.Cancel(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
        Assert.NotNull(sameVolume);
        await sameVolume;
        var completed = (await service.ReadAsync(key.Project, CancellationToken.None))!;
        Assert.Contains(completed.Audit, entry => entry.Action == "restore" && entry.Outcome == "cancelled");
        Assert.Single(completed.Schedules.Single(item => item.Volume == "logs").Backups);
    }

    [Fact]
    public void ConsecutiveDeployJobsRetainVolumeIdentityAndDatabaseBinding()
    {
        var (service, repo, _) = Build();
        File.WriteAllText(Path.Combine(repo, "docs", "deployments", "environments", "stage.json"),
            JsonSerializer.Serialize(new
            {
                id = "stage", name = "Stage", kind = "stage", targetHostId = "stage-host",
                address = "https://stage.example", template = "container", destination = "8081",
                healthUrl = "https://stage.example/health", approval = "person", secretRefs = Array.Empty<string>(),
                data = new
                {
                    database = new { secretRef = "secret://demo/stage/DATABASE_URL", environmentVariable = "DATABASE_URL" },
                    volumes = new[] { new { name = "uploads", mountPath = "/app/uploads" } },
                },
            }));
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "Declare hosted data");
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        var jobs = new List<DeployJob>();
        foreach (var marker in new[] { 'b', 'c' })
        {
            var artifact = "registry.example/app@sha256:" + new string(marker, 64);
            Assert.True(QueueGateRelease(service, artifact, commit));
            var stage = service.Claim("stage-host")!;
            jobs.Add(stage);
            Assert.Equal(["DATABASE_URL"], stage.SecretRefs);
            Assert.True(service.Report("demo", stage.Id, new(stage.LeaseToken!, true, "health-ok")));
            Assert.True(service.Decide("demo", artifact, true, "operator"));
            var production = service.Claim("prod-host")!;
            Assert.True(service.Report("demo", production.Id, new(production.LeaseToken!, true, "health-ok")));
        }
        Assert.Equal(jobs[0].Data.Database, jobs[1].Data.Database);
        Assert.Equal(jobs[0].Data.Volumes.Select(item => (item.Name, item.MountPath)),
            jobs[1].Data.Volumes.Select(item => (item.Name, item.MountPath)));
        Assert.Equal("uploads", Assert.Single(jobs[1].Data.Volumes).Name);
        Assert.Equal("/app/uploads", jobs[1].Data.Volumes[0].MountPath);
        Assert.Equal("secret://demo/stage/DATABASE_URL", jobs[1].Data.Database!.SecretRef);
    }

    [Fact]
    public void PreviewDeclarationCanSpecifyItsOwnHostAndSecretReferences()
    {
        var (service, repo, _) = Build();
        var file = Path.Combine(repo, "docs", "deployments", "environments", "preview.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            id = "preview", name = "Preview", kind = "preview", targetHostId = "preview-host",
            address = "https://preview.internal", template = "static",
            destination = "/srv/agent-studio/previews", secretRefs = new[] { "TEST_DATA" },
        }));
        Git(repo, "add", "docs/deployments/environments/preview.json");
        Git(repo, "commit", "-m", "declare preview");
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        var preview = Assert.Single(service.DeclarationsAtCommit("demo", commit)!,
            declaration => declaration.Kind == "preview");
        Assert.Equal("preview-host", preview.TargetHostId);
        Assert.Equal(["TEST_DATA"], preview.SecretRefs);
        Assert.Equal("static", preview.Template);
    }

    [Fact]
    public void PreviewJobUsesTheNormalLeaseAndRequiresScreenshotAfterDeploy()
    {
        using var factory = QuotaFactory();
        var (quota, workspaceId) = QuotaFor(factory, new AgentStudio.Quota.QuotaLimits(null, null, 1));
        var (service, repo, _) = Build(quota: quota);
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        var spec = new PreviewJobSpec("demo", "agt-3180", commit, "preview-host",
            "https://agt-3180.demo.preview.internal", "static", "/srv/agent-studio/previews/demo/agt-3180",
            ["npm run build"], ["TEST_DATA"], 8080)
        { SourceRepository = repo, SourceRef = commit, ArtifactPath = "dist" };
        var queued = service.QueuePreview(spec);
        Assert.Equal("preview", queued.Kind);
        Assert.Equal(commit, queued.SourceCommit);
        Assert.Equal(["TEST_DATA"], queued.SecretRefs);
        var claimed = service.Claim("preview-host")!;
        Assert.Equal(queued.Id, claimed.Id);
        Assert.False(service.Report("demo", claimed.Id, new DeployReportRequest(claimed.LeaseToken!, true, "health-ok")));
        var png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC";
        Assert.True(service.Report("demo", claimed.Id,
            new DeployReportRequest(claimed.LeaseToken!, true, "health-ok", ScreenshotBase64: png)));
        var ready = service.PreviewJob("demo", claimed.Id)!;
        Assert.Equal("healthy", ready.Status);
        Assert.NotNull(ready.ScreenshotUrl);
        Assert.NotNull(service.PreviewScreenshotPath("demo", "agt-3180"));
        Assert.Equal(1, quota.Snapshot(workspaceId).Usage.HostingSlots);
        _clock.Advance(TimeSpan.FromMinutes(1));
        service.ReconcileHostingSlots();
        Assert.Equal(1, quota.Snapshot(workspaceId).Usage.HostingSlots);
        var removal = service.QueuePreviewRemoval(spec);
        Assert.Empty(removal.SecretRefs);
        var removalClaim = service.Claim("preview-host")!;
        Assert.Equal(removal.Id, removalClaim.Id);
        Assert.True(service.Report("demo", removal.Id,
            new DeployReportRequest(removalClaim.LeaseToken!, true, "health-ok")));
        Assert.Null(service.PreviewScreenshotPath("demo", "agt-3180"));
        Assert.Equal(0, quota.Snapshot(workspaceId).Usage.HostingSlots);
    }

    [Fact]
    public void PreviewJobsCannotBeQueuedOrClaimedOnAProductionHost()
    {
        var (service, repo, _) = Build();
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        PreviewJobSpec Spec(string host) => new("demo", "agt-3180", commit, host,
            "https://agt-3180.demo.preview.internal", "static",
            "/srv/agent-studio/previews/demo/agt-3180", ["npm run build"], [], 8080)
        { SourceRepository = repo, SourceRef = commit, ArtifactPath = "dist" };

        Assert.Throws<InvalidOperationException>(() => service.QueuePreview(Spec("prod-host")));
        Assert.Throws<InvalidOperationException>(() => service.QueuePreviewRemoval(Spec("prod-host")));

        var queued = service.QueuePreview(Spec("preview-host"));
        var declaration = Path.Combine(repo, "docs", "deployments", "environments", "production.json");
        using var production = JsonDocument.Parse(File.ReadAllText(declaration));
        var values = production.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
        values["targetHostId"] = "preview-host";
        File.WriteAllText(declaration, JsonSerializer.Serialize(values));

        Assert.Null(service.Claim("preview-host"));
        Assert.Equal("queued", service.PreviewJob("demo", queued.Id)!.Status);
    }

    [Fact]
    public void ProjectNameWithSpaces_PersistsAndReadsEnvironmentState()
    {
        var (service, repo, _) = Build(project: "Operator Demo");
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        WriteReleaseEvidence("Operator Demo", _artifact, commit);
        Assert.True(service.RegisterGateArtifact("Operator Demo", _artifact, commit));
        Assert.True(service.QueueRelease("Operator Demo", _artifact, commit));
        Assert.Equal("stage", Assert.Single(service.Read("Operator Demo")!.Environments
            .Where(item => item.PendingArtifact is not null)).Declaration.Id);
        Assert.Equal("stage", service.Claim("stage-host")!.EnvironmentId);
    }

    [Fact]
    public void HostMove_RoutesClaimToCurrentHostWithoutChangingReleaseSshTarget()
    {
        var (service, repo, _) = Build();
        var snapshot = service.Declarations("demo")!.Single(item => item.Id == "stage") with
        {
            SshSteps = [new ProjectDeployStep("container", "app", "stage-host", null, null, null,
                [], ["staging"]) { ComposeFile = "/srv/apps/web/compose.yml", Service = "web" }],
        };
        var declarationPath = Path.Combine(repo, "docs", "deployments", "environments", "stage.json");
        File.WriteAllText(declarationPath, File.ReadAllText(declarationPath)
            .Replace("stage-host", "new-host", StringComparison.Ordinal));

        var queued = service.QueueDeployment("demo", snapshot, _artifact, "operator", rollback: false);
        Assert.NotNull(queued);
        Assert.Equal("new-host", queued.HostId);
        Assert.Equal("stage-host", Assert.Single(queued.SshSteps).Target);
        Assert.Null(service.Claim("stage-host"));
        var claimed = service.Claim("new-host");
        Assert.Equal("stage-host", Assert.Single(claimed!.SshSteps).Target);
    }

    [Fact]
    public void Lease_renewal_keeps_claim_live_and_old_token_is_fenced_after_reclaim()
    {
        var (service, repo, _) = Build();
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        Assert.True(QueueGateRelease(service, _artifact, commit));
        var first = service.Claim("stage-host")!;

        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True(service.RenewClaim("demo", first.Id, first.LeaseToken!));
        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.NotNull(service.FindClaimedJob("demo", first.Id, first.LeaseToken!));
        Assert.Null(service.Claim("stage-host"));

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(service.RenewClaim("demo", first.Id, first.LeaseToken!));
        Assert.False(service.Report("demo", first.Id, new(first.LeaseToken!, true, "health-ok")));
        var restarted = service.Claim("stage-host")!;
        Assert.NotEqual(first.LeaseToken, restarted.LeaseToken);
        Assert.False(service.RenewClaim("demo", first.Id, first.LeaseToken!));
        Assert.True(service.RenewClaim("demo", restarted.Id, restarted.LeaseToken!));
    }

    [Fact]
    public void ReleaseRequiresExactGateBuiltArtifactForCommit()
    {
        var (service, repo, _) = Build();
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        Assert.False(service.QueueRelease("demo", _artifact, commit));
        Assert.False(service.RegisterGateArtifact("demo", _artifact + "\n", commit));
        Assert.False(service.RegisterGateArtifact("demo", _artifact, commit));
        WriteReleaseEvidence("demo", _artifact, commit);
        Assert.True(service.RegisterGateArtifact("demo", _artifact, commit));
        Assert.False(service.QueueRelease("demo", "registry.example/other@sha256:" + new string('b', 64), commit));
        Assert.True(service.QueueRelease("demo", _artifact, commit));
    }

    [Fact]
    public void ReleaseCredentialCannotAttestImageWithoutIndependentBuildAndGateEvidence()
    {
        var (service, repo, _) = Build();
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        var directory = Path.Combine(_root, "release-evidence", commit);
        Assert.False(service.RegisterGateArtifact("demo", _artifact, commit));
        WriteReleaseEvidence("demo", _artifact, commit);
        File.WriteAllText(Path.Combine(directory, "full-gate.log"), "PROMOTION_FULL_GATE=failed\n");
        Assert.False(service.RegisterGateArtifact("demo", _artifact, commit));
        File.WriteAllText(Path.Combine(directory, "full-gate.log"), "PROMOTION_FULL_GATE=passed\n");
        File.WriteAllText(Path.Combine(directory, "release.tar.gz"), "different bytes");
        Assert.False(service.RegisterGateArtifact("demo", _artifact, commit));
        WriteReleaseEvidence("demo", _artifact, commit);
        Assert.True(service.RegisterGateArtifact("demo", _artifact, commit));
    }

    [Fact]
    public void GateRegistration_BindsDigestToProjectCommitRunTemplateAndBuilder()
    {
        var (service, repo, _) = Build();
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        WriteReleaseEvidence("demo", _artifact, commit);
        var registry = new ReleaseArtifactRegistry(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TaskRepository"] = Path.Combine(_root, "workspace"),
                ["Deploy:ReleaseEvidenceRoot"] = Path.Combine(_root, "release-evidence") }).Build());

        Assert.True(service.RegisterGateArtifact("demo", _artifact, commit, "build-runner"));
        var record = registry.Verified("demo", commit, _artifact, "container");
        Assert.NotNull(record);
        Assert.Equal(("release/test-gate", "build-runner", "external"),
            (record.GateRunId, record.BuilderIdentity, record.Location));
        Assert.Null(registry.Verified("other", commit, _artifact, "container"));
        Assert.Null(registry.Verified("demo", new string('b', 40), _artifact, "container"));
        Assert.Null(registry.Verified("demo", commit, _artifact, "static"));
        Assert.False(service.RegisterGateArtifact("demo", _artifact, commit, "different-builder"));
        Assert.True(service.CanQueueRelease("demo", _artifact, commit));

        var path = Directory.GetFiles(Path.Combine(_root, "workspace", "release-registry", "records"),
            "*.json", SearchOption.AllDirectories).Single();
        File.WriteAllText(path, File.ReadAllText(path).Replace("build-runner", "imposter", StringComparison.Ordinal));
        Assert.False(service.CanQueueRelease("demo", _artifact, commit));
    }

    [Fact]
    public void GateRegistration_RefusesEvidenceWithoutRunIdentity()
    {
        var (service, repo, _) = Build();
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        WriteReleaseEvidence("demo", _artifact, commit);
        var path = Path.Combine(_root, "release-evidence", commit, "promotion-record.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"gateRunId\":\"release/test-gate\",", "",
            StringComparison.Ordinal));
        Assert.False(service.RegisterGateArtifact("demo", _artifact, commit));
    }

    [Fact]
    public void BuiltInRegistry_StoresPackageByDigestAndRefusesChangedBytes()
    {
        var (service, repo, _) = Build();
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        var bytes = Encoding.UTF8.GetBytes("gate-built-release-for-" + commit);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var artifact = "release-package@sha256:" + digest;
        WriteReleaseEvidence("demo", artifact, commit);
        Assert.True(service.RegisterGateArtifact("demo", artifact, commit, "build-runner"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["TaskRepository"] = Path.Combine(_root, "workspace") }).Build();
        var registry = new ReleaseArtifactRegistry(config);
        var record = registry.Verified("demo", commit, artifact, "static");
        Assert.NotNull(record);
        var blob = registry.VerifiedBlob(record);
        Assert.NotNull(blob);
        Assert.Equal(bytes, File.ReadAllBytes(blob));
        Assert.False(service.QueueRelease("demo", artifact, commit)); // Both declared environments require a container.
        File.WriteAllText(blob, "changed");
        Assert.Null(registry.Verified("demo", commit, artifact, "static"));
        Assert.False(service.CanQueueRelease("demo", artifact, commit));
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task PromotionRoutes_AcceptRegistryDigestInRequestBody(string action)
    {
        await using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = Path.Combine(_root, "workspace"),
                    ["Logging:BackendFile:LogDirectory"] = Path.Combine(_root, "logs"),
                    ["Security:Profile"] = "local",
                }));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var password = $"test-{Guid.NewGuid():N}!";
        using var bootstrap = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            username = "local.owner", password, displayName = "Local Owner",
        });
        bootstrap.EnsureSuccessStatusCode();
        var auth = await bootstrap.Content.ReadFromJsonAsync<AuthStatusResponse>();
        client.DefaultRequestHeaders.Add("X-CSRF-Token", auth!.CsrfToken);

        using var response = await client.PostAsJsonAsync(
            $"/api/projects/demo/environments/promotions/{action}",
            new PromotionDecisionRequest(_artifact));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Promotion is not pending", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GateReleaseRoute_RequiresDedicatedReleaseScope()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = Path.Combine(_root, "network-workspace"),
                    ["Security:Profile"] = "networked",
                    ["AllowedHosts"] = "studio.test",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test") });
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var body = new GateArtifactRequest("demo", _artifact, new string('a', 40));
        var engineEnrollment = security.CreateEnrollment(new RunnerEnrollmentRequest("engine",
            [RunnerScopes.Orchestration], null, null));
        var engine = security.EnrollRunner(engineEnrollment.Code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", engine.Secret);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/deploy/gate-releases", body)).StatusCode);
        var deployEnrollment = security.CreateEnrollment(new RunnerEnrollmentRequest("deploy",
            [RunnerScopes.Deploy], null, null));
        var deploy = security.EnrollRunner(deployEnrollment.Code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deploy.Secret);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/deploy/gate-releases", body)).StatusCode);
        var releaseEnrollment = security.CreateEnrollment(new RunnerEnrollmentRequest("release-gate",
            [RunnerScopes.Release], null, null));
        var release = security.EnrollRunner(releaseEnrollment.Code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", release.Secret);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/deploy/gate-releases", body)).StatusCode);
    }

    [Fact]
    public async Task GateReleaseRoute_LocalProfileAuthenticatesOnlyTheReleaseCredential()
    {
        const string project = "Operator Demo";
        var (_, repo, watch) = Build(project: project);
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "declarations");
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        WriteReleaseEvidence(project, _artifact, commit);
        await using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("TaskRepository", Path.Combine(_root, "workspace"));
            builder.UseSetting("Deploy:ReleaseEvidenceRoot", Path.Combine(_root, "release-evidence"));
            builder.UseSetting("WatchPaths:0:Name", project);
            builder.UseSetting("WatchPaths:0:Path", watch);
            builder.UseSetting("WatchPaths:0:RootPath", repo);
            builder.UseSetting("WatchPaths:0:RepositoryPath", repo);
            builder.UseSetting("Security:Profile", "local");
            builder.UseSetting("Logging:BackendFile:LogDirectory", Path.Combine(_root, "logs"));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
            });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var request = new GateArtifactRequest(project, _artifact, commit);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/deploy/gate-releases", request)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-credential");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/deploy/gate-releases", request)).StatusCode);
        var deploy = security.EnrollRunner(security.CreateEnrollment(new RunnerEnrollmentRequest("deploy-only",
            [RunnerScopes.Deploy], null, null)).Code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deploy.Secret);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/deploy/gate-releases", request)).StatusCode);
        var builder = security.EnrollRunner(security.CreateEnrollment(new RunnerEnrollmentRequest("release-builder",
            [RunnerScopes.Release], null, null)).Code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", builder.Secret);
        using var accepted = await client.PostAsJsonAsync("/api/deploy/gate-releases", request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var registered = factory.Services.GetRequiredService<ReleaseArtifactRegistry>()
            .Verified(project, commit, _artifact, "container");
        Assert.Equal(builder.Runner.Id, registered?.BuilderIdentity);
        Assert.Equal(project, factory.Services.GetRequiredService<ProjectEnvironmentService>()
            .Claim("stage-host", requireVerifiedRelease: true)?.Project);
    }

    [Fact]
    public void StageDeclaration_RequiresHealthUrl()
    {
        var (service, repo, _) = Build();
        var stage = Path.Combine(repo, "docs", "deployments", "environments", "stage.json");
        File.WriteAllText(stage, JsonSerializer.Serialize(new
        {
            id = "stage", name = "Stage", kind = "stage", targetHostId = "stage-host",
            address = "https://stage.example", template = "container", destination = "8081",
        }));
        Assert.Contains("health URL", Assert.Throws<JsonException>(() => service.Declarations("demo")).Message);
    }

    [Fact]
    public void ClaimedJob_IsFoundForSecretIssuanceOnlyWithItsLeaseUntilReportedOrStale()
    {
        var (service, repo, _) = Build();
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        Assert.True(QueueGateRelease(service, _artifact, commit));
        var stage = service.Claim("stage-host")!;

        Assert.Equal(stage.Id, service.FindClaimedJob("demo", stage.Id, stage.LeaseToken!)!.Id);
        Assert.Null(service.FindClaimedJob("demo", stage.Id, "other-token"));
        Assert.Null(service.FindClaimedJob("../demo", stage.Id, stage.LeaseToken!));
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(service.FindClaimedJob("demo", stage.Id, stage.LeaseToken!));

        var reclaimed = service.Claim("stage-host")!;
        Assert.True(service.Report("demo", reclaimed.Id, new(reclaimed.LeaseToken!, true, "health-ok")));
        Assert.Null(service.FindClaimedJob("demo", reclaimed.Id, reclaimed.LeaseToken!));
    }

    [Fact]
    public void BrokenDeclaration_IsAJsonError_AndDeclarationFilesKeepTheValidOnes()
    {
        var (service, repo, _) = Build();
        var stage = Path.Combine(repo, "docs", "deployments", "environments", "stage.json");
        foreach (var (broken, message) in new[]
        {
            (JsonSerializer.Serialize(new { id = "stage", kind = "stage" }), "requires targetHostId"),
            (JsonSerializer.Serialize(new { id = "stage", kind = "stage", targetHostId = "h", template = "container", destination = "8081", healthUrl = "https://stage.example/health", name = 5 }), "name must be a string"),
            (JsonSerializer.Serialize(new { id = "stage", kind = "stage", targetHostId = "h", template = "container", secretRefs = "X" }),
                "secretRefs must be an array"),
            ("[]", "must be a JSON object"),
        })
        {
            File.WriteAllText(stage, broken);
            Assert.Contains(message, Assert.Throws<JsonException>(() => service.Declarations("demo")).Message);
            var files = service.DeclarationFiles("demo")!;
            Assert.Equal(["production", "stage"], files.Select(item => item.EnvironmentId));
            Assert.NotNull(files[0].Declaration);
            Assert.Null(files[1].Declaration);
            Assert.Contains(message, files[1].Error);
        }
    }

    [Fact]
    public void Release_Stages_ThenApprovesExactArtifact_AndCanRollBack()
    {
        var (service, repo, watch) = Build();
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        var folder = Path.Combine(watch, "completed", "card");
        IntegrationVerificationStore.Write(folder, new IntegrationVerificationRecord
        {
            State = IntegrationVerificationStates.Verified, Sha = commit, IntegrationBranch = "develop",
            RecordedAtUtc = _clock.GetUtcNow(),
        });
        Assert.True(QueueGateRelease(service, _artifact, commit));
        Assert.False(service.QueueRelease("demo", _artifact, commit));
        var stage = service.Claim("stage-host");
        Assert.NotNull(stage);
        Assert.Equal("stage", stage!.EnvironmentId);
        Assert.True(service.Report("demo", stage.Id, new(stage.LeaseToken!, true, "health-ok")));
        Assert.Equal("pending", Assert.Single(service.Read("demo")!.Promotions).Status);
        Assert.True(service.Decide("demo", _artifact, true, "operator"));
        var production = service.Claim("prod-host");
        Assert.NotNull(production);
        Assert.Equal(_artifact, production!.Artifact);
        Assert.Equal(["DATABASE_URL"], production.SecretRefs);
        Assert.True(service.Report("demo", production.Id, new(production.LeaseToken!, true, "health-ok")));
        Assert.Equal(_artifact, service.Read("demo")!.Environments.Single(item => item.Declaration.Id == "production").DeployedArtifact);
        Assert.False(service.Rollback("demo", "production", "operator"));
    }

    [Fact]
    public void FailedProductionHealth_QueuesPreviousArtifact_AndRedactsHostEvidence()
    {
        var (service, repo, watch) = Build();
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        IntegrationVerificationStore.Write(Path.Combine(watch, "card"), new IntegrationVerificationRecord
        { State = IntegrationVerificationStates.Verified, Sha = commit, IntegrationBranch = "develop" });
        var first = "registry.example/app@sha256:" + new string('b', 64);
        Deploy(first);
        Deploy(_artifact, failProduction: true);
        var rollback = service.Claim("prod-host");
        Assert.NotNull(rollback);
        Assert.Equal(first, rollback!.Artifact);
        Assert.True(service.Report("demo", rollback.Id, new(rollback.LeaseToken!, true, "health-ok")));
        var response = JsonSerializer.Serialize(service.Read("demo"));
        Assert.DoesNotContain("sensitive-value", response);
        Assert.DoesNotContain("raw-host-secret", response);
        Assert.Contains("health-ok", response);

        void Deploy(string artifact, bool failProduction = false)
        {
            Assert.True(QueueGateRelease(service, artifact, commit));
            var stage = service.Claim("stage-host")!;
            Assert.True(service.Report("demo", stage.Id, new(stage.LeaseToken!, true, "health-ok")));
            Assert.True(service.Decide("demo", artifact, true, "operator"));
            var production = service.Claim("prod-host")!;
            Assert.True(service.Report("demo", production.Id,
                new(production.LeaseToken!, !failProduction, "raw-host-secret=sensitive-value")));
        }
    }

    [Fact]
    public void OlderGuidedFailure_DoesNotQueueRollbackAfterNewerManualRollbackCompleted()
    {
        var (service, _, _) = Build();
        var stage = service.Declarations("demo")!.Single(item => item.Id == "stage");
        var first = "registry.example/app@sha256:" + new string('b', 64);
        var initial = service.QueueDeployment("demo", stage, first, "owner", rollback: false)!;
        var initialClaim = service.Claim("stage-host")!;
        Assert.Equal(initial.Id, initialClaim.Id);
        Assert.True(service.Report("demo", initial.Id, new(initialClaim.LeaseToken!, true, "health-ok")));

        var failed = service.QueueDeployment("demo", stage with { GuidedFailure = true }, _artifact,
            "owner", rollback: false)!;
        var failedClaim = service.Claim("stage-host")!;
        Assert.True(service.Report("demo", failed.Id,
            new(failedClaim.LeaseToken!, false, "health-http-failed")));
        var manual = service.QueueDeployment("demo", stage, first, "owner", rollback: true)!;
        var manualClaim = service.Claim("stage-host")!;
        Assert.True(service.Report("demo", manual.Id, new(manualClaim.LeaseToken!, true, "health-ok")));

        Assert.True(service.RollbackFailedJob("demo", failed.Id));
        Assert.Null(service.Claim("stage-host"));
        var jobs = service.Read("demo")!.Environments.Single(item => item.Declaration.Id == "stage");
        Assert.Equal(first, jobs.DeployedArtifact);
        Assert.Null(jobs.PendingArtifact);
        Assert.Equal("rolled-back", jobs.History[0].Status);
    }

    [Fact]
    public void Reject_PreventsProductionClaim_AndManualRollbackUsesPreviousDigest()
    {
        var (service, repo, watch) = Build();
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        IntegrationVerificationStore.Write(Path.Combine(watch, "card"), new IntegrationVerificationRecord
        { State = IntegrationVerificationStates.Verified, Sha = commit, IntegrationBranch = "develop" });
        var first = "registry.example/app@sha256:" + new string('b', 64);
        var second = "registry.example/app@sha256:" + new string('c', 64);
        foreach (var artifact in new[] { first, second })
        {
            Assert.True(QueueGateRelease(service, artifact, commit));
            var stage = service.Claim("stage-host")!;
            Assert.True(service.Report("demo", stage.Id, new(stage.LeaseToken!, true, "health-ok")));
            Assert.True(service.Decide("demo", artifact, true, "operator"));
            var production = service.Claim("prod-host")!;
            Assert.True(service.Report("demo", production.Id, new(production.LeaseToken!, true, "health-ok")));
        }
        Assert.True(QueueGateRelease(service, _artifact, commit));
        var rejectedStage = service.Claim("stage-host")!;
        Assert.True(service.Report("demo", rejectedStage.Id, new(rejectedStage.LeaseToken!, true, "health-ok")));
        Assert.True(service.Decide("demo", _artifact, false, "operator"));
        Assert.Null(service.Claim("prod-host"));
        Assert.True(service.Rollback("demo", "production", "operator"));
        var rollback = service.Claim("prod-host")!;
        Assert.Equal(first, rollback.Artifact);
        Assert.True(rollback.IsRollback);
        Assert.Equal("operator", rollback.RequestedBy);
        Assert.True(service.Report("demo", rollback.Id, new(rollback.LeaseToken!, true, "health-ok")));
        Assert.Equal("rolled-back", service.Read("demo")!.Environments.Single(item => item.Declaration.Id == "production").History[0].Status);
    }

    [Fact]
    public void TimedApproval_NeverQueuesProductionWithoutAPerson()
    {
        var (service, repo, watch) = Build(timedApproval: true);
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        IntegrationVerificationStore.Write(Path.Combine(watch, "card"), new IntegrationVerificationRecord
        { State = IntegrationVerificationStates.Verified, Sha = commit, IntegrationBranch = "develop" });
        var first = "registry.example/app@sha256:" + new string('b', 64);
        Assert.True(QueueGateRelease(service, first, commit));
        var firstStage = service.Claim("stage-host")!;
        Assert.True(service.Report("demo", firstStage.Id, new(firstStage.LeaseToken!, true, "health-ok")));
        // First production promotion always needs a person.
        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal("pending", service.Read("demo")!.Promotions.Last().Status);
        Assert.True(service.Decide("demo", first, true, "operator"));
        var firstProduction = service.Claim("prod-host")!;
        Assert.True(service.Report("demo", firstProduction.Id, new(firstProduction.LeaseToken!, true, "health-ok")));
        Assert.True(QueueGateRelease(service, _artifact, commit));
        var stage = service.Claim("stage-host")!;
        Assert.True(service.Report("demo", stage.Id, new(stage.LeaseToken!, true, "health-ok")));
        // BD-1: production always waits for a person, also under the timed rule.
        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal("pending", service.Read("demo")!.Promotions.Last().Status);
        Assert.Null(service.Claim("prod-host"));
        Assert.True(service.Decide("demo", _artifact, true, "operator"));
        Assert.Equal(_artifact, service.Claim("prod-host")!.Artifact);
    }

    [Fact]
    public void Hosting_slots_admit_deploys_meter_jobs_and_free_on_stop()
    {
        using var factory = QuotaFactory();
        var (quota, workspaceId) = QuotaFor(factory, new AgentStudio.Quota.QuotaLimits(null, null, 1));
        var (service, repo, watch) = Build(quota: quota);
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        IntegrationVerificationStore.Write(Path.Combine(watch, "card"), new IntegrationVerificationRecord
        { State = IntegrationVerificationStates.Verified, Sha = commit, IntegrationBranch = "develop" });

        Assert.True(QueueGateRelease(service, _artifact, commit));
        var stage = service.Claim("stage-host")!;
        Assert.True(stage.ReservedSlot);
        _clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(service.Report("demo", stage.Id, new(stage.LeaseToken!, true, "health-ok")));
        var running = quota.Snapshot(workspaceId);
        Assert.Equal(1, running.Usage.HostingSlots);
        Assert.Equal(0.05m, running.Usage.ComputeCoreHours);

        // Production is not running yet and the only slot is taken by stage.
        var refused = Assert.Throws<DeployQuotaRefusedException>(() => service.Decide("demo", _artifact, true, "operator"));
        Assert.Contains("All 1 hosting slots are in use", refused.Message, StringComparison.Ordinal);
        Assert.Equal("pending", Assert.Single(service.Read("demo")!.Promotions).Status);

        Assert.True(service.Stop("demo", "stage", "operator"));
        // The stop waits for the stage host; until it acknowledges, the environment keeps its slot.
        Assert.Equal(1, quota.Snapshot(workspaceId).Usage.HostingSlots);
        var stop = service.Claim("stage-host")!;
        Assert.True(stop.IsStop);
        Assert.True(service.Report("demo", stop.Id, new(stop.LeaseToken!, true, "stop-ok")));
        Assert.Equal(0, quota.Snapshot(workspaceId).Usage.HostingSlots);
        Assert.Equal("stopped", service.Read("demo")!.Environments.Single(item => item.Declaration.Id == "stage").Health);
        Assert.True(service.Decide("demo", _artifact, true, "operator"));
        var production = service.Claim("prod-host")!;
        Assert.True(service.Report("demo", production.Id, new(production.LeaseToken!, true, "health-ok")));
        Assert.Equal(1, quota.Snapshot(workspaceId).Usage.HostingSlots);
    }

    [Fact]
    public void Deploy_claim_waits_at_the_slot_boundary_and_a_failed_first_deploy_returns_its_slot()
    {
        using var factory = QuotaFactory();
        var (quota, workspaceId) = QuotaFor(factory, new AgentStudio.Quota.QuotaLimits(null, null, 1));
        var (service, repo, watch) = Build(quota: quota);
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        IntegrationVerificationStore.Write(Path.Combine(watch, "card"), new IntegrationVerificationRecord
        { State = IntegrationVerificationStates.Verified, Sha = commit, IntegrationBranch = "develop" });
        Assert.True(QueueGateRelease(service, _artifact, commit));
        // Another deploy takes the last slot between queueing and claiming.
        Assert.True(quota.ReserveProjectEnvironment("demo", "website"));

        Assert.Null(service.Claim("stage-host"));
        Assert.Equal("queued", Assert.Single(service.Read("demo")!.Jobs).Status);

        quota.SetProjectEnvironment("demo", "website", false);
        var stage = service.Claim("stage-host")!;
        Assert.True(service.Report("demo", stage.Id, new(stage.LeaseToken!, false, "health-http-failed")));
        Assert.Equal(0, quota.Snapshot(workspaceId).Usage.HostingSlots);
    }

    [Fact]
    public void Healthy_deploy_keeps_its_slot_through_the_minute_reconciliation()
    {
        using var factory = QuotaFactory();
        var (quota, workspaceId) = QuotaFor(factory, new AgentStudio.Quota.QuotaLimits(null, null, 1));
        var (service, repo, watch) = Build(quota: quota);
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        IntegrationVerificationStore.Write(Path.Combine(watch, "card"), new IntegrationVerificationRecord
        { State = IntegrationVerificationStates.Verified, Sha = commit, IntegrationBranch = "develop" });
        Assert.True(QueueGateRelease(service, _artifact, commit));
        var stage = service.Claim("stage-host")!;
        Assert.True(stage.ReservedSlot);
        Assert.True(service.Report("demo", stage.Id, new(stage.LeaseToken!, true, "health-ok")));

        // The report settled the reservation; the running environment now holds the slot.
        Assert.False(Assert.Single(service.Read("demo")!.Jobs).ReservedSlot);
        for (var minute = 0; minute < 10; minute++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            service.ReconcileHostingSlots();
        }
        Assert.Equal(1, quota.Snapshot(workspaceId).Usage.HostingSlots);
        Assert.Equal("healthy", service.Read("demo")!.Environments.Single(item => item.Declaration.Id == "stage").Health);
    }

    [Fact]
    public void Unconfirmed_stop_leaves_the_environment_running_and_its_slot_occupied()
    {
        using var factory = QuotaFactory();
        var (quota, workspaceId) = QuotaFor(factory, new AgentStudio.Quota.QuotaLimits(null, null, 1));
        var (service, repo, watch) = Build(quota: quota);
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        IntegrationVerificationStore.Write(Path.Combine(watch, "card"), new IntegrationVerificationRecord
        { State = IntegrationVerificationStates.Verified, Sha = commit, IntegrationBranch = "develop" });
        Assert.True(QueueGateRelease(service, _artifact, commit));
        var stage = service.Claim("stage-host")!;
        Assert.True(service.Report("demo", stage.Id, new(stage.LeaseToken!, true, "health-ok")));

        Assert.True(service.Stop("demo", "stage", "operator"));
        Assert.False(service.Stop("demo", "stage", "operator"));
        // Another host cannot claim the stop, and an unclaimed stop changes nothing.
        Assert.Null(service.Claim("prod-host"));
        service.ReconcileHostingSlots();
        Assert.Equal(1, quota.Snapshot(workspaceId).Usage.HostingSlots);
        Assert.Equal("healthy", service.Read("demo")!.Environments.Single(item => item.Declaration.Id == "stage").Health);

        // The host fails the stop: still running and counted.
        var failed = service.Claim("stage-host")!;
        Assert.True(service.Report("demo", failed.Id, new(failed.LeaseToken!, false, "stop-failed")));
        Assert.Equal(1, quota.Snapshot(workspaceId).Usage.HostingSlots);
        Assert.Equal("healthy", service.Read("demo")!.Environments.Single(item => item.Declaration.Id == "stage").Health);

        // A claimed stop whose host never reports is lost without stopping anything.
        Assert.True(service.Stop("demo", "stage", "operator"));
        Assert.True(service.Claim("stage-host")!.IsStop);
        _clock.Advance(ProjectEnvironmentService.HostLostAfter + TimeSpan.FromMinutes(1));
        service.ReconcileHostingSlots();
        Assert.Null(service.Claim("stage-host"));
        Assert.Equal(1, quota.Snapshot(workspaceId).Usage.HostingSlots);
        Assert.Equal("healthy", service.Read("demo")!.Environments.Single(item => item.Declaration.Id == "stage").Health);
    }

    [Fact]
    public void Stop_settles_an_abandoned_redeploy_before_the_host_claims_the_stop()
    {
        using var factory = QuotaFactory();
        var (quota, workspaceId) = QuotaFor(factory, new AgentStudio.Quota.QuotaLimits(null, null, 1));
        var (service, repo, watch) = Build(quota: quota);
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        IntegrationVerificationStore.Write(Path.Combine(watch, "card"), new IntegrationVerificationRecord
        { State = IntegrationVerificationStates.Verified, Sha = commit, IntegrationBranch = "develop" });
        Assert.True(QueueGateRelease(service, _artifact, commit));
        var first = service.Claim("stage-host")!;
        Assert.True(service.Report("demo", first.Id, new(first.LeaseToken!, true, "health-ok")));

        var stage = service.Declarations("demo")!.Single(item => item.Id == "stage");
        Assert.NotNull(service.QueueDeployment("demo", stage, _artifact, "operator", rollback: false));
        var redeploy = service.Claim("stage-host")!;
        _clock.Advance(ProjectEnvironmentService.HostLostAfter + TimeSpan.FromSeconds(1));

        Assert.True(service.Stop("demo", "stage", "operator"));
        var lost = service.Read("demo")!.Jobs.Single(job => job.Id == redeploy.Id);
        Assert.Equal("failed", lost.Status);
        Assert.Equal("host-lost", lost.Evidence);
        Assert.False(service.Report("demo", redeploy.Id, new(redeploy.LeaseToken!, true, "health-ok")));
        Assert.Equal(1, quota.Snapshot(workspaceId).Usage.HostingSlots);

        var stop = service.Claim("stage-host")!;
        Assert.True(stop.IsStop);
        Assert.True(service.Report("demo", stop.Id, new(stop.LeaseToken!, true, "stop-ok")));
        Assert.Equal("stopped", service.Read("demo")!.Environments.Single(item => item.Declaration.Id == "stage").Health);
        Assert.Equal(0, quota.Snapshot(workspaceId).Usage.HostingSlots);
    }

    [Fact]
    public void Expired_unreported_deploy_claim_gives_its_slot_back_and_reading_changes_no_slot()
    {
        using var factory = QuotaFactory();
        var (quota, workspaceId) = QuotaFor(factory, new AgentStudio.Quota.QuotaLimits(null, null, 1));
        var (service, repo, watch) = Build(quota: quota);
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        IntegrationVerificationStore.Write(Path.Combine(watch, "card"), new IntegrationVerificationRecord
        { State = IntegrationVerificationStates.Verified, Sha = commit, IntegrationBranch = "develop" });
        Assert.True(QueueGateRelease(service, _artifact, commit));
        Assert.True(service.Claim("stage-host")!.ReservedSlot);
        // A slot of a declaration that no longer exists stays until the reconciliation, not the GET read.
        quota.SetProjectEnvironment("demo", ProjectEnvironmentService.SlotKey("retired"), true);

        service.Read("demo");
        Assert.Equal(2, quota.Snapshot(workspaceId).Usage.HostingSlots);
        service.ReconcileHostingSlots();
        Assert.Equal(1, quota.Snapshot(workspaceId).Usage.HostingSlots);

        // The deploy host never reports; after the five-minute lease the reservation is released.
        _clock.Advance(TimeSpan.FromMinutes(6));
        service.ReconcileHostingSlots();
        Assert.Equal(0, quota.Snapshot(workspaceId).Usage.HostingSlots);
        Assert.False(Assert.Single(service.Read("demo")!.Jobs).ReservedSlot);
        // A later claim of the same job reserves the slot again.
        Assert.True(service.Claim("stage-host")!.ReservedSlot);
        Assert.Equal(1, quota.Snapshot(workspaceId).Usage.HostingSlots);
    }

    private WebApplicationFactory<Program> QuotaFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "quota-workspace"), ["PublicDemo:Enabled"] = "false",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_clock);
        });
    });

    private (AgentStudio.Quota.WorkspaceQuotaService Quota, string WorkspaceId) QuotaFor(
        WebApplicationFactory<Program> factory, AgentStudio.Quota.QuotaLimits limits)
    {
        var services = factory.Services;
        var workspace = services.GetRequiredService<AgentStudio.Registry.WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var storage = Path.Combine(_root, "quota-workspace", "projects", "demo");
        Directory.CreateDirectory(storage);
        services.GetRequiredService<AgentStudio.Registry.ProjectRegistry>()
            .EnsureProjectForStorage(storage, "demo", workspace.Id, _clock);
        var quota = services.GetRequiredService<AgentStudio.Quota.WorkspaceQuotaService>();
        quota.SetLimits(workspace.Id, null, limits);
        return (quota, workspace.Id);
    }

    private bool QueueGateRelease(ProjectEnvironmentService service, string artifact, string commit)
    {
        WriteReleaseEvidence("demo", artifact, commit);
        return service.RegisterGateArtifact("demo", artifact, commit) && service.QueueRelease("demo", artifact, commit);
    }

    private void WriteReleaseEvidence(string project, string artifact, string commit)
    {
        var directory = Path.Combine(_root, "release-evidence", commit);
        Directory.CreateDirectory(directory);
        var archive = Encoding.UTF8.GetBytes("gate-built-release-for-" + commit);
        File.WriteAllBytes(Path.Combine(directory, "release.tar.gz"), archive);
        var digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        File.WriteAllText(Path.Combine(directory, "release.sha256"), digest + "\n");
        File.WriteAllText(Path.Combine(directory, "full-gate.log"), "PROMOTION_FULL_GATE=passed\n");
        File.WriteAllText(Path.Combine(directory, "promotion-record.json"), JsonSerializer.Serialize(new
        {
            status = "promoted", gate = "passed", gateRunId = "release/test-gate", candidateSha = commit, atomicPush = true,
            releasePackage = new { status = "written", sha256 = digest },
        }));
        File.WriteAllText(Path.Combine(directory, "deploy-artifact.json"), JsonSerializer.Serialize(new
        {
            project, commit, artifact, sourceSha256 = digest,
        }));
    }

    private (ProjectEnvironmentService Environments, IConfiguration Config, HostedVolumeKey Key, string Path) PrepareVolumeBackups()
    {
        var (environments, repo, _) = Build();
        File.WriteAllText(Path.Combine(repo, "docs", "deployments", "environments", "stage.json"),
            JsonSerializer.Serialize(new
            {
                id = "stage", name = "Stage", kind = "stage", targetHostId = "stage-host",
                address = "https://stage.example", template = "container", destination = "8081",
                healthUrl = "https://stage.example/health", approval = "person", secretRefs = Array.Empty<string>(),
                data = new { volumes = new[] { new { name = "uploads", mountPath = "/app/uploads" },
                    new { name = "logs", mountPath = "/app/logs" } } },
            }));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
        }).Build();
        var key = new HostedVolumeKey("demo", "stage", "uploads");
        var path = Path.Combine(_root, "volumes", key.Project, key.Environment, key.Volume);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "data.txt"), "initial");
        var logs = Path.Combine(_root, "volumes", key.Project, key.Environment, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "data.txt"), "other-volume");
        return (environments, config, key, path);
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TimeProvider _clock;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BlockingReadStream(TimeProvider clock) => _clock = clock;
        public Task Entered => _entered.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, _clock, cancellationToken);
            return 0;
        }
    }

    private sealed class BlockingRestoreEngine : IHostedVolumeBackupEngine
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RestoreEntered => _entered.Task;

        public Task<HostedVolumeBackup> BackupAsync(HostedVolumeKey key, DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HostedVolumeBackup(Guid.NewGuid().ToString("N"), key, now, "digest", 0));
        }

        public Task RestoreAsync(HostedVolumeBackup backup, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            return _never.Task.WaitAsync(cancellationToken);
        }

        public Task PruneBackupsAsync(HostedVolumeKey key, IReadOnlySet<string> retainedIds,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingPruneEngine(IHostedVolumeBackupEngine inner) : IHostedVolumeBackupEngine
    {
        public bool FailPrune { get; set; }
        public int BackupCount { get; private set; }

        public async Task<HostedVolumeBackup> BackupAsync(HostedVolumeKey key, DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            BackupCount++;
            return await inner.BackupAsync(key, now, cancellationToken);
        }

        public Task RestoreAsync(HostedVolumeBackup backup, CancellationToken cancellationToken)
            => inner.RestoreAsync(backup, cancellationToken);

        public Task PruneBackupsAsync(HostedVolumeKey key, IReadOnlySet<string> retainedIds,
            CancellationToken cancellationToken)
            => FailPrune ? Task.FromException(new IOException("Archive cleanup is busy."))
                : inner.PruneBackupsAsync(key, retainedIds, cancellationToken);
    }

    private (ProjectEnvironmentService Service, string Repo, string Watch) Build(bool timedApproval = false,
        string project = "demo", AgentStudio.Quota.WorkspaceQuotaService? quota = null)
    {
        var repo = Path.Combine(_root, "repo");
        var watch = Path.Combine(_root, "workspace", "projects", project);
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(watch);
        Git(repo, "init", "-b", "develop");
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(repo, "initial"), "initial");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "initial");
        var declarations = Path.Combine(repo, "docs", "deployments", "environments");
        Directory.CreateDirectory(declarations);
        File.WriteAllText(Path.Combine(declarations, "stage.json"), JsonSerializer.Serialize(new
        {
            id = "stage", name = "Stage", kind = "stage", targetHostId = "stage-host",
            address = "https://stage.example", template = "container", destination = "8081",
            healthUrl = "https://stage.example/health", approval = "person", secretRefs = Array.Empty<string>(),
        }));
        File.WriteAllText(Path.Combine(declarations, "production.json"), JsonSerializer.Serialize(new
        {
            id = "production", name = "Production", kind = "production", targetHostId = "prod-host",
            address = "https://example", template = "container", destination = "8082",
            healthUrl = "https://example/health", approval = timedApproval ? "stage-healthy-10m" : "person",
            secretRefs = new[] { "DATABASE_URL" },
        }));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["Deploy:ReleaseEvidenceRoot"] = Path.Combine(_root, "release-evidence"),
            ["WatchPaths:0:Name"] = project, ["WatchPaths:0:Path"] = watch,
            ["WatchPaths:0:RootPath"] = repo, ["WatchPaths:0:RepositoryPath"] = repo,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        return (new(config, scanner, git, settings, _clock, quota: quota), repo, watch);
    }

    private string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
