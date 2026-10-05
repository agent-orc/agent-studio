using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// I05 human identity and project bootstrap (docs/operations/deployment-story/index.html,
/// D5 option A): installation identity, installer-armed owner bootstrap,
/// owner recovery custody, ordinary operator accounts, one-time enrolment of
/// separately revocable service principals, and API registration of each
/// project's single canonical repository. Decisions live in
/// <c>IdentityBootstrapPolicy.cs</c>; this file reads state and performs the
/// bounded writes. One-time secrets are stored only as SHA-256 hashes.
/// </summary>
public sealed partial class TaskServerStore
{
    private const string InstallationIdKey = "installation_id";
    private const string InstallationCreatedAtKey = "installation_created_at";
    private const string OwnerBootstrapCodeHashKey = "owner_bootstrap_code_hash";
    public const int DefaultEnrolmentTimeToLiveSeconds = 900;
    public const int MaximumEnrolmentTimeToLiveSeconds = 86_400;

    private async Task ApplyIdentityBootstrapMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS studio_recovery_codes(
                user_id TEXT PRIMARY KEY REFERENCES studio_users(id),
                code_hash TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS service_enrolments(
                enrolment_id TEXT PRIMARY KEY,
                code_hash TEXT NOT NULL UNIQUE,
                principal_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                runner_id TEXT,
                created_by TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                consumed_at TEXT,
                revoked_at TEXT
            );
            CREATE TABLE IF NOT EXISTS project_repositories(
                project_id TEXT PRIMARY KEY REFERENCES projects(id),
                repository_id TEXT NOT NULL UNIQUE,
                repository_url TEXT NOT NULL UNIQUE,
                integration_ref TEXT NOT NULL,
                release_ref TEXT,
                delivery_policy TEXT NOT NULL,
                registered_at TEXT NOT NULL,
                registered_by TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS project_repository_probes(
                project_id TEXT NOT NULL REFERENCES projects(id),
                runner_id TEXT NOT NULL,
                admitted INTEGER NOT NULL,
                verdict TEXT NOT NULL,
                detail TEXT,
                observed_at TEXT NOT NULL,
                PRIMARY KEY(project_id, runner_id)
            );
            """, ct);
        if (await ReadMetaAsync(connection, null, InstallationIdKey, ct) is null)
        {
            await SetMetaAsync(connection, null, InstallationIdKey,
                $"ins_{RandomNumberGenerator.GetHexString(32).ToLowerInvariant()}", ct);
            await SetMetaAsync(connection, null, InstallationCreatedAtKey, Iso(UtcNow), ct);
        }
    }

    public async Task<InstallationIdentityDto> GetInstallationIdentityAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var id = await ReadMetaAsync(connection, null, InstallationIdKey, ct)
                 ?? throw new InvalidOperationException("Installation identity is not initialized.");
        var created = await ReadMetaAsync(connection, null, InstallationCreatedAtKey, ct);
        var owners = Convert.ToInt64(await ScalarAsync(connection,
            "SELECT count(*) FROM studio_users WHERE role = $role;", ct, ("$role", StudioUserRoles.Owner)));
        var armed = await ReadMetaAsync(connection, null, OwnerBootstrapCodeHashKey, ct) is not null;
        return new InstallationIdentityDto(id, created is null ? DateTime.UnixEpoch : Parse(created), owners > 0, armed);
    }

    /// <summary>
    /// Adopts the installer's one-time owner code from its restricted host
    /// file. Never re-arms after an owner exists and never replaces an armed
    /// code with a different file value.
    /// </summary>
    public async Task<OwnerBootstrapPolicy.ArmDecision> ArmOwnerBootstrapCodeAsync(string? code, CancellationToken ct)
    {
        var fileHash = string.IsNullOrWhiteSpace(code) ? null : HashOneTimeSecret(RequireOneTimeSecret(code));
        var decision = OwnerBootstrapPolicy.ArmDecision.NoCode;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var owners = Convert.ToInt64(await ScalarAsync(
                connection, "SELECT count(*) FROM studio_users;", ct, transaction));
            var armed = await ReadMetaAsync(connection, transaction, OwnerBootstrapCodeHashKey, ct);
            decision = OwnerBootstrapPolicy.DecideArm(owners > 0, armed, fileHash);
            if (decision == OwnerBootstrapPolicy.ArmDecision.Arm)
            {
                await SetMetaAsync(connection, transaction, OwnerBootstrapCodeHashKey, fileHash!, ct);
                await AuditAsync(connection, transaction, "installer", "installation.owner-bootstrap-armed",
                    "installation", "owner-bootstrap", "{}", ct);
            }
        }, ct, requireReady: false);
        return decision;
    }

    public async Task<StudioRecoverResult> RecoverStudioUserAsync(StudioRecoverRequest request, CancellationToken ct)
    {
        var username = RequireStudioUsername(request.Username);
        var newPassword = RequireStudioPassword(request.NewPassword);
        StudioRecoverResult? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var user = await ReadStudioUserByUsernameAsync(connection, transaction, username, ct);
            var storedHash = user is null
                ? null
                : Convert.ToString(await ScalarAsync(connection,
                    "SELECT code_hash FROM studio_recovery_codes WHERE user_id = $id;", ct, transaction,
                    ("$id", user.Id)));
            if (user is null || user.Disabled || string.IsNullOrEmpty(storedHash)
                || string.IsNullOrWhiteSpace(request.RecoveryCode)
                || !FixedTimeEqualsHex(HashOneTimeSecret(request.RecoveryCode.Trim()), storedHash))
                throw new StudioAuthenticationException(
                    "invalid-recovery", "The username or recovery code is incorrect.");

            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                UPDATE studio_users SET password_hash = $hash, must_change_password = 0, updated_at = $now WHERE id = $id;
                UPDATE studio_sessions SET revoked_at = COALESCE(revoked_at, $now) WHERE user_id = $id;
                """, ct, transaction, ("$hash", HashStudioPassword(newPassword)), ("$now", now), ("$id", user.Id));
            var replacement = await ReplaceRecoveryCodeAsync(connection, transaction, user.Id, ct);
            await AuditAsync(connection, transaction, user.Id, "studio-user.recovered", "studio-user", user.Id, "{}", ct);
            result = new StudioRecoverResult(
                ToStudioAuthUserDto(user with { MustChangePassword = false, UpdatedAt = Parse(now) }), replacement);
        }, ct);
        return result!;
    }

    /// <summary>Explicit, password-confirmed recovery code rotation; nothing rotates it implicitly.</summary>
    public async Task<string> ReissueStudioRecoveryCodeAsync(
        string? sessionToken, StudioReissueRecoveryCodeRequest request, CancellationToken ct)
    {
        string? code = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var session = await ResolveStudioSessionAsync(connection, transaction, sessionToken, ct)
                          ?? throw new StudioAuthenticationException("authentication-required", "A studio session is required.");
            if (!VerifyStudioPassword(request.CurrentPassword ?? string.Empty, session.User.PasswordHash))
                throw new StudioAuthenticationException("invalid-credentials", "The current password is incorrect.");
            code = await ReplaceRecoveryCodeAsync(connection, transaction, session.User.Id, ct);
            await AuditAsync(connection, transaction, session.User.Id, "studio-user.recovery-reissued",
                "studio-user", session.User.Id, "{}", ct);
        }, ct);
        return code!;
    }

    /// <summary>Resolves the human session and requires the owner role.</summary>
    public async Task<StudioAuthUserDto> RequireStudioOwnerAsync(string? sessionToken, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var session = await ResolveStudioSessionAsync(connection, null, sessionToken, ct)
                      ?? throw new StudioAuthenticationException("authentication-required", "A studio session is required.");
        if (session.User.Disabled || !StudioRolePolicy.MayAdministerIdentity(session.User.Role))
            throw new StudioAuthorizationException(
                "owner-role-required", "Only an installation owner may administer identity and projects.");
        return ToStudioAuthUserDto(session.User);
    }

    public async Task<StudioAuthUserDto> CreateStudioUserAsync(
        StudioCreateUserRequest request, string ownerId, CancellationToken ct)
    {
        var username = RequireStudioUsername(request.Username);
        var password = RequireStudioPassword(request.Password);
        var role = (request.Role ?? string.Empty).Trim().ToLowerInvariant();
        if (!StudioRolePolicy.IsAssignableRole(role))
            throw new ArgumentException("Role must be operator or viewer; the owner is created only by bootstrap.");
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? username : request.DisplayName.Trim();
        var userId = StableOrGeneratedId(null, "usr");
        var now = UtcNow;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            if (await ReadStudioUserByUsernameAsync(connection, transaction, username, ct) is not null)
                throw new TaskServerConflictException("studio-user-exists", $"User '{username}' already exists.");
            await ExecuteAsync(connection, """
                INSERT INTO studio_users(
                    id, username, display_name, role, password_hash, project_ids_json,
                    disabled, must_change_password, created_at, updated_at)
                VALUES ($id, $username, $display, $role, $hash, '[]', 0, 1, $now, $now);
                """, ct, transaction,
                ("$id", userId), ("$username", username), ("$display", displayName),
                ("$role", role), ("$hash", HashStudioPassword(password)), ("$now", Iso(now)));
            await AuditAsync(connection, transaction, ownerId, "studio-user.created", "studio-user", userId,
                JsonSerializer.Serialize(new { username, role }), ct);
        }, ct);
        return new StudioAuthUserDto(userId, username, displayName, role, [], false, true);
    }

    public async Task<IReadOnlyList<StudioAuthUserDto>> ListStudioUsersAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, username, display_name, role, password_hash,
                   project_ids_json, disabled, must_change_password, created_at, updated_at
              FROM studio_users ORDER BY created_at, username;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<StudioAuthUserDto>();
        while (await reader.ReadAsync(ct))
            result.Add(ToStudioAuthUserDto(ReadStudioUser(reader, offset: 0)));
        return result;
    }

    public async Task<IssuedEnrolment> CreateEnrolmentAsync(
        CreateEnrolmentRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var principalId = RequireIdentifier(request.PrincipalId, "Principal id");
        var kind = NormalizeKind(request.Kind);
        var runnerId = kind == TaskServerPrincipalKinds.Runner
            ? RequireIdentifier(request.RunnerId, "Runner id")
            : request.RunnerId is null
                ? null
                : throw new ArgumentException("Runner id is valid only for runner principals.");
        var ttl = request.TimeToLiveSeconds ?? DefaultEnrolmentTimeToLiveSeconds;
        if (ttl < 30 || ttl > MaximumEnrolmentTimeToLiveSeconds)
            throw new ArgumentException($"TimeToLiveSeconds must be between 30 and {MaximumEnrolmentTimeToLiveSeconds}.");
        var enrolmentId = $"enl_{RandomNumberGenerator.GetHexString(16).ToLowerInvariant()}";
        var code = $"enr_{RandomNumberGenerator.GetHexString(64).ToLowerInvariant()}";
        var now = UtcNow;
        var expires = now.AddSeconds(ttl);
        string installationId = string.Empty;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            installationId = await ReadMetaAsync(connection, transaction, InstallationIdKey, ct)
                             ?? throw new InvalidOperationException("Installation identity is not initialized.");
            if (await ReadPrincipalAsync(connection, transaction, principalId, ct) is not null)
                throw new TaskServerConflictException(
                    "principal-exists", $"Principal '{principalId}' already exists. Rotate or revoke it explicitly.");
            if (runnerId is not null && await ScalarAsync(connection,
                    "SELECT 1 FROM principals WHERE runner_id = $runner;", ct, transaction, ("$runner", runnerId)) is not null)
                throw new TaskServerConflictException(
                    "runner-identity-exists", $"Runner '{runnerId}' already has a principal.");
            // A pending, unexpired enrolment for the same principal is withdrawn so only one code can ever succeed.
            await ExecuteAsync(connection, """
                UPDATE service_enrolments SET revoked_at = $now
                 WHERE principal_id = $principal AND consumed_at IS NULL AND revoked_at IS NULL;
                INSERT INTO service_enrolments(
                    enrolment_id, code_hash, principal_id, kind, runner_id, created_by, created_at, expires_at)
                VALUES ($id, $hash, $principal, $kind, $runner, $actor, $now, $expires);
                """, ct, transaction,
                ("$id", enrolmentId), ("$hash", HashOneTimeSecret(code)), ("$principal", principalId),
                ("$kind", kind), ("$runner", runnerId), ("$actor", actorId), ("$now", Iso(now)),
                ("$expires", Iso(expires)));
            await AuditAsync(connection, transaction, actorId, "enrolment.created", "principal", principalId,
                JsonSerializer.Serialize(new { enrolmentId, kind, runnerId, ttl }), ct);
        }, ct);
        return new IssuedEnrolment(enrolmentId, principalId, kind, runnerId, installationId, code, expires);
    }

    public async Task<IReadOnlyList<EnrolmentDto>> ListEnrolmentsAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT enrolment_id, principal_id, kind, runner_id, created_by, created_at, expires_at, consumed_at, revoked_at
              FROM service_enrolments ORDER BY created_at, enrolment_id;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<EnrolmentDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new EnrolmentDto(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
                Parse(reader.GetString(5)), Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : Parse(reader.GetString(7)),
                reader.IsDBNull(8) ? null : Parse(reader.GetString(8))));
        return result;
    }

    public async Task RevokeEnrolmentAsync(string enrolmentId, string actorId, CancellationToken ct)
    {
        RequireWritable();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var changed = await ExecuteAsync(connection, """
                UPDATE service_enrolments SET revoked_at = COALESCE(revoked_at, $now)
                 WHERE enrolment_id = $id AND consumed_at IS NULL;
                """, ct, transaction, ("$now", Iso(UtcNow)), ("$id", enrolmentId));
            if (changed == 0)
                throw new KeyNotFoundException("Pending enrolment was not found.");
            await AuditAsync(connection, transaction, actorId, "enrolment.revoked", "enrolment", enrolmentId, "{}", ct);
        }, ct);
    }

    /// <summary>
    /// Host-side, unauthenticated exchange of a one-time enrolment code for a
    /// new principal credential. Every refusal is one generic 401 to the
    /// caller; the precise reason goes to the audit log.
    /// </summary>
    public async Task<ExchangedEnrolment> ExchangeEnrolmentAsync(ExchangeEnrolmentRequest request, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.EnrolmentCode)
            || !request.EnrolmentCode.Trim().StartsWith("enr_", StringComparison.Ordinal))
            throw new StudioAuthenticationException("enrolment-denied", "The enrolment code was not accepted.");
        var codeHash = HashOneTimeSecret(request.EnrolmentCode.Trim());
        var credential = GenerateCredential();
        var now = UtcNow;
        ExchangedEnrolment? result = null;
        EnrolmentExchangePolicy.Decision decision = EnrolmentExchangePolicy.Decision.Unknown;
        string? deniedEnrolmentId = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var installationId = await ReadMetaAsync(connection, transaction, InstallationIdKey, ct) ?? string.Empty;
            await using var command = Command(connection, """
                SELECT enrolment_id, principal_id, kind, runner_id, expires_at, consumed_at, revoked_at
                  FROM service_enrolments WHERE code_hash = $hash;
                """, transaction, ("$hash", codeHash));
            await using var reader = await command.ExecuteReaderAsync(ct);
            var found = await reader.ReadAsync(ct);
            var enrolmentId = found ? reader.GetString(0) : null;
            var principalId = found ? reader.GetString(1) : string.Empty;
            var kind = found ? reader.GetString(2) : string.Empty;
            var runnerId = found && !reader.IsDBNull(3) ? reader.GetString(3) : null;
            var expiresAt = found ? Parse(reader.GetString(4)) : DateTime.MinValue;
            var consumed = found && !reader.IsDBNull(5);
            var revoked = found && !reader.IsDBNull(6);
            await reader.DisposeAsync();
            var principalExists = found
                && (await ReadPrincipalAsync(connection, transaction, principalId, ct) is not null
                    || runnerId is not null && await ScalarAsync(connection,
                        "SELECT 1 FROM principals WHERE runner_id = $runner;", ct, transaction, ("$runner", runnerId)) is not null);
            decision = EnrolmentExchangePolicy.Decide(
                found, consumed, revoked, expiresAt, now,
                string.Equals(installationId, request.ExpectedInstallationId?.Trim(), StringComparison.Ordinal),
                principalExists);
            if (decision != EnrolmentExchangePolicy.Decision.Issue)
            {
                deniedEnrolmentId = enrolmentId;
                return;
            }

            var scopes = DefaultScopes(kind);
            await InsertPrincipalAsync(connection, transaction, principalId, kind, scopes, runnerId, credential, now, ct);
            await ExecuteAsync(connection,
                "UPDATE service_enrolments SET consumed_at = $now WHERE enrolment_id = $id;",
                ct, transaction, ("$now", Iso(now)), ("$id", enrolmentId));
            await AuditAsync(connection, transaction, principalId, "enrolment.exchanged", "principal", principalId,
                JsonSerializer.Serialize(new { enrolmentId, kind, runnerId }), ct);
            result = new ExchangedEnrolment(
                installationId,
                new IssuedPrincipalCredential(ToDto(principalId, kind, scopes, runnerId, now, null, null), credential, now));
        }, ct);
        if (result is not null) return result;

        await InWriteTransactionAsync(async (connection, transaction) =>
            await AuditAsync(connection, transaction, "anonymous-enrolment", "enrolment.denied", "enrolment",
                deniedEnrolmentId ?? "unknown", JsonSerializer.Serialize(new { reason = decision.ToString() }), ct), ct);
        throw new StudioAuthenticationException("enrolment-denied", "The enrolment code was not accepted.");
    }

    /// <summary>
    /// Registers a project through the API with a stable id and its one
    /// canonical, credential-free repository. Repeating an identical
    /// registration is idempotent; any differing value is a conflict, never
    /// an implicit change.
    /// </summary>
    public async Task<(ProjectRepositoryDto Registration, bool Created)> RegisterProjectRepositoryAsync(
        RegisterProjectRepositoryRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.ProjectId))
            throw new ArgumentException("A stable project id is required.");
        var projectId = StableOrGeneratedId(request.ProjectId, "prj");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.TaskKeyPrefix)
            || string.IsNullOrWhiteSpace(request.WorkspaceId))
            throw new ArgumentException("Workspace id, project name and task key prefix are required.");
        var url = ProjectRepositoryPolicy.Canonicalize(request.RepositoryUrl);
        var repositoryId = RepositoryIdentityContract.FromUrl(url)!;
        var integrationRef = ProjectRepositoryPolicy.RequireRef(request.IntegrationRef, "Integration ref");
        var releaseRef = string.IsNullOrWhiteSpace(request.ReleaseRef)
            ? null
            : ProjectRepositoryPolicy.RequireRef(request.ReleaseRef, "Release ref");
        var policy = (request.DeliveryPolicy ?? string.Empty).Trim();
        if (!ProjectDeliveryPolicies.All.Contains(policy))
            throw new ArgumentException("Delivery policy must be reviewed-publication or manual-publication.");

        var now = UtcNow;
        ProjectRepositoryDto? existing = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            // The repeat check must share the write transaction with the insert.
            // Otherwise concurrent identical requests can both see no row and
            // the second request reports a false ownership conflict.
            existing = await ReadProjectRepositoryAsync(connection, transaction, projectId, ct);
            bool projectExists;
            bool projectMatches;
            await using (var projectCommand = Command(connection, """
                SELECT workspace_id, name, task_key_prefix FROM projects WHERE id = $project;
                """, transaction, ("$project", projectId)))
            {
                await using var projectReader = await projectCommand.ExecuteReaderAsync(ct);
                projectExists = await projectReader.ReadAsync(ct);
                projectMatches = projectExists
                    && projectReader.GetString(0) == request.WorkspaceId
                    && projectReader.GetString(1) == request.Name.Trim()
                    && projectReader.GetString(2) == request.TaskKeyPrefix.Trim().ToUpperInvariant();
            }
            if (projectExists && !projectMatches)
                throw new TaskServerConflictException(
                    "project-repository-registered",
                    $"Project '{projectId}' already has different project metadata.");
            if (existing is not null)
            {
                if (projectMatches && existing.RepositoryUrl == url && existing.IntegrationRef == integrationRef
                    && existing.ReleaseRef == releaseRef && existing.DeliveryPolicy == policy)
                    return;
                throw new TaskServerConflictException(
                    "project-repository-registered",
                    $"Project '{projectId}' already has a different repository registration.");
            }
            var owner = Convert.ToString(await ScalarAsync(connection,
                "SELECT project_id FROM project_repositories WHERE repository_url = $url;", ct, transaction, ("$url", url)));
            if (!string.IsNullOrEmpty(owner))
                throw new TaskServerConflictException(
                    "repository-owned-by-other-project",
                    $"Repository is already registered to project '{owner}'.");
            if (!projectExists)
                await CreateProjectInTransactionAsync(connection, transaction,
                    new CreateProjectRequest(request.WorkspaceId, request.Name, request.TaskKeyPrefix, projectId),
                    projectId, actorId, Iso(now), ct);
            await ExecuteAsync(connection, """
                INSERT INTO project_repositories(
                    project_id, repository_id, repository_url, integration_ref, release_ref,
                    delivery_policy, registered_at, registered_by)
                VALUES ($project, $repository, $url, $integration, $release, $policy, $now, $actor);
                """, ct, transaction,
                ("$project", projectId), ("$repository", repositoryId), ("$url", url),
                ("$integration", integrationRef), ("$release", releaseRef), ("$policy", policy),
                ("$now", Iso(now)), ("$actor", actorId));
            await AuditAsync(connection, transaction, actorId, "project.repository-registered", "project", projectId,
                JsonSerializer.Serialize(new { repositoryId, url, integrationRef, releaseRef, policy }), ct);
        }, ct);
        if (existing is not null) return (existing, false);
        return (new ProjectRepositoryDto(projectId, repositoryId, url, integrationRef, releaseRef, policy, now, actorId), true);
    }

    public async Task<ProjectRepositoryDto?> GetProjectRepositoryAsync(string projectId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ReadProjectRepositoryAsync(connection, null, projectId, ct);
    }

    private static async Task<ProjectRepositoryDto?> ReadProjectRepositoryAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string projectId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT project_id, repository_id, repository_url, integration_ref, release_ref,
                   delivery_policy, registered_at, registered_by
              FROM project_repositories WHERE project_id = $project;
            """, transaction, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ProjectRepositoryDto(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
                Parse(reader.GetString(6)), reader.GetString(7))
            : null;
    }

    /// <summary>
    /// Records what one runner host proved against the registered origin. A
    /// probe that succeeded only through a fallback remote is stored as not
    /// admitted; the fallback never stands in for the project's repository.
    /// </summary>
    public async Task<ProjectRepositoryProbeDto> RecordProjectRepositoryProbeAsync(
        string projectId, string runnerId, ProjectRepositoryProbeRequest probe, CancellationToken ct)
    {
        RequireWritable();
        var runner = RequireIdentifier(runnerId, "Runner id");
        var registration = await GetProjectRepositoryAsync(projectId, ct)
                           ?? throw new KeyNotFoundException("Project has no registered repository.");
        var verdict = ProjectRepositoryPolicy.DecideProbe(registration.RepositoryUrl, probe);
        var admitted = verdict == ProjectRepositoryProbeVerdicts.Admitted;
        var detail = string.IsNullOrWhiteSpace(probe.Detail) ? null : RedactCredentials(probe.Detail.Trim());
        if (detail is { Length: > 500 }) detail = detail[..500];
        var now = UtcNow;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO project_repository_probes(project_id, runner_id, admitted, verdict, detail, observed_at)
                VALUES ($project, $runner, $admitted, $verdict, $detail, $now)
                ON CONFLICT(project_id, runner_id) DO UPDATE SET
                    admitted = excluded.admitted, verdict = excluded.verdict,
                    detail = excluded.detail, observed_at = excluded.observed_at;
                """, ct, transaction,
                ("$project", projectId), ("$runner", runner), ("$admitted", admitted ? 1 : 0),
                ("$verdict", verdict), ("$detail", detail), ("$now", Iso(now)));
            await AuditAsync(connection, transaction, $"runner:{runner}", "project.repository-probed", "project", projectId,
                JsonSerializer.Serialize(new { runner, verdict }), ct);
        }, ct);
        return new ProjectRepositoryProbeDto(projectId, runner, admitted, verdict, detail, now);
    }

    public async Task<IReadOnlyList<ProjectRepositoryProbeDto>> ListProjectRepositoryProbesAsync(
        string projectId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT project_id, runner_id, admitted, verdict, detail, observed_at
              FROM project_repository_probes WHERE project_id = $project ORDER BY runner_id;
            """, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ProjectRepositoryProbeDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new ProjectRepositoryProbeDto(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0, reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), Parse(reader.GetString(5))));
        return result;
    }

    private async Task<string> ReplaceRecoveryCodeAsync(
        SqliteConnection connection, SqliteTransaction transaction, string userId, CancellationToken ct)
    {
        var code = $"rcv_{RandomNumberGenerator.GetHexString(40).ToLowerInvariant()}";
        await ExecuteAsync(connection, """
            INSERT INTO studio_recovery_codes(user_id, code_hash, created_at) VALUES ($user, $hash, $now)
            ON CONFLICT(user_id) DO UPDATE SET code_hash = excluded.code_hash, created_at = excluded.created_at;
            """, ct, transaction, ("$user", userId), ("$hash", HashOneTimeSecret(code)), ("$now", Iso(UtcNow)));
        return code;
    }

    private static async Task<string?> ReadMetaAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string key, CancellationToken ct)
        => Convert.ToString(await ScalarAsync(connection, "SELECT value FROM meta WHERE key = $key;", ct, transaction,
            ("$key", key))) is { Length: > 0 } value ? value : null;

    private static string RequireOneTimeSecret(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length < 32)
            throw new ArgumentException("One-time codes must contain at least 128 bits of random material.");
        return trimmed;
    }

    private static string HashOneTimeSecret(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedTimeEqualsHex(string actualHash, string expectedHash)
    {
        var actual = Encoding.ASCII.GetBytes(actualHash);
        var expected = Encoding.ASCII.GetBytes(expectedHash);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string RedactCredentials(string value)
        => System.Text.RegularExpressions.Regex.Replace(value, @"://[^/@\s]+@", "://***@");
}

public sealed class StudioAuthorizationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
