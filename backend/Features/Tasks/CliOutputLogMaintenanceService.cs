namespace AgentStudio.Tasks;

/// <summary>
/// Boot-time compatibility sweep for logs created before bounded writes were
/// introduced. It also keeps the one rotation file out of workspace evidence
/// commits by maintaining the central task repository's ignore rule.
/// </summary>
internal sealed class CliOutputLogMaintenanceService
{
    private readonly TaskScannerService _scanner;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CliOutputLogMaintenanceService> _logger;

    public CliOutputLogMaintenanceService(
        TaskScannerService scanner,
        IConfiguration configuration,
        ILogger<CliOutputLogMaintenanceService> logger)
    {
        _scanner = scanner;
        _configuration = configuration;
        _logger = logger;
    }

    internal CliOutputLogMaintenanceResult Run()
    {
        var migrated = 0;
        var failed = 0;
        var paths = _scanner.ScanAllJobs()
            .Select(job => TaskPaths.CliOutputLog(job.FolderPath))
            .Where(File.Exists)
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToList();

        foreach (var path in paths)
        {
            try
            {
                if (CliOutputLogFile.MigrateExisting(path)) migrated++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "cli-output-log-migration-failed path={Path}", path);
            }
        }

        var ignoreUpdated = false;
        var runtimeDeleted = 0;
        var taskRepository = _configuration["TaskRepository"];
        if (!string.IsNullOrWhiteSpace(taskRepository) && Directory.Exists(taskRepository))
        {
            try
            {
                ignoreUpdated = EnsureRotationIgnored(taskRepository);
                runtimeDeleted = DeleteExpiredRuntimeFiles(taskRepository, DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "cli-output-log-ignore-migration-failed repository={Repository}", taskRepository);
            }
        }

        _logger.LogInformation(
            "cli-output-log-maintenance scanned={Scanned} migrated={Migrated} failed={Failed} ignoreUpdated={IgnoreUpdated} runtimeDeleted={RuntimeDeleted}",
            paths.Count, migrated, failed, ignoreUpdated, runtimeDeleted);
        return new CliOutputLogMaintenanceResult(paths.Count, migrated, failed, ignoreUpdated, runtimeDeleted);
    }

    internal static bool EnsureRotationIgnored(string taskRepository)
    {
        var ignorePath = Path.Combine(Path.GetFullPath(taskRepository), ".gitignore");
        var existing = File.Exists(ignorePath) ? File.ReadAllText(ignorePath) : string.Empty;
        var lines = existing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var rules = new[]
        {
            CliOutputLogFile.RotationIgnorePattern,
            "/logs/bus/",
            "/.metadata/attempt-authority*",
            "/.runtime/",
        }.Where(rule => !lines.Contains(rule)).ToList();
        if (rules.Count == 0) return false;

        var prefix = existing.Length == 0 || existing.EndsWith('\n') ? string.Empty : Environment.NewLine;
        File.AppendAllText(ignorePath, prefix
            + "# Runtime retention data is never committed; cli-output.log remains the durable audit record."
            + Environment.NewLine + string.Join(Environment.NewLine, rules) + Environment.NewLine);
        return true;
    }

    internal static int DeleteExpiredRuntimeFiles(string taskRepository, DateTimeOffset now)
    {
        var deleted = 0;
        var bus = Path.Combine(taskRepository, "logs", "bus");
        if (Directory.Exists(bus))
            deleted += DeleteOlderThan(Directory.EnumerateFiles(bus, "*", SearchOption.AllDirectories), now.AddDays(-30));
        var metadata = Path.Combine(taskRepository, ".metadata");
        if (Directory.Exists(metadata))
            deleted += DeleteOlderThan(Directory.EnumerateFiles(metadata, "attempt-authority.archive-*.json"), now.AddDays(-90));
        return deleted;
    }

    private static int DeleteOlderThan(IEnumerable<string> paths, DateTimeOffset cutoff)
    {
        var deleted = 0;
        foreach (var path in paths)
        {
            if (new FileInfo(path).LastWriteTimeUtc >= cutoff.UtcDateTime) continue;
            File.Delete(path);
            deleted++;
        }
        return deleted;
    }
}

internal sealed record CliOutputLogMaintenanceResult(
    int Scanned,
    int Migrated,
    int Failed,
    bool IgnoreUpdated,
    int RuntimeDeleted);
