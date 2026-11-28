using AgentStudio.CliHosting;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Owns the writable preparation copies in this runner's work directory.</summary>
internal sealed class RunnerPreparationCopies(RunnerOptions options, RunnerStateStore state, Action<string> log,
    string? legacyPilotCacheRoot = null)
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

    internal static string? ResolveLegacyPilotCacheRoot()
    {
        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return Path.IsPathFullyQualified(data) ? Path.Combine(data, "agent-studio", "cache", "agent-studio-m1-pilot") : null;
    }

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
                && Paths.Equals(Path.GetDirectoryName(root), runs)) return root;
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
            TryRelease(path, HostProcessBindings.Find([path]).Contains(path), log);
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
            Interlocked.Exchange(ref _bytes, Sweep(options.WorkDir, state.LoadAll, limit, startup, log, legacyPilotCacheRoot));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            log($"preparation-copy-sweep-failed error={ex.Message}");
        }
    }

    public async Task MonitorAsync(RunnerCleanContexts cleanContexts, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                Sweep(startup: false);
                await cleanContexts.SweepAsync(DateTimeOffset.UtcNow, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    internal static long Sweep(string workDir, Func<IReadOnlyList<PersistedRunnerSlot>> slots,
        long limit, bool startup, Action<string> log, string? legacyPilotCacheRoot = null, DateTime? nowUtc = null)
    {
        var roots = new List<(DirectoryInfo Directory, long Bytes, bool Pending, bool Legacy)>();
        var work = new DirectoryInfo(Path.GetFullPath(workDir));
        if (work.Exists)
        {
            foreach (var project in work.EnumerateDirectories().Prepend(work))
            {
                var runs = new DirectoryInfo(Runs(project.FullName));
                if (!runs.Exists || new[] { project, runs.Parent!.Parent!, runs.Parent, runs }
                    .Any(directory => directory.Attributes.HasFlag(FileAttributes.ReparsePoint))) continue;
                InspectRuns(runs, legacy: false);
            }
        }
        if (legacyPilotCacheRoot is not null && OperatingSystem.IsLinux())
        {
            var runs = Path.Combine(legacyPilotCacheRoot, ProjectPreparationCacheSweep.RunsDirectoryName);
            if (TaskCleanContextStore.IsPhysicalDirectory(runs)) InspectRuns(new DirectoryInfo(runs), legacy: true);
        }

        void InspectRuns(DirectoryInfo runs, bool legacy)
        {
            foreach (var dir in runs.EnumerateDirectories())
            {
                if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || legacy && !Guid.TryParseExact(dir.Name, "N", out _)) continue;
                roots.Add((dir, Measure(dir), File.Exists(Path.Combine(dir.FullName, ReleasePending)), legacy));
            }
        }

        var total = roots.Sum(root => root.Bytes);
        if (roots.Count == 0) return total;
        var legacyCutoff = (nowUtc ?? DateTime.UtcNow) - ProjectPreparationCacheSweep.DefaultRunRootRetention;
        // A slot reserves its unique id before creating any files. Reading the slots
        // after listing candidates closes the race with a concurrently starting attempt.
        var referenced = slots().Select(slot => RootFor(workDir, slot)).OfType<string>().ToHashSet(Paths);
        var candidates = roots.Where(root => !referenced.Contains(root.Directory.FullName)
                && (root.Legacy ? root.Pending || root.Directory.LastWriteTimeUtc <= legacyCutoff
                    : startup || root.Pending || total > limit))
            .OrderBy(root => root.Directory.LastWriteTimeUtc).ThenBy(root => root.Directory.FullName, Paths).ToArray();
        if (candidates.Length == 0) return total;
        var processReferences = HostProcessBindings.Find(candidates.Select(root => root.Directory.FullName).ToArray());
        foreach (var (dir, bytes, pending, legacy) in candidates)
        {
            if (!legacy && !pending && !startup && total <= limit) continue;
            if (!TryRelease(dir.FullName, processReferences.Contains(dir.FullName), log)) continue;
            total -= bytes;
            log($"preparation-copy-removed path={dir.FullName} bytes={bytes}");
        }
        return total;
    }

    internal static long Measure(DirectoryInfo root)
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

}
