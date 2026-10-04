using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>
/// Watches the resolved job-folder paths configured under
/// <c>WatchPaths</c> and fans out a single <see cref="OnJobChanged"/>
/// event when task.json semantics or task-folder structure changes on disk.
/// High-churn logs and generated sidecars remain available through the raw
/// <see cref="OnPathChanged"/> stream but cannot invalidate the task index.
/// Subscribers include
/// <see cref="TaskIndexCache.Invalidate"/> (Cycle 1 cache invalidation),
/// the SignalR hub (broadcasts <c>jobsChanged</c> to clients), and
/// <see cref="TaskRunnerService.ReconcileRunnerForPath"/> (releases the
/// runner's active-job latch when the folder leaves <c>3-progress</c>
/// outside the API).
///
/// <para><b>Pre-Cycle-1 history:</b> this service used to read
/// <c>WatchPaths</c> as <c>List&lt;string&gt;</c> while the actual config
/// schema is <c>List&lt;WatchPathEntry&gt;</c>; the cast returned an
/// empty list, no <see cref="FileSystemWatcher"/> was ever constructed,
/// and <see cref="OnJobChanged"/> never fired. SignalR <c>jobsChanged</c>
/// pushes and runner reconciliation were dead silent against external
/// folder moves. Cycle 1 fixes the config read by delegating to
/// <see cref="TaskScannerService.GetWatchPaths"/> (which already honours
/// the <c>.orchestrator.yml</c> pointer flow) and watching the resolved
/// <c>entry.Path</c>.</para>
/// </summary>
public class TaskWatcherService : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly ILogger<TaskWatcherService> _logger;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly List<FileSystemWatcher> _wikiWatchers = [];
    private readonly TimeSpan _debounce;
    private readonly ConcurrentDictionary<string, string> _taskIndexSignatures = new(PathComparer);
    private DateTime? _startedAt;
    private int _configuredPathCount;
    private string? _lastError;

    public event Action<string>? OnJobChanged;
    /// <summary>
    /// Debounced docs/ changes, carrying the owning project name and changed
    /// path. The wiki cache subscribes with an eager rebuild.
    /// </summary>
    public event Action<string, string>? OnWikiChanged;
    /// <summary>
    /// Raw filesystem change stream for consumers that own their own narrow
    /// filtering (for example conversation projection of cli-output.log).
    /// Unlike <see cref="OnJobChanged"/>, this event never invalidates the
    /// task index by itself.
    /// </summary>
    public event Action<string>? OnPathChanged;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public TaskWatcherService(TaskScannerService scanner, ILogger<TaskWatcherService> logger, IConfiguration config)
    {
        _scanner = scanner;
        _logger = logger;
        var debounceMs = int.TryParse(config["TaskWatcher:DebounceMs"], out var v) ? v : 250;
        _debounce = TimeSpan.FromMilliseconds(Math.Max(50, debounceMs));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var entries = _scanner.GetWatchPaths();
        lock (_lock)
        {
            _startedAt = DateTime.UtcNow;
            _configuredPathCount = entries.Count;
            _lastError = null;
        }
        foreach (var entry in entries)
        {
            EnsureWatching(entry);
        }
        stoppingToken.Register(DisposeResources);
        return Task.CompletedTask;
    }

    /// <summary>Add a live watcher for an API-created project.</summary>
    public bool EnsureWatching(WatchPathEntry entry)
    {
        EnsureWikiWatching(entry);
        if (string.IsNullOrWhiteSpace(entry.Path))
        {
            _logger.LogWarning("WatchPath '{Name}' resolved to empty path; skipping watcher", entry.Name);
            return false;
        }

        lock (_lock)
        {
            if (_watchers.Any(w => string.Equals(w.Path, entry.Path, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        try
        {
            Directory.CreateDirectory(entry.Path);
            var watcher = new FileSystemWatcher(entry.Path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.Size,
                EnableRaisingEvents = true,
                InternalBufferSize = 64 * 1024
            };
            watcher.Changed += (_, e) => HandleChange(entry.Path, e.FullPath, e.ChangeType);
            watcher.Created += (_, e) => HandleChange(entry.Path, e.FullPath, e.ChangeType);
            watcher.Deleted += (_, e) => HandleChange(entry.Path, e.FullPath, e.ChangeType);
            watcher.Renamed += (_, e) => HandleChange(entry.Path, e.FullPath, e.ChangeType, e.OldFullPath);
            watcher.Error += (_, e) =>
            {
                var error = e.GetException();
                lock (_lock) _lastError = error?.Message ?? "FileSystemWatcher reported an unknown error.";
                _logger.LogWarning(error, "FileSystemWatcher error for {Path}", entry.Path);
            };
            lock (_lock)
            {
                _watchers.Add(watcher);
                _configuredPathCount = Math.Max(_configuredPathCount, _watchers.Count);
            }
            _logger.LogInformation("watch-path-activated project={Name} path={Path}", entry.Name, entry.Path);
            return true;
        }
        catch (Exception ex)
        {
            lock (_lock) _lastError = ex.Message;
            _logger.LogError(ex, "Failed to start watcher for {Path}", entry.Path);
            return false;
        }
    }

    private void EnsureWikiWatching(WatchPathEntry entry)
    {
        var repositoryRoot = !string.IsNullOrWhiteSpace(entry.RepositoryPath)
            && Directory.Exists(entry.RepositoryPath)
                ? entry.RepositoryPath
                : entry.RootPath;
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot)) return;
        var docsPath = Path.Combine(repositoryRoot, ProjectDocsService.WikiRel);
        if (!Directory.Exists(docsPath)) return;

        lock (_lock)
        {
            if (_wikiWatchers.Any(w => string.Equals(w.Path, docsPath, StringComparison.OrdinalIgnoreCase)))
                return;
        }

        try
        {
            var watcher = new FileSystemWatcher(docsPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                    | NotifyFilters.DirectoryName | NotifyFilters.Size,
                EnableRaisingEvents = true,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Changed += (_, e) => HandleWikiChange(entry.Name, e.FullPath);
            watcher.Created += (_, e) => HandleWikiChange(entry.Name, e.FullPath);
            watcher.Deleted += (_, e) => HandleWikiChange(entry.Name, e.FullPath);
            watcher.Renamed += (_, e) => HandleWikiChange(entry.Name, e.FullPath);
            watcher.Error += (_, e) =>
            {
                var error = e.GetException();
                lock (_lock) _lastError = error?.Message ?? "Wiki FileSystemWatcher reported an unknown error.";
                _logger.LogWarning(error, "Wiki FileSystemWatcher error for {Path}", docsPath);
            };
            lock (_lock) _wikiWatchers.Add(watcher);
            _logger.LogInformation("wiki-watch-activated project={Name} path={Path}", entry.Name, docsPath);
        }
        catch (Exception ex)
        {
            lock (_lock) _lastError = ex.Message;
            _logger.LogError(ex, "Failed to start wiki watcher for {Path}", docsPath);
        }
    }

    /// <summary>
    /// Cheap read-only watcher health used by the orchestrator context digest.
    /// It deliberately reports the actual active handle count and last observed
    /// event/error instead of inferring health from the static <c>/healthz</c>
    /// response. No paths are exposed in the snapshot.
    /// </summary>
    public TaskWatcherHealthSnapshot GetHealthSnapshot()
    {
        lock (_lock)
        {
            var active = _watchers.Count;
            return new TaskWatcherHealthSnapshot(
                StartedAt: _startedAt,
                ConfiguredPathCount: _configuredPathCount,
                ActiveWatcherCount: active,
                LastEventAt: _lastEvent == DateTime.MinValue ? null : _lastEvent,
                LastError: _lastError,
                Healthy: _startedAt != null
                    && string.IsNullOrWhiteSpace(_lastError)
                    && (_configuredPathCount == 0 || active == _configuredPathCount));
        }
    }

    private DateTime _lastEvent = DateTime.MinValue;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PendingDispatch> _pendingDispatches = new(PathComparer);
    private readonly Dictionary<string, PendingWikiDispatch> _pendingWikiDispatches =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private sealed class PendingDispatch(string path, Timer timer)
    {
        public string Path { get; set; } = path;
        public Timer Timer { get; } = timer;
    }

    private sealed class PendingWikiDispatch(string projectName, string path, Timer timer)
    {
        public string ProjectName { get; } = projectName;
        public string Path { get; set; } = path;
        public Timer Timer { get; } = timer;
    }

    internal void HandleChange(
        string watchPath,
        string path,
        WatcherChangeTypes changeType,
        string? oldPath = null)
    {
        try { OnPathChanged?.Invoke(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "OnPathChanged subscriber threw for {Path}", path); }

        if (!ShouldNotifyIndexChange(watchPath, path, changeType, oldPath)) return;
        Debounce(path);
    }

    internal void HandleWikiChange(string projectName, string path)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(path)) return;
        lock (_lock)
        {
            if (_disposed) return;
            if (_pendingWikiDispatches.TryGetValue(projectName, out var pending))
            {
                pending.Path = path;
                pending.Timer.Change(_debounce, Timeout.InfiniteTimeSpan);
                return;
            }

            var timer = new Timer(
                _ => DispatchPendingWiki(projectName),
                null,
                _debounce,
                Timeout.InfiniteTimeSpan);
            _pendingWikiDispatches[projectName] = new PendingWikiDispatch(projectName, path, timer);
        }
    }

    private void DispatchPendingWiki(string projectName)
    {
        PendingWikiDispatch? pending;
        lock (_lock)
        {
            if (_disposed || !_pendingWikiDispatches.Remove(projectName, out pending)) return;
            _lastEvent = DateTime.UtcNow;
        }
        pending.Timer.Dispose();
        _logger.LogDebug("Wiki watcher fired for {Project} {Path}", pending.ProjectName, pending.Path);
        try { OnWikiChanged?.Invoke(pending.ProjectName, pending.Path); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OnWikiChanged subscriber threw for {Project} {Path}", pending.ProjectName, pending.Path);
        }
    }

    /// <summary>
    /// Coalesces bursty FileSystemWatcher events into a single trailing-edge
    /// <see cref="OnJobChanged"/> invocation per task after a quiet window.
    /// Filtering happens before this method so generated sidecars never enter
    /// the task-index invalidation path.
    /// </summary>
    private void Debounce(string path)
    {
        string key;
        lock (_lock)
        {
            if (_disposed) return;
            key = string.Equals(Path.GetFileName(path), "task.json", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(path) ?? path
                : path;
            if (_pendingDispatches.TryGetValue(key, out var pending))
            {
                pending.Path = path;
                pending.Timer.Change(_debounce, Timeout.InfiniteTimeSpan);
                return;
            }

            var timer = new Timer(
                _ => DispatchPending(key),
                null,
                _debounce,
                Timeout.InfiniteTimeSpan);
            _pendingDispatches[key] = new PendingDispatch(path, timer);
        }
    }

    private void DispatchPending(string key)
    {
        PendingDispatch? pending;
        lock (_lock)
        {
            if (_disposed || !_pendingDispatches.Remove(key, out pending)) return;
            _lastEvent = DateTime.UtcNow;
        }
        pending.Timer.Dispose();

        _logger.LogDebug("Watcher fired for {Path}", pending.Path);
        // FileSystemWatcher delivers callbacks on the thread pool. An
        // unhandled exception escaping a subscriber goes through
        // AppDomain.UnhandledException and terminates the host - the
        // silent-kill class we are guarding against. Log and swallow so a
        // single bad subscriber cannot crash the process.
        try { OnJobChanged?.Invoke(pending.Path); }
        catch (Exception ex) { _logger.LogWarning(ex, "OnJobChanged subscriber threw for {Path}", pending.Path); }
    }

    private void DisposeResources()
    {
        FileSystemWatcher[] watchers;
        FileSystemWatcher[] wikiWatchers;
        Timer[] timers;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            watchers = _watchers.ToArray();
            wikiWatchers = _wikiWatchers.ToArray();
            timers = _pendingDispatches.Values.Select(p => p.Timer)
                .Concat(_pendingWikiDispatches.Values.Select(p => p.Timer))
                .ToArray();
            _watchers.Clear();
            _wikiWatchers.Clear();
            _pendingDispatches.Clear();
            _pendingWikiDispatches.Clear();
        }
        foreach (var timer in timers) timer.Dispose();
        foreach (var watcher in watchers)
        {
            try { watcher.Dispose(); }
            catch (Exception ex) { SilentCatch.Note(ex, "TaskWatcherService: watcher dispose"); }
        }
        foreach (var watcher in wikiWatchers)
        {
            try { watcher.Dispose(); }
            catch (Exception ex) { SilentCatch.Note(ex, "TaskWatcherService: wiki watcher dispose"); }
        }
    }

    public override void Dispose()
    {
        DisposeResources();
        base.Dispose();
    }

    /// <summary>
    /// Decides whether a filesystem event can change a cached
    /// <see cref="TaskInfo"/>. Only task.json content and task-folder
    /// structural moves/deletes qualify. Generated sidecars, logs, results,
    /// lifecycle output and index mirrors are intentionally excluded: API
    /// writers invalidate synchronously, while genuinely external edits are
    /// covered by the safety TTL.
    ///
    /// Task-local <c>lastProgressAt</c> and <c>contextUsage</c> telemetry is
    /// excluded after the first observation. Neither field is projected into
    /// <see cref="TaskInfo"/>; treating each activity flush as a board mutation
    /// caused a full workspace scan feedback loop.
    /// </summary>
    internal bool ShouldNotifyIndexChange(
        string watchPath,
        string path,
        WatcherChangeTypes changeType,
        string? oldPath = null)
    {
        if (string.IsNullOrWhiteSpace(watchPath) || string.IsNullOrWhiteSpace(path)) return false;

        if (string.Equals(Path.GetFileName(path), "task.json", StringComparison.OrdinalIgnoreCase))
        {
            if (changeType == WatcherChangeTypes.Deleted)
            {
                _taskIndexSignatures.TryRemove(path, out _);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(oldPath)
                && !string.Equals(oldPath, path, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            {
                _taskIndexSignatures.TryRemove(oldPath, out _);
            }

            var signature = ComputeTaskIndexSignature(path);
            if (signature == null) return true; // partial/locked write: fail open
            if (_taskIndexSignatures.TryGetValue(path, out var previous)
                && string.Equals(previous, signature, StringComparison.Ordinal))
                return false;
            _taskIndexSignatures[path] = signature;
            return true;
        }

        if (changeType is WatcherChangeTypes.Deleted or WatcherChangeTypes.Renamed)
            return IsTaskFolderPath(watchPath, path)
                   || (!string.IsNullOrWhiteSpace(oldPath) && IsTaskFolderPath(watchPath, oldPath));

        return false;
    }

    internal static string? ComputeTaskIndexSignature(string taskJsonPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(taskJsonPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var signature = new StringBuilder();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "lastProgressAt", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "contextUsage", StringComparison.OrdinalIgnoreCase))
                    continue;
                signature.Append(property.Name.Length).Append(':').Append(property.Name)
                    .Append('=').Append(property.Value.GetRawText()).Append(';');
            }
            return signature.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTaskFolderPath(string watchPath, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(watchPath, path);
            if (relative == "." || Path.IsPathRooted(relative)) return false;
            var parts = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(part => part == "..")) return false;

            // Legacy: <root>/<lane>/<slug>. Flat: <root>/tasks/<bucket>/<key>.
            return parts.Length == 2 && TaskStates.All.Contains(parts[0], StringComparer.Ordinal)
                   || parts.Length == 3
                   && string.Equals(parts[0], TaskStorageLayout.JobsDirName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Read-only health projection for <see cref="TaskWatcherService"/>.</summary>
public sealed record TaskWatcherHealthSnapshot(
    DateTime? StartedAt,
    int ConfiguredPathCount,
    int ActiveWatcherCount,
    DateTime? LastEventAt,
    string? LastError,
    bool Healthy);
