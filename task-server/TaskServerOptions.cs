namespace AgentStudio.TaskServer;

public sealed class TaskServerOptions
{
    public const string SectionName = "TaskServer";

    public string DataDirectory { get; set; } = "data";
    public string? BackupDirectory { get; set; }
    public string ListenUrl { get; set; } = "http://127.0.0.1:5071";
    public int MinimumLeaseSeconds { get; set; } = 30;
    public int MaximumLeaseSeconds { get; set; } = 900;
    public int ResultRetentionDays { get; set; } = 30;
    public int ResultFinalizationMaxAttempts { get; set; } = 3;
    public bool ResultRefGcEnabled { get; set; } = true;
    public int ResultRefGcSweepMinutes { get; set; } = 360;
    public int ResultRefGcBatchSize { get; set; } = 50;
    public int ResultRefGcDeleteTimeoutSeconds { get; set; } = 60;
    public string GitCommand { get; set; } = "git";
    public int InvariantReconciliationSeconds { get; set; } = 30;
    public int InventoryGraceSeconds { get; set; } = 120;
    public int MaximumEventPayloadBytes { get; set; } = 256 * 1024;
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
    [Obsolete("Use RetentionScheduleHour, which is interpreted in server local time.")]
    public int? RetentionScheduleHourUtc { get; set; }
    public int RetentionSchedulerIntervalMinutes { get; set; } = 60;
    public string? BackupPathFull { get; set; }

    public string ResolveRetentionArchivePath()
        => string.IsNullOrWhiteSpace(RetentionArchivePath)
            ? Path.Combine(ResolveDataDirectory(), "archive")
            : Path.GetFullPath(Path.IsPathRooted(RetentionArchivePath)
                ? RetentionArchivePath
                : Path.Combine(AppContext.BaseDirectory, RetentionArchivePath));

    public string ResolveFullBackupDirectory()
        => string.IsNullOrWhiteSpace(BackupPathFull)
            ? Path.Combine(ResolveBackupDirectory(), "full")
            : Path.GetFullPath(Path.IsPathRooted(BackupPathFull)
                ? BackupPathFull
                : Path.Combine(AppContext.BaseDirectory, BackupPathFull));

    public int ResolveRetentionScheduleHour()
        => Math.Clamp(RetentionScheduleHourUtc ?? RetentionScheduleHour, 0, 23);

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
