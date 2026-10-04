using System.Runtime.CompilerServices;

namespace AgentStudio.TestSupport;

/// <summary>
/// Single place that answers "where is the repository checkout this test was
/// compiled from?".
///
/// Walking up from <see cref="AppContext.BaseDirectory"/> only works while the
/// build output sits inside the checkout. The documented isolation recipe for a
/// host with a running backend builds elsewhere
/// (<c>dotnet test -p:ArtifactsPath=/c/scratch/obj-tests</c>), and from there
/// no parent directory holds <c>agent-taskboard.sln</c>: every repository-reading
/// guard failed or silently skipped (AGT-3003). The calling test's source file
/// is inside the checkout wherever the binaries land, so it is tried first; the
/// base and working directories remain fallbacks for relocated sources.
/// </summary>
public static class RepositoryRoot
{
    private const string Marker = "agent-taskboard.sln";

    /// <summary>The checkout root. Throws with the searched locations when none is found.</summary>
    public static string Find([CallerFilePath] string callerSourceFile = "")
        => TryFind(callerSourceFile)
           ?? throw new DirectoryNotFoundException(
               $"{Marker} was not found above {string.Join(", ", Starts(callerSourceFile).Select(start => $"'{start}'"))}.");

    /// <summary>The checkout root, or <c>null</c> when no start location lies inside one.</summary>
    public static string? TryFind([CallerFilePath] string callerSourceFile = "")
    {
        foreach (var start in Starts(callerSourceFile))
        {
            for (var current = start; !string.IsNullOrEmpty(current); current = System.IO.Path.GetDirectoryName(current))
            {
                if (File.Exists(System.IO.Path.Combine(current, Marker))) return current;
            }
        }
        return null;
    }

    private static IEnumerable<string> Starts(string callerSourceFile)
    {
        if (!string.IsNullOrEmpty(callerSourceFile) && System.IO.Path.GetDirectoryName(callerSourceFile) is { Length: > 0 } sourceDirectory)
            yield return sourceDirectory;
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
    }
}
