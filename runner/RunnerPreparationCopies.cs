using System.Runtime.InteropServices;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Owns the writable preparation copies in this runner's work directory.</summary>
internal sealed class RunnerPreparationCopies(RunnerOptions options, RunnerStateStore state, Action<string> log)
{
    internal const long DefaultMaxBytes = 20_000_000_000;
    private const string ReleasePending = ".release-pending";
    private static readonly StringComparer Paths = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private long _bytes = -1;

    public long? Bytes
    {
        get
        {
            var bytes = Interlocked.Read(ref _bytes);
            return bytes < 0 ? null : bytes;
        }
    }

    private static string Runs(string project) =>
        Path.Combine(Path.GetFullPath(project), "caches", "preparation", ProjectPreparationCacheSweep.RunsDirectoryName);

    internal static string? RootFor(string workDir, PersistedRunnerSlot slot)
    {
        var runs = Runs(GitWorkspace.CachePathForProject(workDir, slot.ProjectId));
        if (slot.PreparationCopyId is { } id) return Path.Combine(runs, id.ToString("N"));

        // Slots written before the copy id existed have their binding in the durable worker spec.
        var environment = DurableAgentProcess.TryReadEnvironment(slot.WorkerDirectory);
        if (environment is null) return null;
        foreach (var key in CacheVariables)
        {
            if (!environment.TryGetValue(key, out var path) || !Path.IsPathFullyQualified(path)) continue;
            var root = Directory.GetParent(path)?.FullName;
            if (root is not null && Guid.TryParseExact(Path.GetFileName(root), "N", out _)
                && IsSameDirectory(Path.GetDirectoryName(root)!, runs)) return root;
        }
        return null;
    }

    private static readonly string[] CacheVariables = ["NUGET_PACKAGES", "NPM_CONFIG_CACHE", "PLAYWRIGHT_BROWSERS_PATH"];

    internal static void Release(string workDir, PersistedRunnerSlot slot, Action<string> log)
    {
        if (RootFor(workDir, slot) is { } path) Release(path, log);
    }

