using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Security;
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
    public async Task FailedArchivePruningPersistsAndPreventsRepeatedBackupGrowth()
    {
        var (environments, config, key, _) = PrepareVolumeBackups();
        var engine = new FailingPruneEngine();
        var service = new HostedVolumeBackupService(config, environments, engine, _clock,
            NullLogger<HostedVolumeBackupService>.Instance);
        for (var index = 0; index < HostedVolumeSchedulePolicy.MaxBackups; index++)
            await service.BackupAsync(key.Project, key.Environment, key.Volume, "owner", CancellationToken.None);
        engine.FailPruning = true;
        await service.BackupAsync(key.Project, key.Environment, key.Volume, "owner", CancellationToken.None);
        Assert.Equal(HostedVolumeSchedulePolicy.MaxBackups + 1, engine.Archives.Count);
        var state = (await service.ReadAsync(key.Project, CancellationToken.None))!;
        Assert.True(Assert.Single(state.Schedules).PendingPrune);
        Assert.NotNull(Assert.Single(state.Schedules).LastError);

        var restarted = new HostedVolumeBackupService(config, environments, engine, _clock,
            NullLogger<HostedVolumeBackupService>.Instance);
        for (var attempt = 0; attempt < 3; attempt++)
            await Assert.ThrowsAsync<IOException>(() => restarted.BackupAsync(key.Project, key.Environment,
                key.Volume, "owner", CancellationToken.None));
        Assert.Equal(HostedVolumeSchedulePolicy.MaxBackups + 1, engine.Archives.Count);
        Assert.Equal(HostedVolumeSchedulePolicy.MaxBackups + 1, engine.BackupCalls);
        var blocked = (await restarted.ReadAsync(key.Project, CancellationToken.None))!;
        Assert.Equal(_clock.GetUtcNow(), Assert.Single(blocked.Schedules).LastAttemptAt);
        Assert.Equal(3, blocked.Audit.Count(entry => entry.Action == "backup" && entry.Outcome == "failed"));

        engine.FailPruning = false;
        await restarted.BackupAsync(key.Project, key.Environment, key.Volume, "owner", CancellationToken.None);
        state = (await restarted.ReadAsync(key.Project, CancellationToken.None))!;
        Assert.False(Assert.Single(state.Schedules).PendingPrune);
        Assert.Null(Assert.Single(state.Schedules).LastError);
        Assert.Equal(HostedVolumeSchedulePolicy.MaxBackups, engine.Archives.Count);
        Assert.Equal(HostedVolumeSchedulePolicy.MaxBackups, Assert.Single(state.Schedules).Backups.Count);
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
        var releaseEnrollment = security.CreateEnrollment(new RunnerEnrollmentRequest("release-gate",
            [RunnerScopes.Release], null, null));
        var release = security.EnrollRunner(releaseEnrollment.Code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", release.Secret);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/deploy/gate-releases", body)).StatusCode);
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
            status = "promoted", gate = "passed", candidateSha = commit, atomicPush = true,
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

    private sealed class FailingPruneEngine : IHostedVolumeBackupEngine
    {
        public HashSet<string> Archives { get; } = [];
        public bool FailPruning { get; set; }
        public int BackupCalls { get; private set; }

        public Task<HostedVolumeBackup> BackupAsync(HostedVolumeKey key, DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BackupCalls++;
            var id = Guid.NewGuid().ToString("N");
            Archives.Add(id);
            return Task.FromResult(new HostedVolumeBackup(id, key, now, "digest", 0));
        }

        public Task RestoreAsync(HostedVolumeBackup backup, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task PruneBackupsAsync(HostedVolumeKey key, IReadOnlySet<string> retainedIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailPruning) throw new IOException("Archive cleanup failed.");
            Archives.RemoveWhere(id => !retainedIds.Contains(id));
            return Task.CompletedTask;
        }
    }

    private (ProjectEnvironmentService Service, string Repo, string Watch) Build(bool timedApproval = false,
        string project = "demo")
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
        return (new(config, scanner, git, settings, _clock), repo, watch);
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
