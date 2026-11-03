using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>How a runner credential may read one card (SM-9).</summary>
public enum RunnerCardAccess
{
    /// <summary>No such card in the addressed project.</summary>
    Missing,
    /// <summary>The runner holds a live coding lease or review attempt on the card.</summary>
    Leased,
    /// <summary>The runner ran an earlier attempt; only lifecycle facts may be read.</summary>
    Former,
    None,
}

/// <summary>
/// SM-9 runner credential binding: the host, role and projects a runner
/// credential is bound to, and the claim lookups route authorization needs.
/// </summary>
public sealed partial class TaskServerStore
{
    internal const string RunnerBindingMigrationKey = "sm9_runner_binding_migration";
    public const string HostProjectPolicyMissingReason = "host-project-policy-missing";

    private static string HostProjectPolicyMissingMessage(string hostId)
        => $"Host '{hostId}' has no project policy, so it holds no project. An Owner sets one under Hosts.";

    /// <summary>
    /// In an authenticated installation every runner is a credential, and a host
    /// without an explicit project policy holds no project. A local, unauthenticated
    /// store keeps the compatibility routing in which a missing policy admits all.
    /// </summary>
    public bool RequiresHostProjectPolicy => _options.RequireHostProjectPolicy == true;

    private DateTime? RunnerCredentialExpiry(string kind, DateTime issuedAt)
        => kind == TaskServerPrincipalKinds.Runner && _options.RunnerCredentialLifetimeDays > 0
            ? issuedAt.AddDays(_options.RunnerCredentialLifetimeDays)
            : null;

    private async Task ApplyRunnerBindingMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await EnsureColumnAsync(connection, "principals", "role", "TEXT", ct);
        await EnsureColumnAsync(connection, "principals", "bound_host_id", "TEXT", ct);
        await EnsureColumnAsync(connection, "service_enrolments", "role", "TEXT", ct);
        if (await ReadMetaAsync(connection, null, RunnerBindingMigrationKey, ct) is not null) return;

        var now = Iso(UtcNow);
        // Hosts that already ran work keep it, but as an explicit, visible policy an
        // Owner can narrow. Every host registered from now on starts with none.
        var migratedHosts = new List<string>();
        await using (var command = Command(connection, """
            SELECT DISTINCT r.host_id FROM runners r
             WHERE NOT EXISTS (SELECT 1 FROM host_project_policies p WHERE p.host_id = r.host_id)
             ORDER BY r.host_id;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) migratedHosts.Add(reader.GetString(0));
        foreach (var hostId in migratedHosts)
        {
            await ExecuteAsync(connection, """
                INSERT INTO host_project_policies(host_id, allow_all_projects, version, updated_at)
                VALUES ($host, 1, 1, $now) ON CONFLICT(host_id) DO NOTHING;
                INSERT INTO audit(occurred_at, actor_id, action, target_type, target_id, detail_json)
                VALUES ($now, 'migration:sm9', 'host-project-policy.migrated', 'host', $host, $detail);
                """, ct,
                ("$host", hostId), ("$now", now),
                ("$detail", JsonSerializer.Serialize(new
                {
                    allowAllProjects = true,
                    reason = "A host registered before explicit project binding keeps all projects until an Owner narrows its policy.",
                })));
        }

        // Existing runner credentials keep their lifetime: a notice, not a silent change.
        var unexpiring = new List<string>();
        await using (var command = Command(connection, """
            SELECT c.credential_id FROM principal_credentials c
              JOIN principals p ON p.principal_id = c.principal_id
             WHERE p.kind = 'runner' AND p.revoked_at IS NULL
               AND c.revoked_at IS NULL AND c.expires_at IS NULL
             ORDER BY c.credential_id;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) unexpiring.Add(reader.GetString(0));
        if (unexpiring.Count > 0)
            await ExecuteAsync(connection, """
                INSERT INTO audit(occurred_at, actor_id, action, target_type, target_id, detail_json)
                VALUES ($now, 'migration:sm9', 'principal.expiry-migration-notice', 'installation', 'runner-credentials', $detail);
                """, ct, ("$now", now),
                ("$detail", JsonSerializer.Serialize(new
                {
                    credentialIds = unexpiring,
                    defaultLifetimeDays = _options.RunnerCredentialLifetimeDays,
                    notice = "These runner credentials have no expiry. They keep working; rotate each one to adopt the default lifetime.",
                })));
        await SetMetaAsync(connection, null, RunnerBindingMigrationKey, now, ct);
    }

