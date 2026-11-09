using System.Reflection;
using System.Security.Cryptography;
using AgentStudio.Registry;
using AgentStudio.Secrets;

namespace AgentStudio.Management;

public sealed record BackupWarning(string Code, string Severity, string Message);

public sealed record BackupOverviewItem(
    string Id, string CreatedAt, long SizeBytes, string Sha256, int FileCount,
    string Verification, string OffHost, int UnsettledFiles);

public sealed record BackupVerificationOverview(string At, string? BackupId, string Outcome, string? Detail);

public sealed record BackupOffHostOverview(
    string Kind, string? Target, string State, string? LastUploadedId, string? LastUploadedAt, string? LastError);

/// <summary>
/// Backup state for the Operations page and operator agents
/// (<c>GET /api/v1/management/backups</c>). <c>State</c> is <c>ok</c>,
/// <c>warning</c>, <c>failing</c>, <c>not-configured</c> or <c>disabled</c>.
/// </summary>
public sealed record BackupOverview(
    string State,
    bool Configured,
    bool Running,
    string? KeyId,
    string? SecretKeyId,
    double IntervalHours,
    double VerifyIntervalHours,
    double StaleAfterHours,
    int RetentionCount,
    string? NextBackupAt,
    BackupOverviewItem? LastGood,
    double? LastGoodAgeHours,
    bool Stale,
    BackupRunRecord? LastRun,
    BackupVerificationOverview? LastVerification,
    BackupOffHostOverview OffHost,
    IReadOnlyList<BackupWarning> Warnings,
    IReadOnlyList<BackupOverviewItem> Backups,
    string GeneratedAt);

/// <summary>
/// Installation backups (AGT-3574): creates consistent encrypted archives,
/// applies retention, verifies the newest archive by restoring it into an
/// isolated staging host, and copies every archive to the off-host target.
/// One operation runs at a time. The management commands
/// <c>backup-create</c>, <c>restore-verify</c>, <c>backup-retention</c> and
/// <c>backup-upload</c> and the <see cref="BackupHostedService"/> schedule all
/// go through here.
/// </summary>
public sealed class StudioBackupService
{
    private readonly IConfiguration _configuration;
    private readonly BackupWritePause _pause;
    private readonly IKeyEncryptionKeySource? _secretKey;
    private readonly ProjectRegistry? _projects;
    private readonly TimeProvider _clock;
    private readonly ILogger<StudioBackupService>? _logger;
    private readonly Func<BackupOptions, IBackupTarget?> _targets;
    private readonly SemaphoreSlim _run = new(1, 1);

    public StudioBackupService(
        IConfiguration configuration,
        BackupWritePause pause,
        TimeProvider clock,
        IKeyEncryptionKeySource? secretKey = null,
        ProjectRegistry? projects = null,
        ILogger<StudioBackupService>? logger = null,
        IHttpClientFactory? http = null,
        Func<BackupOptions, IBackupTarget?>? targets = null)
    {
        _configuration = configuration;
        _pause = pause;
        _clock = clock;
        _secretKey = secretKey;
        _projects = projects;
        _logger = logger;
        var processes = new BackupProcessRunner();
        _targets = targets ?? (options => BackupTargets.Create(options.Target,
            () => http?.CreateClient("studio-backup-target") ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            processes, clock));
    }

    /// <summary>Test hook: runs after each captured file, inside the write hold.</summary>
    internal Action<string>? AfterCapture { get; set; }

    /// <summary>Test hook: registered task storage paths without a project registry.</summary>
    internal Func<IReadOnlyList<string>>? StorageLocations { get; set; }

    public BackupOptions Options => BackupOptions.From(_configuration);

    public bool IsRunning => _run.CurrentCount == 0;

    /// <summary>How long a held mutation waits for a backup's read phase; read per request, so kept cheap.</summary>
    public TimeSpan MaxRequestHold
        => TimeSpan.FromSeconds(Math.Clamp(_configuration.GetValue("Backup:MaxRequestHoldSeconds", 60), 0, 600));

    public IReadOnlyList<BackupRecord> List() => new BackupCatalog(Options.Directory).List();