    private static void Release(string path, Action<string> log)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            TryRelease(path, ProcessReferences([path]).Contains(path), log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"preparation-copy-release-failed path={path} error={ex.Message}");
        }
    }

    internal static async Task<T> DuringAttemptAsync<T>(string workDir, PersistedRunnerSlot slot,
        RunnerStateStore state, CancellationToken daemonShutdown, Func<Task<T>> run, Action<string> log)
    {
        // Capture the legacy worker binding before finalization can remove its spec.
        var path = RootFor(workDir, slot);
        try { return await run(); }
        finally
        {
            if (path is not null && Directory.Exists(path))
            {
                try
                {
                    var latest = state.LoadAll().FirstOrDefault(item => item.AttemptId == slot.AttemptId);
                    if (latest is null || !StillUsesCopy(latest, daemonShutdown))
                        Release(path, log);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    log($"preparation-copy-owner-unavailable path={path} error={ex.Message}");
                }
            }
        }
    }

    private static bool StillUsesCopy(PersistedRunnerSlot slot, CancellationToken daemonShutdown)
    {
        DurableAgentProcess.TryRecoverIdentity(slot, out var worker, out _);
        var observation = DurableAgentProcess.InspectForReattach(worker);
        // A handed-off result may still trigger an in-attempt same-session resume.
        return observation.IsLive || (daemonShutdown.IsCancellationRequested && observation.Result is not null);
    }

    private static bool TryRelease(string path, bool referenced, Action<string> log)
    {
        if (!referenced && TryDelete(path, log)) return true;
        // Startup and pressure can discover an orphan whose terminal cleanup never ran.
        // Retry it below the limit too, including after a partial delete or runner restart.
        try
        {
            using var marker = File.Open(Path.Combine(path, ReleasePending), FileMode.OpenOrCreate,
                FileAccess.Write, FileShare.ReadWrite);
            log($"preparation-copy-release-deferred path={path}");
        }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"preparation-copy-release-marker-failed path={path} error={ex.Message}");
        }
        return false;
    }

    private static bool TryDelete(string path, Action<string> log)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"preparation-copy-release-failed path={path} error={ex.Message}");
            return false;
        }
    }

    public void Sweep(bool startup)
    {
        try
        {
            var disk = HostFilesystemProbes.Default.Measure([("runner work", options.WorkDir)]).FirstOrDefault();
            var limit = disk is { TotalBytes: > 0 }
                ? Math.Min(options.PreparationCopiesMaxBytes, (long)(disk.TotalBytes * 0.15m))
                : options.PreparationCopiesMaxBytes;
            Interlocked.Exchange(ref _bytes, Sweep(options.WorkDir, state.LoadAll, limit, startup, log));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            log($"preparation-copy-sweep-failed error={ex.Message}");
        }
    }

    public async Task MonitorAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stop)) Sweep(startup: false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    internal static long Sweep(string workDir, Func<IReadOnlyList<PersistedRunnerSlot>> slots,
        long limit, bool startup, Action<string> log)
    {
        var roots = new List<(DirectoryInfo Directory, long Bytes, bool Pending)>();
        if (PhysicalDirectory(Path.GetFullPath(workDir)) is not { } physicalWork) return 0;
        var work = new DirectoryInfo(physicalWork);
        if (!work.Exists) return 0;
        var scanned = new HashSet<string>(Paths);
        foreach (var project in work.EnumerateDirectories().Prepend(work))
        {
            var cache = new DirectoryInfo(Runs(project.FullName));
            if (!cache.Exists || new[] { project, cache.Parent!.Parent!, cache.Parent }
                .Any(dir => dir.Attributes.HasFlag(FileAttributes.ReparsePoint))) continue;
            // A .runs link can relocate this store or share it across projects.
            if (PhysicalDirectory(cache.FullName) is not { } physicalRuns || !scanned.Add(physicalRuns)) continue;
            foreach (var dir in new DirectoryInfo(physicalRuns).EnumerateDirectories())
            {
                if (!Guid.TryParseExact(dir.Name, "N", out _)
                    || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                roots.Add((dir, Measure(dir), File.Exists(Path.Combine(dir.FullName, ReleasePending))));
            }
        }

        var total = roots.Sum(root => root.Bytes);
        if (roots.Count == 0 || (!startup && total <= limit && !roots.Any(root => root.Pending))) return total;
        // A slot reserves its unique id before creating any files. Reading the slots
        // after listing candidates closes the race with a concurrently starting attempt.
        var referenced = slots().Select(slot => RootFor(workDir, slot)).OfType<string>()
            .Select(path => PhysicalDirectory(path) ?? path).ToHashSet(Paths);
        var candidates = roots.Where(root => !referenced.Contains(root.Directory.FullName))
            .OrderBy(root => root.Directory.LastWriteTimeUtc).ThenBy(root => root.Directory.FullName, Paths).ToArray();
        if (candidates.Length == 0) return total;
        var processReferences = ProcessReferences(candidates.Select(root => root.Directory.FullName).ToArray());
        foreach (var (dir, bytes, pending) in candidates)
        {
            if (!pending && !startup && total <= limit) continue;
            if (!TryRelease(dir.FullName, processReferences.Contains(dir.FullName), log)) continue;
            total -= bytes;
            log($"preparation-copy-removed path={dir.FullName} bytes={bytes}");
        }
        return total;
    }

    private static long Measure(DirectoryInfo root)
    {
        long bytes = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var file in root.EnumerateFiles("*", options))
            {
                try { bytes += file.Length; }
                catch (FileNotFoundException) { }
            }
        }
        catch (DirectoryNotFoundException) { }
        return bytes;
    }

    // On Linux realpath(path, NULL) resolves all symlinked ancestors. The string
    // return marshaller copies and frees the buffer allocated by libc.
    [DllImport("libc", EntryPoint = "realpath")]
    [return: MarshalAs(UnmanagedType.LPUTF8Str)]
    private static extern string? RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint buffer);

    private static bool IsSameDirectory(string first, string second) =>
        Paths.Equals(first, second)
        || (PhysicalDirectory(first) is { } physical && Paths.Equals(physical, PhysicalDirectory(second)));

    private static string? PhysicalDirectory(string path)
    {
        if (OperatingSystem.IsLinux()) return RealPath(path, 0);
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) return null;
        if (directory.Parent is { } parent)
        {
            if (PhysicalDirectory(parent.FullName) is not { } physicalParent) return null;
            directory = new DirectoryInfo(Path.Combine(physicalParent, directory.Name));
        }
        return directory.ResolveLinkTarget(returnFinalTarget: true) is { } target
            ? PhysicalDirectory(target.FullName)
            : directory.FullName;
    }

    private static string? ResolveExistingPrefix(string path)
    {
        // A binding or argument can name a cache subdirectory the tool has yet to create.
        for (string? prefix = path; prefix is not null; prefix = Path.GetDirectoryName(prefix))
        {
            if (RealPath(prefix, 0) is { } physical)
                return physical;
        }
        return null;
    }

    // A child can exec with a clean environment and keep its cwd, file descriptors
    // or mapped package files. Read procfs once per batch and retain only copy roots.
    private static HashSet<string> ProcessReferences(string[] candidates)
    {
        var referenced = new HashSet<string>(Paths);
        if (!OperatingSystem.IsLinux()) return referenced;
        // Keep the configured spelling as well as the physical path reported by kernel links.
        var spellings = new List<(string Prefix, string Root)>();
        foreach (var root in candidates)
        {
            var physical = RealPath(root, 0);
            if (physical is null)
            {
                referenced.Add(root); // Retry when we can check the physical path.
                continue;
            }
            spellings.Add((root, root));
            if (physical != root) spellings.Add((physical, root));
        }
        if (spellings.Count == 0) return referenced;
        // Skip parsing large maps that contain no candidate directory.
        // Include physical parents because .runs itself can be a symlink.
        var runsDirectories = spellings.Select(s => Path.GetDirectoryName(s.Prefix)!).Distinct().ToArray();
        foreach (var process in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(process), out _)) continue;
            foreach (var path in ProcessPaths(process, runsDirectories))
            {
                foreach (var (prefix, root) in spellings)
                {
                    if (path.StartsWith(prefix, StringComparison.Ordinal)
                        && (path.Length == prefix.Length || path[prefix.Length] == '/'))
                        referenced.Add(root);
                }
                if (referenced.Count == candidates.Length) return referenced;
            }
        }
        return referenced;
    }

    private static IEnumerable<string> ProcessPaths(string process, string[] runsDirectories)
    {
        foreach (var supplied in ProcessSuppliedPaths(process))
        {
            var path = Path.GetFullPath(supplied);
            yield return path;
            if (ResolveExistingPrefix(supplied) is { } physical && physical != path) yield return physical;
        }

        if (new DirectoryInfo(Path.Combine(process, "cwd")).LinkTarget is { } cwd) yield return cwd;
        if (new FileInfo(Path.Combine(process, "exe")).LinkTarget is { } exe) yield return exe;
        string[] descriptors;
        try { descriptors = Directory.GetFileSystemEntries(Path.Combine(process, "fd")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { descriptors = []; }
        foreach (var descriptor in descriptors)
            if (new FileInfo(descriptor).LinkTarget is { } target) yield return target;

        // A loader can close the descriptor once it has mapped the file.
        var maps = ReadProcFile(process, "maps");
        if (!runsDirectories.Any(maps.Contains)) yield break;
        using var lines = new StringReader(maps);
        while (lines.ReadLine() is { } line)
        {
            var start = line.IndexOf('/');
            if (start >= 0) yield return line[start..];
        }
    }

    private static IEnumerable<string> ProcessSuppliedPaths(string process)
    {
        var environment = ReadProcFile(process, "environ");
        foreach (var entry in environment.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = entry.IndexOf('=');
            if (split > 0 && CacheVariables.Contains(entry[..split], StringComparer.OrdinalIgnoreCase)
                && Path.IsPathFullyQualified(entry[(split + 1)..]))
                yield return entry[(split + 1)..];
        }

        // Some tools receive a package or browser path as an argument and open it later.
        var arguments = ReadProcFile(process, "cmdline");
        foreach (var argument in arguments.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var value = argument.StartsWith('-') ? argument[(argument.IndexOf('=') + 1)..] : argument;
            if (Path.IsPathFullyQualified(value)) yield return value;
        }
    }

    private static string ReadProcFile(string process, string name)
    {
        try { return File.ReadAllText(Path.Combine(process, name)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return string.Empty; }
    }
}
