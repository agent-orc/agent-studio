namespace AgentStudio.TaskServer.Contracts;

/// <summary>A file system used by this installation, measured on the reporting host.</summary>
public sealed record HostFilesystemUsageDto(string Purpose, string Path, long FreeBytes, long TotalBytes,
    IReadOnlyDictionary<string, long>? OwnedBytes = null);

public enum HostDiskPressureLevel { Healthy, Warning, Critical }

public sealed record HostDiskPressureThresholds(long WarningFreeBytes = 30L * 1024 * 1024 * 1024,
    double WarningFreePercent = 15, long CriticalFreeBytes = 8L * 1024 * 1024 * 1024,
    double CriticalFreePercent = 5);

public sealed record HostDiskPressureDecision(HostDiskPressureLevel Level, string Reason,
    HostFilesystemUsageDto? Filesystem);

/// <summary>Critical admission stays closed until every measured file system clears warning.</summary>
public static class HostDiskPressurePolicy
{
    public static HostDiskPressureDecision Decide(IReadOnlyList<HostFilesystemUsageDto>? filesystems,
        HostDiskPressureThresholds thresholds, bool previouslyCritical = false)
    {
        if (filesystems is null || filesystems.Count == 0)
            return new(HostDiskPressureLevel.Healthy, "Disk space has not been reported.", null);
        foreach (var filesystem in filesystems)
        {
            if (filesystem.TotalBytes <= 0) continue;
            var percent = 100d * filesystem.FreeBytes / filesystem.TotalBytes;
            var critical = filesystem.FreeBytes <= thresholds.CriticalFreeBytes
                || percent <= thresholds.CriticalFreePercent;
            var warning = filesystem.FreeBytes <= thresholds.WarningFreeBytes
                || percent <= thresholds.WarningFreePercent;
            if (critical || (previouslyCritical && warning))
                return new(HostDiskPressureLevel.Critical, Describe(filesystem, percent, "critical"), filesystem);
        }
        foreach (var filesystem in filesystems)
        {
            if (filesystem.TotalBytes <= 0) continue;
            var percent = 100d * filesystem.FreeBytes / filesystem.TotalBytes;
            if (filesystem.FreeBytes <= thresholds.WarningFreeBytes || percent <= thresholds.WarningFreePercent)
                return new(HostDiskPressureLevel.Warning, Describe(filesystem, percent, "low"), filesystem);
        }
        return new(HostDiskPressureLevel.Healthy, "Disk space recovered above the warning level.", null);
    }

    private static string Describe(HostFilesystemUsageDto filesystem, double percent, string level)
    {
        var consumers = filesystem.OwnedBytes is { Count: > 0 }
            ? "; product consumers: " + string.Join(", ", filesystem.OwnedBytes
                .OrderByDescending(entry => entry.Value)
                .Select(entry => $"{entry.Key} {entry.Value / 1073741824d:0.0} GiB"))
            : "";
        return $"{filesystem.Purpose} file system at {filesystem.Path} is {level}: "
            + $"{filesystem.FreeBytes / 1073741824d:0.0} GiB free ({percent:0.0}%){consumers}.";
    }
}

/// <summary>Reads only configured installation paths and never follows directory links.</summary>
public static class HostFilesystemProbe
{
    public static IReadOnlyList<HostFilesystemUsageDto> Measure(
        IEnumerable<(string Purpose, string Path)> paths,
        IEnumerable<(string Name, string Path)>? ownedDirectories = null)
    {
        var drives = DriveInfo.GetDrives().Where(drive => drive.IsReady)
            .OrderByDescending(drive => drive.RootDirectory.FullName.Length).ToArray();
        var consumers = ownedDirectories?.ToArray() ?? [];
        var result = new List<HostFilesystemUsageDto>();
        foreach (var (purpose, configuredPath) in paths)
        {
            if (string.IsNullOrWhiteSpace(configuredPath)) continue;
            try
            {
                var path = Path.GetFullPath(configuredPath);
                var drive = drives.FirstOrDefault(candidate => IsUnder(path, candidate.RootDirectory.FullName));
                if (drive is null || result.Any(item => item.Path == drive.RootDirectory.FullName)) continue;
                var owned = consumers.Where(item => !string.IsNullOrWhiteSpace(item.Path)
                        && IsUnder(Path.GetFullPath(item.Path), drive.RootDirectory.FullName))
                    .ToDictionary(item => item.Name, item => DirectoryBytes(item.Path), StringComparer.Ordinal);
                result.Add(new HostFilesystemUsageDto(purpose, drive.RootDirectory.FullName,
                    drive.AvailableFreeSpace, drive.TotalSize, owned));
            }
            catch (Exception) { /* A missing mount must not stop heartbeat delivery. */ }
        }
        return result;
    }

    private static bool IsUnder(string path, string root)
        => path.Equals(root, StringComparison.Ordinal)
            || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static long DirectoryBytes(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long bytes = 0;
        var remaining = new Stack<string>();
        remaining.Push(root);
        var visited = 0;
        while (remaining.Count > 0 && visited < 250_000)
        {
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(remaining.Pop()); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (++visited >= 250_000) break;
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0) remaining.Push(entry);
                    else bytes += new FileInfo(entry).Length;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return bytes;
    }
}
