using AgentStudio.Operations.Contracts;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class OperationPermitTests
{
    [Fact]
    public async Task Opaque_permit_binds_every_command_field_and_live_task_fence()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(new("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(new(workspace.WorkspaceId, "Project", "TS"), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId, new("Task", "Inspect host", "2-ready"), "test", default);
        await store.RegisterRunnerAsync("runner-a", new("runner", "host-a", "boot-a", "1.0.0", TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]), "test", default);
        var claim = await store.ClaimAsync(new("runner-a", "boot-a"), "test", default);
        Assert.Equal("claimed", claim.Status);
        var lease = claim.Lease!;
        var subject = new OperationSubject(task.TaskId, claim.Run!.RunId, lease.Fence);
        var grant = new IssueOperationPermitRequest("edge", "operator", subject, "host.inspect", 1,
            OperationsProtocol.Digest("{}"), "host-a", OperationsProtocol.Audience, "correlation", clock.GetUtcNow().AddSeconds(20));
        var issued = await store.IssueOperationPermitAsync(grant, "studio", default);
        var request = new PermitIntrospectionRequest(issued.Permit, grant.PrincipalId, grant.Actor, subject,
            grant.OperationId, grant.Version, grant.InputDigest, grant.AgentId, grant.ResultAudience, grant.CorrelationId, issued.ValidUntil);
        Assert.True((await store.IntrospectOperationPermitAsync(request, default)).Active);
        PermitIntrospectionRequest[] altered =
        [
            request with { Permit = "forged" }, request with { PrincipalId = "another-edge" },
            request with { Actor = "another-actor" }, request with { CorrelationId = "other" }, request with { AgentId = "host-b" },
            request with { OperationId = "shell.exec" }, request with { Version = 2 },
            request with { InputDigest = OperationsProtocol.Digest("different") },
            request with { Subject = subject with { Fence = lease.Fence + 1 } },
            request with { Subject = subject with { TaskId = "different" } },
            request with { ResultAudience = "task-server" }, request with { Deadline = issued.ValidUntil.AddSeconds(1) },
        ];
        foreach (var invalid in altered) Assert.False((await store.IntrospectOperationPermitAsync(invalid, default)).Active);
        var restarted = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await restarted.InitializeForBackupAsync();
        Assert.True((await restarted.IntrospectOperationPermitAsync(request, default)).Active);
        await store.ReleaseLeaseAsync(claim.Run.RunId, new("runner-a", "boot-a", lease.LeaseId, lease.Fence, "runner-process-missing"), "test", default);
        Assert.False((await store.IntrospectOperationPermitAsync(request, default)).Active);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.IssueOperationPermitAsync(grant, "studio", default));
    }

    [Fact]
    public async Task Operations_principal_can_only_introspect_and_cannot_mint_task_authority()
    {
        using var temp = new TempDirectory();
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), TimeProvider.System);
        await store.InitializeAsync();
        var issued = await store.CreatePrincipalAsync(new("operations", TaskServerPrincipalKinds.Operations), "owner", default);
        var authenticated = await store.AuthenticatePrincipalAsync(issued.Credential, default);
        Assert.Equal(new[] { TaskServerScopes.OperationsInspect }, authenticated!.Scopes);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreatePrincipalAsync(
            new("compromised", TaskServerPrincipalKinds.Operations, [TaskServerScopes.TasksWrite]), "owner", default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreatePrincipalAsync(
            new("compromised-agent", TaskServerPrincipalKinds.Runner, [TaskServerScopes.OperationsIssue], "runner-a"), "owner", default));
    }

    [Fact]
    public async Task Schema_19_upgrade_preserves_existing_credentials_and_allows_operations_kind()
    {
        using var temp = new TempDirectory();
        var options = Options.Create(new TaskServerOptions { DataDirectory = temp.Path });
        var store = new TaskServerStore(options, TimeProvider.System);
        await store.InitializeAsync();
        var original = await store.CreatePrincipalAsync(new("studio-old", TaskServerPrincipalKinds.Studio), "owner", default);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys = OFF;
                CREATE TABLE principals_v19(
                    principal_id TEXT PRIMARY KEY,
                    kind TEXT NOT NULL CHECK(kind IN ('studio', 'engine', 'runner')),
                    scopes_json TEXT NOT NULL, runner_id TEXT UNIQUE, created_at TEXT NOT NULL,
                    revoked_at TEXT, last_seen_at TEXT, CHECK(kind = 'runner' OR runner_id IS NULL)
                );
                INSERT INTO principals_v19 SELECT * FROM principals;
                DROP TABLE principals;
                ALTER TABLE principals_v19 RENAME TO principals;
                UPDATE meta SET value = '19' WHERE key = 'schema_version';
                DROP TABLE operation_permits;
                PRAGMA foreign_keys = ON;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var upgraded = new TaskServerStore(options, TimeProvider.System);
        await upgraded.InitializeAsync();
        Assert.NotNull(await upgraded.AuthenticatePrincipalAsync(original.Credential, default));
        var operations = await upgraded.CreatePrincipalAsync(new("operations-new", TaskServerPrincipalKinds.Operations), "owner", default);
        Assert.NotNull(await upgraded.AuthenticatePrincipalAsync(operations.Credential, default));
        await upgraded.RevokePrincipalAsync("studio-old", "owner", default);
        Assert.Null(await upgraded.AuthenticatePrincipalAsync(original.Credential, default));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("released")]
    [InlineData("fence")]
    [InlineData("task")]
    [InlineData("finished")]
    [InlineData("missing")]
    public void Authority_policy_rejects_each_stale_subject(string condition)
    {
        var now = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
        var subject = new OperationSubject("task", "run", 3);
        LeaseDto? lease = new("lease", "run", "task", "runner", "boot", 3, now.UtcDateTime, now.AddMinutes(1).UtcDateTime, "active");
        var run = new RunDto("run", "task", "running", "runner", 3, now.UtcDateTime, now.UtcDateTime, null);
        Assert.True(OperationPermitPolicy.Active(subject, lease, run, now));
        switch (condition)
        {
            case "expired": lease = lease with { ExpiresAt = now.UtcDateTime }; break;
            case "released": lease = lease with { Status = "released" }; break;
            case "fence": subject = subject with { Fence = 2 }; break;
            case "task": subject = subject with { TaskId = "other" }; break;
            case "finished": run = run with { Status = "completed" }; break;
            case "missing": lease = null; break;
        }
        Assert.False(OperationPermitPolicy.Active(subject, lease, run, now));
    }
}