    public async Task<BackupRecord> CreateAsync(string trigger, CancellationToken cancellationToken)
    {
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CreateLockedAsync(trigger, cancellationToken).ConfigureAwait(false); }
        finally { _run.Release(); }
    }

    public async Task<BackupVerificationRecord> VerifyAsync(string? backupId, string trigger, CancellationToken cancellationToken)
    {
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => VerifyLocked(backupId, trigger), cancellationToken).ConfigureAwait(false); }
        finally { _run.Release(); }
    }

    public async Task<int> UploadPendingAsync(bool force, CancellationToken cancellationToken)
    {
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await UploadLockedAsync(force, cancellationToken).ConfigureAwait(false); }
        finally { _run.Release(); }
    }

    /// <summary>Keeps the newest <paramref name="keep"/> local archives. Retention at the off-host target is configured there.</summary>
    public (int Matched, int Removed) ApplyRetention(int? keep, bool dryRun)
    {
        var options = Options;
        var count = Math.Clamp(keep ?? options.RetentionCount, 1, 500);
        var catalog = new BackupCatalog(options.Directory);
        var surplus = catalog.List().Skip(count).ToArray();
        if (dryRun) return (surplus.Length, 0);
        _run.Wait();
        try
        {
            surplus = catalog.List().Skip(count).ToArray();
            foreach (var record in surplus) catalog.Delete(record.Id);
            if (surplus.Length > 0 || catalog.List().Count > 0)
                catalog.WriteState(catalog.ReadState() with { LastRetentionAt = _clock.GetUtcNow().ToString("O") });
            return (surplus.Length, surplus.Length);
        }
        finally { _run.Release(); }
    }

    /// <summary>One scheduler tick: back up when due, retry pending uploads, verify when due.</summary>
    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var options = Options;
        // A configured but missing key is reported by the overview; retrying every tick adds nothing.
        if (!options.Configured || !File.Exists(options.KeyFile)) return;
        var catalog = new BackupCatalog(options.Directory);
        if (BackupSchedulePolicy.BackupDue(_clock.GetUtcNow(), options, catalog.ReadState(), catalog.List().FirstOrDefault()))
        {
            try { await CreateAsync("schedule", cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError("backup-scheduled-run-failed {Reason}", ex.Message);
            }
        }
        try { await UploadPendingAsync(force: false, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError("backup-upload-failed {Reason}", ex.Message);
        }
        if (BackupSchedulePolicy.VerifyDue(_clock.GetUtcNow(), options, catalog.ReadState(), NewestGood(catalog.List())))
        {
            try { await VerifyAsync(null, "schedule", cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError("backup-scheduled-verification-failed {Reason}", ex.Message);
            }
        }
    }

    public BackupOverview Overview()
    {
        var options = Options;
        var now = _clock.GetUtcNow();
        var catalog = new BackupCatalog(options.Directory);
        var records = catalog.List();
        var state = catalog.ReadState();
        IBackupTarget? target = null;
        string? targetProblem = null;
        try { target = _targets(options); }
        catch (BackupException ex) { targetProblem = ex.Message; }

        var newestGood = NewestGood(records);
        var lastGood = newestGood is null ? null : Item(newestGood);
        double? ageHours = newestGood is null ? null
            : Math.Round((now - BackupSchedulePolicy.Parse(newestGood.CreatedAt)).TotalHours, 2);
        var stale = options.Configured && (newestGood is null
            ? state.LastRun is not null
            : now - BackupSchedulePolicy.Parse(newestGood.CreatedAt) > options.StaleAfter);
        var lastUploaded = records.FirstOrDefault(record => record.Upload?.State == "uploaded");
        var offHostState = target is null ? targetProblem is null ? "not-configured" : "invalid"
            : newestGood is null ? "idle"
            : newestGood.Upload?.State switch
            {
                "uploaded" => "current",
                "failed" => "failed",
                _ => "pending",
            };
        var offHost = new BackupOffHostOverview(
            target?.Kind ?? options.Target.Kind, target?.Description, offHostState,
            lastUploaded?.Id, lastUploaded?.Upload?.UploadedAt,
            targetProblem ?? newestGood?.Upload?.Error);

        var warnings = new List<BackupWarning>();
        if (!options.Enabled)
            warnings.Add(new("backup-disabled", "warning", "Scheduled backups are switched off (Backup:Enabled is false)."));
        else if (!options.Configured)
            warnings.Add(new("backup-not-configured", "warning",
                "Backups are off: no backup key is configured (Backup:KeyFile). See the backup runbook."));
        if (options.Configured && !File.Exists(options.KeyFile))
            warnings.Add(new("backup-key-missing", "danger",
                "The backup key file is missing, so no backup can be made. Create it as the backup runbook describes."));
        if (options.Configured)
        {
            if (newestGood is null)
                warnings.Add(new("backup-missing", state.LastRun is null ? "warning" : "danger", "No good backup exists yet."));
            else if (stale)
                warnings.Add(new("backup-stale", "danger",
                    $"The newest good backup is {ageHours:0.#} hours old; the threshold is {options.StaleAfter.TotalHours:0.#} hours."));
            if (state.LastRun is { Outcome: "failed" } failed)
                warnings.Add(new("backup-failed", "danger", $"The last backup run failed: {failed.Error}"));
            if (state.LastVerification is { Outcome: "failed" } failedVerification)
                warnings.Add(new("verification-failed", "danger", $"The last restore verification failed: {failedVerification.Error}"));
            else if (newestGood is not null
                     && now - BackupSchedulePolicy.Parse(newestGood.CreatedAt) > TimeSpan.FromHours(1)
                     && (state.LastVerification is null
                         || now - BackupSchedulePolicy.Parse(state.LastVerification.At) > options.VerifyInterval * 2))
                warnings.Add(new("verification-overdue", "warning", "No restore verification ran within two verification intervals."));
            switch (offHostState)
            {
                case "not-configured":
                    warnings.Add(new("offhost-not-configured", "warning",
                        "No off-host target is configured: backups stay on this host only."));
                    break;
                case "invalid":
                    warnings.Add(new("offhost-invalid", "danger", $"The off-host target is misconfigured: {targetProblem}"));
                    break;
                case "failed":
                    warnings.Add(new("offhost-failed", "danger", $"The off-host copy failed: {newestGood?.Upload?.Error}"));
                    break;
                case "pending" when newestGood is not null
                                    && now - BackupSchedulePolicy.Parse(newestGood.CreatedAt) > TimeSpan.FromHours(1):
                    warnings.Add(new("offhost-pending", "warning", "The newest backup has not reached the off-host target yet."));
                    break;
            }
        }
        var overall = !options.Enabled ? "disabled"
            : !options.Configured ? "not-configured"
            : warnings.Any(warning => warning.Severity == "danger") ? "failing"
            : warnings.Count > 0 ? "warning"
            : "ok";
        BackupVerificationOverview? verification = state.LastVerification is { } last
            ? new(last.At, last.BackupId, last.Outcome, last.Error)
            : null;
        return new BackupOverview(
            overall, options.Configured, IsRunning, SafeKeyId(options), SecretKeyId(),
            options.Interval.TotalHours, options.VerifyInterval.TotalHours, options.StaleAfter.TotalHours,
            options.RetentionCount,
            BackupSchedulePolicy.NextBackupAt(options, state, records.FirstOrDefault()) is { } next
                ? (next < now ? now : next).ToString("O") : null,
            lastGood, ageHours, stale, state.LastRun, verification, offHost, warnings,
            records.Take(20).Select(Item).ToArray(), now.ToString("O"));
    }

    private async Task<BackupRecord> CreateLockedAsync(string trigger, CancellationToken cancellationToken)
    {
        var options = Options;
        var catalog = new BackupCatalog(options.Directory);
        var started = _clock.GetUtcNow();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var id = "studio-backup-" + started.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'") + "-"
                 + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
        try
        {
            if (!options.Enabled) throw new BackupException("backup-disabled", "Backups are switched off (Backup:Enabled is false).");
            if (!options.Configured)
                throw new BackupException("backup-not-configured", "No backup key is configured (Backup:KeyFile).");
            Validate(options);
            var key = BackupKey.Load(options.KeyFile!);
            var secretKeyId = SecretKeyId();
            var fingerprints = new List<Func<byte[], bool>> { material => BackupKey.Fingerprint(material) == key.Id };
            if (secretKeyId is not null)
                fingerprints.Add(material => new KeyEncryptionKey(material, "backup-probe").Id == secretKeyId);
            var storage = StorageLocations?.Invoke() ?? _projects?.List().Select(project => project.StorageLocation)
                .OfType<string>().Where(path => path.Length > 0).ToArray() ?? [];
            var request = new BackupCollectRequest(id, started, options, key, secretKeyId, fingerprints, storage, StudioVersion());
            var collector = new BackupCollector(catalog, _pause) { AfterCapture = AfterCapture };
            var record = await Task.Run(() => collector.CreateAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);
            IBackupTarget? target = null;
            try { target = _targets(options); }
            catch (BackupException ex)
            {
                // The archive is good; the overview reports the misconfigured target.
                _logger?.LogError("backup-target-invalid {Reason}", ex.Message);
            }
            if (target is not null)
            {
                record = record with { Upload = new BackupUploadRecord(target.Description, "pending", null, null, null, 0) };
                catalog.Save(record);
            }
            catalog.WriteState(catalog.ReadState() with
            {
                LastRun = new BackupRunRecord(started.ToString("O"), trigger, "succeeded", id, null, watch.ElapsedMilliseconds),
            });
            _logger?.LogInformation("backup-created {BackupId} {SizeBytes} {Files}", record.Id, record.SizeBytes, record.FileCount);
            var surplus = catalog.List().Skip(options.RetentionCount).ToArray();
            foreach (var old in surplus) catalog.Delete(old.Id);
            if (surplus.Length > 0)
                catalog.WriteState(catalog.ReadState() with { LastRetentionAt = _clock.GetUtcNow().ToString("O") });
            return record;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                catalog.WriteState(catalog.ReadState() with
                {
                    LastRun = new BackupRunRecord(started.ToString("O"), trigger, "failed", null, ex.Message, watch.ElapsedMilliseconds),
                });
            }
            catch (Exception stateFailure) when (stateFailure is IOException or UnauthorizedAccessException)
            {
                _logger?.LogError("backup-state-write-failed {Reason}", stateFailure.Message);
            }
            throw;
        }
    }

    private BackupVerificationRecord VerifyLocked(string? backupId, string trigger)
    {
        var options = Options;
        var catalog = new BackupCatalog(options.Directory);
        var records = catalog.List();
        var record = backupId is { Length: > 0 }
            ? records.FirstOrDefault(item => item.Id == backupId) ?? throw new BackupException("backup-not-found", $"Backup {backupId} does not exist.")
            : NewestGood(records) ?? throw new BackupException("backup-not-found", "No backup exists to verify.");
        var started = _clock.GetUtcNow();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var staging = Path.Combine(catalog.StagingDirectory, "restore-" + record.Id);
        BackupVerificationRecord verification;
        try
        {
            if (!options.Configured) throw new BackupException("backup-not-configured", "No backup key is configured (Backup:KeyFile).");
            var key = BackupKey.Load(options.KeyFile!);
            var archive = catalog.ArchivePath(record.Id);
            string actual;
            using (var stream = File.OpenRead(archive)) actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!string.Equals(actual, record.Sha256, StringComparison.Ordinal))
                throw new BackupException("backup-checksum-mismatch", "The archive no longer matches the checksum recorded at creation.");
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            OwnerOnlyAccess.CreateDirectory(catalog.StagingDirectory);
            var result = BackupRestore.Restore(archive, key,
                new BackupRestorePlan(new Dictionary<string, string>(), CloneCheckouts: false, EverySourceUnder: staging));
            var expected = result.Contents.Files.Count(file => !file.Path.StartsWith(BackupArchive.BundlePrefix, StringComparison.Ordinal));
            if (result.FilesRestored != expected)
                throw new BackupException("backup-restore-incomplete", $"Restored {result.FilesRestored} of {expected} files.");
            verification = new BackupVerificationRecord(started.ToString("O"), "verified",
                $"Restored {result.FilesRestored} files and {result.Manifest.Checkouts.Count} checkout records into an empty staging host; every hash matched.",
                result.FilesRestored);
        }
        catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException
                                       or InvalidDataException or System.Text.Json.JsonException or FormatException)
        {
            verification = new BackupVerificationRecord(started.ToString("O"), "failed", ex.Message, 0);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        catalog.Save(record with { Verification = verification });
        catalog.WriteState(catalog.ReadState() with
        {
            LastVerification = new BackupRunRecord(verification.At, trigger, verification.Outcome, record.Id,
                verification.Outcome == "verified" ? null : verification.Detail, watch.ElapsedMilliseconds),
        });
        _logger?.LogInformation("backup-verified {BackupId} {Outcome}", record.Id, verification.Outcome);
        return verification;
    }

    private async Task<int> UploadLockedAsync(bool force, CancellationToken cancellationToken)
    {
        var options = Options;
        if (!options.Configured) return 0;
        var target = _targets(options);
        if (target is null) return 0;
        var catalog = new BackupCatalog(options.Directory);
        var uploaded = 0;
        foreach (var record in catalog.List())
        {
            if (record.Upload?.State == "uploaded") continue;
            if (!force && !BackupSchedulePolicy.UploadDue(_clock.GetUtcNow(), record.Upload)) continue;
            var attempt = _clock.GetUtcNow().ToString("O");
            var attempts = (record.Upload?.Attempts ?? 0) + 1;
            try
            {
                await target.UploadAsync(catalog.ArchivePath(record.Id), record.FileName, cancellationToken).ConfigureAwait(false);
                await target.UploadAsync(catalog.ChecksumPath(record.Id), record.FileName + ".sha256", cancellationToken)
                    .ConfigureAwait(false);
                catalog.Save(record with
                {
                    Upload = new BackupUploadRecord(target.Description, "uploaded", _clock.GetUtcNow().ToString("O"), attempt, null, attempts),
                });
                uploaded++;
                _logger?.LogInformation("backup-uploaded {BackupId} {Target}", record.Id, target.Kind);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                catalog.Save(record with
                {
                    Upload = new BackupUploadRecord(target.Description, "failed", null, attempt, ex.Message, attempts),
                });
                _logger?.LogError("backup-upload-failed {BackupId} {Reason}", record.Id, ex.Message);
            }
        }
        return uploaded;
    }

    /// <summary>The newest archive whose verification did not fail.</summary>
    public static BackupRecord? NewestGood(IReadOnlyList<BackupRecord> records)
        => records.FirstOrDefault(record => record.Verification?.Outcome != "failed");

    private static void Validate(BackupOptions options)
    {
        foreach (var source in options.Sources)
        {
            if (BackupCollector.Inside(options.Directory, source.Path))
                throw new BackupException("backup-directory-inside-source",
                    $"The backup directory must lie outside the backed-up source {source.Name}.");
            if (BackupCollector.Inside(options.KeyFile!, source.Path))
                throw new BackupException("backup-key-inside-source",
                    $"The backup key must not lie inside the backed-up source {source.Name}.");
        }
        if (BackupCollector.Inside(options.KeyFile!, options.Directory))
            throw new BackupException("backup-key-inside-backup-directory", "The backup key must not lie next to the backups.");
        if (options.SecretKeyFile is not null
            && string.Equals(Path.GetFullPath(options.SecretKeyFile), options.KeyFile, StringComparison.Ordinal))
            throw new BackupException("backup-key-is-secret-key", "The backup key must not be the secret store key.");
        if (options.Target.Kind == "directory" && options.Target.Directory is { } target
            && (BackupCollector.Inside(target, options.Directory) || options.Sources.Any(source => BackupCollector.Inside(target, source.Path))))
            throw new BackupException("backup-target-not-off-host", "The directory target must not lie in the backup directory or a source.");
    }

    private string? SecretKeyId()
    {
        if (_secretKey is null) return null;
        try { return _secretKey.Load(createIfMissing: false).Id; }
        catch (Exception ex) when (ex is SecretKeyUnavailableException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? SafeKeyId(BackupOptions options)
    {
        if (!options.Configured) return null;
        try { return BackupKey.Load(options.KeyFile!).Id; }
        catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException or SecretKeyUnavailableException)
        {
            return null;
        }
    }

    private static BackupOverviewItem Item(BackupRecord record) => new(
        record.Id, record.CreatedAt, record.SizeBytes, record.Sha256, record.FileCount,
        record.Verification?.Outcome ?? "unverified",
        record.Upload?.State ?? "not-configured",
        record.Consistency.Unsettled.Count);

    private static string StudioVersion()
    {
        var assembly = typeof(StudioBackupService).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
