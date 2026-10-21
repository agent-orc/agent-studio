using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.AspNetCore.Http.HttpResults;
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
    public async Task Bindings_require_production_approval_and_preflight_checks_inherited_and_revoked_values()
    {
        var (environments, _, _) = Build();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["Secrets:KeyFile"] = Path.Combine(_root, "keys", "secrets.kek"),
        }).Build();
        var provider = new SecretProvider(
            new BuiltInSecretStore(config, new ConfiguredKeyEncryptionKeySource(config, _ => null, _ => { })),
            new SecretAuditLog(config), new SecretValueScrubber(_clock), _clock,
            NullLogger<SecretProvider>.Instance);
        var bindings = new ProjectSecretBindings(config, provider, environments, _clock);
        var before = await bindings.CheckAsync("demo", "production");
        Assert.Equal("not-bound", Assert.Single(before.Missing).Reason);
        Assert.Throws<SecretAccessDeniedException>(() => bindings.Bind("demo", "production", "DATABASE_URL",
            "secret://demo/production/DATABASE_URL", "human:owner", null));
        Assert.Throws<SecretValidationException>(() => bindings.Bind("demo", "production", "DATABASE_URL",
            "secret://other/DATABASE_URL", "human:owner", "owner"));
        bindings.Bind("demo", "production", "DATABASE_URL", "secret://demo/production/DATABASE_URL",
            "human:owner", "owner");
        Assert.Equal("not-found", Assert.Single((await bindings.CheckAsync("demo", "production")).Missing).Reason);
        var value = "runtime-" + Guid.NewGuid().ToString("N");
        await provider.PutAsync(SecretScope.ForProject("demo"), "DATABASE_URL", value, SecretActor.Human("owner"));
        Assert.True((await bindings.CheckAsync("demo", "production")).Ready);
        Assert.DoesNotContain(value, File.ReadAllText(Directory.GetFiles(Path.Combine(_root, "workspace", ".security", "secret-bindings"))[0]));
        await provider.RevokeAsync(SecretScope.ForProject("demo"), "DATABASE_URL", null, SecretActor.Human("owner"));
        Assert.Equal("revoked", Assert.Single((await bindings.CheckAsync("demo", "production")).Missing).Reason);
    }

    [Fact]
    public async Task Production_binding_uses_declared_kind_when_environment_id_is_custom()
    {
        var (environments, repo, _) = Build();
        var declarations = Path.Combine(repo, "docs", "deployments", "environments");
        var original = Path.Combine(declarations, "production.json");
        var custom = Path.Combine(declarations, "live-eu.json");
        File.Move(original, custom);
        File.WriteAllText(custom, File.ReadAllText(custom).Replace("\"id\":\"production\"", "\"id\":\"live-eu\"", StringComparison.Ordinal));
        Assert.Equal("production", environments.Declarations("demo")!.Single(item => item.Id == "live-eu").Kind);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["Secrets:KeyFile"] = Path.Combine(_root, "keys", "secrets.kek"),
        }).Build();
        var provider = new SecretProvider(
            new BuiltInSecretStore(config, new ConfiguredKeyEncryptionKeySource(config, _ => null, _ => { })),
            new SecretAuditLog(config), new SecretValueScrubber(_clock), _clock,
            NullLogger<SecretProvider>.Instance);
        var bindings = new ProjectSecretBindings(config, provider, environments, _clock);
        const string reference = "secret://demo/live-eu/DATABASE_URL";

        Assert.Throws<SecretAccessDeniedException>(() => bindings.Bind("demo", "live-eu", "DATABASE_URL",
            reference, "human:owner", null));
        Assert.Equal("not-bound", Assert.Single((await bindings.CheckAsync("demo", "live-eu")).Missing).Reason);
        bindings.Bind("demo", "live-eu", "DATABASE_URL", reference, "human:owner", "owner");
        Assert.Equal(reference, bindings.ApprovedReferences("demo", "live-eu")["DATABASE_URL"]);
        Assert.Equal("not-found", Assert.Single((await bindings.CheckAsync("demo", "live-eu")).Missing).Reason);
    }

    [Fact]
    public async Task Claimed_job_uses_snapshot_names_and_kind_after_live_declaration_changes()
    {
        var (environments, repo, _) = Build();
        var config = SecretConfig();
        var provider = SecretProvider(config);
        var bindings = new ProjectSecretBindings(config, provider, environments, _clock);
        var target = environments.Declarations("demo")!.Single(item => item.Kind == "production");
        const string reference = "secret://demo/DATABASE_URL";
        bindings.Bind("demo", target.Id, "DATABASE_URL", reference, "human:owner", "owner");
        await provider.PutAsync(SecretScope.ForProject("demo"), "DATABASE_URL", "runtime-" + Guid.NewGuid().ToString("N"),
            SecretActor.Human("owner"));
        Assert.NotNull(environments.QueueDeployment("demo", target, _artifact, "owner", false));
        var job = environments.Claim("prod-host")!;
        Assert.Equal("production", job.EnvironmentKind);

        ChangeProductionDeclaration(repo, "stage", "NEW_KEY");
        Assert.Equal("not-bound", Assert.Single((await bindings.CheckAsync("demo", target.Id)).Missing).Reason);
        Assert.True((await bindings.CheckAsync(job.Project, job.EnvironmentId, job.SecretRefs, job.EnvironmentKind!)).Ready);

        var scanner = Scanner(config);
        var leases = new SecretWorkloadLeases(environments,
            new RunLeaseService(NullLogger<RunLeaseService>.Instance, () => _clock.GetUtcNow().UtcDateTime), scanner, bindings);
        var claimed = leases.FindClaimedDeployJob(job.Project, job.Id, job.LeaseToken!)!;
        Assert.Equal(["DATABASE_URL"], claimed.SecretRefs);
        Assert.Equal(reference, claimed.SecretBindings!["DATABASE_URL"]);
        Assert.False(claimed.SecretBindings.ContainsKey("NEW_KEY"));
        var workload = new SecretWorkload(SecretWorkload.DeployJob, job.HostId, job.Project, job.EnvironmentId,
            claimed.SecretRefs, claimed.SecretBindings);
        Assert.True(SecretAccessPolicy.ForWorkload(SecretReference.Parse(reference), workload, "deploy:production").Allowed);
        Assert.Equal("reference-not-declared", SecretAccessPolicy.ForWorkload(
            SecretReference.Parse("secret://demo/NEW_KEY"), workload, "deploy:production").Code);
    }

    [Fact]
    public async Task Legacy_production_job_keeps_approval_requirement_after_current_kind_changes()
    {
        var (environments, repo, _) = Build();
        var target = environments.Declarations("demo")!.Single(item => item.Kind == "production");
        Assert.NotNull(environments.QueueDeployment("demo", target, _artifact, "owner", false));
        var queueFile = Path.Combine(_root, "workspace", "deployments", "demo.json");
        var state = JsonNode.Parse(File.ReadAllText(queueFile))!;
        state["jobs"]![0]!.AsObject().Remove("environmentKind");
        File.WriteAllText(queueFile, state.ToJsonString());
        ChangeProductionDeclaration(repo, "stage", "DATABASE_URL");

        var config = SecretConfig();
        var bindings = new ProjectSecretBindings(config, SecretProvider(config), environments, _clock);
        bindings.Bind("demo", target.Id, "DATABASE_URL", "secret://demo/DATABASE_URL", "human:operator", null);
        var job = environments.Claim("prod-host")!;
        Assert.Equal("production", job.EnvironmentKind);
        Assert.Equal("production", environments.FindClaimedJob(job.Project, job.Id, job.LeaseToken!)!.EnvironmentKind);
        Assert.Equal("approval-required", Assert.Single((await bindings.CheckAsync(
            job.Project, job.EnvironmentId, job.SecretRefs, job.EnvironmentKind!)).Missing).Reason);
        Assert.Empty(bindings.ApprovedReferences(job.Project, job.EnvironmentId, job.SecretRefs, job.EnvironmentKind!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claimed_job_preflight_failure_returns_reportable_missing_references(bool providerFailure)
    {
        var (environments, _, _) = Build();
        var target = environments.Declarations("demo")!.Single(item => item.Kind == "production");
        Assert.NotNull(environments.QueueDeployment("demo", target, _artifact, "owner", false));
        var job = environments.Claim("prod-host")!;
        var config = providerFailure ? SecretConfig() : new ConfigurationBuilder().Build();
        ISecretProvider provider = providerFailure ? new FailingListingProvider() : SecretProvider(SecretConfig());
        var bindings = new ProjectSecretBindings(config, provider, environments, _clock);

        var result = await ProjectOperatorDashboardEndpoints.CompleteClaimAsync(job, _ => { }, bindings, CancellationToken.None);
        var returned = Assert.IsType<Ok<DeployJob>>(result).Value!;
        Assert.Equal(job.LeaseToken, returned.LeaseToken);
        Assert.Null(returned.SecretBindings);
        Assert.Equal("preflight-unavailable", Assert.Single(returned.MissingSecretRefs!).Reason);
        Assert.True(environments.Report(job.Project, job.Id, new DeployReportRequest(returned.LeaseToken!, false,
            "secret-reference-missing")));
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

    [Theory]
    [InlineData("DATABASE-URL", "DATABASE_URL")]
    [InlineData("DATABASE.URL", "DATABASE_URL")]
    [InlineData("Database_URL", "DATABASE_URL")]
    public void Declaration_rejects_secret_names_that_collide_as_container_variables(string first, string second)
    {
        var (service, repo, _) = Build();
        var stage = Path.Combine(repo, "docs", "deployments", "environments", "stage.json");
        var declaration = JsonNode.Parse(File.ReadAllText(stage))!;
        declaration["secretRefs"] = JsonSerializer.SerializeToNode(new[] { first, second });
        File.WriteAllText(stage, declaration.ToJsonString());

        Assert.Contains("collide", Assert.Throws<JsonException>(() => service.Declarations("demo")).Message);
        var files = service.DeclarationFiles("demo")!;
        Assert.Null(files.Single(item => item.EnvironmentId == "stage").Declaration);
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

    private IConfiguration SecretConfig() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["TaskRepository"] = Path.Combine(_root, "workspace"),
        ["Secrets:KeyFile"] = Path.Combine(_root, "keys", "secrets.kek"),
    }).Build();

    private SecretProvider SecretProvider(IConfiguration config) => new(
        new BuiltInSecretStore(config, new ConfiguredKeyEncryptionKeySource(config, _ => null, _ => { })),
        new SecretAuditLog(config), new SecretValueScrubber(_clock), _clock,
        NullLogger<SecretProvider>.Instance);

    private static TaskScannerService Scanner(IConfiguration config) => new(config,
        NullLogger<TaskScannerService>.Instance,
        new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));

    private static void ChangeProductionDeclaration(string repo, string kind, string name)
    {
        var path = Path.Combine(repo, "docs", "deployments", "environments", "production.json");
        var declaration = JsonNode.Parse(File.ReadAllText(path))!;
        declaration["kind"] = kind;
        declaration["secretRefs"] = JsonSerializer.SerializeToNode(new[] { name });
        File.WriteAllText(path, declaration.ToJsonString());
    }

    private sealed class FailingListingProvider : ISecretProvider
    {
        public Task<SecretListing> ListAsync(SecretScope? scope = null, CancellationToken ct = default)
            => throw new IOException("Injected provider failure.");
        public Task<SecretRecord> PutAsync(SecretScope scope, string name, string value, SecretActor actor,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SecretRecord> RotateAsync(SecretScope scope, string name, string value, SecretActor actor,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SecretRecord> RevokeAsync(SecretScope scope, string name, int? version, SecretActor actor,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(SecretScope scope, string name, SecretActor actor,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SecretAuditEntry>> ReadAuditAsync(SecretScope? scope, int limit,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ResolvedSecret> GetForWorkloadAsync(SecretReference reference, SecretWorkload workload,
            string purpose, CancellationToken ct = default) => throw new NotSupportedException();
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
