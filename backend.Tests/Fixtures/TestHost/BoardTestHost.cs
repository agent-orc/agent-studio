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
/// <para>A host built per test scanned folders the test had already written.
/// This one is up before the test writes anything, so <see cref="CreateClient"/>
/// rescans the task index first: the first read sees the same folders the
/// per-test host's first scan saw. <see cref="Reset"/> empties every lane, so a
/// test starts from the empty projects a fresh host started from.</para>
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
        return Factory.CreateClient();
    }

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
