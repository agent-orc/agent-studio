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
        // Warm mirrors own their objects and do not depend on the source cache surviving.
        // Bound their count and age independently; a live check holds this same file lease.
        var mirrors = Directory.EnumerateDirectories(root, "*.integration.git")
            .OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray();
        for (var index = 0; index < mirrors.Length; index++)
        {
            var mirror = mirrors[index];
            if (index < MaximumEntries && now - Directory.GetLastWriteTimeUtc(mirror) < TimeSpan.FromHours(24)) continue;
            try
            {
                using var held = new FileStream(mirror + ".lock", FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                var workspace = BuildTestGateRunner.WarmWorkspacePath(mirror);
                if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
                Directory.Delete(mirror, recursive: true);
            }
            catch (IOException exception) { SilentCatch.Note(exception, "Warm integration cache eviction deferred while unavailable"); }
        }
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
