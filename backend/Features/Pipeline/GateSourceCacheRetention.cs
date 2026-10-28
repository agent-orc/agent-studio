namespace AgentStudio.Pipeline;

/// <summary>Host-level source cache lease and size bound for outbound pull gate slots.</summary>
internal static class GateSourceCacheRetention
{
    private const long MaximumBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 8;

    internal static async Task<FileStream> LockAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, ct); }
        }
    }

    internal static void Sweep(string root, string? active, DateTime now)
    {
        if (!Directory.Exists(root)) return;
        if (active is not null && Directory.Exists(active)) Directory.SetLastWriteTimeUtc(active, now);
        var entries = Directory.EnumerateDirectories(root)
            .Where(path => Path.GetFileName(path).Length == 64)
            .Select(path => (Path: path, Stamp: Directory.GetLastWriteTimeUtc(path),
                Bytes: Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Sum(file => new FileInfo(file).Length)))
            .OrderBy(entry => entry.Stamp).ToList();
        var bytes = entries.Sum(entry => entry.Bytes);
        while (entries.Count > 0 && (entries.Count > MaximumEntries || bytes > MaximumBytes))
        {
            var victim = entries.FirstOrDefault(entry => entry.Path != active);
            if (victim.Path is null) victim = entries[0];
            try
            {
                using var held = new FileStream(victim.Path + ".lock", FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                Directory.Delete(victim.Path, recursive: true);
                entries.Remove(victim);
                bytes -= victim.Bytes;
            }
            catch (IOException) { entries.Remove(victim); }
        }
    }
}
