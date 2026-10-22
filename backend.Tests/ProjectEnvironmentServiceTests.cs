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
            new DeployReportRequest(claimed.LeaseToken!, true, "health-ok", png)));
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
            status = "promoted", gate = "passed", candidateSha = commit, atomicPush = true,
            releasePackage = new { status = "written", sha256 = digest },
        }));
        File.WriteAllText(Path.Combine(directory, "deploy-artifact.json"), JsonSerializer.Serialize(new
        {
            project, commit, artifact, sourceSha256 = digest,
        }));
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
        return (new(config, scanner, git, settings, _clock, quota), repo, watch);
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
