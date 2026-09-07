using System.Collections.Concurrent;

namespace AgentStudio.Retention;

/// <summary>Process-wide serialization gate shared by legacy workspace commits and retention runs.</summary>
public static class RepositoryWriteGate
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static object SyncRoot(string repositoryRoot)
        => Gates.GetOrAdd(Path.GetFullPath(repositoryRoot), _ => new object());

    public static T Run<T>(string repositoryRoot, Func<T> action)
    {
        var root = Path.GetFullPath(repositoryRoot);
        lock (SyncRoot(root))
            return action();
    }

    public static void Run(string repositoryRoot, Action action)
        => Run(repositoryRoot, () => { action(); return true; });
}
