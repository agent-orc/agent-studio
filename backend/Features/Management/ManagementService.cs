using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Registry;

namespace AgentStudio.Management;

/// <summary>
/// Task Server management authority. Consoles call this service only through
/// the authenticated API; all mutations are idempotent and appended to the
/// durable management audit ledger.
/// </summary>
public sealed class ManagementService
{
    private static readonly HashSet<string> OwnerCommands = new(StringComparer.Ordinal)
    {
        "runner-enrollment-create",
        "runner-credential-rotate",
        "runner-credential-revoke",
        "runner-revoke",
    };

    private static readonly DateTime StartedAt = DateTime.UtcNow;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IConfiguration _configuration;
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly ClientIdentityStore _clients;
    private readonly AccessSecurityStore _security;
    private readonly ProjectRegistry _projects;
    private readonly MigrationStateStore _migrations;
    private readonly ReviewAttemptTaskLifecycleService? _reviewAttemptLifecycle;
    private readonly TaskIntegrationStatusService? _integrationStatus;
    private readonly object _gate = new();

    public ManagementService(
        IConfiguration configuration,
        TaskScannerService scanner,
        TaskStateMachine states,
        ClientIdentityStore clients,
        AccessSecurityStore security,
        ProjectRegistry projects,
        MigrationStateStore migrations,
        ReviewAttemptTaskLifecycleService? reviewAttemptLifecycle = null,
        TaskIntegrationStatusService? integrationStatus = null)
    {
        _configuration = configuration;
        _scanner = scanner;
        _states = states;
        _clients = clients;
        _security = security;
        _projects = projects;
        _migrations = migrations;
        _reviewAttemptLifecycle = reviewAttemptLifecycle;
        _integrationStatus = integrationStatus;
    }

    private string Root => Path.GetFullPath(_configuration["TaskRepository"]
        ?? Path.Combine(AppContext.BaseDirectory, "workspace"));
    private string Metadata => Path.Combine(Root, ".metadata");
    private string StatePath => Path.Combine(Metadata, "management-state.json");
    private string AuditPath => Path.Combine(Root, ".audit", "management.jsonl");
    private string BackupDirectory => Path.GetFullPath(_configuration["Management:BackupDirectory"]
        ?? Path.Combine(Path.GetDirectoryName(Root)!, Path.GetFileName(Root) + "-backups"));
    private int DefaultRetention => Math.Clamp(_configuration.GetValue("Management:BackupRetention", 7), 1, 100);

    public ManagementStatus Snapshot(string url)
    {
        var jobs = _scanner.ScanAllAutomationJobs();
        var maintenance = ReadState();
        var migrations = _migrations.List();
        var backupFailure = ReadBackupFailure();
        var reasons = new List<string>();
        if (!Directory.Exists(Root)) reasons.Add("data-directory-missing");
        if (migrations.Any(x => x.State == "running")) reasons.Add("migration-running");
        if (migrations.Any(x => x.State == "failed")) reasons.Add("migration-failed");
        if (maintenance.Mode != "normal") reasons.Add("maintenance-" + maintenance.Mode);
        if (!string.IsNullOrWhiteSpace(backupFailure)) reasons.Add("backup-failed");
        var ready = Directory.Exists(Root)
                    && maintenance.Mode == "normal"
                    && migrations.All(x => x.State is not ("running" or "failed"));
        var files = Directory.Exists(Root)
            ? Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).ToArray()
            : [];
        var eventFiles = files.Where(IsEventFile).ToArray();
        var artifactFiles = files.Where(IsArtifactFile).ToArray();
        var lastEvidence = eventFiles.Concat(artifactFiles)
            .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();

        var assembly = typeof(ManagementService).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        var networked = SecurityProfiles.IsNetworked(_configuration);
        var identities = networked ? [] : _clients.ListAll();
        var runners = ReadRunners(identities);
        var identityCount = networked
            ? _security.ListUsers().Count + _security.ListRunners().Count
            : identities.Count;
        var healthState = maintenance.Mode != "normal" || migrations.Any(x => x.State == "running")
            ? "maintenance"
            : reasons.Any(reason => reason is "data-directory-missing" or "migration-failed" or "backup-failed")
                ? "degraded"
                : "healthy";

