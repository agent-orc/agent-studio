using AgentStudio.TaskServer.Contracts;

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
    public int ReviewFailureMaxRetries { get; set; } = ReviewFailureRetryPolicy.DefaultMaxRetries;
    public bool ResultRefGcEnabled { get; set; } = true;
    public int ResultRefGcSweepMinutes { get; set; } = 360;
    public int ResultRefGcBatchSize { get; set; } = 50;
    public int ResultRefGcDeleteTimeoutSeconds { get; set; } = 60;
    public string GitCommand { get; set; } = "git";
    public int InvariantReconciliationSeconds { get; set; } = 30;
    public int InventoryGraceSeconds { get; set; } = 120;
    public int MaximumEventPayloadBytes { get; set; } = 256 * 1024;
    public bool RequireAuthentication { get; set; }
    public string? StudioBearerToken { get; set; }
    public string? RunnerBearerToken { get; set; }

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
