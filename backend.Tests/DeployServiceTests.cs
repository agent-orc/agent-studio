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

    [Fact]
    public void Runbook_executes_on_environment_without_creating_or_changing_a_release()
    {
        var (deploy, environments, store) = Build();
        WriteProjectYml("runbooks:\n  - name: restart\n    steps: [restart-container, health]\nvariables:\n  - name: LOG_LEVEL\n    value: info\n");
        Commit("runbook-definition");
        var runbooks = Runbooks(environments, store);

        var requested = runbooks.Request("demo", "stage", "restart", Operator);
        Assert.Equal(DeployOutcomeKind.Ok, requested.Kind);
        Assert.Equal(DeploymentStates.Queued, requested.Value!.State);
        var job = environments.Claim("stage-host")!;
        Assert.Equal("restart", job.RunbookName);
        Assert.Equal(["restart-container", "health"], job.RunbookSteps);
        Assert.Equal("8081", job.Destination);
        Assert.Equal("info", job.RunbookVariables["LOG_LEVEL"]);
        runbooks.SyncAfterHostCall("demo");
        Assert.Equal(DeploymentStates.Running, runbooks.Run("demo", requested.Value.Id)!.State);
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(environments.Claim("stage-host"));
        Assert.True(environments.Report("demo", job.Id, new(job.LeaseToken!, true, "health-ok",
            [new(0, true), new(1, true)])));
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
            [new(0, false)])));
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
            new DeployReportRequest(claimed.LeaseToken!, true, "health-ok", [new(0, true)]));
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
    public void ReportAfterTheLease_IsAcceptedWhileTheClaimStands()
    {
        var (deploy, environments, _) = Build();
        CreateRelease(deploy, Artifact('a'), Head());
        var job = environments.Claim("stage-host")!;
        _clock.Advance(TimeSpan.FromMinutes(8));
        Assert.True(environments.Report("demo", job.Id, new(job.LeaseToken!, true, "health-ok")));
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
    public void RollbackRoute_OnTheFirstRelease_FallsBackToTheArtifactFromBeforeReleases()
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
        var job = environments.Claim("new-host")!;
        Assert.Equal((Artifact('a'), true, "8082"), (job.Artifact, job.IsRollback, job.Destination));
        Assert.True(environments.Report("demo", job.Id, new(job.LeaseToken!, true, "health-ok")));
        deploy.SyncAfterHostCall("demo");
        Assert.Equal(EnvironmentHealth.Healthy, deploy.Status("demo")![1].Health);
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
        WriteProjectYml("variables:\n  - name: LOG_LEVEL\n    value: info\n");
        var r2 = CreateRelease(deploy, Artifact('b'), Commit("variables"));
        Report(deploy, environments, "stage-host", healthy: true);
        var r3 = CreateRelease(deploy, Artifact('c'), Commit("unrelated"));
        Report(deploy, environments, "stage-host", healthy: true);
        WriteProjectYml("variables:\n  - name: LOG_LEVEL\n    value: debug\n");
        var r4 = CreateRelease(deploy, Artifact('d'), Commit("debug"));

        Assert.NotEqual(r1.VariablesVersion, r2.VariablesVersion);
        Assert.Equal(r2.VariablesVersion, r3.VariablesVersion);
        Assert.NotEqual(r2.VariablesVersion, r4.VariablesVersion);
    }

    [Fact]
    public async Task Routes_ServeTheSharedContract()
    {
        Build();
        var older = Head();
        var commit = Commit("second");
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

        // Production approval needs a signed-in Owner or Maintainer, also on
        // the local profile: a caller without a session, such as a coding
        // agent, is refused on the deploy and the AGT-3176 routes.
        var hosts = factory.Services.GetRequiredService<ProjectEnvironmentService>();
        var service = factory.Services.GetRequiredService<DeployService>();
        var stage = hosts.Claim("stage-host")!;
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
        using var approved = await client.PostAsync($"/api/projects/demo/deploy/deployments/{production.Id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(auth.User!.Id, service.DeploymentById("demo", production.Id)!.ApprovedBy);
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
        WriteProduction(host: "prod-host", destination: "8082");
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

    private void WriteProduction(string host, string destination)
        => File.WriteAllText(Path.Combine(_repo, "docs", "deployments", "environments", "production.json"),
            JsonSerializer.Serialize(new
            {
                id = "production", name = "Production", kind = "production", targetHostId = host,
                address = "https://example", template = "container", destination,
                healthUrl = "https://example/health", approval = "person", secretRefs = new[] { "DATABASE_URL" },
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

            """ + sections);
    }

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
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
