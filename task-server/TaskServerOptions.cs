namespace AgentStudio.TaskServer;

public sealed class TaskServerOptions
{
    public const string SectionName = "TaskServer";

    public string DataDirectory { get; set; } = "data";
    public string? BackupDirectory { get; set; }
    public string ListenUrl { get; set; } = "http://127.0.0.1:5071";
    public string? QuotaAdmissionBaseUrl { get; set; }
    public string? QuotaAdmissionBearerToken { get; set; }
    public int MinimumLeaseSeconds { get; set; } = 30;
    public int MaximumLeaseSeconds { get; set; } = 900;
    /// <summary>Shadow is the default. Canary admission requires an explicit operator switch.</summary>
    public string DynamicPlacementMode { get; set; } = "shadow";
    public string[] DynamicPlacementCanaryHostIds { get; set; } = [];
    public int DynamicPlacementWarmPreferenceSeconds { get; set; } = 30;
    /// <summary>
    /// DP-S8 post-work subjects: <c>shadow</c> (default) records subjects and
    /// backend passes without host claims; <c>canary</c> admits host claims for
    /// drained cohorts; <c>off</c> refuses subject materialization.
    /// </summary>
    public string PostWorkMode { get; set; } = "shadow";
    public int ResultRetentionDays { get; set; } = 30;
    public int ResultFinalizationMaxAttempts { get; set; } = 3;
    public bool ResultRefGcEnabled { get; set; } = true;
    public int ResultRefGcSweepMinutes { get; set; } = 360;
    public int ResultRefGcBatchSize { get; set; } = 50;
    public int ResultRefGcDeleteTimeoutSeconds { get; set; } = 60;
    public string GitCommand { get; set; } = "git";
    public int InvariantReconciliationSeconds { get; set; } = 30;
    public int InventoryGraceSeconds { get; set; } = 120;
    public int RunnerInfrastructureFailureBudget { get; set; } = 3;
    public int MaximumEventPayloadBytes { get; set; } = 256 * 1024;
    public long MaxRequestBodyBytes { get; set; } = 25L * 1024 * 1024;
    public long ResultArtifactMaxFileBytes { get; set; } = 8L * 1024 * 1024;
    public long ResultArtifactMaxTotalBytes { get; set; } = 100L * 1024 * 1024;
    /// <summary>Size cap of the derived source data store (AGT-3318); least recently read objects are evicted above it.</summary>
    public long SourceFactsMaxStoredBytes { get; set; } = 512L * 1024 * 1024;
    [Obsolete("Use AUTH=bearer and persisted Task Server principals. Removal planned after Phase B migration.")]
    public bool RequireAuthentication { get; set; }
    [Obsolete("Use STUDIO_AUTH_TOKEN_FILE for one-time bootstrap. Removal planned after Phase B migration.")]
    public string? StudioBearerToken { get; set; }
    [Obsolete("Mint one bound principal per Runner. Removal planned after Phase B migration.")]
    public string? RunnerBearerToken { get; set; }
    public int PrincipalRotationOverlapSeconds { get; set; } = 300;
    public int MaximumPrincipalRotationOverlapSeconds { get; set; } = 3600;
    public string? RetentionArchivePath { get; set; }
    public bool RetentionSchedulerEnabled { get; set; } = true;
    public int RetentionScheduleHour { get; set; } = 3;
    public int RetentionSchedulerIntervalMinutes { get; set; } = 60;
    public double RetentionMaximumLoadPerCore { get; set; } = 1.5;
    public string? BackupPathFull { get; set; }
    /// <summary>Task file and run artifact content above this size goes to the content object store (AGT-3320).</summary>
    public long ContentInlineLimitBytes { get; set; } = 1024 * 1024;
    /// <summary>Content object store root; defaults to <c>&lt;DataDirectory&gt;/objects</c>.</summary>
    public string? ContentObjectStorePath { get; set; }
    /// <summary>Clone of the workspace repository the periodic export writes; unset turns the export job off.</summary>
    public string? WorkspaceExportRoot { get; set; }
    public int WorkspaceExportIntervalMinutes { get; set; } = 15;
    public bool WorkspaceExportPush { get; set; }
    public string WorkspaceExportBranch { get; set; } = "main";
    public int WorkspaceExportPushTimeoutSeconds { get; set; } = 600;
    public string CodexCliTargetVersion { get; set; } = "0.154.0";
    public string ClaudeCliTargetVersion { get; set; } = "2.1.269";

    public string ResolveRetentionArchivePath()
        => string.IsNullOrWhiteSpace(RetentionArchivePath)
            ? Path.Combine(ResolveDataDirectory(), "archive")
            : Path.GetFullPath(Path.IsPathRooted(RetentionArchivePath)
                ? RetentionArchivePath
                : Path.Combine(AppContext.BaseDirectory, RetentionArchivePath));

    public string ResolveContentObjectStorePath()
        => string.IsNullOrWhiteSpace(ContentObjectStorePath)
            ? Path.Combine(ResolveDataDirectory(), "objects")
            : Path.GetFullPath(Path.IsPathRooted(ContentObjectStorePath)
                ? ContentObjectStorePath
                : Path.Combine(AppContext.BaseDirectory, ContentObjectStorePath));

    public string ResolveFullBackupDirectory()
        => string.IsNullOrWhiteSpace(BackupPathFull)
            ? Path.Combine(ResolveBackupDirectory(), "full")
            : Path.GetFullPath(Path.IsPathRooted(BackupPathFull)
                ? BackupPathFull
                : Path.Combine(AppContext.BaseDirectory, BackupPathFull));

    public string ResolveDataDirectory()
    {
        if (string.Equals(DataDirectory, "user-data", StringComparison.OrdinalIgnoreCase))
        {
            var userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(userData, "AgentStudio", "task-server");
        }
        return Path.GetFullPath(Path.IsPathRooted(DataDirectory)
            ? DataDirectory
            : Path.Combine(AppContext.BaseDirectory, DataDirectory));
    }

    public string ResolveBackupDirectory()
        => string.IsNullOrWhiteSpace(BackupDirectory)
            ? Path.Combine(ResolveDataDirectory(), "backups")
            : Path.GetFullPath(Path.IsPathRooted(BackupDirectory)
                ? BackupDirectory
                : Path.Combine(AppContext.BaseDirectory, BackupDirectory));
}
