using AgentStudio.Docs;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests;

/// <summary>
/// A <see cref="SharedTestHost"/> watching one or more projects inside its own
/// workspace (AGT-3416), for classes that write task folders and read them
/// back over HTTP.
///
/// <para>A host built per test started after the test wrote its folders: its
/// boot scanned them, and its file watcher never saw them being written. This
/// one is up before the test writes anything, so <see cref="CreateClient"/>
/// replays what a boot does with such folders: it rescans the task index, runs
/// the boot's result-document backfill, lets the watcher deliver every write
/// so far, rescans again and refills the wiki cache. The first request then
/// sees what the per-test host's first request saw, and no late watcher event
/// republishes a card behind it. <see cref="Reset"/> empties every lane,
/// so a test starts from the empty projects a fresh host started from.</para>
/// </summary>
public abstract class BoardTestHost : SharedTestHost
{
    private readonly (string Name, string Path)[] _projects;

    protected BoardTestHost(string prefix, params string[] projects) : base(prefix)
    {
        _projects = projects.Select(name => (name, System.IO.Path.Combine(Root, "projects", name))).ToArray();
        foreach (var (_, path) in _projects)
            CreateLanes(path);
    }

    /// <summary>The <c>TaskRepository</c> the host was started with.</summary>
    public string Workspace => Root;

    public string WatchPath(string project) => _projects.Single(entry => entry.Name == project).Path;

    /// <summary>Settings beyond the workspace and its watch paths.</summary>
    protected virtual IEnumerable<KeyValuePair<string, string?>> Settings() => [];

    protected virtual void ConfigureServices(IServiceCollection services) { }

    protected sealed override void ConfigureHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.ConfigureServices(ConfigureServices);
        var settings = new Dictionary<string, string?> { ["TaskRepository"] = Workspace };
        for (var i = 0; i < _projects.Length; i++)
        {
            settings[$"WatchPaths:{i}:Name"] = _projects[i].Name;
            settings[$"WatchPaths:{i}:Path"] = _projects[i].Path;
            settings[$"WatchPaths:{i}:RootPath"] = _projects[i].Path;
        }
        foreach (var (key, value) in Settings())
            settings[key] = value;
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
    }

    /// <summary>
    /// Empties every lane of every project and rescans, so the host reads no
    /// task an earlier test wrote. The lane folders themselves stay: the
    /// host's file watcher holds the project folders.
    /// </summary>
    public void Reset()
    {
        foreach (var (_, path) in _projects)
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo lane && TaskStates.All.Contains(lane.Name))
                {
                    foreach (var child in lane.EnumerateFileSystemInfos())
                        Delete(child);
                }
                else
                {
                    Delete(entry);
                }
            }
            CreateLanes(path);
        }
        Index.ForceRefresh();
    }

    public TaskIndexCache Index => Services.GetRequiredService<TaskIndexCache>();

    /// <summary>A client for the host, after a rescan of what the test wrote.</summary>
    public HttpClient CreateClient()
    {
        Index.ForceRefresh();
        Services.GetRequiredService<TaskTransitionService>().BackfillMissingResultDocuments();
        DrainWatcher();
        Index.ForceRefresh();
        var wiki = Services.GetRequiredService<WikiContentCache>();
        foreach (var (name, _) in _projects)
            wiki.Invalidate(name);
        return Factory.CreateClient();
    }

    /// <summary>
    /// Returns once the file watcher has handled every change made so far and
    /// passed each on to the index. A sentinel at the project root marks the
    /// end of those changes: the watcher reports changes in order, and a file
    /// outside the lanes is not a task, so the sentinel itself reaches no
    /// index. A change reaches the index one debounce after it was reported.
    /// </summary>
    private void DrainWatcher()
    {
        var watcher = Services.GetRequiredService<TaskWatcherService>();
        foreach (var (_, path) in _projects)
        {
            var name = ".watcher-drain-" + Guid.NewGuid().ToString("N");
            var sentinel = System.IO.Path.Combine(path, name);
            using var reported = new ManualResetEventSlim();
            void Observe(string changed)
            {
                if (System.IO.Path.GetFileName(changed) == name) reported.Set();
            }
            watcher.OnPathChanged += Observe;
            try
            {
                File.WriteAllText(sentinel, "");
                if (!reported.Wait(WatcherTimeout))
                    throw new TimeoutException("The file watcher did not report the drain sentinel in " + path);
            }
            finally
            {
                watcher.OnPathChanged -= Observe;
                File.Delete(sentinel);
            }
        }

        var debounce = TimeSpan.FromMilliseconds(Math.Max(50,
            int.TryParse(Services.GetRequiredService<IConfiguration>()["TaskWatcher:DebounceMs"], out var ms) ? ms : 250));
        // The watcher stamps its last event with the system time, which is
        // the clock of every host this fixture starts.
        var clock = Services.GetRequiredService<TimeProvider>();
        Thread.Sleep(2 * debounce);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (watcher.GetHealthSnapshot().LastEventAt is { } last
               && clock.GetUtcNow().UtcDateTime - last < debounce
               && waited.Elapsed < WatcherTimeout)
            Thread.Sleep(debounce);
    }

    private static readonly TimeSpan WatcherTimeout = TimeSpan.FromSeconds(30);

    private static void CreateLanes(string path)
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(System.IO.Path.Combine(path, state));
    }

    private static void Delete(FileSystemInfo entry)
    {
        if (entry is DirectoryInfo directory) directory.Delete(recursive: true);
        else entry.Delete();
    }
}
