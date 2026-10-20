using AgentStudio.Secrets;
using AgentStudio.Security;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Direct matrix tests of the pure secret policies: versions, workload scope, human rights, lease binding.</summary>
public sealed class SecretPolicyTests
{
    private static readonly SecretActor Owner = SecretActor.Human("first.owner");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero));
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public void Put_creates_version_one_then_replaces_by_revoking_every_earlier_version()
    {
        var scope = SecretScope.ForProject("memowikis");
        var first = SecretVersionPolicy.Put(null, "id", scope, "DB", Owner, Now);
        Assert.Equal([1], first.Versions.Select(item => item.Version));
        Assert.Equal("human:first.owner", first.CreatedBy);

        _clock.Advance(TimeSpan.FromMinutes(5));
        var rotated = SecretVersionPolicy.Rotate(first, first.Reference, Owner, Now);
        _clock.Advance(TimeSpan.FromMinutes(5));
        var replaced = SecretVersionPolicy.Put(rotated, "id", scope, "DB", Owner, Now);

        Assert.Equal([1, 2, 3], replaced.Versions.Select(item => item.Version));
        Assert.Equal([false, false, true], replaced.Versions.Select(item => item.Active));
        Assert.All(replaced.Versions.Take(2), item => Assert.Equal("replaced:human:first.owner", item.RevokedBy));
        Assert.Equal(3, SecretVersionPolicy.CurrentVersion(replaced));
    }

    [Fact]
    public void Rotate_keeps_the_previous_version_resolvable_until_it_is_revoked()
    {
        var record = SecretVersionPolicy.Put(null, "id", SecretScope.Workspace, "MAIL", Owner, Now);
        _clock.Advance(TimeSpan.FromHours(1));
        record = SecretVersionPolicy.Rotate(record, record.Reference, Owner, Now);

        Assert.Equal(Now, record.RotatedAt);
        Assert.Equal(2, SecretVersionPolicy.Select(record, null).Version);
        Assert.Equal(1, SecretVersionPolicy.Select(record, 1).Version);

        record = SecretVersionPolicy.Revoke(record, 1, Owner, Now);
        Assert.Throws<SecretRevokedException>(() => SecretVersionPolicy.Select(record, 1));
        Assert.Equal(2, SecretVersionPolicy.Select(record, null).Version);

        var again = SecretVersionPolicy.Revoke(record, 1, Owner, Now);
        Assert.Same(record, again);

        record = SecretVersionPolicy.Revoke(record, null, Owner, Now);
        Assert.Null(SecretVersionPolicy.CurrentVersion(record));
        Assert.Throws<SecretRevokedException>(() => SecretVersionPolicy.Select(record, null));
        Assert.Throws<SecretNotFoundException>(() => SecretVersionPolicy.Select(record, 9));
        Assert.Throws<SecretNotFoundException>(() => SecretVersionPolicy.Revoke(record, 9, Owner, Now));
    }

    [Fact]
    public void Rotate_requires_an_existing_secret()
    {
        var reference = new SecretReference(SecretScope.Workspace, "MISSING");
        Assert.Throws<SecretNotFoundException>(() => SecretVersionPolicy.Rotate(null, reference, Owner, Now));
    }

    [Theory]
    [InlineData("secret://workspace/MAIL", "deploy-job", "memowikis", "stage", true, "allowed")]
    [InlineData("secret://memowikis/MAIL", "deploy-job", "memowikis", "stage", true, "allowed")]
    [InlineData("secret://memowikis/stage/MAIL", "deploy-job", "memowikis", "stage", true, "allowed")]
    [InlineData("secret://MemoWikis/stage/MAIL", "deploy-job", "memowikis", "stage", true, "allowed")]
    [InlineData("secret://memowikis/prod/MAIL", "deploy-job", "memowikis", "stage", false, "environment-scope-denied")]
    [InlineData("secret://other/MAIL", "deploy-job", "memowikis", "stage", false, "project-scope-denied")]
    [InlineData("secret://other/stage/MAIL", "deploy-job", "memowikis", "stage", false, "project-scope-denied")]
    [InlineData("secret://memowikis/MAIL", "task-run", "memowikis", null, true, "allowed")]
    [InlineData("secret://memowikis/stage/MAIL", "task-run", "memowikis", null, false, "environment-scope-denied")]
    public void Workload_scope_matrix(string reference, string kind, string project, string? environment, bool allowed, string code)
    {
        var decision = SecretAccessPolicy.ForWorkload(SecretReference.Parse(reference),
            new SecretWorkload(kind, "runner_1", project, environment), "deploy:stage");

        Assert.Equal(allowed, decision.Allowed);
        Assert.Equal(code, decision.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Deploy")]
    [InlineData("deploy stage")]
    public void Workload_needs_a_purpose(string? purpose)
    {
        var decision = SecretAccessPolicy.ForWorkload(SecretReference.Parse("secret://workspace/MAIL"),
            new SecretWorkload("task-run", "runner_1", "memowikis", null), purpose);
        Assert.Equal("invalid-purpose", decision.Code);
    }

    [Fact]
    public void Deploy_job_reads_only_declared_names()
    {
        var workload = new SecretWorkload("deploy-job", "host_1", "memowikis", "stage", ["DATABASE_URL"]);
        Assert.True(SecretAccessPolicy.ForWorkload(SecretReference.Parse("secret://memowikis/stage/DATABASE_URL"), workload, "deploy").Allowed);
        Assert.Equal("reference-not-declared",
            SecretAccessPolicy.ForWorkload(SecretReference.Parse("secret://memowikis/stage/STRIPE_KEY"), workload, "deploy").Code);
    }

    [Theory]
    [InlineData("owner", true, "workspace", true, true)]
    [InlineData("operator", true, "workspace", true, true)]
    [InlineData("operator", false, "workspace", true, false)]
    [InlineData("operator", false, "memowikis", true, true)]
    [InlineData("operator", false, "memowikis/prod", true, true)]
    [InlineData("operator", false, "memowikis", false, false)]
    [InlineData("viewer", true, "workspace", true, false)]
    [InlineData("viewer", true, "memowikis", true, false)]
    [InlineData("contributor", false, "memowikis", true, false)]
    [InlineData(null, true, "memowikis", true, false)]
    public void Human_write_matrix(string? role, bool unrestricted, string scope, bool member, bool allowed)
    {
        Assert.Equal(allowed, SecretManagementPolicy.CanWrite(role, unrestricted, SecretScope.Parse(scope), member).Allowed);
    }

    [Fact]
    public void Metadata_is_visible_in_the_workspace_and_member_projects()
    {
        Func<string, bool> member = project => project == "memowikis";
        Assert.True(SecretManagementPolicy.CanSeeMetadata(SecretScope.Workspace, false, member));
        Assert.True(SecretManagementPolicy.CanSeeMetadata(SecretScope.Parse("memowikis/stage"), false, member));
        Assert.False(SecretManagementPolicy.CanSeeMetadata(SecretScope.Parse("other"), false, member));
        Assert.True(SecretManagementPolicy.CanSeeMetadata(SecretScope.Parse("other"), true, member));
    }

    [Fact]
    public void Deploy_job_binding_needs_deploy_scope_matching_host_and_lease()
    {
        var host = Runner("host_1", RunnerScopes.Deploy);
        var request = new SecretWorkloadRequest("deploy-job", "memowikis", "job-1", "lease");
        var job = new DeployJobLease("job-1", "memowikis", "stage", "host_1", ["DATABASE_URL"]);

        var (workload, decision) = SecretWorkloadBinding.Decide(host, request, job, null);
        Assert.True(decision.Allowed);
        Assert.Equal(new[] { "DATABASE_URL" }, workload!.DeclaredNames);
        Assert.Equal(("memowikis", "stage"), (workload.Project, workload.Environment));

        Assert.Equal("runner-scope-denied", SecretWorkloadBinding.Decide(Runner("host_1", RunnerScopes.Lease), request, job, null).Decision.Code);
        Assert.Equal("workload-lease-invalid", SecretWorkloadBinding.Decide(host, request, null, null).Decision.Code);
        Assert.Equal("workload-identity-denied", SecretWorkloadBinding.Decide(Runner("host_2", RunnerScopes.Deploy), request, job, null).Decision.Code);
    }

    [Fact]
    public void Task_run_binding_needs_lease_scope_and_the_live_lease_of_this_runner()
    {
        var runner = Runner("runner_1", RunnerScopes.Lease);
        var request = new SecretWorkloadRequest("task-run", TaskKey: "AGT-1", LeaseId: "lease-1");
        var run = new TaskRunLease("AGT-1", "memowikis", "runner_1", "lease-1");

        var (workload, decision) = SecretWorkloadBinding.Decide(runner, request, null, run);
        Assert.True(decision.Allowed);
        Assert.Null(workload!.Environment);

        Assert.Equal("runner-scope-denied", SecretWorkloadBinding.Decide(Runner("runner_1", RunnerScopes.Logs), request, null, run).Decision.Code);
        Assert.Equal("workload-lease-invalid", SecretWorkloadBinding.Decide(runner, request with { LeaseId = "stale" }, null, run).Decision.Code);
        Assert.Equal("workload-identity-denied", SecretWorkloadBinding.Decide(Runner("runner_2", RunnerScopes.Lease), request, null, run).Decision.Code);
        Assert.Equal("workload-invalid", SecretWorkloadBinding.Bind(runner, new SecretWorkloadRequest("human"), new NoLeases()).Decision.Code);
        Assert.Equal("workload-invalid", SecretWorkloadBinding.Bind(runner, null, new NoLeases()).Decision.Code);
    }

    private static RunnerPrincipal Runner(string id, params string[] scopes)
        => new(id, id, "cred_" + id, new HashSet<string>(scopes, StringComparer.Ordinal));

    private sealed class NoLeases : ISecretWorkloadLeases
    {
        public DeployJobLease? FindClaimedDeployJob(string project, string jobId, string leaseToken) => null;
        public TaskRunLease? FindLiveTaskRun(string taskKey) => null;
    }
}
