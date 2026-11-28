using System.Runtime.InteropServices;

namespace AgentStudio.CliHosting;

/// <summary>Finds live Linux processes that reference any of the supplied directory trees.</summary>
public static class HostProcessBindings
{
    // On Linux realpath(path, NULL) resolves all symlinked ancestors. The string
    // return marshaller copies and frees the buffer allocated by libc.
    [DllImport("libc", EntryPoint = "realpath")]
    [return: MarshalAs(UnmanagedType.LPUTF8Str)]
    private static extern string? RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint buffer);

    // A child can exec with a clean environment and keep its cwd, file descriptors
    // or mapped files. Scan procfs once per batch.
    private static readonly string[] BindingVariables =
        ["NUGET_PACKAGES", "NPM_CONFIG_CACHE", "PLAYWRIGHT_BROWSERS_PATH", "CODEX_HOME", "CLAUDE_CONFIG_DIR"];
    public static HashSet<string> Find(string[] candidates)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        if (!OperatingSystem.IsLinux()) return referenced;
        // argv and environ can use the configured spelling. Kernel links use the physical path.
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
        var parents = spellings.Select(s => Path.GetDirectoryName(s.Prefix)!).Distinct().ToArray();
        foreach (var process in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(process), out _)) continue;
            foreach (var path in ProcessPaths(process, parents))
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

    private static IEnumerable<string> ProcessPaths(string process, string[] parents)
    {
        var environment = ReadProcFile(process, "environ");
        foreach (var entry in environment.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = entry.IndexOf('=');
            if (split > 0 && BindingVariables.Contains(entry[..split], StringComparer.OrdinalIgnoreCase)
                && Path.IsPathFullyQualified(entry[(split + 1)..]))
                yield return Path.GetFullPath(entry[(split + 1)..]);
        }

        // Some tools receive a package or browser path as an argument and open it later.
        var arguments = ReadProcFile(process, "cmdline");
        foreach (var argument in arguments.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var value = argument.StartsWith('-') ? argument[(argument.IndexOf('=') + 1)..] : argument;
            if (Path.IsPathFullyQualified(value)) yield return Path.GetFullPath(value);
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
        if (!parents.Any(maps.Contains)) yield break;
        using var lines = new StringReader(maps);
        while (lines.ReadLine() is { } line)
        {
            var start = line.IndexOf('/');
            if (start >= 0) yield return line[start..];
        }
    }

    private static string ReadProcFile(string process, string name)
    {
        try { return File.ReadAllText(Path.Combine(process, name)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return string.Empty; }
    }
}
