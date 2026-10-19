using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Deploy;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DeployServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deploy-bd1-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private static readonly DeployActor Owner = new("owner", true);
    private static readonly DeployActor Operator = new("operator", false);
    private string _repo = "";
    private string _watch = "";
    private ProjectEnvironmentService? _environments;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void GreenIntegrationBuild_CreatesImmutableRelease_AndQueuesStagingAutomatically()
    {
        var (deploy, environments, store) = Build();
        var commit = Head();
        Card("DEMO-1", commit);
        var createdAt = _clock.GetUtcNow().UtcDateTime;
        var release = CreateRelease(deploy, Artifact('a'), commit);

        Assert.Equal("2026.10.16.1", release.Version);
        Assert.Equal(commit, release.CommitSha);
        Assert.Equal([Artifact('a')], release.ArtifactDigests);
        Assert.Equal(["DEMO-1"], release.CardKeys);
        Assert.Equal(ReleaseSnapshot.DefaultChannel, release.Channel);
        Assert.Matches("^[0-9a-f]{12}$", release.ProcessVersion);
        Assert.Matches("^[0-9a-f]{12}$", release.VariablesVersion);
        Assert.Equal(createdAt, release.CreatedAtUtc);

        var staging = Assert.Single(deploy.Deployments("demo", "stage")!);
        Assert.Equal((DeploymentStates.Queued, release.Id, "release-gate"), (staging.State, staging.ReleaseId, staging.RequestedBy));
        Assert.Null(staging.ApprovedBy);
        Assert.Equal(Artifact('a'), environments.Read("demo")!.Environments[0].PendingArtifact);

        // The stored release never changes, neither by a second write with
        // other content nor by the deploys that follow.
        var file = Directory.GetFiles(Path.Combine(_root, "workspace", "deployments", "projects", "demo", "releases")).Single();
        var bytes = File.ReadAllBytes(file);
        var (stored, created) = store.AppendRelease("demo", release with { Version = "tampered", CardKeys = ["X-1"] });
        Assert.False(created);
        Assert.Equal(release.Version, stored.Version);
        Assert.Equal(release.CardKeys, stored.CardKeys);
        DeployThroughProduction(deploy, environments, release, Owner);
        Assert.Equal(bytes, File.ReadAllBytes(file));
        var reread = Assert.Single(deploy.Releases("demo")!);
        Assert.Equal((release.Id, release.Version, release.CommitSha), (reread.Id, reread.Version, reread.CommitSha));
        Assert.Equal(release.CardKeys, reread.CardKeys);
    }

    [Fact]
    public void SameCommitAndArtifact_AfterFailedStaging_ReusesTheRelease()
    {
        var (deploy, environments, _) = Build();
        var commit = Head();
        var first = CreateRelease(deploy, Artifact('a'), commit);
        Report(deploy, environments, "stage-host", healthy: false);
        Assert.Equal(DeploymentStates.Failed, Assert.Single(deploy.Deployments("demo", "stage")!).State);

        var again = CreateRelease(deploy, Artifact('a'), commit);
        Assert.Equal((first.Id, first.Version), (again.Id, again.Version));
        Assert.Single(deploy.Releases("demo")!);
        Assert.Equal([DeploymentStates.Queued, DeploymentStates.Failed],
            deploy.Deployments("demo", "stage")!.Select(item => item.State));
    }

    [Fact]
    public void Release_IncludesOnlyCardsIntegratedSincePreviousRelease()
    {
        var (deploy, _, _) = Build();
        var first = Head();
        Card("DEMO-1", first);
        CreateRelease(deploy, Artifact('a'), first);
        var second = Commit("second");
        Card("DEMO-2", second);
        Card("DEMO-3", "0000000000000000000000000000000000000000", deliveryShas: [second]);
        var release = CreateRelease(deploy, Artifact('b'), second);
        Assert.Equal("2026.10.16.2", release.Version);
        Assert.Equal(["DEMO-2", "DEMO-3"], release.CardKeys);
    }

    [Fact]
    public void Production_WaitsForApproval_ByOwnerOrMaintainerOnly()
    {
        var (deploy, environments, _) = Build();
        var release = CreateRelease(deploy, Artifact('a'), Head());

        // Production before staging succeeded breaks the lifecycle.
        var early = deploy.RequestDeployment("demo", release.Id, new("production", Approve: true), Owner);
        Assert.Equal(DeployOutcomeKind.Conflict, early.Kind);

        Report(deploy, environments, "stage-host", healthy: true);
        var waiting = Assert.Single(deploy.Deployments("demo", "production")!);
        Assert.Equal(DeploymentStates.AwaitingApproval, waiting.State);
        Assert.Equal(DeployStepNames.Approval, waiting.Steps[0].Name);
        Assert.Null(environments.Claim("prod-host"));

        var refused = deploy.Approve("demo", waiting.Id, Operator);
        Assert.Equal(DeployOutcomeKind.Forbidden, refused.Kind);
        Assert.Contains("Owner or Maintainer", refused.Error);
        Assert.Null(environments.Claim("prod-host"));

        var approved = deploy.Approve("demo", waiting.Id, Owner);
        Assert.Equal(DeployOutcomeKind.Ok, approved.Kind);
        Assert.Equal((DeploymentStates.Queued, "owner"), (approved.Value!.State, approved.Value.ApprovedBy));
        Assert.Equal("production-queued", environments.Read("demo")!.Promotions.Single().Status);
        Report(deploy, environments, "prod-host", healthy: true);

        var production = deploy.Status("demo")!.Single(item => item.Environment.Kind == EnvironmentKinds.Production);
        Assert.Equal(release.Id, production.CurrentRelease?.Id);
        Assert.Equal(EnvironmentHealth.Healthy, production.Health);
        Assert.Null(production.NewerRelease);
        var run = production.CurrentDeployment!;
        Assert.Equal(DeploymentStates.Succeeded, run.State);
        Assert.Equal([DeployStepNames.Approval, DeployStepNames.StartContainer, DeployStepNames.Health],
            run.Steps.Select(step => step.Name));
        Assert.All(run.Steps, step => Assert.Equal(DeploymentStates.Succeeded, step.State));
        Assert.Equal($"/api/projects/demo/deploy/deployments/{run.Id}/log?step=health", run.Steps[2].LogRef);
        var log = deploy.Log("demo", run.Id, null)!.Lines;
        Assert.Contains(log, line => line.Step == DeployStepNames.Approval && line.Message == "Approved by owner.");
        Assert.Contains(log, line => line.Step == DeployStepNames.Health && line.Message.Contains("health-ok"));
    }

    [Fact]
    public void Reject_ClosesTheWaitingRunAndThePromotion()
    {
        var (deploy, environments, _) = Build();
        CreateRelease(deploy, Artifact('a'), Head());
        Report(deploy, environments, "stage-host", healthy: true);
        var waiting = Assert.Single(deploy.Deployments("demo", "production")!);

        Assert.Equal(DeployOutcomeKind.Forbidden, deploy.Reject("demo", waiting.Id, Operator).Kind);
        var rejected = deploy.Reject("demo", waiting.Id, Owner);
        Assert.Equal(DeploymentStates.Cancelled, rejected.Value!.State);
        Assert.Equal("rejected", environments.Read("demo")!.Promotions.Single().Status);
        Assert.Null(environments.Claim("prod-host"));
        // Read again: the closed promotion does not ask for approval twice.
        Assert.Single(deploy.Deployments("demo", "production")!);
    }

    [Fact]
    public void Redeploy_AndRollbackToThePreviousRelease()
    {
        var (deploy, environments, _) = Build();
        var r1 = CreateRelease(deploy, Artifact('a'), Head());
        DeployThroughProduction(deploy, environments, r1, Owner);
        var r2 = CreateRelease(deploy, Artifact('b'), Commit("second"));
        DeployThroughProduction(deploy, environments, r2, Owner);

        // A production rollback by a non-approver waits for approval.
        var requested = deploy.Rollback("demo", "production", approve: false, Operator);
        Assert.Equal(DeploymentStates.AwaitingApproval, requested.Value!.State);
        Assert.Equal((r1.Id, DeploymentTriggers.Rollback), (requested.Value.ReleaseId, requested.Value.Trigger));
        Assert.Equal(DeployOutcomeKind.Conflict, deploy.Rollback("demo", "production", approve: true, Owner).Kind);
        var approved = deploy.Approve("demo", requested.Value.Id, Owner);
        Assert.Equal(DeploymentStates.Queued, approved.Value!.State);
        var job = environments.Claim("prod-host")!;
        Assert.True(job.IsRollback);
        Assert.Equal(Artifact('a'), job.Artifact);
        Assert.True(environments.Report("demo", job.Id, new(job.LeaseToken!, true, "health-ok")));
        deploy.SyncAfterHostCall("demo");

        var production = deploy.Status("demo")!.Single(item => item.Environment.Id == "production");
        Assert.Equal(r1.Id, production.CurrentRelease?.Id);
        Assert.Equal(r2.Id, production.NewerRelease?.Id);
        Assert.Equal(DeployOutcomeKind.Conflict, deploy.Rollback("demo", "production", approve: true, Owner).Kind);

        // Staging redeploys its current release without approval.
        var redeploy = deploy.Redeploy("demo", "stage", approve: false, Operator);
        Assert.Equal((DeploymentStates.Queued, r2.Id, DeploymentTriggers.Redeploy),
            (redeploy.Value!.State, redeploy.Value.ReleaseId, redeploy.Value.Trigger));
        Assert.Equal(DeployOutcomeKind.Conflict, deploy.Redeploy("demo", "stage", approve: false, Operator).Kind);
        Assert.Equal(Artifact('b'), environments.Claim("stage-host")!.Artifact);
    }

    [Fact]
    public void Cancel_StopsQueuedWorkBeforeAHostClaimsIt()
    {
        var (deploy, environments, _) = Build();
        var release = CreateRelease(deploy, Artifact('a'), Head());
        var staging = Assert.Single(deploy.Deployments("demo", "stage")!);
        var cancelled = deploy.Cancel("demo", staging.Id, Operator);
        Assert.Equal(DeployOutcomeKind.Ok, cancelled.Kind);
        Assert.Equal(DeploymentStates.Cancelled, cancelled.Value!.State);
        Assert.All(cancelled.Value.Steps, step => Assert.Equal(DeploymentStates.Cancelled, step.State));
        Assert.Null(environments.Claim("stage-host"));
        Assert.Equal("cancelled", environments.Read("demo")!.Promotions.Single().Status);
        Assert.Equal(DeployOutcomeKind.Conflict, deploy.Cancel("demo", staging.Id, Operator).Kind);

        // The cancelled release can start again.
        Assert.Equal(release.Id, CreateRelease(deploy, Artifact('a'), release.CommitSha).Id);
        Assert.Equal("stage", environments.Claim("stage-host")!.EnvironmentId);
        Assert.Equal(DeployOutcomeKind.Conflict,
            deploy.Cancel("demo", deploy.Deployments("demo", "stage")!.First().Id, Operator).Kind);
    }

    [Fact]
    public void EveryChange_LeavesAnAuditEntry()
    {
        var (deploy, environments, _) = Build();
        var release = CreateRelease(deploy, Artifact('a'), Head());
        DeployThroughProduction(deploy, environments, release, Owner);
        var waiting = deploy.Redeploy("demo", "production", approve: false, Operator).Value!;
        deploy.Cancel("demo", waiting.Id, Operator);

        var actions = deploy.Audit("demo")!.Reverse().Select(item => (item.Action, item.Actor)).ToArray();
        Assert.Equal(
            [
                ("release-created", "release-gate"),
                ("deployment-queued", "release-gate"),
                ("deployment-started", "stage-host"),
                ("deployment-succeeded", "stage-host"),
                ("approval-requested", DeploymentReconciler.LifecycleActor),
                ("deployment-approved", "owner"),
                ("deployment-started", "prod-host"),
                ("deployment-succeeded", "prod-host"),
                ("approval-requested", "operator"),
                ("deployment-cancelled", "operator"),
            ],
            actions);
    }

    [Fact]
    public void Matrix_ListsProjectsWithTheirEnvironments()
    {
        var (deploy, environments, _) = Build();
        var release = CreateRelease(deploy, Artifact('a'), Head());
        Report(deploy, environments, "stage-host", healthy: true);

        var matrix = deploy.Matrix();
        Assert.Equal([EnvironmentKinds.Staging, EnvironmentKinds.Production], matrix.EnvironmentKinds);
        var row = Assert.Single(matrix.Projects);
        Assert.Equal("demo", row.ProjectId);
        Assert.Equal([release.Id, null], row.Statuses.Select(item => item.CurrentRelease?.Id));
        Assert.Equal(DeploymentStates.AwaitingApproval, row.Statuses[1].CurrentDeployment?.State);
        Assert.Equal(release.Id, row.Statuses[1].NewerRelease?.Id);
    }

    [Fact]
    public async Task Routes_ServeTheSharedContract()
    {
        Build();
        var commit = Head();
        WriteReleaseEvidence(Artifact('a'), commit);
        await using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("TaskRepository", Path.Combine(_root, "workspace"));
            builder.UseSetting("Deploy:ReleaseEvidenceRoot", Path.Combine(_root, "release-evidence"));
            builder.UseSetting("WatchPaths:0:Name", "demo");
            builder.UseSetting("WatchPaths:0:Path", _watch);
            builder.UseSetting("WatchPaths:0:RootPath", _repo);
            builder.UseSetting("WatchPaths:0:RepositoryPath", _repo);
            builder.UseSetting("ReviewDecisionOrchestrator:Enabled", "false");
            builder.UseSetting("Logging:BackendFile:LogDirectory", Path.Combine(_root, "logs"));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
            });
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
        Assert.True(factory.Services.GetRequiredService<ProjectEnvironmentService>()
            .RegisterGateArtifact("demo", Artifact('a'), commit));

        using var created = await client.PostAsJsonAsync("/api/projects/demo/environments/releases",
            new ReleaseArtifactRequest(Artifact('a'), commit));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var releaseId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();

        using var status = JsonDocument.Parse(await client.GetStringAsync("/api/projects/demo/deploy/status"));
        var staging = status.RootElement[0];
        Assert.Equal("staging", staging.GetProperty("environment").GetProperty("kind").GetString());
        Assert.Equal(0, staging.GetProperty("environment").GetProperty("order").GetInt32());
        Assert.Equal(JsonValueKind.Null, staging.GetProperty("currentRelease").ValueKind);
        Assert.Equal(releaseId, staging.GetProperty("newerRelease").GetProperty("id").GetString());
        Assert.Equal("unknown", staging.GetProperty("health").GetString());
        var run = staging.GetProperty("currentDeployment");
        Assert.Equal("queued", run.GetProperty("state").GetString());
        foreach (var name in new[] { "id", "releaseId", "environmentId", "startedAtUtc", "finishedAtUtc", "approvedBy", "steps" })
            Assert.True(run.TryGetProperty(name, out _), name);
        Assert.True(staging.TryGetProperty("cardsSinceLastDeploy", out _));

        using var release = JsonDocument.Parse(await client.GetStringAsync($"/api/projects/demo/deploy/releases/{releaseId}"));
        foreach (var name in new[] { "id", "projectId", "version", "commitSha", "artifactDigests", "processVersion",
                     "variablesVersion", "cardKeys", "createdAtUtc", "channel" })
            Assert.True(release.RootElement.GetProperty("release").TryGetProperty(name, out _), name);

        var deploymentId = run.GetProperty("id").GetString();
        using var approve = await client.PostAsync($"/api/projects/demo/deploy/deployments/{deploymentId}/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        using var unknown = await client.PostAsJsonAsync("/api/projects/demo/deploy/releases/missing/deployments",
            new DeploymentRequest("stage"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var matrix = JsonDocument.Parse(await client.GetStringAsync("/api/deploy/matrix"));
        Assert.Equal("demo", matrix.RootElement.GetProperty("projects")[0].GetProperty("projectId").GetString());
        using var log = JsonDocument.Parse(await client.GetStringAsync(
            run.GetProperty("steps")[0].GetProperty("logRef").GetString()!));
        Assert.Contains("queued for Stage", log.RootElement.GetProperty("lines")[0].GetProperty("message").GetString());
    }

    private void DeployThroughProduction(DeployService deploy, ProjectEnvironmentService environments,
        Release release, DeployActor approver)
    {
        Report(deploy, environments, "stage-host", healthy: true);
        var waiting = deploy.Deployments("demo", "production")!.First(item =>
            item.ReleaseId == release.Id && item.State == DeploymentStates.AwaitingApproval);
        Assert.Equal(DeployOutcomeKind.Ok, deploy.Approve("demo", waiting.Id, approver).Kind);
        Report(deploy, environments, "prod-host", healthy: true);
    }

    private void Report(DeployService deploy, ProjectEnvironmentService environments, string host, bool healthy)
    {
        var job = environments.Claim(host)!;
        deploy.SyncAfterHostCall("demo");
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(environments.Report("demo", job.Id,
            new(job.LeaseToken!, healthy, healthy ? "health-ok" : "deploy-step-failed")));
        deploy.SyncAfterHostCall("demo");
        _clock.Advance(TimeSpan.FromMinutes(1));
    }

    private Release CreateRelease(DeployService deploy, string artifact, string commit)
    {
        WriteReleaseEvidence(artifact, commit);
        Assert.True(_environments!.RegisterGateArtifact("demo", artifact, commit));
        var outcome = deploy.CreateRelease("demo", artifact, commit, "release-gate");
        Assert.True(outcome.Kind == DeployOutcomeKind.Ok, outcome.Error);
        _clock.Advance(TimeSpan.FromMinutes(1));
        return outcome.Value!;
    }

    private static string Artifact(char fill) => "registry.example/app@sha256:" + new string(fill, 64);

    private void Card(string key, string sha, IReadOnlyList<string>? deliveryShas = null)
    {
        var folder = Path.Combine(_watch, "6-completed", key.ToLowerInvariant());
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = key.ToLowerInvariant(), key, title = "Card " + key, state = "6-completed", agent = "claude",
            createdAt = _clock.GetUtcNow(),
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "fixture");
        IntegrationVerificationStore.Write(folder, new IntegrationVerificationRecord
        {
            State = IntegrationVerificationStates.Verified, Sha = sha, IntegrationBranch = "develop",
            DeliveryShas = [.. deliveryShas ?? []], RecordedAtUtc = _clock.GetUtcNow(),
        });
    }

    private void WriteReleaseEvidence(string artifact, string commit)
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
            project = "demo", commit, artifact, sourceSha256 = digest,
        }));
    }

    private (DeployService Deploy, ProjectEnvironmentService Environments, DeployStore Store) Build()
    {
        _repo = Path.Combine(_root, "repo");
        _watch = Path.Combine(_root, "workspace", "projects", "demo");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_watch);
        Git("init", "-b", "develop");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test User");
        var declarations = Path.Combine(_repo, "docs", "deployments", "environments");
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
            healthUrl = "https://example/health", approval = "person", secretRefs = new[] { "DATABASE_URL" },
        }));
        Commit("initial");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["Deploy:ReleaseEvidenceRoot"] = Path.Combine(_root, "release-evidence"),
            ["WatchPaths:0:Name"] = "demo", ["WatchPaths:0:Path"] = _watch,
            ["WatchPaths:0:RootPath"] = _repo, ["WatchPaths:0:RepositoryPath"] = _repo,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var environments = new ProjectEnvironmentService(config, scanner, git, settings, _clock);
        var store = new DeployStore(config);
        _environments = environments;
        return (new DeployService(environments, store, git, scanner, _clock, NullLogger<DeployService>.Instance),
            environments, store);
    }

    private string Head() => Git("rev-parse", "HEAD").Trim();

    private string Commit(string name)
    {
        File.WriteAllText(Path.Combine(_repo, name), name);
        Git("add", ".");
        Git("commit", "-m", name);
        return Head();
    }

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _repo,
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