    /// <summary>The host, role and project set of one authenticated runner credential.</summary>
    public async Task<RunnerCredentialBinding> ResolveRunnerBindingAsync(
        TaskServerPrincipal principal, CancellationToken ct)
    {
        if (principal.RunnerId is not { } runnerId)
            return new RunnerCredentialBinding(principal.PrincipalId, null, null, null, RunnerProjectScope.None);
        await using var connection = await OpenReadyAsync(ct);
        string? recordedRole = null, boundHost = null, enrolledHost = null, enrolledRole = null, registeredHost = null;
        await using (var command = Command(connection, """
            SELECT role, bound_host_id FROM principals WHERE principal_id = $principal;
            """, ("$principal", principal.PrincipalId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct))
            {
                recordedRole = reader.IsDBNull(0) ? null : reader.GetString(0);
                boundHost = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        await using (var command = Command(connection, """
            SELECT host_id, role FROM host_enrolment_principals WHERE principal_id = $runner;
            """, ("$runner", runnerId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct))
            {
                enrolledHost = reader.GetString(0);
                enrolledRole = reader.GetString(1);
            }
        if (enrolledHost is null && boundHost is null)
            registeredHost = Convert.ToString(await ScalarAsync(connection,
                "SELECT host_id FROM runners WHERE id = $runner;", ct, ("$runner", runnerId)),
                CultureInfo.InvariantCulture);
        var hostId = enrolledHost ?? boundHost ?? (string.IsNullOrEmpty(registeredHost) ? null : registeredHost);
        var projects = hostId is null
            ? RunnerProjectScope.None
            : RunnerCredentialBindingPolicy.Projects(await ReadHostProjectPolicyAsync(connection, null, hostId, ct));
        return new RunnerCredentialBinding(principal.PrincipalId, runnerId, hostId,
            RunnerCredentialBindingPolicy.Role(enrolledRole, recordedRole, principal.Scopes), projects);
    }

    /// <summary>
    /// Pins the host of a runner credential on its first registration and refuses
    /// a registration from any other host afterwards (a copied credential).
    /// </summary>
    public async Task<RunnerBindingRefusal?> BindRunnerHostAsync(
        TaskServerPrincipal principal, string hostId, CancellationToken ct)
    {
        if (principal.Kind != TaskServerPrincipalKinds.Runner) return null;
        if (principal.RunnerId is null)
            return new RunnerBindingRefusal(StatusCodes.Status403Forbidden, "runner-credential-unbound",
                "This runner credential is not bound to a runner identity. Enrol one credential per host.");
        RunnerBindingRefusal? refusal = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var boundHost = Convert.ToString(await ScalarAsync(connection,
                "SELECT bound_host_id FROM principals WHERE principal_id = $principal;", ct, transaction,
                ("$principal", principal.PrincipalId)), CultureInfo.InvariantCulture);
            var enrolledHost = Convert.ToString(await ScalarAsync(connection,
                "SELECT host_id FROM host_enrolment_principals WHERE principal_id = $runner;", ct, transaction,
                ("$runner", principal.RunnerId)), CultureInfo.InvariantCulture);
            var decision = RunnerCredentialBindingPolicy.HostRegistration(
                string.IsNullOrEmpty(enrolledHost) ? null : enrolledHost,
                string.IsNullOrEmpty(boundHost) ? null : boundHost,
                hostId);
            if (decision == RunnerHostRegistration.Refuse)
            {
                refusal = new RunnerBindingRefusal(StatusCodes.Status403Forbidden, "runner-host-mismatch",
                    $"This runner credential is bound to host '{(string.IsNullOrEmpty(enrolledHost) ? boundHost : enrolledHost)}', not '{hostId}'.");
                await AuditAsync(connection, transaction, principal.PrincipalId, "principal.host-refused", "principal",
                    principal.PrincipalId, JsonSerializer.Serialize(new { requestedHostId = hostId }), ct);
                return;
            }
            if (decision == RunnerHostRegistration.Bind)
            {
                await ExecuteAsync(connection,
                    "UPDATE principals SET bound_host_id = $host WHERE principal_id = $principal;",
                    ct, transaction, ("$host", hostId), ("$principal", principal.PrincipalId));
                await AuditAsync(connection, transaction, principal.PrincipalId, "principal.host-bound", "principal",
                    principal.PrincipalId, JsonSerializer.Serialize(new { hostId }), ct);
            }
        }, ct);
        return refusal;
    }

    public async Task<(RunnerCardAccess Access, string? ProjectId)> RunnerCardAccessAsync(
        string runnerId, string projectId, string taskIdentity, CancellationToken ct)
    {
        var task = await GetTaskAsync(projectId, taskIdentity, ct);
        return task is null
            ? (RunnerCardAccess.Missing, null)
            : (await RunnerTaskAccessAsync(runnerId, task.TaskId, ct), task.ProjectId);
    }

    public async Task<(RunnerCardAccess Access, string? ProjectId)> RunnerCardKeyAccessAsync(
        string runnerId, string taskKey, CancellationToken ct)
    {
        string? taskId = null, projectId = null;
        await using (var connection = await OpenReadyAsync(ct))
        await using (var command = Command(connection,
                         "SELECT id, project_id FROM tasks WHERE task_key = upper($key);", ("$key", taskKey)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct))
            {
                taskId = reader.GetString(0);
                projectId = reader.GetString(1);
            }
        return taskId is null
            ? (RunnerCardAccess.Missing, null)
            : (await RunnerTaskAccessAsync(runnerId, taskId, ct), projectId);
    }

    private async Task<RunnerCardAccess> RunnerTaskAccessAsync(string runnerId, string taskId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        if (await ScalarAsync(connection, """
                SELECT 1 FROM leases
                 WHERE task_id = $task AND runner_id = $runner AND status IN ('active', 'process-unknown')
                UNION ALL
                SELECT 1 FROM review_attempts
                 WHERE task_id = $task AND executor_id = $runner AND status IN ('leased', 'process-unknown')
                LIMIT 1;
                """, ct, ("$task", taskId), ("$runner", runnerId)) is not null)
            return RunnerCardAccess.Leased;
        return await ScalarAsync(connection, """
                SELECT 1 FROM runs WHERE task_id = $task AND runner_id = $runner
                UNION ALL
                SELECT 1 FROM review_attempts WHERE task_id = $task AND executor_id = $runner
                LIMIT 1;
                """, ct, ("$task", taskId), ("$runner", runnerId)) is not null
            ? RunnerCardAccess.Former
            : RunnerCardAccess.None;
    }

    /// <summary>
    /// The runner claimed this run, or reviews it now: a live review attempt of
    /// this runner names the run as its source. Reports need the claim itself.
    /// </summary>
    public async Task<bool> RunnerOwnsRunAsync(string runnerId, string runId, bool includeReviewedSource, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        if (await ScalarAsync(connection,
                "SELECT 1 FROM runs WHERE id = $run AND runner_id = $runner;", ct,
                ("$run", runId), ("$runner", runnerId)) is not null)
            return true;
        return includeReviewedSource && await ScalarAsync(connection, """
            SELECT 1 FROM review_attempts a JOIN review_subjects s ON s.id = a.subject_id
             WHERE s.source_run_id = $run AND a.executor_id = $runner
               AND a.status IN ('leased', 'process-unknown')
             LIMIT 1;
            """, ct, ("$run", runId), ("$runner", runnerId)) is not null;
    }

    public async Task<bool> RunnerOwnsReviewAttemptAsync(string runnerId, string attemptId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ScalarAsync(connection,
            "SELECT 1 FROM review_attempts WHERE id = $attempt AND executor_id = $runner;", ct,
            ("$attempt", attemptId), ("$runner", runnerId)) is not null;
    }

    public async Task<bool> RunnerOwnsReviewSubjectAsync(string runnerId, string subjectId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ScalarAsync(connection,
            "SELECT 1 FROM review_attempts WHERE subject_id = $subject AND executor_id = $runner LIMIT 1;", ct,
            ("$subject", subjectId), ("$runner", runnerId)) is not null;
    }

    public async Task<bool> RunnerOwnsGateSubjectAsync(string runnerId, string subjectId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ScalarAsync(connection,
            "SELECT 1 FROM gate_attempts WHERE subject_id = $subject AND executor_id = $runner LIMIT 1;", ct,
            ("$subject", subjectId), ("$runner", runnerId)) is not null;
    }

    /// <summary>The runner reviews this subject, or gates a card the subject belongs to.</summary>
    public async Task<bool> RunnerOwnsReviewSourceAsync(string runnerId, string reviewSubjectId, CancellationToken ct)
    {
        if (await RunnerOwnsReviewSubjectAsync(runnerId, reviewSubjectId, ct)) return true;
        await using var connection = await OpenReadyAsync(ct);
        return await ScalarAsync(connection, """
            SELECT 1 FROM review_subjects r
              JOIN gate_subjects g ON g.task_id = r.task_id
              JOIN gate_attempts a ON a.subject_id = g.id
             WHERE r.id = $subject AND a.executor_id = $runner
             LIMIT 1;
            """, ct, ("$subject", reviewSubjectId), ("$runner", runnerId)) is not null;
    }

    public async Task<string?> RepositoryProjectAsync(string repositoryId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ScalarAsync(connection, """
            SELECT project_id FROM project_repositories WHERE repository_id = $repository OR project_id = $repository
             LIMIT 1;
            """, ct, ("$repository", repositoryId)) as string;
    }

    /// <summary>The registered repositories of the bound projects, as the repository catalog lists them.</summary>
    public async Task<IReadOnlySet<string>> RunnerRepositoryUrlsAsync(RunnerProjectScope scope, CancellationToken ct)
    {
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in await ListProjectsAsync(null, ct))
            if (scope.Allows(project.ProjectId)
                && await GetProjectRepositoryAsync(project.ProjectId, ct) is { RepositoryUrl: { } url })
                urls.Add(url);
        return urls;
    }
}
