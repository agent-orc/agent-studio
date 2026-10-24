using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private IConfiguration? _configuration;
    private TaskScannerService? _scanner;
    private GitService? _git;
    private ProjectSettingsService? _settings;

    [Fact]
    public void QueuedJobAfterRestart_BindsItsRunAndRefusesAnUnlinkedDigest()
    {
        var (deploy, environments, store) = Build();
        var commit = Head();
        var release = CreateRelease(deploy, Artifact('a'), commit);
        StripPersistedReleaseIds();
        var restarted = RestartEnvironments();

        var claimed = restarted.Claim("stage-host", requireVerifiedRelease: true)!;
        Assert.Equal(release.Id, claimed.ReleaseId);
        Assert.Equal(release.Id, restarted.FindClaimedJob("demo", claimed.Id, claimed.LeaseToken!)!.ReleaseId);
        Assert.Equal(release.Id, restarted.Read("demo")!.Jobs.Single().ReleaseId);

        Assert.True(restarted.Report("demo", claimed.Id, new(claimed.LeaseToken!, false, "deploy-step-failed"),
            requireVerifiedRelease: true));
        deploy.SyncAfterHostCall("demo");
        var retry = CreateRelease(deploy, Artifact('a'), commit);
        Assert.Equal(release.Id, retry.Id);
        StripPersistedReleaseIds();
        var queued = environments.Read("demo")!.Jobs.Last(item => item.Status == "queued");
        store.SaveDeployments("demo", store.ReadDeployments("demo").Where(item => item.JobId != queued.Id).ToArray());

        restarted = RestartEnvironments();
        Assert.Null(restarted.Claim("stage-host", requireVerifiedRelease: true));
        var refused = restarted.Read("demo")!.Jobs.Single(item => item.Id == queued.Id);
        Assert.Equal(("failed", "artifact-unverified"), (refused.Status, refused.Evidence));
        Assert.Null(refused.LeaseToken);
    }

    [Fact]
    public void AutomaticRollbackAfterRestart_BindsTheHealthySourceReleaseAndChecksProvenance()
    {
        var (deploy, environments, _) = Build();
        var first = CreateRelease(deploy, Artifact('a'), Head());
        var healthy = environments.Claim("stage-host", requireVerifiedRelease: true)!;
        Assert.True(environments.Report("demo", healthy.Id, new(healthy.LeaseToken!, true, "health-ok"),
            requireVerifiedRelease: true));
        deploy.SyncAfterHostCall("demo");

        var secondCommit = Commit("second");
        CreateRelease(deploy, Artifact('b'), secondCommit);
        var failed = environments.Claim("stage-host", requireVerifiedRelease: true)!;
        var advancing = new ProjectEnvironmentService(_configuration!, _scanner!, _git!, _settings!,
            new AdvancingTimeProvider(_clock, TimeSpan.FromSeconds(1)),
            new ReleaseArtifactRegistry(_configuration!), new DeployStore(_configuration!));
        Assert.True(advancing.Report("demo", failed.Id, new(failed.LeaseToken!, false, "deploy-step-failed"),
            requireVerifiedRelease: true));
        var queuedRollback = environments.Read("demo")!.Jobs.Last();
        Assert.Equal(failed.Id, queuedRollback.RollbackOfJobId);
        Assert.True(queuedRollback.CreatedAt > environments.Read("demo")!.Jobs.Single(item => item.Id == failed.Id).FinishedAt);
        // The queue has durably written the automatic rollback, but the run
        // reconciler has not yet adopted it when the process restarts.
        StripPersistedReleaseIds();
        var restarted = RestartEnvironments();
        var rollback = restarted.Claim("stage-host", requireVerifiedRelease: true)!;
        Assert.True(rollback.IsRollback);
        Assert.Equal(first.Id, rollback.ReleaseId);
        Assert.Equal(first.Id, restarted.FindClaimedJob("demo", rollback.Id, rollback.LeaseToken!)!.ReleaseId);

        var provenanceFile = Directory.GetFiles(Path.Combine(_root, "workspace", "release-registry", "records"),
            "*.json", SearchOption.AllDirectories).Single(file => file.Contains(first.CommitSha, StringComparison.Ordinal));
        File.Delete(provenanceFile);
        Assert.Null(restarted.FindClaimedJob("demo", rollback.Id, rollback.LeaseToken!));
        Assert.False(restarted.Report("demo", rollback.Id, new(rollback.LeaseToken!, true, "health-ok"),
            requireVerifiedRelease: true));
        Assert.Equal("artifact-unverified", restarted.Read("demo")!.Jobs.Single(item => item.Id == rollback.Id).Evidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Claim_VerifiesOnlyTheSelectedHostsJob(bool otherHostClaimed)
    {
        var (deploy, environments, _) = Build();
        var first = CreateRelease(deploy, Artifact('a'), Head());
        var stage = environments.Claim("stage-host", requireVerifiedRelease: true)!;
        Assert.True(environments.Report("demo", stage.Id, new(stage.LeaseToken!, true, "health-ok"),
            requireVerifiedRelease: true));
        deploy.SyncAfterHostCall("demo");
        var waiting = deploy.Deployments("demo", "production")!.Single(item => item.ReleaseId == first.Id);
        Assert.Equal(DeployOutcomeKind.Ok, deploy.Approve("demo", waiting.Id, Owner).Kind);
        var second = CreateRelease(deploy, Artifact('b'), Commit("second"));
        var registry = new FaultingRegistry(_configuration!);
        var claimant = EnvironmentsWithRegistry(registry);
        var other = claimant.Read("demo")!.Jobs.Single(item => item.ReleaseId == second.Id);
        if (otherHostClaimed)
        {
            var claim = claimant.Claim("stage-host", requireVerifiedRelease: true)!;
            Assert.Equal(other.Id, claim.Id);
        }
        registry.UnavailableArtifact = Artifact('b');
        registry.Calls.Clear();

        var production = claimant.Claim("prod-host", requireVerifiedRelease: true)!;

        Assert.Equal((first.Id, Artifact('a')), (production.ReleaseId, production.Artifact));
        Assert.DoesNotContain(Artifact('b'), registry.Calls);
        var untouched = claimant.Read("demo")!.Jobs.Single(item => item.Id == other.Id);
        Assert.Equal(otherHostClaimed ? "claimed" : "queued", untouched.Status);
        Assert.Null(untouched.Evidence);
    }

    [Fact]
    public void TransientRegistryRead_KeepsClaimAndReportRetryable()
    {
        var (deploy, _, _) = Build();
        CreateRelease(deploy, Artifact('a'), Head());
        var registry = new FaultingRegistry(_configuration!) { UnavailableArtifact = Artifact('a') };
        var claimant = EnvironmentsWithRegistry(registry);

        Assert.Null(claimant.Claim("stage-host", requireVerifiedRelease: true));
        Assert.Equal("queued", claimant.Read("demo")!.Jobs.Single().Status);
        registry.UnavailableArtifact = null;
        var job = claimant.Claim("stage-host", requireVerifiedRelease: true)!;
        registry.UnavailableArtifact = Artifact('a');
        Assert.Null(claimant.FindClaimedJob("demo", job.Id, job.LeaseToken!, requireVerifiedRelease: true));
        Assert.False(claimant.Report("demo", job.Id, new(job.LeaseToken!, true, "health-ok"),
            requireVerifiedRelease: true));
        Assert.Equal("claimed", claimant.Read("demo")!.Jobs.Single().Status);
        registry.UnavailableArtifact = null;
        Assert.NotNull(claimant.FindClaimedJob("demo", job.Id, job.LeaseToken!, requireVerifiedRelease: true));
        Assert.True(claimant.Report("demo", job.Id, new(job.LeaseToken!, true, "health-ok"),
            requireVerifiedRelease: true));
    }

    [Fact]
    public void RegistryRecordReadError_IsRetryableButMissingRecordIsUnverified()
    {
        var (deploy, environments, _) = Build();
        var release = CreateRelease(deploy, Artifact('a'), Head());
        var record = Directory.GetFiles(Path.Combine(_root, "workspace", "release-registry", "records"),
            "*.json", SearchOption.AllDirectories).Single();
        var backup = record + ".backup";
        File.Move(record, backup);
        Directory.CreateDirectory(record);
        try
        {
            Assert.Null(environments.Claim("stage-host", requireVerifiedRelease: true));
            Assert.Equal("queued", environments.Read("demo")!.Jobs.Single().Status);
            Assert.Throws<UnauthorizedAccessException>(() => new ReleaseArtifactRegistry(_configuration!)
                .VerifiedForDeployment("demo", release.CommitSha, Artifact('a'), "container"));
        }
        finally
        {
            Directory.Delete(record);
            File.Move(backup, record);
        }
        var claimed = environments.Claim("stage-host", requireVerifiedRelease: true)!;
        Assert.Equal(release.Id, claimed.ReleaseId);
        Assert.True(environments.Report("demo", claimed.Id, new(claimed.LeaseToken!, true, "health-ok"),
            requireVerifiedRelease: true));
    }

    [Fact]
    public void RetryableFirstRelease_PreservesEnvironmentOrderAndAllowsOtherEnvironment()
    {
        var (deploy, environments, _) = Build();
        WriteProduction(host: "stage-host", destination: "8082");
        var first = CreateRelease(deploy, Artifact('a'), Commit("shared-host"));
        var stage = environments.Claim("stage-host", requireVerifiedRelease: true)!;
        Assert.True(environments.Report("demo", stage.Id, new(stage.LeaseToken!, true, "health-ok"),
            requireVerifiedRelease: true));
        deploy.SyncAfterHostCall("demo");
        var second = CreateRelease(deploy, Artifact('b'), Commit("second"));
        var third = CreateRelease(deploy, Artifact('c'), Commit("third"));
        var production = deploy.Deployments("demo", "production")!.Single(item => item.ReleaseId == first.Id);
        Assert.Equal(DeployOutcomeKind.Ok, deploy.Approve("demo", production.Id, Owner).Kind);
        var registry = new FaultingRegistry(_configuration!) { UnavailableArtifact = Artifact('b') };
        var claimant = EnvironmentsWithRegistry(registry);

        var unrelated = claimant.Claim("stage-host", requireVerifiedRelease: true)!;
        Assert.Equal("production", unrelated.EnvironmentId);
        Assert.Equal(first.Id, unrelated.ReleaseId);
        Assert.DoesNotContain(Artifact('c'), registry.Calls);
        Assert.Equal([second.Id, third.Id], claimant.Read("demo")!.Jobs
            .Where(item => item.EnvironmentId == "stage" && item.Status == "queued")
            .Select(item => item.ReleaseId));
        Assert.True(claimant.Report("demo", unrelated.Id, new(unrelated.LeaseToken!, true, "health-ok"),
            requireVerifiedRelease: true));
        Assert.Null(claimant.Claim("stage-host", requireVerifiedRelease: true));
        registry.UnavailableArtifact = null;
        Assert.Equal(second.Id, claimant.Claim("stage-host", requireVerifiedRelease: true)!.ReleaseId);
    }

    private sealed class AdvancingTimeProvider(FakeTimeProvider clock, TimeSpan step) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            var now = clock.GetUtcNow();
            clock.Advance(step);
            return now;
        }
    }

    private ProjectEnvironmentService EnvironmentsWithRegistry(ReleaseArtifactRegistry registry)
        => new(_configuration!, _scanner!, _git!, _settings!, _clock, registry, new DeployStore(_configuration!));

    private sealed class FaultingRegistry(IConfiguration configuration) : ReleaseArtifactRegistry(configuration)
    {
        public string? UnavailableArtifact { get; set; }
        public List<string> Calls { get; } = [];

        public override RegisteredArtifact? VerifiedForDeployment(string project, string commit, string artifact,
            string template)
        {
            Calls.Add(artifact);
            if (artifact == UnavailableArtifact) throw new IOException("Transient registry read failure.");
            return base.VerifiedForDeployment(project, commit, artifact, template);
        }
    }

    [Fact]
    public void Runbook_executes_on_environment_without_creating_or_changing_a_release()
    {
        var (deploy, environments, store) = Build();
        WriteProjectYml("runbooks:\n  - name: restart\n    steps: [restart-container, health]\n");
        var definitionPath = Path.Combine(_repo, ".agent-studio", "project.yml");
        File.WriteAllText(definitionPath, File.ReadAllText(definitionPath).Replace(
            "variables:\n  - name: DATABASE_URL",
            "variables:\n  - name: LOG_LEVEL\n    value: info\n  - name: DATABASE_URL",
            StringComparison.Ordinal));
        Commit("runbook-definition");
        var runbooks = Runbooks(environments, store);

        var requested = runbooks.Request("demo", "stage", "restart", Operator);
        Assert.Equal(DeployOutcomeKind.Ok, requested.Kind);
        Assert.Equal(DeploymentStates.Queued, requested.Value!.State);
        var job = environments.Claim("stage-host", requireVerifiedRelease: true)!;
        Assert.Equal("restart", job.RunbookName);
        Assert.NotNull(environments.FindClaimedJob("demo", job.Id, job.LeaseToken!, requireVerifiedRelease: true));
        Assert.Equal(["restart-container", "health"], job.RunbookSteps);
        Assert.Equal("8081", job.Destination);
        Assert.Equal("info", job.RunbookVariables["LOG_LEVEL"]);
        runbooks.SyncAfterHostCall("demo");
        Assert.Equal(DeploymentStates.Running, runbooks.Run("demo", requested.Value.Id)!.State);
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(environments.Claim("stage-host"));
        Assert.True(environments.Report("demo", job.Id, new(job.LeaseToken!, true, "health-ok",
            RunbookResults: [new(0, true), new(1, true)]), requireVerifiedRelease: true));
        runbooks.SyncAfterHostCall("demo");

        var result = runbooks.Run("demo", requested.Value.Id)!;
        Assert.Equal(DeploymentStates.Succeeded, result.State);
        Assert.All(result.Steps, step => Assert.Equal(DeploymentStates.Succeeded, step.State));
        Assert.Contains(runbooks.Log("demo", result.Id, "health")!.Lines,
            line => line.Message == "Operation succeeded.");
        Assert.Contains(deploy.Audit("demo")!, entry => entry.Action == "runbook-succeeded"
            && entry.DeploymentId == result.Id && entry.EnvironmentId == "stage");
        Assert.Empty(deploy.Releases("demo")!);
        Assert.Empty(deploy.Deployments("demo", null)!);
        Assert.Null(deploy.Status("demo")!.Single(item => item.Environment.Id == "stage").CurrentRelease);
        Assert.Empty(environments.Read("demo")!.Environments.Single(item => item.Declaration.Id == "stage").History);
    }

    [Fact]
    public void Production_runbook_waits_for_owner_or_maintainer_and_records_rejection()
    {
        var (_, environments, store) = Build();
        WriteProjectYml("runbooks:\n  - name: rotate\n    steps: [rotate-secrets]\n    environments: [production]\n");
        Commit("runbook-definition");
        var runbooks = Runbooks(environments, store);

        Assert.Equal(DeployOutcomeKind.NotFound, runbooks.Request("demo", "stage", "rotate", Owner).Kind);
        var waiting = runbooks.Request("demo", "production", "rotate", Operator).Value!;
        Assert.Equal(DeploymentStates.AwaitingApproval, waiting.State);
        Assert.Null(environments.Claim("prod-host"));
        Assert.Equal(DeployOutcomeKind.Forbidden, runbooks.Approve("demo", waiting.Id, Operator).Kind);
        Assert.Equal(DeployOutcomeKind.Forbidden, runbooks.Reject("demo", waiting.Id, Operator).Kind);
        Assert.Equal(DeploymentStates.Queued, runbooks.Approve("demo", waiting.Id, Owner).Value!.State);
        var job = environments.Claim("prod-host")!;
        Assert.Equal("prod-host", job.HostId);
        Assert.Contains("DATABASE_URL", job.SecretRefs);
        Assert.True(environments.Report("demo", job.Id, new(job.LeaseToken!, false, "deploy-step-failed",
            RunbookResults: [new(0, false)])));
        runbooks.SyncAfterHostCall("demo");
        Assert.Equal(DeploymentStates.Failed, runbooks.Run("demo", waiting.Id)!.State);
        Assert.Contains(store.ReadAudit("demo", 20), entry => entry.Action == "runbook-approved"
            && entry.Actor == Owner.Id);
        Assert.Contains(store.ReadAudit("demo", 20), entry => entry.Action == "runbook-failed");

        var second = runbooks.Request("demo", "production", "rotate", Operator).Value!;
        Assert.Equal(DeploymentStates.Cancelled, runbooks.Reject("demo", second.Id, Owner).Value!.State);
        Assert.Contains(store.ReadAudit("demo", 20), entry => entry.Action == "runbook-rejected");
        Assert.Null(environments.Claim("prod-host"));
    }

    [Fact]
    public async Task Concurrent_runbook_and_deployment_audit_appends_preserve_every_event()
    {
        var (_, _, store) = Build();
        const int Rounds = 200;
        using var start = new Barrier(2);
        var now = _clock.GetUtcNow().UtcDateTime;
        // The runbook and deployment services hold separate gates and share this store.
        Task Writer(string actor, string action) => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < Rounds; i++)
                store.AppendAudit("demo", [new(now, actor, action, null, $"{action}-{i}", "stage", $"{action} {i}")]);
        });

        await Task.WhenAll(Writer("deploy-host", "runbook-succeeded"), Writer("operator", "deployment-queued"));

        var audit = store.ReadAudit("demo", Rounds * 4);
        Assert.Equal(Rounds * 2, audit.Count);
        Assert.Equal(Rounds, audit.Select(entry => entry.DeploymentId).Distinct()
            .Count(id => id!.StartsWith("runbook-succeeded-", StringComparison.Ordinal)));
        Assert.Equal(Rounds, audit.Select(entry => entry.DeploymentId).Distinct()
            .Count(id => id!.StartsWith("deployment-queued-", StringComparison.Ordinal)));
    }

    [Fact]
    public void Runbook_recovers_a_queued_host_job_when_the_run_record_write_is_lost()
    {
        var (_, environments, store) = Build();
        WriteProjectYml("runbooks:\n  - name: backup\n    steps: [backup]\n");
        Commit("runbook-definition");
        var runbooks = Runbooks(environments, store);
        var requested = runbooks.Request("demo", "stage", "backup", Operator).Value!;
        store.SaveRunbookRuns("demo", []);

        var recovered = runbooks.Run("demo", requested.Id)!;
        Assert.Equal(DeploymentStates.Queued, recovered.State);
        Assert.Equal(requested.DefinitionCommit, recovered.DefinitionCommit);
        Assert.Contains(store.ReadAudit("demo", 20), entry => entry.Action == "runbook-recovered"
            && entry.DeploymentId == requested.Id);
        Assert.NotNull(environments.Claim("stage-host"));
    }

    [Fact]
    public async Task Corrupt_runbook_store_does_not_fail_host_claim_or_report_and_later_reconciles()
    {
        var (_, environments, store) = Build();
        WriteProjectYml("runbooks:\n  - name: backup\n    steps: [backup]\n");
        Commit("runbook-definition");
        var runbooks = Runbooks(environments, store);
        var requested = runbooks.Request("demo", "stage", "backup", Operator).Value!;
        await using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("TaskRepository", Path.Combine(_root, "workspace"));
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
        var file = Path.Combine(_root, "workspace", "deployments", "projects", "demo", "runbooks.json");
        File.WriteAllText(file, "{invalid-json");

        using var claim = await client.PostAsJsonAsync("/api/deploy/jobs/claim", new DeployClaimRequest("stage-host"));
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var claimed = (await claim.Content.ReadFromJsonAsync<DeployJob>())!;
        Assert.Equal(requested.Id, claimed.RunbookRunId);
        using var report = await client.PostAsJsonAsync($"/api/deploy/jobs/demo/{claimed.Id}/report",
            new DeployReportRequest(claimed.LeaseToken!, true, "health-ok", RunbookResults: [new(0, true)]));
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.Equal("healthy", environments.Read("demo")!.Jobs.Single().Status);

        File.Delete(file);
        Assert.Equal(DeploymentStates.Succeeded, runbooks.Run("demo", requested.Id)!.State);
        Assert.Contains(store.ReadAudit("demo", 20), entry => entry.Action == "runbook-succeeded"
            && entry.DeploymentId == requested.Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void GuidedFailure_Pauses_AndRetrySucceedsWithOneRunAndAuditTrail()
    {
        var (deploy, environments, _) = Build(guidedStage: true);
        CreateRelease(deploy, Artifact('a'), Head());
        Report(deploy, environments, "stage-host", healthy: false);
        var waiting = Assert.Single(deploy.Deployments("demo", "stage")!);
        Assert.Equal(DeploymentStates.WaitingForIntervention, waiting.State);
        Assert.Equal(DeployStepNames.StartContainer, waiting.FailedStep);
        Assert.NotNull(waiting.InterventionExpiresAtUtc);
        Assert.Null(environments.Claim("stage-host"));
        Assert.Equal(DeploymentStates.WaitingForIntervention,
            deploy.Status("demo")![0].CurrentDeployment?.State);
        Assert.Contains(deploy.Log("demo", waiting.Id, null)!.Lines,
            line => line.Message.Contains("Waiting for intervention", StringComparison.Ordinal));
        Assert.Contains(deploy.Audit("demo")!, entry => entry.Action == "intervention-requested");

        var retried = deploy.DecideIntervention("demo", waiting.Id, new("retry"), Owner);
        Assert.Equal(DeployOutcomeKind.Ok, retried.Kind);
        Assert.Equal(DeploymentStates.Queued, retried.Value?.State);
        Report(deploy, environments, "stage-host", healthy: true);
        Assert.Equal(DeploymentStates.Succeeded, Assert.Single(deploy.Deployments("demo", "stage")!).State);
        Assert.Contains(deploy.Log("demo", waiting.Id, null)!.Lines,
            line => line.Message.Contains("Retry of", StringComparison.Ordinal));
        Assert.Contains(deploy.Audit("demo")!, entry => entry.Action == "intervention-retry" && entry.Actor == Owner.Id);
        Assert.Equal("pending", Assert.Single(environments.Read("demo")!.Promotions).Status);
    }

    [Fact]
    public void GuidedFailure_SkipRequiresReasonAndApprover_AndDoesNotPromoteSkippedRelease()
    {
        var (deploy, environments, _) = Build(guidedStage: true);
        CreateRelease(deploy, Artifact('a'), Head());
        Report(deploy, environments, "stage-host", healthy: false);
        var waiting = Assert.Single(deploy.Deployments("demo", "stage")!);
        Assert.Equal(DeployOutcomeKind.Forbidden,
            deploy.DecideIntervention("demo", waiting.Id, new("retry"), Operator).Kind);
        Assert.Equal(DeployOutcomeKind.Forbidden,
            deploy.DecideIntervention("demo", waiting.Id, new("abort"), Operator).Kind);
        Assert.Equal(DeployOutcomeKind.Invalid,
            deploy.DecideIntervention("demo", waiting.Id, new("skip", " "), Owner).Kind);
        Assert.Equal(DeployOutcomeKind.Forbidden,
            deploy.DecideIntervention("demo", waiting.Id, new("skip", "test"), Operator).Kind);

        var skipped = deploy.DecideIntervention("demo", waiting.Id, new("skip", "Known optional step"), Owner);
        Assert.Equal(DeployOutcomeKind.Ok, skipped.Kind);
        Assert.Equal(DeploymentStepStates.Skipped, skipped.Value?.Steps[0].State);
        Assert.Null(deploy.Status("demo")![0].CurrentRelease);
        Assert.Contains(deploy.Log("demo", waiting.Id, null)!.Lines,
            line => line.Message.Contains("Known optional step", StringComparison.Ordinal));
        Assert.Contains(deploy.Audit("demo")!, entry => entry.Action == "intervention-skip"
            && entry.Detail.Contains("Known optional step", StringComparison.Ordinal));
    }

    [Fact]
    public void GuidedFailure_HealthSkipKeepsReleaseCurrentAndPreviousReleaseAsRollbackTarget()
    {
        var (deploy, environments, _) = Build(guidedStage: true);
        var first = CreateRelease(deploy, Artifact('a'), Head());
        Report(deploy, environments, "stage-host", healthy: true);
        var second = CreateRelease(deploy, Artifact('b'), Commit("second"));
        Report(deploy, environments, "stage-host", healthy: false, evidence: DeployEvidence.HealthFailed);
        var waiting = deploy.Deployments("demo", "stage")!.First(item => item.ReleaseId == second.Id);
        Assert.Equal((DeploymentStates.WaitingForIntervention, DeployStepNames.Health),
            (waiting.State, waiting.FailedStep));

        Assert.Equal(DeployOutcomeKind.Ok,
            deploy.DecideIntervention("demo", waiting.Id, new("skip", "Health endpoint is optional"), Owner).Kind);
        Assert.Null(environments.Claim("stage-host"));
        var stage = deploy.Status("demo")!.Single(item => item.Environment.Id == "stage");
        Assert.Equal(second.Id, stage.CurrentRelease?.Id);
        Assert.Equal(EnvironmentHealth.Unhealthy, stage.Health);
        Assert.Equal((waiting.Id, "skip", Owner.Id), (stage.Intervention?.DeploymentId,
            stage.Intervention?.Decision, stage.Intervention?.DecidedBy));

        var rollback = deploy.Rollback("demo", "stage", approve: true, Owner);
        Assert.Equal(DeployOutcomeKind.Ok, rollback.Kind);
        Assert.Equal(first.Id, rollback.Value?.ReleaseId);
        Assert.Equal(Artifact('a'), environments.Claim("stage-host")?.Artifact);
    }

    [Fact]
    public void GuidedFailure_AbortResumesTheExistingRollbackRule()
    {
        var (deploy, environments, _) = Build(guidedStage: true);
        CreateRelease(deploy, Artifact('a'), Head());
        Report(deploy, environments, "stage-host", healthy: true);
        CreateRelease(deploy, Artifact('b'), Commit("second"));
        Report(deploy, environments, "stage-host", healthy: false);
        var waiting = deploy.Deployments("demo", "stage")!.First();
        Assert.Equal(DeploymentStates.WaitingForIntervention, waiting.State);
        Assert.Equal(DeployOutcomeKind.Forbidden, deploy.Cancel("demo", waiting.Id, Operator).Kind);
        Assert.Equal(DeployOutcomeKind.Ok,
            deploy.Cancel("demo", waiting.Id, Owner).Kind);
        Assert.Equal(DeploymentStates.Cancelled, deploy.DeploymentById("demo", waiting.Id)?.State);
        Assert.Equal(Artifact('a'), environments.Claim("stage-host")?.Artifact);
        Assert.Contains(deploy.Audit("demo")!, entry => entry.Action == "intervention-abort");
        Assert.Equal(("abort", Owner.Id),
            (deploy.Status("demo")![0].Intervention?.Decision, deploy.Status("demo")![0].Intervention?.DecidedBy));

    }

    [Fact]
    public void GuidedFailure_TimeoutFailsAndQueuesRollback()
    {
        var (deploy, environments, _) = Build(guidedStage: true);
        CreateRelease(deploy, Artifact('a'), Head());
        Report(deploy, environments, "stage-host", healthy: true);
        CreateRelease(deploy, Artifact('b'), Commit("second"));
        Report(deploy, environments, "stage-host", healthy: false);
        var timed = deploy.Deployments("demo", "stage")!.First();
        _clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(DeploymentStates.Failed, deploy.DeploymentById("demo", timed.Id)?.State);
        Assert.Contains(deploy.Audit("demo")!, entry => entry.Action == "intervention-expired");
        Assert.Contains(deploy.Log("demo", timed.Id, null)!.Lines,
            line => line.Message.Contains("timed out", StringComparison.Ordinal));
        Assert.Equal(Artifact('a'), environments.Claim("stage-host")?.Artifact);
    }

    [Fact]
    public void GuidedFailure_PerRunOverrideEnablesInterventionInProduction()
    {
        var (deploy, environments, _) = Build();
        CreateRelease(deploy, Artifact('a'), Head());
        Report(deploy, environments, "stage-host", healthy: true);
        var waiting = Assert.Single(deploy.Deployments("demo", "production")!);
        var started = deploy.Approve("demo", waiting.Id, Owner, guidedFailure: true);
        Assert.Equal(DeployOutcomeKind.Ok, started.Kind);
        Report(deploy, environments, "prod-host", healthy: false, evidence: DeployEvidence.HealthFailed);
        Assert.Equal(DeploymentStates.WaitingForIntervention,
            deploy.DeploymentById("demo", started.Value!.Id)?.State);
        Assert.Null(environments.Claim("prod-host"));
        Assert.Equal(DeployOutcomeKind.Ok,
            deploy.DecideIntervention("demo", started.Value.Id, new("retry"), Owner).Kind);
        var queued = deploy.DeploymentById("demo", started.Value.Id)!;
        Assert.Equal((DeploymentStates.Succeeded, DeploymentStepStates.Pending),
            (queued.Steps[1].State, queued.Steps[2].State));
        var retry = environments.Claim("prod-host")!;
        Assert.Equal(DeployStepNames.Health, retry.RetryStep);
        deploy.SyncAfterHostCall("demo");
        var running = deploy.DeploymentById("demo", started.Value.Id)!;
        Assert.Equal(DeploymentStates.Succeeded, running.Steps[1].State);
        Assert.True(environments.Report("demo", retry.Id, new(retry.LeaseToken!, true, DeployEvidence.HealthOk)));
        deploy.SyncAfterHostCall("demo");
        var complete = deploy.DeploymentById("demo", started.Value.Id)!;
        Assert.Equal((DeploymentStates.Succeeded, DeploymentStates.Succeeded, DeploymentStates.Succeeded),
            (complete.State, complete.Steps[1].State, complete.Steps[2].State));
        Assert.Contains(deploy.Log("demo", started.Value.Id, null)!.Lines,
            line => line.Message.Contains("Health check passed on retry", StringComparison.Ordinal));
    }

    [Fact]
    public void GuidedFailure_AutomaticProductionApprovalUsesTheReleaseSnapshot()
    {
        var (deploy, environments, store) = Build(guidedProduction: true);
        var release = CreateRelease(deploy, Artifact('a'), Head());
        Report(deploy, environments, "stage-host", healthy: true);
        var waiting = Assert.Single(deploy.Deployments("demo", "production")!);
        Assert.Equal(DeploymentStates.AwaitingApproval, waiting.State);
        Assert.True(Assert.Single(store.ReadDeployments("demo"), item => item.Id == waiting.Id).GuidedFailure);
        Assert.Equal(23, store.ReadDeployments("demo").Single(item => item.Id == waiting.Id).InterventionTimeoutMinutes);

        Assert.Equal(DeployOutcomeKind.Ok, deploy.Approve("demo", waiting.Id, Owner).Kind);
        Report(deploy, environments, "prod-host", healthy: false);
        var paused = deploy.DeploymentById("demo", waiting.Id)!;
        Assert.Equal(DeploymentStates.WaitingForIntervention, paused.State);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddMinutes(22), paused.InterventionExpiresAtUtc);
    }

    [Fact]
    public void GuidedFailure_RetryJobSavedBeforeRun_RestartAttachesTheOriginalRun()
    {
        var (deploy, environments, store) = Build(guidedStage: true);
        var release = CreateRelease(deploy, Artifact('a'), Head());
        Report(deploy, environments, "stage-host", healthy: false);
        var waiting = Assert.Single(deploy.Deployments("demo", "stage")!);
        var failedJobId = Assert.Single(environments.Read("demo")!.Jobs).Id;
        var target = store.ReadReleaseRecords("demo").Single(item => item.Release.Id == release.Id)
            .Environments.Single(item => item.Id == "stage");
        var retry = environments.QueueRetry("demo", target, Artifact('a'), Owner.Id, failedJobId, null)!;
        Assert.Equal(failedJobId, retry.RetryOfJobId);
        Assert.Single(store.ReadDeployments("demo"));
        Assert.Equal(DeploymentStates.WaitingForIntervention, store.ReadDeployments("demo")[0].State);

        var restarted = Reopen();
        var attached = Assert.Single(restarted.Deployments("demo", "stage")!);
        Assert.Equal((waiting.Id, retry.Id, DeploymentStates.Queued), (attached.Id,
            store.ReadDeployments("demo")[0].JobId, attached.State));
        Assert.Contains(restarted.Audit("demo")!, entry => entry.Action == "intervention-retry" && entry.Actor == Owner.Id);
        Assert.Contains(restarted.Log("demo", waiting.Id, null)!.Lines,
            line => line.Message.Contains("Retry of", StringComparison.Ordinal));
        Report(restarted, environments, "stage-host", healthy: true);
        Assert.Equal(DeploymentStates.Succeeded, Assert.Single(restarted.Deployments("demo", "stage")!).State);
        Assert.Equal("pending", Assert.Single(environments.Read("demo")!.Promotions).Status);
        Assert.Single(restarted.Deployments("demo", "stage")!);
    }

    [Fact]
    public void GuidedFailure_ManualRollbackCanPauseAndRetryAsRollback()
    {
        var (deploy, environments, _) = Build(guidedProduction: true);
        var first = CreateRelease(deploy, Artifact('a'), Head());
        DeployThroughProduction(deploy, environments, first, Owner);
        var second = CreateRelease(deploy, Artifact('b'), Commit("second"));
        DeployThroughProduction(deploy, environments, second, Owner);

        var rollback = deploy.Rollback("demo", "production", approve: true, Owner);
        Assert.Equal(DeployOutcomeKind.Ok, rollback.Kind);
        var originalJob = environments.Read("demo")!.Jobs.Last(item => item.EnvironmentId == "production");
        Assert.True(originalJob.IsRollback);
        Report(deploy, environments, "prod-host", healthy: false);
        Assert.Equal(DeploymentStates.WaitingForIntervention,
            deploy.DeploymentById("demo", rollback.Value!.Id)?.State);
        Assert.Equal(DeployOutcomeKind.Ok,
            deploy.DecideIntervention("demo", rollback.Value.Id, new("retry"), Owner).Kind);
        var retried = environments.Read("demo")!.Jobs.Last(item => item.EnvironmentId == "production");
        Assert.True(retried.IsRollback);
        Assert.Equal(originalJob.Id, retried.RetryOfJobId);
        Report(deploy, environments, "prod-host", healthy: true);
        var run = deploy.DeploymentById("demo", rollback.Value.Id)!;
        Assert.Equal((DeploymentTriggers.Rollback, DeploymentStates.Succeeded), (run.Trigger, run.State));
        Assert.Equal(first.Id, deploy.Status("demo")![1].CurrentRelease?.Id);
    }

    [Fact]
    public void GuidedFailure_OlderRunCannotRetryOrRollBackOverANewerJob()
    {
        var (deploy, environments, _) = Build(guidedStage: true);
        CreateRelease(deploy, Artifact('a'), Head());
        Report(deploy, environments, "stage-host", healthy: false);
        var older = Assert.Single(deploy.Deployments("demo", "stage")!);
        CreateRelease(deploy, Artifact('b'), Commit("second"));
        Report(deploy, environments, "stage-host", healthy: true);

        Assert.Equal(DeployOutcomeKind.Conflict,
            deploy.DecideIntervention("demo", older.Id, new("retry"), Owner).Kind);
        Assert.Equal(DeployOutcomeKind.Ok,
            deploy.DecideIntervention("demo", older.Id, new("abort"), Owner).Kind);
        Assert.Null(environments.Claim("stage-host"));
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
        var (stored, created) = store.AppendRelease("demo",
            new ReleaseRecord(release with { Version = "tampered", CardKeys = ["X-1"] }, []));
        Assert.False(created);
        Assert.Equal(release.Version, stored.Release.Version);
        Assert.Equal(release.CardKeys, stored.Release.CardKeys);
        Assert.Equal(["stage", "production"], stored.Environments.Select(item => item.Id));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp"));
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
    public void PagedReads_ClampTheSliceAndFilterBeforeCounting()
    {
        var (deploy, environments, _) = Build();
        var commit = Head();
        var release = CreateRelease(deploy, Artifact('a'), commit);
        Report(deploy, environments, "stage-host", healthy: false);
        CreateRelease(deploy, Artifact('a'), commit);

        var releases = deploy.ReleasePage("demo", 0, 100)!;
        Assert.Equal((0, 50, 1), (releases.Offset, releases.Limit, releases.Total));
        Assert.Equal(release.Id, Assert.Single(releases.Items).Id);
        Assert.Empty(deploy.ReleasePage("demo", 1, 25)!.Items);

        var failed = deploy.DeploymentPage("demo", "stage", DeploymentStates.Failed, null, 0, 1)!;
        Assert.Equal(1, failed.Total);
        Assert.Equal(DeploymentStates.Failed, Assert.Single(failed.Items).State);
        Assert.Equal(2, deploy.DeploymentPage("demo", "stage", null, null, 0, 1)!.Total);
        Assert.Single(deploy.DeploymentPage("demo", "stage", null, null, 1, 1)!.Items);
        Assert.Empty(deploy.DeploymentPage("demo", "stage", null, _clock.GetUtcNow().UtcDateTime.AddMinutes(1), 0, 25)!.Items);
    }

    [Fact]
    public void ReleasePage_FindsTheReleaseOfAnArtifactByDigestAndDeployTime()
    {
        var (deploy, _, _) = Build();
        var first = CreateRelease(deploy, Artifact('a'), Head());
        var firstCreated = _clock.GetUtcNow().UtcDateTime;
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = CreateRelease(deploy, Artifact('b'), Commit("second"));

        var byDigest = deploy.ReleasePage("demo", 0, 1, "other.registry/app@sha256:" + new string('a', 64))!;
        Assert.Equal((1, first.Id), (byDigest.Total, Assert.Single(byDigest.Items).Id));
        Assert.Equal(second.Id, Assert.Single(deploy.ReleasePage("demo", 0, 1, Artifact('b'))!.Items).Id);
        Assert.Empty(deploy.ReleasePage("demo", 0, 1, Artifact('b'), firstCreated)!.Items);
        Assert.Empty(deploy.ReleasePage("demo", 0, 1, Artifact('c'))!.Items);
        Assert.Equal(2, deploy.ReleasePage("demo", 0, 25)!.Total);
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
    public void SquashedCommitMessage_KeepsIntegratedCardMapping()
    {
        var (deploy, _, _) = Build();
        var commit = Commit("feat(DEMO-4) improve the product");
        Card("DEMO-4", new string('0', 40));

        var release = CreateRelease(deploy, Artifact('d'), commit);

        Assert.Equal(["DEMO-4"], release.CardKeys);
        Assert.Equal(release.CardKeys, Assert.Single(deploy.Releases("demo")!).CardKeys);
    }

    [Fact]
    public void RewrittenCommitWithoutCardKey_UsesDurableIntegrationVerification()
    {
        var (deploy, _, _) = Build();
        var first = Head();
        CreateRelease(deploy, Artifact('a'), first);
        var integrated = Commit("integrated");
        Card("DEMO-5", integrated);
        // A squash from develop onto the release branch keeps the integrated tree but neither the SHA nor the key.
        var rewritten = Git("commit-tree", integrated + "^{tree}", "-p", first,
            "-m", "Improve the product after integration").Trim();
        Git("checkout", "-b", "main", rewritten);

        var release = CreateRelease(deploy, Artifact('b'), rewritten);

        Assert.Equal(["DEMO-5"], release.CardKeys);
        Assert.Equal(release.CardKeys, deploy.ReleaseDetail("demo", release.Id)!.Release.CardKeys);
    }

    [Fact]
    public void ReleaseWithoutTheIntegratedTree_DoesNotClaimEarlierVerifiedCard()
    {
        var (deploy, _, _) = Build();
        var first = Head();
        CreateRelease(deploy, Artifact('a'), first);
        Card("DEMO-7", Commit("integrated"));
        _clock.Advance(TimeSpan.FromHours(1));
        Git("checkout", "-b", "hotfix", first);
        var hotfix = Commit("hotfix");

        var release = CreateRelease(deploy, Artifact('b'), hotfix);

        Assert.Empty(release.CardKeys);
    }

    [Fact]
    public void SquashMessageNamingAReleasedCard_AddsOnlyTheNewCard()
    {
        var (deploy, _, _) = Build();
        var first = Head();
        Card("DEMO-1", first);
        CreateRelease(deploy, Artifact('a'), first);
        var squashed = Commit("Squash DEMO-1 and DEMO-2");
        Card("DEMO-2", new string('0', 40));

        var release = CreateRelease(deploy, Artifact('b'), squashed);

        Assert.Equal(["DEMO-2"], release.CardKeys);
    }

    [Fact]
    public void DelayedGateForOlderCommit_DoesNotClaimLaterVerifiedCard()
    {
        var (deploy, _, _) = Build();
        var olderCommit = Head();
        _clock.Advance(TimeSpan.FromHours(1));
        Card("DEMO-6", new string('0', 40));

        var release = CreateRelease(deploy, Artifact('a'), olderCommit);

        Assert.Empty(release.CardKeys);
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
    public void Cancel_StopsQueuedWork_AndAbortsWorkTheHostRuns()
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

        // The cancelled release can start again. Once a host claims it, a
        // cancel aborts the run and revokes the host's lease.
        Assert.Equal(release.Id, CreateRelease(deploy, Artifact('a'), release.CommitSha).Id);
        var job = environments.Claim("stage-host")!;
        Assert.Equal("stage", job.EnvironmentId);
        var running = deploy.Deployments("demo", "stage")!.First();
        Assert.Equal(DeploymentStates.Running, running.State);
        var aborted = deploy.Cancel("demo", running.Id, Operator);
        Assert.Equal(DeployOutcomeKind.Ok, aborted.Kind);
        Assert.Equal(DeploymentStates.Cancelled, aborted.Value!.State);
        Assert.False(environments.Report("demo", job.Id, new(job.LeaseToken!, true, "health-ok")));
        Assert.Contains(deploy.Log("demo", running.Id, null)!.Lines,
            line => line.Message == "Aborted by operator while deploy host stage-host was running it.");
        Assert.Equal("deployment-aborted", deploy.Audit("demo")![0].Action);
        Assert.Equal(EnvironmentHealth.Unhealthy, deploy.Status("demo")![0].Health);
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
    public void TwoReleasesWaitingForProduction_ApprovingTheNewer_SupersedesTheOlder()
    {
        var (deploy, environments, _) = Build();
        var r1 = CreateRelease(deploy, Artifact('a'), Head());
        Report(deploy, environments, "stage-host", healthy: true);
        var r2 = CreateRelease(deploy, Artifact('b'), Commit("second"));
        Report(deploy, environments, "stage-host", healthy: true);
        var waiting = deploy.Deployments("demo", "production")!;
        Assert.Equal([r2.Id, r1.Id], waiting.Select(item => item.ReleaseId));
        Assert.All(waiting, item => Assert.Equal(DeploymentStates.AwaitingApproval, item.State));

        Assert.Equal(DeployOutcomeKind.Ok, deploy.Approve("demo", waiting[0].Id, Owner).Kind);
        var older = deploy.DeploymentById("demo", waiting[1].Id)!;
        Assert.Equal(DeploymentStates.Cancelled, older.State);
        Assert.Contains(deploy.Log("demo", older.Id, null)!.Lines,
            line => line.Message == $"Superseded by release {r2.Version} (deploy) queued by owner.");
        Assert.Contains(environments.Read("demo")!.Promotions, item => item.Artifact == Artifact('a') && item.Status == "rejected");

        // Neither the deploy route nor the AGT-3176 promotion route brings the older release back.
        Assert.Equal(DeployOutcomeKind.Conflict, deploy.Approve("demo", older.Id, Owner).Kind);
        Assert.Equal(DeployOutcomeKind.Conflict, deploy.DecidePromotion("demo", Artifact('a'), true, Owner).Kind);
        var queued = environments.Claim("prod-host")!;
        Assert.Equal(Artifact('b'), queued.Artifact);
        Assert.True(environments.Report("demo", queued.Id, new(queued.LeaseToken!, true, "health-ok")));
        var backwards = deploy.RequestDeployment("demo", r1.Id, new("production", Approve: true), Owner);
        Assert.Equal(DeployOutcomeKind.Conflict, backwards.Kind);
        Assert.Contains("Use rollback", backwards.Error);
        Assert.Equal(r2.Id, deploy.Status("demo")!.Single(item => item.Environment.Id == "production").CurrentRelease?.Id);
        Assert.Null(environments.Claim("prod-host"));
    }

    [Fact]
    public void TwoReleasesWaitingForProduction_ApprovingTheOlder_HoldsTheNewerUntilItFinished()
    {
        var (deploy, environments, _) = Build();
        var r1 = CreateRelease(deploy, Artifact('a'), Head());
        Report(deploy, environments, "stage-host", healthy: true);
        var r2 = CreateRelease(deploy, Artifact('b'), Commit("second"));
        Report(deploy, environments, "stage-host", healthy: true);
        var waiting = deploy.Deployments("demo", "production")!.ToDictionary(item => item.ReleaseId, item => item.Id);

        Assert.Equal(DeployOutcomeKind.Ok, deploy.Approve("demo", waiting[r1.Id], Owner).Kind);
        Assert.Equal(DeploymentStates.AwaitingApproval, deploy.DeploymentById("demo", waiting[r2.Id])!.State);
        var refused = deploy.Approve("demo", waiting[r2.Id], Owner);
        Assert.Equal(DeployOutcomeKind.Conflict, refused.Kind);
        Assert.Contains("Wait until", refused.Error);
        Assert.Equal(DeployOutcomeKind.Conflict, deploy.DecidePromotion("demo", Artifact('b'), true, Owner).Kind);

        Report(deploy, environments, "prod-host", healthy: true);
        Assert.Equal(DeployOutcomeKind.Ok, deploy.DecidePromotion("demo", Artifact('b'), true, Owner).Kind);
        Report(deploy, environments, "prod-host", healthy: true);
        var production = deploy.Status("demo")!.Single(item => item.Environment.Id == "production");
        Assert.Equal(r2.Id, production.CurrentRelease?.Id);
        Assert.Equal([DeploymentStates.Succeeded, DeploymentStates.Succeeded],
            deploy.Deployments("demo", "production")!.Select(item => item.State));
    }

    [Fact]
    public void LostHost_FailsTheRunAfterItsClaimExpires_AndLogsEachReclaim()
    {
        var (deploy, environments, _) = Build();
        var release = CreateRelease(deploy, Artifact('a'), Head());
        var first = environments.Claim("stage-host")!;
        deploy.SyncAfterHostCall("demo");
        var run = Assert.Single(deploy.Deployments("demo", "stage")!);
        Assert.Equal(DeploymentStates.Running, run.State);

        // Within the lease the host gets no second job; after it, the
        // restarted host claims its own job again with a new lease.
        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Null(environments.Claim("stage-host"));
        _clock.Advance(TimeSpan.FromMinutes(2));
        var second = environments.Claim("stage-host")!;
        deploy.SyncAfterHostCall("demo");
        Assert.Equal((first.Id, 2), (second.Id, second.Claims));
        Assert.False(environments.Report("demo", first.Id, new(first.LeaseToken!, true, "health-ok")));
        Assert.Contains(deploy.Log("demo", run.Id, null)!.Lines,
            line => line.Message == "Deploy host stage-host claimed the job again after its lease expired (claim 2).");
        Assert.Contains(deploy.Audit("demo")!, item => item.Action == "deployment-reclaimed");

        _clock.Advance(ProjectEnvironmentService.HostLostAfter);
        run = Assert.Single(deploy.Deployments("demo", "stage")!);
        Assert.Equal(DeploymentStates.Failed, run.State);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, run.FinishedAtUtc);
        Assert.Contains(deploy.Log("demo", run.Id, null)!.Lines, line => line.Message.Contains("host-lost"));
        Assert.Equal(EnvironmentHealth.Unhealthy, deploy.Status("demo")![0].Health);
        Assert.False(environments.Report("demo", second.Id, new(second.LeaseToken!, true, "health-ok")));
        Assert.Null(environments.Claim("stage-host"));
        Assert.Equal("failed", environments.Read("demo")!.Promotions.Single().Status);

        // The environment is free again: the release can start over.
        Assert.Equal(release.Id, CreateRelease(deploy, Artifact('a'), release.CommitSha).Id);
        Assert.NotEqual(first.Id, environments.Claim("stage-host")!.Id);
    }

    [Fact]
    public void FailedDeploy_WithANewerJobQueued_GetsNoAutomaticRollbackBehindIt()
    {
        var (deploy, environments, _) = Build();
        CreateRelease(deploy, Artifact('a'), Head());
        Report(deploy, environments, "stage-host", healthy: true);
        CreateRelease(deploy, Artifact('b'), Commit("second"));
        var r3 = CreateRelease(deploy, Artifact('c'), Commit("third"));

        Report(deploy, environments, "stage-host", healthy: false);
        var next = environments.Claim("stage-host")!;
        Assert.Equal((Artifact('c'), false), (next.Artifact, next.IsRollback));
        Assert.True(environments.Report("demo", next.Id, new(next.LeaseToken!, true, "health-ok")));
        Assert.Null(environments.Claim("stage-host"));
        Assert.Equal(r3.Id, deploy.Status("demo")![0].CurrentRelease?.Id);
    }

    [Fact]
    public void ReportAfterTheLease_RequiresAReclaimedToken()
    {
        var (deploy, environments, _) = Build();
        CreateRelease(deploy, Artifact('a'), Head());
        var job = environments.Claim("stage-host")!;
        _clock.Advance(TimeSpan.FromMinutes(8));
        Assert.False(environments.Report("demo", job.Id, new(job.LeaseToken!, true, "health-ok")));
        var reclaimed = environments.Claim("stage-host")!;
        Assert.Equal(job.Id, reclaimed.Id);
        Assert.NotEqual(job.LeaseToken, reclaimed.LeaseToken);
        Assert.True(environments.Report("demo", reclaimed.Id, new(reclaimed.LeaseToken!, true, "health-ok")));
        deploy.SyncAfterHostCall("demo");
        Assert.Equal(DeploymentStates.Succeeded, deploy.Deployments("demo", "stage")!.Single().State);
    }

    [Fact]
    public void RunningProductionDeployment_IsAbortedOnlyByAnApprover()
    {
        var (deploy, environments, _) = Build();
        CreateRelease(deploy, Artifact('a'), Head());
        Report(deploy, environments, "stage-host", healthy: true);
        var waiting = Assert.Single(deploy.Deployments("demo", "production")!);

        // Closing the pending promotion is an approval decision.
        Assert.Equal(DeployOutcomeKind.Forbidden, deploy.Cancel("demo", waiting.Id, Operator).Kind);
        Assert.Equal("pending", environments.Read("demo")!.Promotions.Single().Status);
        Assert.Equal(DeployOutcomeKind.Ok, deploy.Approve("demo", waiting.Id, Owner).Kind);
        var job = environments.Claim("prod-host")!;
        deploy.SyncAfterHostCall("demo");

        var refused = deploy.Cancel("demo", waiting.Id, Operator);
        Assert.Equal(DeployOutcomeKind.Forbidden, refused.Kind);
        Assert.Contains("Aborting a running Production deployment", refused.Error);
        var aborted = deploy.Cancel("demo", waiting.Id, Owner);
        Assert.Equal(DeploymentStates.Cancelled, aborted.Value!.State);
        Assert.False(environments.Report("demo", job.Id, new(job.LeaseToken!, true, "health-ok")));
        Assert.Equal("cancelled", environments.Read("demo")!.Promotions.Single().Status);
        Assert.Null(environments.Claim("prod-host"));
    }

    [Fact]
    public void ChangedDeclarationAfterReleaseCreation_DoesNotChangeWhatTheReleaseDeploys()
    {
        var (deploy, environments, _) = Build();
        var r1 = CreateRelease(deploy, Artifact('a'), Head());
        // The working tree changes production after the release: a new host
        // and port, first uncommitted, then committed.
        WriteProduction(host: "new-host", destination: "9090");
        Report(deploy, environments, "stage-host", healthy: true);
        var r2 = CreateRelease(deploy, Artifact('b'), Commit("moved-production"));
        Assert.NotEqual(r1.ProcessVersion, r2.ProcessVersion);

        // r1 deploys its own declaration (port 8082) on the host production
        // declares now; the retired host never gets the job.
        var approval = deploy.Approve("demo",
            deploy.Deployments("demo", "production")!.Single(item => item.ReleaseId == r1.Id).Id, Owner);
        Assert.Equal(DeployOutcomeKind.Ok, approval.Kind);
        Assert.Null(environments.Claim("prod-host"));
        var old = environments.Claim("new-host")!;
        Assert.Equal((Artifact('a'), "8082"), (old.Artifact, old.Destination));
        Assert.Contains(deploy.Log("demo", approval.Value!.Id, null)!.Lines, line => line.Message
            == $"Release {r1.Version} declared deploy host prod-host; the environment now declares deploy host new-host, "
            + "so the run goes there with the release's declaration.");
        Assert.True(environments.Report("demo", old.Id, new(old.LeaseToken!, true, "health-ok")));
        deploy.SyncAfterHostCall("demo");

        // The newer release deploys its own snapshot.
        Report(deploy, environments, "stage-host", healthy: true);
        Assert.Equal(DeployOutcomeKind.Ok, deploy.Approve("demo", deploy.Deployments("demo", "production")!
            .Single(item => item.ReleaseId == r2.Id && item.State == DeploymentStates.AwaitingApproval).Id, Owner).Kind);
        var moved = environments.Claim("new-host")!;
        Assert.Equal((Artifact('b'), "9090"), (moved.Artifact, moved.Destination));
        Assert.True(environments.Report("demo", moved.Id, new(moved.LeaseToken!, true, "health-ok")));
        deploy.SyncAfterHostCall("demo");

        // A rollback repeats r1's snapshot, not the current declaration, on
        // the current host.
        Assert.Equal(DeployOutcomeKind.Ok, deploy.Rollback("demo", "production", approve: true, Owner).Kind);
        Assert.Null(environments.Claim("prod-host"));
        var rollback = environments.Claim("new-host")!;
        Assert.Equal((Artifact('a'), "8082", true), (rollback.Artifact, rollback.Destination, rollback.IsRollback));
    }

    [Fact]
    public void ReleasesInReverseCommitOrder_NeverMoveStagingOrProductionBack()
    {
        var (deploy, environments, store) = Build();
        var a = Head();
        var middle = Commit("middle");
        var b = Commit("second");
        var rA = CreateRelease(deploy, Artifact('a'), a);
        Report(deploy, environments, "stage-host", healthy: true);
        var rB = CreateRelease(deploy, Artifact('b'), b);
        Report(deploy, environments, "stage-host", healthy: true);

        // The gate run of an ancestor commit finishes after the run of b.
        WriteReleaseEvidence(Artifact('d'), middle);
        Assert.True(environments.RegisterGateArtifact("demo", Artifact('d'), middle));
        var late = deploy.CreateRelease("demo", Artifact('d'), middle, "release-gate");
        Assert.Equal(DeployOutcomeKind.Conflict, late.Kind);
        Assert.Contains("ancestor", late.Error);
        Assert.Equal(DeployRefusalReasons.Superseded, late.Reason);
        Assert.Equal([rA.Id, rB.Id], store.ReadReleases("demo").Select(item => item.Id));

        // Production takes the newest release, which closes the older
        // release's promotion.
        var waiting = deploy.Deployments("demo", "production")!.ToDictionary(item => item.ReleaseId, item => item.Id);
        Assert.Equal(DeployOutcomeKind.Ok, deploy.Approve("demo", waiting[rB.Id], Owner).Kind);
        Report(deploy, environments, "prod-host", healthy: true);
        Assert.Contains(environments.Read("demo")!.Promotions, item => item.Artifact == Artifact('a') && item.Status == "rejected");

        // Re-posting the older release after that does not queue Staging backwards.
        var repost = deploy.CreateRelease("demo", Artifact('a'), a, "release-gate");
        Assert.Equal(DeployOutcomeKind.Conflict, repost.Kind);
        Assert.Contains("Staging runs only the newest release", repost.Error);
        Assert.Equal(DeployRefusalReasons.Superseded, repost.Reason);
        Assert.Null(environments.Claim("stage-host"));
        Assert.Equal(2, deploy.Deployments("demo", "stage")!.Count);

        // Neither does anything else move production back to the older release.
        Assert.Equal(DeployOutcomeKind.Conflict, deploy.Approve("demo", waiting[rA.Id], Owner).Kind);
        Assert.Equal(DeployOutcomeKind.Conflict,
            deploy.RequestDeployment("demo", rA.Id, new("production", Approve: true), Owner).Kind);
        Assert.Equal(DeployOutcomeKind.Conflict, deploy.CreateRelease("demo", Artifact('d'), middle, "release-gate").Kind);
        Assert.Null(environments.Claim("stage-host"));
        Assert.Null(environments.Claim("prod-host"));
        Assert.All(deploy.Status("demo")!, status => Assert.Equal(rB.Id, status.CurrentRelease?.Id));

        // A rebuild of the newest commit is still a release.
        Assert.Equal(b, CreateRelease(deploy, Artifact('c'), b).CommitSha);
    }

    [Fact]
    public void RollbackRoute_OnTheFirstRelease_RefusesUnverifiedArtifactFromBeforeReleases()
    {
        var (deploy, environments, _) = Build();
        var a = Head();
        // Production runs an artifact from before releases existed (AGT-3176 only).
        WriteReleaseEvidence(Artifact('a'), a);
        Assert.True(environments.RegisterGateArtifact("demo", Artifact('a'), a));
        Assert.True(environments.QueueRelease("demo", Artifact('a'), a));
        Report(deploy, environments, "stage-host", healthy: true);
        Assert.Equal(DeployOutcomeKind.Ok, deploy.DecidePromotion("demo", Artifact('a'), true, Owner).Kind);
        Report(deploy, environments, "prod-host", healthy: true);
        var r1 = CreateRelease(deploy, Artifact('b'), Commit("second"));
        DeployThroughProduction(deploy, environments, r1, Owner);
        Assert.Equal(DeployOutcomeKind.Conflict, deploy.Rollback("demo", "production", approve: true, Owner).Kind);

        // Production moved hosts meanwhile; the history rollback goes to the new one.
        WriteProduction(host: "new-host", destination: "8082");
        Assert.Equal(DeployOutcomeKind.Ok, deploy.RollbackEnvironment("demo", "production", Owner).Kind);
        var busy = deploy.RollbackEnvironment("demo", "production", Owner);
        Assert.Equal(DeployOutcomeKind.Conflict, busy.Kind);
        Assert.Contains("Wait until", busy.Error);
        Assert.Null(environments.Claim("prod-host"));
        Assert.Null(environments.Claim("new-host", requireVerifiedRelease: true));
        var refused = environments.Read("demo")!.Jobs.Last(item => item.IsRollback);
        Assert.Equal((Artifact('a'), "failed", "artifact-unverified"),
            (refused.Artifact, refused.Status, refused.Evidence));
        Assert.Null(refused.LeaseToken);
    }

    [Fact]
    public void RollbackRoute_AfterALifecycleRollbackToTheOldestRelease_IsAConflict_AndQueuesNothing()
    {
        var (deploy, environments, _) = Build();
        var r1 = CreateRelease(deploy, Artifact('a'), Head());
        DeployThroughProduction(deploy, environments, r1, Owner);
        var r2 = CreateRelease(deploy, Artifact('b'), Commit("second"));
        DeployThroughProduction(deploy, environments, r2, Owner);
        var rollback = deploy.Rollback("demo", "production", approve: true, Owner);
        Assert.Equal((r1.Id, DeploymentStates.Queued), (rollback.Value!.ReleaseId, rollback.Value.State));
        Report(deploy, environments, "prod-host", healthy: true);
        var runs = deploy.Deployments("demo", "production")!.Count;

        // The artifact history would pick r2's artifact, the release just left.
        var refused = deploy.RollbackEnvironment("demo", "production", Owner);
        Assert.Equal(DeployOutcomeKind.Conflict, refused.Kind);
        Assert.Contains("no previous release to roll back to", refused.Error);
        Assert.Null(environments.Claim("prod-host"));
        Assert.Equal(runs, deploy.Deployments("demo", "production")!.Count);
        var production = deploy.Status("demo")!.Single(item => item.Environment.Id == "production");
        Assert.Equal((r1.Id, r2.Id), (production.CurrentRelease?.Id, production.NewerRelease?.Id));
    }

    [Fact]
    public void Release_WhenGitCannotCompareItWithTheNewestRelease_IsRefused()
    {
        var (deploy, environments, store) = Build();
        var b = Commit("second");
        var r1 = CreateRelease(deploy, Artifact('a'), b);
        Report(deploy, environments, "stage-host", healthy: true);
        // An unrelated commit becomes the integration tip; the newest release's
        // commit object is then lost, so merge-base fails instead of answering.
        Git("checkout", "--orphan", "rewritten");
        var c = Commit("unrelated");
        Git("branch", "-f", "develop", c);
        DeleteLooseObject(b);

        WriteReleaseEvidence(Artifact('c'), c);
        Assert.True(environments.RegisterGateArtifact("demo", Artifact('c'), c));
        var refused = deploy.CreateRelease("demo", Artifact('c'), c, "release-gate");
        Assert.Equal(DeployOutcomeKind.Conflict, refused.Kind);
        Assert.Contains("Git could not compare", refused.Error);
        // Not superseded: the release driver must stop on this refusal.
        Assert.Null(refused.Reason);
        Assert.Equal([r1.Id], store.ReadReleases("demo").Select(item => item.Id));
        Assert.Null(environments.Claim("stage-host"));
    }

    [Theory]
    [InlineData("""{"name":"Production","kind":"production"}""")]
    [InlineData("""{"id":"production","name":"Production","kind":"production","targetHostId":"prod-host","template":"container","destination":"8082","healthUrl":"https://example/health","secretRefs":"DATABASE_URL"}""")]
    [InlineData("""{"id":"production","name":"Production","kind":"production","targetHostId":"prod-host","template":"container","destination":"8082","healthUrl":"https://example/health","approval":7}""")]
    public void Release_WithAMalformedCommittedDeclaration_IsInvalid(string production)
    {
        var (deploy, environments, store) = Build();
        File.WriteAllText(Path.Combine(_repo, "docs", "deployments", "environments", "production.json"), production);
        var malformed = Commit("malformed");
        // The working tree has moved on; only the committed snapshot is malformed.
        WriteProduction(host: "prod-host", destination: "8082");

        WriteReleaseEvidence(Artifact('a'), malformed);
        Assert.True(environments.RegisterGateArtifact("demo", Artifact('a'), malformed));
        var refused = deploy.CreateRelease("demo", Artifact('a'), malformed, "release-gate");
        Assert.Equal(DeployOutcomeKind.Invalid, refused.Kind);
        Assert.Contains("are invalid", refused.Error);
        Assert.Empty(store.ReadReleases("demo"));
    }

    [Fact]
    public void Release_WithAnUnreadableDeclaration_IsRefused_AndNoPartialSnapshotIsStored()
    {
        var (deploy, environments, store) = Build();
        // A file name git would quote without -z still reads.
        Git("mv", "docs/deployments/environments/production.json", "docs/deployments/environments/produktion-ä.json");
        var renamed = Commit("renamed");
        var r1 = CreateRelease(deploy, Artifact('a'), renamed);
        Assert.Equal(["stage", "production"], store.ReadReleaseRecords("demo").Single().Environments.Select(item => item.Id));
        Report(deploy, environments, "stage-host", healthy: true);

        File.WriteAllText(Path.Combine(_repo, "docs", "deployments", "environments", "other.json"), JsonSerializer.Serialize(new
        {
            id = "preview", name = "Preview", kind = "other", targetHostId = "preview-host", address = "https://preview",
            template = "container", destination = "8083", approval = "person",
        }));
        var broken = Commit("other-environment");
        DeleteLooseObject(Git("rev-parse", $"{broken}:docs/deployments/environments/other.json").Trim());

        WriteReleaseEvidence(Artifact('b'), broken);
        Assert.True(environments.RegisterGateArtifact("demo", Artifact('b'), broken));
        var refused = deploy.CreateRelease("demo", Artifact('b'), broken, "release-gate");
        Assert.Equal(DeployOutcomeKind.Conflict, refused.Kind);
        Assert.Contains("could not be read completely", refused.Error);
        Assert.Equal([r1.Id], store.ReadReleases("demo").Select(item => item.Id));
        Assert.Null(environments.Claim("stage-host"));
    }

    [Fact]
    public void Approve_AfterTheDeclarationWasRemoved_IsAConflict()
    {
        var (deploy, environments, _) = Build();
        CreateRelease(deploy, Artifact('a'), Head());
        Report(deploy, environments, "stage-host", healthy: true);
        var waiting = Assert.Single(deploy.Deployments("demo", "production")!);
        File.Delete(Path.Combine(_repo, "docs", "deployments", "environments", "production.json"));

        var refused = deploy.Approve("demo", waiting.Id, Owner);
        Assert.Equal(DeployOutcomeKind.Conflict, refused.Kind);
        Assert.Contains("no longer declared", refused.Error);
        Assert.Null(environments.Claim("prod-host"));
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

    [Theory]
    [InlineData("""{"id":"production","name":"Production","kind":"production","targetHostId":"prod-host","address":"https://example","template":"container","destination":"8082","healthUrl":"https://example/health","secretRefs":"DATABASE_URL"}""")]
    [InlineData("""{"id":"production","name":"Production","kind":"production","targetHostId":"prod-host","address":"https://example","template":"container","destination":"8082","healthUrl":"https://example/health","containerPort":"8080"}""")]
    [InlineData("""{"id":"production","name":"Production","kind":"production","targetHostId":"prod-host","address":"https://example","template":"container","destination":"8082","healthUrl":"https://example/health","healthChecks":[{"id":7}]}""")]
    [InlineData("""["production"]""")]
    public void Matrix_WithAMalformedDeclaration_ReportsTheProjectInsteadOfFailing(string production)
    {
        var (deploy, _, _) = Build();
        File.WriteAllText(Path.Combine(_repo, "docs", "deployments", "environments", "production.json"), production);

        var row = Assert.Single(deploy.Matrix().Projects);
        Assert.Equal("demo", row.ProjectId);
        Assert.Empty(row.Statuses);
        Assert.Contains("production.json", row.Error);
    }

    [Fact]
    public void VariablesVersion_FollowsTheProjectYmlVariablesAtTheReleaseCommit()
    {
        var (deploy, environments, _) = Build();
        var r1 = CreateRelease(deploy, Artifact('a'), Head());
        Report(deploy, environments, "stage-host", healthy: true);
        WriteProjectYml("  - name: LOG_LEVEL\n    value: info\n");
        var r2 = CreateRelease(deploy, Artifact('b'), Commit("variables"));
        Report(deploy, environments, "stage-host", healthy: true);
        var r3 = CreateRelease(deploy, Artifact('c'), Commit("unrelated"));
        Report(deploy, environments, "stage-host", healthy: true);
        WriteProjectYml("  - name: LOG_LEVEL\n    value: debug\n");
        var r4 = CreateRelease(deploy, Artifact('d'), Commit("debug"));

        Assert.NotEqual(r1.VariablesVersion, r2.VariablesVersion);
        Assert.Equal(r2.VariablesVersion, r3.VariablesVersion);
        Assert.NotEqual(r2.VariablesVersion, r4.VariablesVersion);
    }

    [Fact]
    public void Release_creation_uses_committed_channel_rules_and_preserves_the_snapshot()
    {
        var (deploy, environments, store) = Build();
        WriteProjectYml("""
            lifecycles:
              standard:
                - phase: staging
                  automatic: true
                - phase: production
                  approval: [Owner]
                  requires: staging
              hotfix:
                - phase: staging
                  automatic: true
                - phase: production
                  automatic: true
                  requires: staging
            channels:
              - name: default
                lifecycle: standard
                default: true
              - name: hotfix
                lifecycle: hotfix
                branchPattern: refs/heads/hotfix/*
            variables:
              - name: LOG_LEVEL
                value: debug
                channels: [hotfix]
            """);
        var commit = Commit("channel-definition");
        var artifact = Artifact('a');
        WriteReleaseEvidence(artifact, commit);
        Assert.True(environments.RegisterGateArtifact("demo", artifact, commit));
        var refused = deploy.CreateRelease("demo", artifact, commit, "release-gate", branch: "refs/heads/main",
            requestedChannel: "hotfix");
        Assert.Equal(DeployOutcomeKind.Invalid, refused.Kind);
        Assert.Contains("branchPattern=refs/heads/hotfix/*", refused.Error);
        var created = deploy.CreateRelease("demo", artifact, commit, "release-gate",
            branch: "refs/heads/hotfix/urgent");
        Assert.Equal(DeployOutcomeKind.Ok, created.Kind);
        Assert.Equal("hotfix", created.Value!.Channel);
        Assert.Equal("staging", Assert.Single(store.ReadReleaseRecords("demo")).Lifecycle[1].Requires);
        Report(deploy, environments, "stage-host", healthy: true);
        Assert.Contains(deploy.Deployments("demo", "production")!, item => item.State == DeploymentStates.Queued);
    }

    [Fact]
    public void Schema_two_jobs_keep_snapshot_steps_and_automatic_rollback_uses_previous_artifact()
    {
        var (deploy, environments, _) = Build();
        WriteSshProjectYml();
        var first = CreateRelease(deploy, Artifact('a'), Commit("ssh-definition"));
        var firstJob = environments.Claim("stage-host")!;
        Assert.Equal("container", Assert.Single(firstJob.SshSteps).Step);
        Assert.True(environments.Report("demo", firstJob.Id, new(firstJob.LeaseToken!, true, "health-ok",
            [new("deploy[0]:container", true)])));
        deploy.SyncAfterHostCall("demo");

        var second = CreateRelease(deploy, Artifact('b'), Commit("new-release"));
        var secondJob = environments.Claim("stage-host")!;
        Assert.Equal(second.ArtifactDigests[0], secondJob.Artifact);
        Assert.True(environments.Report("demo", secondJob.Id, new(secondJob.LeaseToken!, false, "deploy-step-failed",
            [new("deploy[0]:container", false)])));
        deploy.SyncAfterHostCall("demo");

        var rollback = environments.Claim("stage-host")!;
        Assert.True(rollback.IsRollback);
        Assert.Equal(first.ArtifactDigests[0], rollback.Artifact);
        Assert.Equal(firstJob.SshSteps.Select(step => (step.Step, step.Target, step.ComposeFile, step.Service)),
            rollback.SshSteps.Select(step => (step.Step, step.Target, step.ComposeFile, step.Service)));
        var failed = deploy.Deployments("demo", "stage")!.First(item => item.ReleaseId == second.Id);
        Assert.Equal(DeploymentStates.Failed, failed.State);
        Assert.Equal(DeploymentStates.Failed, failed.Steps[0].State);
    }

    [Fact]
    public void Schema_two_guided_health_retry_reexecutes_the_snapshot_steps()
    {
        var (deploy, environments, _) = Build(guidedStage: true);
        WriteSshProjectYml();
        var release = CreateRelease(deploy, Artifact('a'), Commit("guided-ssh"));
        Report(deploy, environments, "stage-host", healthy: false, evidence: DeployEvidence.HealthFailed);
        var waiting = Assert.Single(deploy.Deployments("demo", "stage")!);
        Assert.Equal(DeploymentStates.WaitingForIntervention, waiting.State);

        Assert.Equal(DeployOutcomeKind.Ok,
            deploy.DecideIntervention("demo", waiting.Id, new("retry"), Owner).Kind);
        var retry = environments.Claim("stage-host")!;
        Assert.Null(retry.RetryStep);
        Assert.Equal("container", Assert.Single(retry.SshSteps).Step);
        Assert.True(environments.Report("demo", retry.Id,
            new(retry.LeaseToken!, true, DeployEvidence.HealthOk, [new("deploy[0]:container", true)])));
        deploy.SyncAfterHostCall("demo");

        var complete = Assert.Single(deploy.Deployments("demo", "stage")!);
        Assert.Equal(release.Id, complete.ReleaseId);
        Assert.Equal(DeploymentStates.Succeeded, complete.State);
        Assert.Equal(DeploymentStates.Succeeded, complete.Steps[0].State);
    }

    [Fact]
    public void Schema_two_release_refuses_current_declaration_divergence()
    {
        var (deploy, _, _) = Build();
        WriteSshProjectYml();
        var commit = Commit("ssh-definition");
        WriteProduction(host: "different-host", destination: "8082");

        var result = deploy.CreateRelease("demo", Artifact('a'), commit, "release-gate");

        Assert.Equal(DeployOutcomeKind.Invalid, result.Kind);
        Assert.Contains("diverges", result.Error);
    }

    [Fact]
    public void Schema_two_release_runs_without_legacy_json_declarations()
    {
        var (deploy, environments, _) = Build();
        WriteSshProjectYml();
        Directory.Delete(Path.Combine(_repo, "docs", "deployments", "environments"), true);
        var commit = Commit("schema-two-only");

        var release = CreateRelease(deploy, Artifact('a'), commit);
        var job = environments.Claim("stage-host");

        Assert.NotNull(release);
        Assert.Equal("stage", job?.EnvironmentId);
        Assert.Equal("container", Assert.Single(job!.SshSteps).Step);
        Assert.Equal(["stage", "production"], environments.Declarations("demo")!.Select(item => item.Id));
    }

    [Fact]
    public async Task Routes_ServeTheSharedContract()
    {
        Build();
        var older = Head();
        var commit = Commit("second");
        Card("DEMO-1", commit, title: "Faster page loading");
        WriteReleaseEvidence(Artifact('a'), commit);
        WriteReleaseEvidence(Artifact('b'), older);
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

        using var unknownChannel = await client.PostAsJsonAsync("/api/projects/demo/environments/releases",
            new ReleaseArtifactRequest(Artifact('a'), commit, Channel: "hotfix"));
        Assert.Equal(HttpStatusCode.BadRequest, unknownChannel.StatusCode);
        Assert.Contains("Channel 'hotfix' is not declared", await unknownChannel.Content.ReadAsStringAsync());

        using var created = await client.PostAsJsonAsync("/api/projects/demo/environments/releases",
            new ReleaseArtifactRequest(Artifact('a'), commit));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var releaseId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();
        // An ancestor commit after a newer release is a conflict with the stored releases.
        Assert.True(factory.Services.GetRequiredService<ProjectEnvironmentService>()
            .RegisterGateArtifact("demo", Artifact('b'), older));
        using var backwards = await client.PostAsJsonAsync("/api/projects/demo/environments/releases",
            new ReleaseArtifactRequest(Artifact('b'), older));
        Assert.Equal(HttpStatusCode.Conflict, backwards.StatusCode);
        using (var refusal = JsonDocument.Parse(await backwards.Content.ReadAsStringAsync()))
        {
            Assert.Contains("ancestor", refusal.RootElement.GetProperty("error").GetString());
            Assert.Equal("superseded", refusal.RootElement.GetProperty("reason").GetString());
        }

        using var status = JsonDocument.Parse(await client.GetStringAsync("/api/projects/demo/deploy/status"));
        var staging = status.RootElement[0];
        Assert.Equal("staging", staging.GetProperty("environment").GetProperty("kind").GetString());
        Assert.Equal(0, staging.GetProperty("environment").GetProperty("order").GetInt32());
        Assert.Equal(JsonValueKind.Null, staging.GetProperty("currentRelease").ValueKind);
        using var releasePage = JsonDocument.Parse(await client.GetStringAsync("/api/projects/demo/deploy/releases/page?offset=0&limit=1"));
        Assert.Equal(1, releasePage.RootElement.GetProperty("total").GetInt32());
        Assert.Single(releasePage.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("default", releasePage.RootElement.GetProperty("items")[0].GetProperty("channel").GetString());
        using var runPage = JsonDocument.Parse(await client.GetStringAsync(
            "/api/projects/demo/deploy/deployments/page?environmentId=stage&state=queued&offset=0&limit=1"));
        Assert.Equal(1, runPage.RootElement.GetProperty("total").GetInt32());
        Assert.Equal("queued", runPage.RootElement.GetProperty("items")[0].GetProperty("state").GetString());
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

        using var notes = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/projects/demo/deploy/releases/{releaseId}/notes"));
        Assert.Equal("Faster page loading", notes.RootElement.GetProperty("notes")[0].GetProperty("text").GetString());
        using var cards = JsonDocument.Parse(await client.GetStringAsync("/api/projects/demo/deploy/cards"));
        Assert.Equal("DEMO-1", cards.RootElement[0].GetProperty("cardKey").GetString());
        Assert.Equal("not-deployed", cards.RootElement[0].GetProperty("environments")[1]
            .GetProperty("state").GetString());

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

        // Production approval needs a signed-in Owner or Maintainer, also on
        // the local profile: a caller without a session, such as a coding
        // agent, is refused on the deploy and the AGT-3176 routes.
        var hosts = factory.Services.GetRequiredService<ProjectEnvironmentService>();
        var service = factory.Services.GetRequiredService<DeployService>();
        var stage = hosts.Claim("stage-host")!;
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var enrollment = security.CreateEnrollment(new RunnerEnrollmentRequest("deploy-host",
            [RunnerScopes.Deploy], null, null));
        var credential = security.EnrollRunner(enrollment.Code);
        using var host = factory.CreateClient();
        var artifactUrl = $"/api/deploy/jobs/demo/{stage.Id}/artifact";
        using var unauthenticatedRequest = new HttpRequestMessage(HttpMethod.Get, artifactUrl);
        unauthenticatedRequest.Headers.Add("X-Deploy-Lease", stage.LeaseToken);
        using var unauthenticated = await host.SendAsync(unauthenticatedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, unauthenticated.StatusCode);
        using var unauthenticatedContentRequest = new HttpRequestMessage(HttpMethod.Get, artifactUrl + "?content=1");
        unauthenticatedContentRequest.Headers.Add("X-Deploy-Lease", stage.LeaseToken);
        using var unauthenticatedContent = await host.SendAsync(unauthenticatedContentRequest);
        Assert.Equal(HttpStatusCode.Forbidden, unauthenticatedContent.StatusCode);
        var releaseCredential = security.EnrollRunner(security.CreateEnrollment(new RunnerEnrollmentRequest(
            "builder-only", [RunnerScopes.Release], null, null)).Code);
        host.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", releaseCredential.Secret);
        using var wrongScopeRequest = new HttpRequestMessage(HttpMethod.Get, artifactUrl);
        wrongScopeRequest.Headers.Add("X-Deploy-Lease", stage.LeaseToken);
        using var wrongScope = await host.SendAsync(wrongScopeRequest);
        Assert.Equal(HttpStatusCode.Forbidden, wrongScope.StatusCode);
        using var wrongScopeContentRequest = new HttpRequestMessage(HttpMethod.Get, artifactUrl + "?content=1");
        wrongScopeContentRequest.Headers.Add("X-Deploy-Lease", stage.LeaseToken);
        using var wrongScopeContent = await host.SendAsync(wrongScopeContentRequest);
        Assert.Equal(HttpStatusCode.Forbidden, wrongScopeContent.StatusCode);
        host.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", credential.Secret);
        using var withoutLease = await host.GetAsync(artifactUrl);
        Assert.Equal(HttpStatusCode.Forbidden, withoutLease.StatusCode);
        using var artifactRequest = new HttpRequestMessage(HttpMethod.Get, artifactUrl);
        artifactRequest.Headers.Add("X-Deploy-Lease", stage.LeaseToken);
        using var verified = await host.SendAsync(artifactRequest);
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        using (var provenance = JsonDocument.Parse(await verified.Content.ReadAsStringAsync()))
        {
            Assert.Equal("demo", provenance.RootElement.GetProperty("project").GetString());
            Assert.Equal(commit, provenance.RootElement.GetProperty("commit").GetString());
            Assert.Equal("release/test-gate", provenance.RootElement.GetProperty("gateRunId").GetString());
            Assert.Equal("container", provenance.RootElement.GetProperty("template").GetString());
            Assert.Equal(new string('a', 64), provenance.RootElement.GetProperty("digest").GetString());
        }
        Assert.Equal(releaseId, stage.ReleaseId);
        var otherCommit = Commit("reuse-digest");
        WriteReleaseEvidence(Artifact('a'), otherCommit);
        Assert.True(hosts.RegisterGateArtifact("demo", Artifact('a'), otherCommit));
        var store = factory.Services.GetRequiredService<DeployStore>();
        var original = Assert.Single(store.ReadReleaseRecords("demo"));
        Assert.True(store.AppendRelease("demo", new ReleaseRecord(
            original.Release with { Id = "ffffffffffff", CommitSha = otherCommit }, original.Environments)).Created);
        using var reusedDigestRequest = new HttpRequestMessage(HttpMethod.Get, artifactUrl);
        reusedDigestRequest.Headers.Add("X-Deploy-Lease", stage.LeaseToken);
        using var reusedDigest = await host.SendAsync(reusedDigestRequest);
        Assert.Equal(HttpStatusCode.OK, reusedDigest.StatusCode);
        using (var provenance = JsonDocument.Parse(await reusedDigest.Content.ReadAsStringAsync()))
            Assert.Equal(commit, provenance.RootElement.GetProperty("commit").GetString());
        File.Delete(Path.Combine(_root, "workspace", "deployments", "projects", "demo", "releases",
            "ffffffffffff.json"));
        var provenanceFile = Directory.GetFiles(Path.Combine(_root, "workspace", "release-registry", "records"),
            "*.json", SearchOption.AllDirectories).Single(file => file.Contains(commit, StringComparison.Ordinal));
        var provenanceBytes = File.ReadAllBytes(provenanceFile);
        File.Delete(provenanceFile);
        using var unverifiedRequest = new HttpRequestMessage(HttpMethod.Get, artifactUrl);
        unverifiedRequest.Headers.Add("X-Deploy-Lease", stage.LeaseToken);
        using var unverified = await host.SendAsync(unverifiedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, unverified.StatusCode);
        File.WriteAllBytes(provenanceFile, provenanceBytes);
        File.Move(provenanceFile, provenanceFile + ".backup");
        Directory.CreateDirectory(provenanceFile);
        try
        {
            using var retryRequest = new HttpRequestMessage(HttpMethod.Get, artifactUrl);
            retryRequest.Headers.Add("X-Deploy-Lease", stage.LeaseToken);
            using var retry = await host.SendAsync(retryRequest);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, retry.StatusCode);
            Assert.Equal("claimed", hosts.Read("demo")!.Jobs.Single(item => item.Id == stage.Id).Status);
        }
        finally
        {
            Directory.Delete(provenanceFile);
            File.Move(provenanceFile + ".backup", provenanceFile);
        }
        Assert.True(hosts.Report("demo", stage.Id, new(stage.LeaseToken!, true, "health-ok")));
        service.SyncAfterHostCall("demo");
        var production = Assert.Single(service.Deployments("demo", "production")!);
        using var agent = factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        using var refused = await agent.PostAsync($"/api/projects/demo/deploy/deployments/{production.Id}/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var refusedPromotion = await agent.PostAsJsonAsync("/api/projects/demo/environments/promotions/approve",
            new PromotionDecisionRequest(Artifact('a')));
        Assert.Equal(HttpStatusCode.Forbidden, refusedPromotion.StatusCode);
        using var approved = await client.PostAsJsonAsync($"/api/projects/demo/deploy/deployments/{production.Id}/approve",
            new DeploymentApprovalRequest(GuidedFailure: true));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(auth.User!.Id, service.DeploymentById("demo", production.Id)!.ApprovedBy);
        var productionJob = hosts.Claim("prod-host")!;
        Assert.True(hosts.Report("demo", productionJob.Id,
            new(productionJob.LeaseToken!, false, DeployEvidence.HealthFailed)));
        service.SyncAfterHostCall("demo");
        Assert.Equal(DeploymentStates.WaitingForIntervention, service.DeploymentById("demo", production.Id)!.State);
        using var unauthorizedIntervention = await agent.PostAsJsonAsync(
            $"/api/projects/demo/deploy/deployments/{production.Id}/intervention",
            new InterventionDecisionRequest("skip", "Optional health check"));
        Assert.Equal(HttpStatusCode.Forbidden, unauthorizedIntervention.StatusCode);
        using var aborted = await client.PostAsJsonAsync(
            $"/api/projects/demo/deploy/deployments/{production.Id}/intervention",
            new InterventionDecisionRequest("abort", "Health check failed"));
        Assert.Equal(HttpStatusCode.OK, aborted.StatusCode);
        Assert.Contains(service.Audit("demo")!, entry => entry.Action == "intervention-abort"
            && entry.Actor == auth.User.Id);
        service.SyncAfterHostCall("demo");
        var failures = factory.Services.GetRequiredService<TaskScannerService>().ScanAllJobsRaw()
            .Where(item => item.Id == "production-deploy-failed-" + production.Id).ToArray();
        var failure = Assert.Single(failures);
        Assert.Equal("bug", failure.TaskType);
        Assert.Contains(releaseId!, File.ReadAllText(Path.Combine(failure.FolderPath, "prompt.md")));
    }

    [Theory]
    [InlineData("networked")]
    [InlineData("local")]
    public async Task ArtifactRoute_RequiresDeployCredentialAndReturnsDigest(string profile)
    {
        Build();
        await using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("TaskRepository", Path.Combine(_root, "workspace"));
            builder.UseSetting("Deploy:ReleaseEvidenceRoot", Path.Combine(_root, "release-evidence"));
            builder.UseSetting("WatchPaths:0:Name", "demo");
            builder.UseSetting("WatchPaths:0:Path", _watch);
            builder.UseSetting("WatchPaths:0:RootPath", _repo);
            builder.UseSetting("WatchPaths:0:RepositoryPath", _repo);
            builder.UseSetting("Security:Profile", profile);
            builder.UseSetting("AllowedHosts", "studio.test");
            builder.UseSetting("Logging:BackendFile:LogDirectory", Path.Combine(_root, "logs"));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test") });
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var deployCredential = security.EnrollRunner(security.CreateEnrollment(new RunnerEnrollmentRequest(
            "stage-deploy", [RunnerScopes.Deploy], null, null)).Code);
        var releaseCredential = security.EnrollRunner(security.CreateEnrollment(new RunnerEnrollmentRequest(
            "builder-only", [RunnerScopes.Release], null, null)).Code);
        var stagePath = Path.Combine(_repo, "docs", "deployments", "environments", "stage.json");
        var declaration = JsonNode.Parse(File.ReadAllText(stagePath))!;
        declaration["targetHostId"] = deployCredential.Runner.Id;
        File.WriteAllText(stagePath, declaration.ToJsonString());
        var commit = Commit("networked-deploy-host");
        WriteReleaseEvidence(Artifact('a'), commit);
        var environments = factory.Services.GetRequiredService<ProjectEnvironmentService>();
        Assert.True(environments.RegisterGateArtifact("demo", Artifact('a'), commit));
        var created = factory.Services.GetRequiredService<DeployService>()
            .CreateRelease("demo", Artifact('a'), commit, "release-gate");
        Assert.Equal(DeployOutcomeKind.Ok, created.Kind);
        var job = environments.Claim(deployCredential.Runner.Id, requireVerifiedRelease: true)!;
        var url = $"/api/deploy/jobs/demo/{job.Id}/artifact";

        using var missingRequest = new HttpRequestMessage(HttpMethod.Get, url);
        missingRequest.Headers.Add("X-Deploy-Lease", job.LeaseToken);
        using var missing = await client.SendAsync(missingRequest);
        Assert.Equal(profile == "networked" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden,
            missing.StatusCode);

        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", releaseCredential.Secret);
        using var wrongScopeRequest = new HttpRequestMessage(HttpMethod.Get, url);
        wrongScopeRequest.Headers.Add("X-Deploy-Lease", job.LeaseToken);
        using var wrongScope = await client.SendAsync(wrongScopeRequest);
        Assert.Equal(HttpStatusCode.Forbidden, wrongScope.StatusCode);

        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", deployCredential.Secret);
        using var validRequest = new HttpRequestMessage(HttpMethod.Get, url);
        validRequest.Headers.Add("X-Deploy-Lease", job.LeaseToken);
        using var valid = await client.SendAsync(validRequest);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using var provenance = JsonDocument.Parse(await valid.Content.ReadAsStringAsync());
        Assert.Equal(new string('a', 64), provenance.RootElement.GetProperty("digest").GetString());
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task PromotionRoute_DecidesOnlyThePendingRegistryDigest(string action)
    {
        Build();
        var artifact = Artifact('a');
        var commit = Head();
        WriteReleaseEvidence(artifact, commit);
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
        using var bootstrap = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            username = "local.owner", password = $"test-{Guid.NewGuid():N}!", displayName = "Local Owner",
        });
        bootstrap.EnsureSuccessStatusCode();
        var auth = await bootstrap.Content.ReadFromJsonAsync<AuthStatusResponse>();
        client.DefaultRequestHeaders.Add("X-CSRF-Token", auth!.CsrfToken);

        var hosts = factory.Services.GetRequiredService<ProjectEnvironmentService>();
        var deploy = factory.Services.GetRequiredService<DeployService>();
        Assert.True(hosts.RegisterGateArtifact("demo", artifact, commit));
        using var release = await client.PostAsJsonAsync("/api/projects/demo/environments/releases",
            new ReleaseArtifactRequest(artifact, commit));
        Assert.Equal(HttpStatusCode.Accepted, release.StatusCode);
        var stage = hosts.Claim("stage-host")!;
        Assert.True(hosts.Report("demo", stage.Id, new(stage.LeaseToken!, true, "health-ok")));
        deploy.SyncAfterHostCall("demo");
        Assert.Equal("pending", Assert.Single(hosts.Read("demo")!.Promotions).Status);

        var route = $"/api/projects/demo/environments/promotions/{action}";
        using var wrong = await client.PostAsJsonAsync(route, new PromotionDecisionRequest(Artifact('b')));
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal("pending", Assert.Single(hosts.Read("demo")!.Promotions).Status);

        using var decision = await client.PostAsJsonAsync(route, new PromotionDecisionRequest(artifact));
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        Assert.Equal(action == "approve" ? "production-queued" : "rejected",
            Assert.Single(hosts.Read("demo")!.Promotions).Status);
        var production = hosts.Claim("prod-host");
        if (action == "approve") Assert.Equal(artifact, production!.Artifact);
        else Assert.Null(production);
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

    private void Report(DeployService deploy, ProjectEnvironmentService environments, string host, bool healthy,
        string? evidence = null)
    {
        var job = environments.Claim(host)!;
        deploy.SyncAfterHostCall("demo");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var steps = job.SshSteps.Select((step, index) =>
            new DeployStepResult($"deploy[{index}]:{step.Step}", healthy))
            .Take(healthy ? job.SshSteps.Count : 1).ToArray();
        Assert.True(environments.Report("demo", job.Id,
            new(job.LeaseToken!, healthy, evidence ?? (healthy ? "health-ok" : "deploy-step-failed"), steps)));
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

    private void StripPersistedReleaseIds()
    {
        var path = Path.Combine(_root, "workspace", "deployments", "demo.json");
        var state = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var job in state["jobs"]!.AsArray()) job!.AsObject().Remove("releaseId");
        File.WriteAllText(path, state.ToJsonString());
    }

    private ProjectEnvironmentService RestartEnvironments()
        => new(_configuration!, _scanner!, _git!, _settings!, _clock,
            new ReleaseArtifactRegistry(_configuration!), new DeployStore(_configuration!));

    private void Card(string key, string sha, IReadOnlyList<string>? deliveryShas = null, string? title = null)
    {
        var folder = Path.Combine(_watch, "6-completed", key.ToLowerInvariant());
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = key.ToLowerInvariant(), key, title = title ?? "Card " + key, state = "6-completed", agent = "claude",
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
            status = "promoted", gate = "passed", gateRunId = "release/test-gate", candidateSha = commit, atomicPush = true,
            releasePackage = new { status = "written", sha256 = digest },
        }));
        File.WriteAllText(Path.Combine(directory, "deploy-artifact.json"), JsonSerializer.Serialize(new
        {
            project = "demo", commit, artifact, sourceSha256 = digest,
        }));
    }

    private (DeployService Deploy, ProjectEnvironmentService Environments, DeployStore Store) Build(
        bool guidedStage = false, bool guidedProduction = false)
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
            guidedFailure = guidedStage,
        }));
        WriteProduction(host: "prod-host", destination: "8082", guidedFailure: guidedProduction);
        Commit("initial");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["Deploy:ReleaseEvidenceRoot"] = Path.Combine(_root, "release-evidence"),
            ["WatchPaths:0:Name"] = "demo", ["WatchPaths:0:Path"] = _watch,
            ["WatchPaths:0:RootPath"] = _repo, ["WatchPaths:0:RepositoryPath"] = _repo,
        }).Build();
        _configuration = config;
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        (_configuration, _scanner, _git, _settings) = (config, scanner, git, settings);
        var environments = new ProjectEnvironmentService(config, scanner, git, settings, _clock);
        var store = new DeployStore(config);
        _environments = environments;
        return (new DeployService(environments, store, git, scanner, _clock, NullLogger<DeployService>.Instance, null),
            environments, store);
    }

    private RunbookService Runbooks(ProjectEnvironmentService environments, DeployStore store)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["WatchPaths:0:Name"] = "demo", ["WatchPaths:0:Path"] = _watch,
            ["WatchPaths:0:RootPath"] = _repo, ["WatchPaths:0:RepositoryPath"] = _repo,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        return new RunbookService(environments, store, git, settings, _clock,
            NullLogger<RunbookService>.Instance);
    }

    private DeployService Reopen()
    {
        var config = _configuration!;
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var environments = new ProjectEnvironmentService(config, scanner, git, settings, _clock);
        return new DeployService(environments, new DeployStore(config), git, scanner, _clock,
            NullLogger<DeployService>.Instance, null);
    }

    private void WriteProduction(string host, string destination, bool guidedFailure = false)
        => File.WriteAllText(Path.Combine(_repo, "docs", "deployments", "environments", "production.json"),
            JsonSerializer.Serialize(new
            {
                id = "production", name = "Production", kind = "production", targetHostId = host,
                address = "https://example", template = "container", destination,
                healthUrl = "https://example/health", approval = "person", secretRefs = new[] { "DATABASE_URL" },
                guidedFailure, interventionTimeoutMinutes = 23,
            }));

    private string Head() => Git("rev-parse", "HEAD").Trim();

    private void WriteProjectYml(string sections)
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".agent-studio"));
        File.WriteAllText(Path.Combine(_repo, ".agent-studio", "project.yml"), """
            schemaVersion: 2
            stack: [node]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build: [npm run build]
              test: [npm test]
              lint: []
            testSuites:
            cachePaths: []
            capabilities: [git]
            environment:
            project:
              id: demo
            artifacts:
              - name: app
                type: container
                dockerfile: Dockerfile
            deploy:
              - step: container
                artifact: app
                target: stage-host
                composeFile: /srv/apps/demo/stage/compose.yml
                service: demo
                healthUrl: https://stage.example/health
                healthTimeoutSeconds: 60
                environments: [staging]
              - step: container
                artifact: app
                target: prod-host
                composeFile: /srv/apps/demo/production/compose.yml
                service: demo
                healthUrl: https://example/health
                healthTimeoutSeconds: 60
                environments: [production]
            variables:
              - name: DATABASE_URL
                secret: DATABASE_URL
                environments: [production]
            """ + "\n" + sections);
    }

    private void WriteSshProjectYml() => WriteProjectYml(string.Empty);

    // Git for Windows writes loose objects read-only, so File.Delete alone throws there.
    private void DeleteLooseObject(string sha)
    {
        var path = Path.Combine(_repo, ".git", "objects", sha[..2], sha[2..]);
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

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
        if (arguments.Length > 0 && arguments[0] == "commit")
        {
            var when = _clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:sszzz",
                System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["GIT_AUTHOR_DATE"] = when;
            start.Environment["GIT_COMMITTER_DATE"] = when;
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
