using System.Diagnostics;
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

    private static readonly DateTime ProcessStartUtc = ReadProcessStartUtc();
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
    private readonly TaskMutationService? _mutations;
    private readonly TimelineLog? _timeline;
    private readonly ManagementStatisticsCache _statistics;
    private readonly StudioBackupService? _backups;
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
        TaskIntegrationStatusService? integrationStatus = null,
        TaskMutationService? mutations = null,
        TimelineLog? timeline = null,
        ManagementStatisticsCache? statistics = null,
        StudioBackupService? backups = null)
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
        _mutations = mutations;
        _timeline = timeline;
        _backups = backups;
        _statistics = statistics ?? new ManagementStatisticsCache(() => ComputeStatistics());
    }

    private string Root => Path.GetFullPath(_configuration["TaskRepository"]
        ?? Path.Combine(AppContext.BaseDirectory, "workspace"));
    private string Metadata => Path.Combine(Root, ".metadata");
    private string StatePath => Path.Combine(Metadata, "management-state.json");
    private string AuditPath => Path.Combine(Root, ".audit", "management.jsonl");
    private StudioBackupService Backups => _backups ?? throw new ManagementException(503, "backups-unavailable");

    public ManagementStatus Snapshot(string url)
    {
        var statistics = _statistics.Current();
        var maintenance = ReadState();
        var migrations = _migrations.List();
        var backupOverview = _backups?.Overview();
        var backupFailure = backupOverview?.LastRun is { Outcome: "failed" } failedRun ? failedRun.Error : null;
        var reasons = new List<string>();
        if (!Directory.Exists(Root)) reasons.Add("data-directory-missing");
        if (migrations.Any(x => x.State == "running")) reasons.Add("migration-running");
        if (migrations.Any(x => x.State == "failed")) reasons.Add("migration-failed");
        if (maintenance.Mode != "normal") reasons.Add("maintenance-" + maintenance.Mode);
        if (!string.IsNullOrWhiteSpace(backupFailure)) reasons.Add("backup-failed");
        if (backupOverview is { Stale: true }) reasons.Add("backup-stale");
        var ready = Directory.Exists(Root)
                    && maintenance.Mode == "normal"
                    && migrations.All(x => x.State is not ("running" or "failed"));
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
            : reasons.Any(reason => reason is "data-directory-missing" or "migration-failed" or "backup-failed" or "backup-stale")
                ? "degraded"
                : "healthy";

        return new ManagementStatus(
            new ServerIdentity(
                _configuration["Management:ServerId"] ?? Environment.MachineName.ToLowerInvariant(),
                url.TrimEnd('/'), version, "1.0", "1.0",
                Math.Max(0, (long)(DateTime.UtcNow - ProcessStartUtc).TotalSeconds)),
            new ServerHealth(healthState, true, ready, reasons),
            new StoreStatus(
                statistics.Snapshot?.SizeBytes ?? 0,
                statistics.Snapshot?.ProjectCount ?? 0,
                statistics.Snapshot?.TaskCount ?? 0,
                statistics.Snapshot?.ArchivedTaskCount ?? 0,
                null, statistics.Snapshot?.ArtifactFiles ?? 0, identityCount),
            new EvidenceStatus(
                statistics.Snapshot is null ? statistics.State :
                    statistics.Snapshot.EventFiles + statistics.Snapshot.ArtifactFiles == 0 ? "empty" : "available",
                statistics.Snapshot?.EventFiles ?? 0, statistics.Snapshot?.ArtifactFiles ?? 0,
                statistics.Snapshot?.LastWriteAt),
            maintenance,
            migrations,
            runners,
            ReadSecurityStatus(),
            new BackupStatus("server-owned backup directory", backupOverview?.RetentionCount ?? 0,
                backupOverview?.Backups.Select(Summary).ToArray() ?? [], backupFailure, backupOverview),
            new StatisticsStatus(statistics.State, statistics.Snapshot?.RefreshedAt));
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
        if (kind == "backup-retention" && role != StudioRoles.Owner) RequireRetentionWithinConfiguration(request, actor);
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
                    "delivery-stale-repair-2026-10-11" => RepairDeliveryBounce(request.DryRun, actor, key),
                    "orphan-sweep" => SweepOrphans(request.DryRun, actor, key),
                    "fixture-sweep" => SweepFixtures(request.DryRun, actor, key),
                    "backup-create" => CreateBackup(request.DryRun, actor, key),
                    "restore-verify" => VerifyRestore(request.DryRun, actor, key, request.BackupId),
                    "backup-retention" => ApplyRetention(request.DryRun, actor, key, request.RetentionCount),
                    "backup-upload" => UploadBackups(request.DryRun, actor, key),
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
                if (!request.DryRun && kind is ("archive-sweep" or "orphan-sweep" or "fixture-sweep"
                    or "backup-create" or "backup-retention"))
                    _statistics.Invalidate();
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

    private ManagementCommandResult RepairDeliveryBounce(bool dry, string actor, string key)
    {
        var integration = _integrationStatus
            ?? throw new ManagementException(503, "delivery-repair-unavailable");
        var mutations = _mutations
            ?? throw new ManagementException(503, "delivery-repair-unavailable");
        var timeline = _timeline
            ?? throw new ManagementException(503, "delivery-repair-unavailable");
        var cards = _scanner.ScanAllAutomationJobs()
            .Where(card => card.State == TaskStates.HumanReview)
            .ToArray();
        var statuses = integration.BuildLookup(cards);
        string? OperatorReason(TaskInfo card)
        {
            var step = integration.ReadLatestMergeStep(card);
            var reason = step?.Reason?.Trim();
            return string.Equals(step?.Verdict, "operator-override", StringComparison.OrdinalIgnoreCase)
                && CompletionContractPolicy.IsUsableReason(reason) ? reason : null;
        }
        var candidates = cards.Where(card =>
        {
            statuses.TryGetValue(card.TaskKey, out var status);
            return DeliveryBounceRepairPolicy.IsEligible(card, status,
                timeline.ReadAll(card.FolderPath), OperatorReason(card) is not null);
        }).ToArray();
        // The incident affected about 180 cards. Refuse a wider population
        // rather than silently broadening a one-time historical repair.
        if (candidates.Length > 250)
            throw new ManagementException(409, "delivery-repair-candidate-limit");
        var blocked = new List<object>();
        var affected = 0;
        if (!dry)
        {
            foreach (var candidate in candidates)
            {
                var card = _scanner.FindJob(candidate.Id, candidate.WatchPath);
                if (card is null)
                {
                    blocked.Add(new { taskKey = candidate.TaskKey, reason = "card-missing" });
                    continue;
                }
                var status = integration.BuildLookup([card]).GetValueOrDefault(card.TaskKey);
                var operatorReason = OperatorReason(card);
                if (!DeliveryBounceRepairPolicy.IsEligible(card, status,
                    timeline.ReadAll(card.FolderPath), operatorReason is not null))
                {
                    blocked.Add(new { taskKey = card.TaskKey, reason = "eligibility-changed" });
                    continue;
                }

                var subject = TaskIntegrationStatusService.CurrentReviewSubject(card);
                var latest = card.Commits.Where(TaskCommitSupersession.IsEffectiveDelivery)
                    .OrderBy(commit => commit.DeliveryGeneration ?? 0)
                    .ThenBy(commit => commit.At).LastOrDefault();
                var claim = new TaskCompletionClaim
                {
                    Basis = operatorReason is null ? CompletionClaimBases.IntegratedDelivery
                        : CompletionClaimBases.OperatorOverride,
                    Evidence = operatorReason is null
                        ? "Restored the earlier acceptance after the 2026-10-11 false stale-delivery bounce; the current delivery is integrated."
                        : "Restored the earlier operator override after the 2026-10-11 false stale-delivery bounce.",
                    Reason = operatorReason,
                    Actor = actor,
                    CommitSha = status?.Sha,
                    ResultSha = subject?.ResultSha ?? latest?.ResultSha ?? latest?.Sha,
                    DeliveryEpoch = subject?.RunAttemptId ?? latest?.RunAttemptId
                        ?? latest?.DeliveryGeneration?.ToString() ?? latest?.Sha,
                    IntegrationBranch = status?.IntegrationBranch ?? card.IntegrationBranch,
                    TargetRefFingerprint = status?.TargetRefFingerprint,
                    RecordedAtUtc = DateTime.UtcNow,
                };
                if (!mutations.SetCompletionClaimOnFolder(card.FolderPath, claim)
                    || _scanner.FindJob(card.Id, card.WatchPath)?.CompletionClaim?.Basis != claim.Basis)
                {
                    blocked.Add(new { taskKey = card.TaskKey, reason = "claim-write-failed" });
                    continue;
                }
                var outcome = _states.MoveJob(card.Id, TaskStates.Completed, card.WatchPath,
                    cause: TimelineActors.System, expectedSourceState: TaskStates.HumanReview,
                    reason: "Restored acceptance after the 2026-10-11 delivery-verdict-stale incident.",
                    transitionCause: LaneChangeCauses.Accepted,
                    transitionDetail: "delivery-verdict-stale-repair-2026-10-11",
                    historicalAcceptanceRepair: operatorReason is not null);
                if (outcome.Status == MoveJobStatus.Success)
                    affected++;
                else
                {
                    TaskJsonFile.RemoveField(card.FolderPath, "completionClaim",
                        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
                    _scanner.InvalidateCache();
                    _scanner.PublishCoreFromKnownFolder(card.FolderPath);
                    blocked.Add(new { taskKey = card.TaskKey, reason = outcome.Status.ToString() });
                }
            }
        }
        return Result("delivery-stale-repair-2026-10-11", dry, candidates.Length, affected,
            dry ? $"{candidates.Length} incident cards would return to Delivered."
                : $"Restored {affected} incident cards to Delivered; {blocked.Count} blocked.",
            actor, key, new { candidates = candidates.Select(card => card.TaskKey).ToArray(), blocked });
    }

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
                    && DeliveryLanePolicy.IsAcceptedDeliveryStale(item, status))
                    blocked.Add(new { taskKey = item.TaskKey, category = "stale-acceptance",
                        recoveryAction = "Repeat human review if the accepted delivery changed or is no longer integrated." });
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

    private ManagementCommandResult CreateBackup(bool dry, string actor, string key)
    {
        if (!Directory.Exists(Root)) throw new ManagementException(409, "data-directory-missing");
        var options = Backups.Options;
        if (!options.Configured) throw new ManagementException(409, "backup-not-configured");
        if (dry)
            return Result("backup-create", true, 1, 0,
                $"An encrypted backup of {options.Sources.Count} sources would be created; writes are held while the stores are read.",
                actor, key);
        try
        {
            var record = Backups.CreateAsync(BackupAudit.ManualPrefix + actor, CancellationToken.None).GetAwaiter().GetResult();
            var copyState = record.Upload?.State switch
            {
                "uploaded" => "Copied off-host.",
                "failed" => "Off-host copy failed. The archive remains on this host; wait for Copied before moving it.",
                "pending" => "Off-host copy pending. Wait for Copied before moving this host.",
                _ => "No off-host target is configured. The archive remains on this host.",
            };
            return Result("backup-create", false, 1, 1,
                $"Created encrypted backup {record.Id} ({record.FileCount} files). {copyState}", actor, key, record);
        }
        catch (BackupException ex) { throw new ManagementException(409, ex.Code + ": " + ex.Message); }
    }

    private ManagementCommandResult VerifyRestore(bool dry, string actor, string key, string? backupId)
    {
        var records = Backups.List();
        var summary = backupId is { Length: > 0 }
            ? records.FirstOrDefault(item => item.Id == backupId.Trim())
            : StudioBackupService.NewestGood(records);
        if (summary is null) throw new ManagementException(404, "backup-not-found");
        if (dry) return Result("restore-verify", true, 1, 0, $"Backup {summary.Id} would be restored into an isolated staging host and verified.", actor, key);
        try
        {
            var verification = Backups.VerifyAsync(summary.Id, BackupAudit.ManualPrefix + actor, CancellationToken.None).GetAwaiter().GetResult();
            var verified = verification.Outcome == "verified";
            return Result("restore-verify", false, 1, verified ? 1 : 0,
                verified
                    ? $"Backup {summary.Id} restore verification: verified ({verification.RestoredFiles} files restored)."
                    : $"Backup {summary.Id} restore verification failed: {verification.Detail}",
                actor, key, new { backupId = summary.Id, verification });
        }
        catch (BackupException ex) { throw new ManagementException(409, ex.Code + ": " + ex.Message); }
    }

    /// <summary>
    /// Keeping fewer archives than configured deletes local history the
    /// configuration promises, so only the Owner may apply it (AGT-3737). A
    /// preview deletes nothing and stays open to operators. The refusal is
    /// audited in the control plane chain.
    /// </summary>
    private void RequireRetentionWithinConfiguration(ManagementCommandRequest request, string actor)
    {
        if (request.DryRun || request.RetentionCount is not { } requested || _backups is null) return;
        var keep = Math.Clamp(requested, 1, 500);
        var configured = _backups.Options.RetentionCount;
        if (keep >= configured) return;
        _backups.Audit(BackupAudit.Record(BackupAudit.ManualPrefix + actor, BackupAudit.Retention, BackupAudit.Denied, null,
            $"Retention down to {keep} {(keep == 1 ? "archive" : "archives")} refused: below the configured {configured} needs the Owner role.",
            new { keep, configured }));
        throw new ManagementException(403, "owner-required");
    }

    private ManagementCommandResult ApplyRetention(bool dry, string actor, string key, int? requested)
    {
        var keep = Math.Clamp(requested ?? Backups.Options.RetentionCount, 1, 500);
        var (matched, affected) = Backups.ApplyRetention(keep, dry, BackupAudit.ManualPrefix + actor);
        return Result("backup-retention", dry, matched, affected,
            dry ? $"{matched} backups would be removed; newest {keep} retained." : $"Removed {affected} backups; newest {keep} retained.", actor, key);
    }

    private ManagementCommandResult UploadBackups(bool dry, string actor, string key)
    {
        var overview = Backups.Overview();
        var pending = Backups.List().Count(record => record.Upload?.State != "uploaded");
        if (overview.OffHost.State is "not-configured" or "invalid")
            throw new ManagementException(409, "backup-target-not-configured");
        if (dry) return Result("backup-upload", true, pending, 0, $"{pending} backups would be copied to {overview.OffHost.Target}.", actor, key);
        var uploaded = Backups.UploadPendingAsync(force: true, BackupAudit.ManualPrefix + actor, CancellationToken.None).GetAwaiter().GetResult();
        return Result("backup-upload", false, pending, uploaded, $"Copied {uploaded} of {pending} backups to {overview.OffHost.Target}.", actor, key);
    }

    private static BackupSummary Summary(BackupOverviewItem item) => new(
        item.Id, item.Id + BackupCatalog.ArchiveSuffix, item.SizeBytes, item.CreatedAt, item.Sha256,
        item.Verification, item.FileCount);

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
    private ManagementStatistics ComputeStatistics()
    {
        var files = ManagementStatisticsCache.ScanFiles(Root);
        var jobs = _scanner.ScanAllAutomationJobs();
        return new ManagementStatistics(files.SizeBytes, _projects.List().Count,
            jobs.Count(x => x.State != TaskStates.Archive),
            jobs.Count(x => x.State == TaskStates.Archive),
            files.EventFiles, files.ArtifactFiles, files.LastWriteAt,
            [], DateTime.UtcNow.ToString("O"));
    }
    private static DateTime ReadProcessStartUtc()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }
}

public sealed class ManagementException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