        return new ManagementStatus(
            new ServerIdentity(
                _configuration["Management:ServerId"] ?? Environment.MachineName.ToLowerInvariant(),
                url.TrimEnd('/'), version, "1.0", "1.0",
                Math.Max(0, (long)(DateTime.UtcNow - StartedAt).TotalSeconds)),
            new ServerHealth(healthState, true, ready, reasons),
            new StoreStatus(
                files.Sum(SafeLength),
                _projects.List().Count,
                jobs.Count(x => x.State != TaskStates.Archive),
                jobs.Count(x => x.State == TaskStates.Archive),
                CountJsonLines(eventFiles), artifactFiles.LongLength, identityCount),
            new EvidenceStatus(
                eventFiles.Length + artifactFiles.Length == 0 ? "empty" : "available",
                eventFiles.LongLength, artifactFiles.LongLength,
                lastEvidence == default ? null : lastEvidence.ToString("O")),
            maintenance,
            migrations,
            runners,
            ReadSecurityStatus(),
            new BackupStatus("server-owned backup directory", DefaultRetention, ListBackups(), backupFailure));
    }

    public RecoveryDiagnostics Diagnostics()
    {
        var status = Snapshot("local");
        var findings = new List<string>(status.Health.Reasons);
        var writable = CanWriteRoot();
        if (!writable) findings.Add("data-directory-not-writable");
        var drive = new DriveInfo(Path.GetPathRoot(Root)!);
        var latest = status.Backups.Items.FirstOrDefault();
        return new RecoveryDiagnostics(
            DateTime.UtcNow.ToString("O"), status.Health.State, status.Health.Ready,
            status.Maintenance.Mode, Directory.Exists(Root), writable,
            drive.IsReady ? drive.AvailableFreeSpace : 0,
            latest?.Id, latest?.VerificationState, findings,
            "systemd/container/service manager owns process start and restart");
    }

    public ManagementCommandResult Execute(
        ManagementCommandRequest request, string actor, string role, string headerKey)
    {
        var kind = (request.Kind ?? "").Trim().ToLowerInvariant();
        if (OwnerCommands.Contains(kind) && role != StudioRoles.Owner)
            throw new ManagementException(403, "owner-required");
        var bodyKey = request.IdempotencyKey?.Trim();
        headerKey = headerKey.Trim();
        if (!string.IsNullOrWhiteSpace(bodyKey) && !string.IsNullOrWhiteSpace(headerKey)
            && !string.Equals(bodyKey, headerKey, StringComparison.Ordinal))
            throw new ManagementException(409, "idempotency-key-conflict");
        var key = string.IsNullOrWhiteSpace(bodyKey) ? headerKey : bodyKey;
        if (string.IsNullOrWhiteSpace(key)) throw new ManagementException(400, "idempotency-key-required");
        if (key.Length > 128) throw new ManagementException(400, "idempotency-key-too-long");
        var requestFingerprint = Fingerprint(request, kind);

        lock (_gate)
        {
            var prior = FindPrior(key, kind, request.DryRun, actor, requestFingerprint);
            if (prior is not null) return prior;
            if (!request.DryRun && !string.Equals(request.Confirmation, kind, StringComparison.Ordinal))
                throw new ManagementException(400, $"confirmation must exactly equal '{kind}'");

            var commandId = "cmd_" + Guid.NewGuid().ToString("N");
            AppendAudit(new ManagementCommandResult(
                commandId, kind, request.DryRun, "started", 0, 0,
                "Command accepted for execution.", DateTime.UtcNow.ToString("O"), actor, key),
                requestFingerprint);
            try
            {
                var result = kind switch
                {
                    "archive-sweep" => SweepArchive(request.DryRun, actor, key),
                    "orphan-sweep" => SweepOrphans(request.DryRun, actor, key),
                    "fixture-sweep" => SweepFixtures(request.DryRun, actor, key),
                    "backup-create" => CreateBackup(request.DryRun, actor, key, request.RetentionCount),
                    "restore-verify" => VerifyRestore(request.DryRun, actor, key, request.BackupId),
                    "backup-retention" => ApplyRetention(request.DryRun, actor, key, request.RetentionCount),
                    "maintenance-enter" => SetMaintenance(request.DryRun, actor, key, "maintenance", request.Reason),
                    "maintenance-read-only" => SetMaintenance(request.DryRun, actor, key, "read-only", request.Reason),
                    "maintenance-exit" => SetMaintenance(request.DryRun, actor, key, "normal", request.Reason),
                    "shutdown-prepare" => PrepareShutdown(request.DryRun, actor, key, request.Reason),
                    "runner-enrollment-create" => CreateRunnerEnrollment(request, actor, key),
                    "runner-credential-rotate" => RotateRunnerCredential(request, actor, key),
                    "runner-credential-revoke" => RevokeRunnerCredential(request, actor, key),
                    "runner-revoke" => RevokeRunner(request, actor, key),
                    "runner-drain" => DrainRunner(request, actor, key, retire: false),
                    "runner-retire" => DrainRunner(request, actor, key, retire: true),
                    _ => throw new ManagementException(400, "unknown-management-command")
                };
                result = result with { CommandId = commandId };
                AppendAudit(result, requestFingerprint);
                return result;
            }
            catch (SecurityOperationException ex)
            {
                AppendAudit(new ManagementCommandResult(
                    commandId, kind, request.DryRun, "failed",
                    0, 0, ex.Code, DateTime.UtcNow.ToString("O"), actor, key),
                    requestFingerprint);
                throw new ManagementException(ex.Status, ex.Code);
            }
            catch (Exception ex)
            {
                AppendAudit(new ManagementCommandResult(
                    commandId, kind, request.DryRun, "failed",
                    0, 0, ex.Message, DateTime.UtcNow.ToString("O"), actor, key),
                    requestFingerprint);
                throw;
            }
        }
    }

    public MaintenanceStatus CurrentMaintenance() => ReadState();

    private ManagementCommandResult SweepArchive(bool dry, string actor, string key)
    {
        var candidates = _scanner.ScanAllAutomationJobs().Where(x => x.State == TaskStates.Completed).ToArray();
        var affected = 0;
        var blocked = new List<object>();
        var guarded = _configuration.GetValue("DeliveryChain:Guarded", true);
        var statuses = guarded ? _integrationStatus?.BuildLookup(candidates) : null;
        if (dry)
            foreach (var item in candidates)
            {
                if (!guarded) continue;
                TaskIntegrationStatus? status = null;
                statuses?.TryGetValue(item.TaskKey, out status);
                var decision = DeliveryLanePolicy.Decide(item.State, TaskStates.Archive,
                    AcceptanceIntegrationPolicy.IsIntegrationRequired(item), status?.Status);
                if (!decision.Allowed)
                    blocked.Add(new { taskKey = item.TaskKey, category = decision.Category,
                        recoveryAction = decision.RecoveryAction });
                else if (AcceptanceIntegrationPolicy.IsIntegrationRequired(item)
                    && (item.CompletionClaim?.Basis != CompletionClaimBases.IntegratedDelivery
                        || string.IsNullOrWhiteSpace(item.CompletionClaim.ResultSha)
                        || string.IsNullOrWhiteSpace(item.CompletionClaim.DeliveryEpoch)
                        || !string.Equals(item.CompletionClaim.TargetRefFingerprint,
                            status?.TargetRefFingerprint, StringComparison.Ordinal)))
                    blocked.Add(new { taskKey = item.TaskKey, category = "stale-acceptance",
                        recoveryAction = "Repeat human review for the current delivery epoch and target ref." });
            }
        if (!dry)
            foreach (var item in candidates)
            {
                MoveJobOutcome MoveCore() => _states.MoveJob(
                    item.Id,
                    TaskStates.Archive,
                    item.WatchPath,
                    "management-archive-sweep",
                    transitionCause: LaneChangeCauses.Archived,
                    transitionDetail: "management-archive-sweep");
                var moved = _reviewAttemptLifecycle is null
                    ? MoveCore()
                    : _reviewAttemptLifecycle.ExecuteTerminalTransition(
                        item,
                        TaskStates.Archive,
                        MoveCore);
                if (moved.Status == MoveJobStatus.Success) affected++;
                else blocked.Add(new { taskKey = item.TaskKey, category = moved.Status.ToString(),
                    recoveryAction = moved.Message });
            }
        return Result("archive-sweep", dry, candidates.Length, affected,
            dry ? $"{candidates.Length - blocked.Count} completed tasks would be archived; {blocked.Count} blocked."
                : $"Archived {affected} completed tasks; {blocked.Count} blocked.",
            actor, key, new { blocked });
    }

    private ManagementCommandResult SweepOrphans(bool dry, string actor, string key)
    {
        var rows = new List<(string WatchPath, string Lane, string Folder)>();
        foreach (var watch in _scanner.GetWatchPaths())
            foreach (var lane in new[] { TaskStates.Archive, TaskStates.FailedPickup })
            {
                var lanePath = Path.Combine(watch.Path, lane);
                if (!Directory.Exists(lanePath)) continue;
                rows.AddRange(Directory.EnumerateDirectories(lanePath)
                    .Where(x => !File.Exists(Path.Combine(x, "task.json")))
                    .Select(x => (watch.Path, lane, Path.GetFileName(x))));
            }
        var affected = 0;
        if (!dry)
            foreach (var row in rows)
                if (_states.DeleteOrphanFolder(row.WatchPath, row.Lane, row.Folder).Status == OrphanFolderDeleteStatus.Success) affected++;
        return Result("orphan-sweep", dry, rows.Count, affected,
            dry ? $"{rows.Count} terminal orphan folders would be removed." : $"Removed {affected} terminal orphan folders.", actor, key);
    }

    private ManagementCommandResult SweepFixtures(bool dry, string actor, string key)
    {
        var candidates = _scanner.ScanAllJobs().Where(x => x.Fixture || FixtureHeuristics.IsLikelyFixture(x)).ToArray();
        var affected = 0;
        if (!dry)
            foreach (var item in candidates)
                if (_states.DeleteJob(item.Id, item.WatchPath)) affected++;
        return Result("fixture-sweep", dry, candidates.Length, affected,
            dry ? $"{candidates.Length} fixture tasks would be deleted." : $"Deleted {affected} fixture tasks.", actor, key);
    }

    private ManagementCommandResult CreateBackup(bool dry, string actor, string key, int? retention)
    {
        if (!Directory.Exists(Root)) throw new ManagementException(409, "data-directory-missing");
        EnsureBackupDirectoryOutsideRoot();
        if (dry) return Result("backup-create", true, 1, 0, "A consistent data-directory backup would be created and verified.", actor, key);
        if (ReadState().Mode == "normal")
            throw new ManagementException(409, "backup-requires-maintenance");
        if (_migrations.List().Any(item => item.State is "running" or "failed"))
            throw new ManagementException(409, "backup-requires-settled-migrations");
        var legacy = SecurityProfiles.IsNetworked(_configuration) ? [] : _clients.ListAll();
        if (ReadRunners(legacy).Any(x => x.ActiveSlots > 0))
            throw new ManagementException(409, "backup-requires-drained-runners");
        Directory.CreateDirectory(BackupDirectory);
        var id = "backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var path = Path.Combine(BackupDirectory, id + ".zip");
        var temp = path + ".tmp";
        try
        {
            ZipFile.CreateFromDirectory(Root, temp, CompressionLevel.Fastest, includeBaseDirectory: false);
            var archive = InspectArchive(temp);
            if (archive.EntryCount == 0) throw new InvalidDataException("Backup verification failed: archive is empty.");
            File.Move(temp, path);
            WriteBackupManifest(path, archive with
            {
                Id = id,
                FileName = Path.GetFileName(path),
                CreatedAt = DateTime.UtcNow.ToString("O"),
            });
            var verified = InspectBackup(path);
            if (verified.VerificationState != "verified")
                throw new InvalidDataException("Backup verification failed after publication.");
            Retain(Math.Clamp(retention ?? DefaultRetention, 1, 100));
            ClearBackupFailure();
            return Result("backup-create", false, 1, 1, $"Created and verified backup {id}.", actor, key, verified);
        }
        catch (Exception ex)
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (File.Exists(path)) File.Delete(path);
            var manifest = BackupManifestPath(path);
            if (File.Exists(manifest)) File.Delete(manifest);
            WriteBackupFailure(ex.Message);
            throw;
        }
    }

    private ManagementCommandResult VerifyRestore(bool dry, string actor, string key, string? backupId)
    {
        var summary = ResolveBackup(backupId);
        if (dry) return Result("restore-verify", true, 1, 0, $"Backup {summary.Id} would be extracted into isolated restore staging and verified.", actor, key);
        var backupPath = Path.Combine(BackupDirectory, summary.FileName);
        var verified = InspectBackup(backupPath);
        if (verified.VerificationState != "verified")
            return Result("restore-verify", false, 1, 0,
                $"Backup {verified.Id} restore verification failed before extraction.",
                actor, key, new { backup = verified, stagingState = "failed", extractedFiles = 0 });
        var staging = Path.Combine(BackupDirectory, ".restore-verification", summary.Id + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(backupPath, staging);
            var extractedFiles = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Count();
            var state = verified.VerificationState == "verified" && extractedFiles == verified.EntryCount
                ? "verified" : "failed";
            return Result("restore-verify", false, 1, state == "verified" ? 1 : 0,
                $"Backup {verified.Id} restore verification: {state} ({extractedFiles} files extracted).",
                actor, key, new { backup = verified, stagingState = state, extractedFiles });
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private ManagementCommandResult ApplyRetention(bool dry, string actor, string key, int? requested)
    {
        var keep = Math.Clamp(requested ?? DefaultRetention, 1, 100);
        var matched = Math.Max(0, ListBackups().Count - keep);
        var affected = dry ? 0 : Retain(keep);
        return Result("backup-retention", dry, matched, affected,
            dry ? $"{matched} backups would be removed; newest {keep} retained." : $"Removed {affected} backups; newest {keep} retained.", actor, key);
    }

    private ManagementCommandResult SetMaintenance(bool dry, string actor, string key, string mode, string? reason)
    {
        if (!dry) WriteState(new MaintenanceStatus(mode, mode != "normal", false, reason, DateTime.UtcNow.ToString("O"), actor));
        return Result(mode == "normal" ? "maintenance-exit" : mode == "read-only" ? "maintenance-read-only" : "maintenance-enter",
            dry, 1, dry ? 0 : 1, dry ? $"Server would enter {mode} mode." : $"Server entered {mode} mode.", actor, key);
    }

    private ManagementCommandResult PrepareShutdown(bool dry, string actor, string key, string? reason)
    {
        if (!dry) WriteState(new MaintenanceStatus("maintenance", true, true, reason, DateTime.UtcNow.ToString("O"), actor));
        return Result("shutdown-prepare", dry, 1, dry ? 0 : 1,
            dry ? "Server would drain and prepare for service-manager shutdown." : "Server drained and is prepared for service-manager shutdown.", actor, key);
    }

    private ManagementCommandResult CreateRunnerEnrollment(ManagementCommandRequest request, string actor, string key)
    {
        if (string.IsNullOrWhiteSpace(request.RunnerName))
            throw new ManagementException(400, "runner-name-required");
        if (request.DryRun)
            return Result("runner-enrollment-create", true, 1, 0,
                $"A one-time enrollment would be created for {request.RunnerName.Trim()}.", actor, key);
        var created = _security.CreateEnrollment(new RunnerEnrollmentRequest(
            request.RunnerName.Trim(), request.Scopes, request.ExpiresAt, null));
        return Result("runner-enrollment-create", false, 1, 1,
            $"Created one-time Runner enrollment for {created.Enrollment.Name}.", actor, key,
            new
            {
                enrollmentCode = created.Code,
                created.Enrollment.Name,
                created.Enrollment.Scopes,
                created.Enrollment.ExpiresAt,
            });
    }

    private ManagementCommandResult RotateRunnerCredential(ManagementCommandRequest request, string actor, string key)
    {
        var runner = RequireRunner(request.RunnerId);
        if (request.DryRun)
            return Result("runner-credential-rotate", true, 1, 0,
                $"A new credential would be issued for {runner.Name}; existing credentials would remain valid until revoked.", actor, key,
                new { runnerId = runner.Id });
        var rotated = _security.RotateRunner(runner.Id, new RunnerRotateRequest(request.Scopes, request.ExpiresAt));
        return Result("runner-credential-rotate", false, 1, 1,
            $"Issued a new one-time credential for {rotated.Runner.Name}.", actor, key,
            new
            {
                runnerId = rotated.Runner.Id,
                credentialId = rotated.Credential.Id,
                secret = rotated.Secret,
                rotated.Credential.Scopes,
                rotated.Credential.ExpiresAt,
            });
    }

    private ManagementCommandResult RevokeRunnerCredential(ManagementCommandRequest request, string actor, string key)
    {
        var runner = RequireRunner(request.RunnerId);
        if (string.IsNullOrWhiteSpace(request.CredentialId)
            || runner.Credentials.All(x => x.Id != request.CredentialId))
            throw new ManagementException(404, "credential-not-found");
        if (!request.DryRun) _security.RevokeCredential(runner.Id, request.CredentialId);
        return Result("runner-credential-revoke", request.DryRun, 1, request.DryRun ? 0 : 1,
            request.DryRun
                ? $"Credential {request.CredentialId} for {runner.Name} would be revoked."
                : $"Revoked credential {request.CredentialId} for {runner.Name}.", actor, key,
            new { runnerId = runner.Id, credentialId = request.CredentialId });
    }

    private ManagementCommandResult RevokeRunner(ManagementCommandRequest request, string actor, string key)
    {
        if (!SecurityProfiles.IsNetworked(_configuration))
        {
            var legacy = RequireLegacyRunner(request.RunnerId);
            if (!request.DryRun) _clients.SoftDelete(legacy.Id);
            return Result("runner-revoke", request.DryRun, 1, request.DryRun ? 0 : 1,
                request.DryRun ? $"Runner {legacy.DisplayName} would be retired." : $"Retired Runner {legacy.DisplayName}.", actor, key,
                new { runnerId = legacy.Id });
        }
        var runner = RequireRunner(request.RunnerId);
        if (!request.DryRun) _security.RevokeRunner(runner.Id);
        return Result("runner-revoke", request.DryRun, 1, request.DryRun ? 0 : 1,
            request.DryRun ? $"Runner {runner.Name} would be revoked immediately." : $"Revoked Runner {runner.Name}.", actor, key,
            new { runnerId = runner.Id });
    }

    private ManagementCommandResult DrainRunner(ManagementCommandRequest request, string actor, string key, bool retire)
    {
        var kind = retire ? "runner-retire" : "runner-drain";
        if (!SecurityProfiles.IsNetworked(_configuration))
        {
            var legacy = RequireLegacyRunner(request.RunnerId);
            if (!request.DryRun) _clients.RequestDrain(legacy.Id, retire);
            return Result(kind, request.DryRun, 1, request.DryRun ? 0 : 1,
                request.DryRun
                    ? $"Runner {legacy.DisplayName} would stop receiving claims{(retire ? " and retire after active work finishes" : "")}."
                    : $"Runner {legacy.DisplayName} is draining{(retire ? " and will retire after active work finishes" : "")}.", actor, key,
                new { runnerId = legacy.Id });
        }
        var runner = RequireRunner(request.RunnerId);
        if (!request.DryRun) _security.RequestRunnerDrain(runner.Id, retire);
        return Result(kind, request.DryRun, 1, request.DryRun ? 0 : 1,
            request.DryRun
                ? $"Runner {runner.Name} would stop receiving claims{(retire ? " and retire after active work finishes" : "")}."
                : $"Runner {runner.Name} is draining{(retire ? " and will retire after active work finishes" : "")}.", actor, key,
            new { runnerId = runner.Id });
    }

    private RunnerServiceIdentity RequireRunner(string? runnerId)
    {
        if (string.IsNullOrWhiteSpace(runnerId)) throw new ManagementException(400, "runner-id-required");
        return _security.ListRunners().FirstOrDefault(x => x.Id == runnerId)
            ?? throw new ManagementException(404, "runner-not-found");
    }

    private ClientIdentity RequireLegacyRunner(string? runnerId)
    {
        if (string.IsNullOrWhiteSpace(runnerId)) throw new ManagementException(400, "runner-id-required");
        var runner = _clients.Find(runnerId);
        return runner is { Kind: ClientIdentityKind.AgentInstance or ClientIdentityKind.Service or ClientIdentityKind.Retired }
            ? runner
            : throw new ManagementException(404, "runner-not-found");
    }

    private ManagementCommandResult Result(string kind, bool dry, int matched, int affected, string summary, string actor, string key, object? detail = null)
        => new("cmd_" + Guid.NewGuid().ToString("N"), kind, dry, "completed", matched, affected,
            summary, DateTime.UtcNow.ToString("O"), actor, key, detail);

    private static string Fingerprint(ManagementCommandRequest request, string kind)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind,
            request.DryRun,
            confirmation = request.Confirmation?.Trim(),
            request.RetentionCount,
            backupId = request.BackupId?.Trim(),
            reason = request.Reason?.Trim(),
            runnerId = request.RunnerId?.Trim(),
            credentialId = request.CredentialId?.Trim(),
            runnerName = request.RunnerName?.Trim(),
            scopes = request.Scopes?.ToArray(),
            request.ExpiresAt,
        }, Json);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private void AppendAudit(ManagementCommandResult result, string requestFingerprint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AuditPath)!);
        var row = new ManagementAuditEvent(result.CompletedAt, result.CommandId, result.Actor,
            result.Kind, result.DryRun, result.IdempotencyKey, result.State,
            result.Matched, result.Affected, result.Summary, requestFingerprint);
        using var stream = new FileStream(AuditPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(JsonSerializer.Serialize(row, Json));
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private ManagementCommandResult? FindPrior(
        string key, string kind, bool dryRun, string actor, string requestFingerprint)
    {
        if (!File.Exists(AuditPath)) return null;
        ManagementAuditEvent? started = null;
        foreach (var line in File.ReadLines(AuditPath).Reverse())
        {
            try
            {
                var row = JsonSerializer.Deserialize<ManagementAuditEvent>(line, Json);
                if (row?.IdempotencyKey != key) continue;
                if (row.Kind != kind || row.DryRun != dryRun || row.Actor != actor
                    || !string.Equals(row.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
                    throw new ManagementException(409, "idempotency-key-conflict");
                if (row.Outcome == "started")
                {
                    started ??= row;
                    continue;
                }
                return new ManagementCommandResult(row.CommandId, row.Kind, row.DryRun, row.Outcome,
                    row.Matched, row.Affected, row.Summary, row.Timestamp, row.Actor, row.IdempotencyKey);
            }
            catch (JsonException) { continue; }
        }
        if (started is not null) throw new ManagementException(409, "idempotency-key-in-doubt");
        return null;
    }

    private MaintenanceStatus ReadState()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<MaintenanceStatus>(File.ReadAllText(StatePath), Json) ?? NormalState() : NormalState(); }
        catch (JsonException) { return new("maintenance", true, false, "management-state-invalid", null, null); }
    }
    private static MaintenanceStatus NormalState() => new("normal", false, false, null, null, null);
    private void WriteState(MaintenanceStatus state)
    {
        Directory.CreateDirectory(Metadata);
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, StatePath, true);
    }

    private SecurityManagementStatus ReadSecurityStatus()
    {
        var networked = SecurityProfiles.IsNetworked(_configuration);
        return new(networked, _security.ListUsers().Count, _security.ListRunners().Count,
            "/api/auth/session", "/api/auth/users", "/api/auth/runners",
            networked
                ? "Shared AGT-2193 user, session, and Runner credential authority"
                : "Local attribution compatibility profile");
    }

    private IReadOnlyList<RunnerManagementStatus> ReadRunners(IReadOnlyList<ClientIdentity> legacy)
    {
        var secured = _security.ListRunners();
        if (SecurityProfiles.IsNetworked(_configuration) || secured.Count > 0)
            return secured.Select(runner =>
            {
                var lastUsed = runner.Credentials
                    .Where(item => item.LastUsedAt is not null)
                    .Select(item => item.LastUsedAt!.Value)
                    .DefaultIfEmpty().Max();
                var state = runner.RevokedAt is not null ? "revoked"
                    : runner.RetiredAt is not null ? "retired"
                    : runner.DrainRequestedAt is not null ? "draining"
                    : runner.LastSeenAt is not null ? "running"
                    : "enrolled";
                return new RunnerManagementStatus(
                    runner.Id, runner.Name, state,
                    lastUsed == default ? null : lastUsed.ToUniversalTime().ToString("O"),
                    runner.LastClaimAt?.ToUniversalTime().ToString("O"),
                    runner.ActiveSlots, runner.AvailableSlots,
                    runner.DrainRequestedAt is not null, runner.RetireRequestedAt is not null,
                    "/api/auth/runners");
            }).ToArray();

        return legacy
            .Where(x => x.Kind is ClientIdentityKind.AgentInstance or ClientIdentityKind.Service or ClientIdentityKind.Retired)
            .Select(x => new RunnerManagementStatus(
                x.Id, x.DisplayName, x.Kind == ClientIdentityKind.Retired ? "retired" : x.RunnerDaemonState ?? "enrolled",
                x.LastSeenAt?.ToUniversalTime().ToString("O"), x.RunnerLastClaimAt?.ToUniversalTime().ToString("O"),
                x.RunnerActiveSlots.GetValueOrDefault(), x.RunnerAvailableSlots.GetValueOrDefault(),
                x.DrainRequestedAt is not null, x.RetireRequestedAt is not null,
                "/api/clients"))
            .ToArray();
    }

    private IReadOnlyList<BackupSummary> ListBackups()
    {
        if (!Directory.Exists(BackupDirectory)) return [];
        return Directory.EnumerateFiles(BackupDirectory, "backup-*.zip")
            .OrderByDescending(File.GetCreationTimeUtc).Select(InspectBackup).ToArray();
    }
    private BackupSummary ResolveBackup(string? id)
    {
        var item = ListBackups().FirstOrDefault(x => string.IsNullOrWhiteSpace(id) || x.Id == id);
        return item ?? throw new ManagementException(404, "backup-not-found");
    }
    private static BackupSummary InspectBackup(string path)
    {
        BackupManifest actual;
        try { actual = InspectArchive(path); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            var info = new FileInfo(path);
            return new(Path.GetFileNameWithoutExtension(path), Path.GetFileName(path), info.Exists ? info.Length : 0,
                info.Exists ? info.CreationTimeUtc.ToString("O") : DateTime.MinValue.ToString("O"), "", "failed", 0);
        }

        var manifestPath = BackupManifestPath(path);
        BackupManifest? expected = null;
        try
        {
            if (File.Exists(manifestPath))
                expected = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            expected = null;
        }

        var verified = expected is not null
                       && expected.Id == actual.Id
                       && expected.FileName == actual.FileName
                       && expected.SizeBytes == actual.SizeBytes
                       && expected.EntryCount == actual.EntryCount
                       && string.Equals(expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase);
        return new(actual.Id, actual.FileName, actual.SizeBytes,
            expected?.CreatedAt ?? actual.CreatedAt, actual.Sha256,
            verified ? "verified" : "failed", actual.EntryCount);
    }

    private static BackupManifest InspectArchive(string path)
    {
        var info = new FileInfo(path);
        using var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries.Count(entry => !string.IsNullOrEmpty(entry.Name));
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new BackupManifest(Path.GetFileNameWithoutExtension(path), Path.GetFileName(path), info.Length,
            info.CreationTimeUtc.ToString("O"), hash, entries);
    }

    private static void WriteBackupManifest(string archivePath, BackupManifest manifest)
    {
        var path = BackupManifestPath(archivePath);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, manifest, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, true);
    }

    private static string BackupManifestPath(string archivePath) => archivePath + ".manifest.json";

    private int Retain(int keep)
    {
        var removed = 0;
        foreach (var item in ListBackups().Skip(keep))
        {
            var archive = Path.Combine(BackupDirectory, item.FileName);
            File.Delete(archive);
            var manifest = BackupManifestPath(archive);
            if (File.Exists(manifest)) File.Delete(manifest);
            removed++;
        }
        return removed;
    }

    private void EnsureBackupDirectoryOutsideRoot()
    {
        var root = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var backup = BackupDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(root, backup, StringComparison.OrdinalIgnoreCase)
            || backup.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ManagementException(409, "backup-directory-must-be-outside-data-directory");
    }

    private bool CanWriteRoot()
    {
        if (!Directory.Exists(Root)) return false;
        try
        {
            if (OperatingSystem.IsWindows())
                return (File.GetAttributes(Root) & FileAttributes.ReadOnly) == 0;
            var mode = File.GetUnixFileMode(Root);
            return (mode & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
    private static long SafeLength(string path) { try { return new FileInfo(path).Length; } catch (IOException) { return 0; } }
    private static bool IsEventFile(string path) => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || path.Contains("timeline", StringComparison.OrdinalIgnoreCase);
    private static bool IsArtifactFile(string path) => path.Contains($"{Path.DirectorySeparatorChar}results{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) || path.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    private static long CountJsonLines(IEnumerable<string> files)
    {
        long count = 0;
        foreach (var file in files.Where(x => x.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
            try { count += File.ReadLines(file).LongCount(); }
            catch (IOException ex) { SilentCatch.Note(ex, $"management event count: {file}"); }
        return count;
    }
    private string? ReadBackupFailure() { var path = Path.Combine(Metadata, "last-backup-failure.txt"); return File.Exists(path) ? File.ReadAllText(path) : null; }
    private void WriteBackupFailure(string message) { Directory.CreateDirectory(Metadata); File.WriteAllText(Path.Combine(Metadata, "last-backup-failure.txt"), message); }
    private void ClearBackupFailure() { var path = Path.Combine(Metadata, "last-backup-failure.txt"); if (File.Exists(path)) File.Delete(path); }
}

internal sealed record BackupManifest(
    string Id, string FileName, long SizeBytes, string CreatedAt,
    string Sha256, int EntryCount);

public sealed class ManagementException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
