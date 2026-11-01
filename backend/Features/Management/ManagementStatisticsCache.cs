namespace AgentStudio.Management;

/// <summary>One low-priority inventory scan shared by all management readers.</summary>
public sealed class ManagementStatisticsCache(Func<ManagementStatistics> scan)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private ManagementStatistics? _snapshot;
    private DateTime _lastAttempt;
    private bool _refreshing;
    private bool _failed;

    public void Invalidate()
    {
        lock (_gate) _lastAttempt = DateTime.MinValue;
    }

    public (ManagementStatistics? Snapshot, string State) Current()
    {
        lock (_gate)
        {
            if (!_refreshing && DateTime.UtcNow - _lastAttempt >= RefreshInterval)
            {
                _refreshing = true;
                _lastAttempt = DateTime.UtcNow;
                var worker = new Thread(Refresh)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = "management-statistics-refresh",
                };
                worker.Start();
            }
            return (_snapshot, _snapshot is null ? (_failed ? "failed" : "pending") : "ready");
        }
    }

    private void Refresh()
    {
        try
        {
            var next = scan();
            lock (_gate)
            {
                _snapshot = next;
                _failed = false;
            }
        }
        catch (Exception ex)
        {
            // A failed inventory must not affect the live management response.
            Console.Error.WriteLine($"Management statistics refresh failed: {ex}");
            lock (_gate) _failed = true;
        }
        finally
        {
            lock (_gate) _refreshing = false;
        }
    }

    public static FileStatistics ScanFiles(string root)
    {
        if (!Directory.Exists(root)) return new(0, 0, 0, null);
        long size = 0;
        long events = 0;
        long artifacts = 0;
        var inspected = 0;
        DateTime lastWrite = default;
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var name = Path.GetFileName(entry);
                    if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if ((attributes & FileAttributes.ReparsePoint) == 0) directories.Push(entry);
                        continue;
                    }
                    var info = new FileInfo(entry);
                    size += info.Length;
                    if (++inspected % 256 == 0) Thread.Yield();
                    var evidence = false;
                    if (entry.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                        || entry.Contains("timeline", StringComparison.OrdinalIgnoreCase))
                    {
                        events++;
                        evidence = true;
                    }
                    if (entry.Contains($"{Path.DirectorySeparatorChar}results{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        || entry.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    {
                        artifacts++;
                        evidence = true;
                    }
                    if (evidence && info.LastWriteTimeUtc > lastWrite) lastWrite = info.LastWriteTimeUtc;
                }
            }
            catch (IOException ex) { SilentCatch.Note(ex, $"management statistics inventory: {directory}"); }
            catch (UnauthorizedAccessException ex) { SilentCatch.Note(ex, $"management statistics inventory: {directory}"); }
        }
        return new(size, events, artifacts, lastWrite == default ? null : lastWrite.ToString("O"));
    }
}

public sealed record FileStatistics(long SizeBytes, long EventFiles, long ArtifactFiles, string? LastWriteAt);

public sealed record ManagementStatistics(
    long SizeBytes, int ProjectCount, int TaskCount, int ArchivedTaskCount,
    long EventFiles, long ArtifactFiles, string? LastWriteAt,
    IReadOnlyList<BackupSummary> Backups, string RefreshedAt);
