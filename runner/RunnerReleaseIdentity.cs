using System.Reflection;

namespace AgentRunner;

/// <summary>
/// Resolves the immutable agent-host deployment identity advertised to the
/// Task Server. Production releases are directories below
/// <c>/opt/agent-host/releases</c> selected by the <c>current</c> symlink.
/// </summary>
internal static class RunnerReleaseIdentity
{
    internal static string Current { get; } = Resolve(AppContext.BaseDirectory);

    /// <summary>
    /// Fully resolved path of the executable this process runs, i.e. the real
    /// <c>/opt/agent-host/releases/&lt;id&gt;/agent-host</c> rather than the
    /// <c>current</c> symlink it was started through. A detached review worker
    /// outlives the promotion that moved that symlink, so the resolved path is
    /// the only truthful answer to "which binary graded this attempt".
    /// </summary>
    internal static string CurrentBinaryPath { get; } = ResolveBinaryPath(Environment.ProcessPath);

    internal static string ResolveBinaryPath(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath)) return "unknown";
        var resolved = Path.GetFullPath(processPath);
        try
        {
            if (new FileInfo(resolved).ResolveLinkTarget(returnFinalTarget: true) is { } file)
                resolved = file.FullName;
            // A release reached through a symlinked directory keeps its literal
            // path above, so the directory has to be resolved as well.
            var directoryPath = Path.GetDirectoryName(resolved);
            if (directoryPath is { Length: > 0 }
                && new DirectoryInfo(directoryPath).ResolveLinkTarget(returnFinalTarget: true)
                    is { } directory)
            {
                resolved = Path.Combine(directory.FullName, Path.GetFileName(resolved));
            }
        }
        catch (IOException)
        {
            // Promotion may replace the symlink during this read. The literal
            // path remains a truthful bounded fallback.
        }
        return resolved;
    }

    internal static string Resolve(string baseDirectory, string? configured = null)
    {
        var explicitId = (configured ?? RunnerOptions.Env("RUNNER_RELEASE_ID")).Trim();
        if (explicitId.Length > 0) return explicitId;

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var directory = new DirectoryInfo(fullPath);
        if (string.Equals(directory.Name, "current", StringComparison.Ordinal))
        {
            try
            {
                var target = directory.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null && target.Name.Length > 0) return target.Name;
            }
            catch (IOException)
            {
                // Promotion may replace the symlink during this read. The
                // assembly identity below remains a truthful bounded fallback.
            }
        }

        if (string.Equals(directory.Parent?.Name, "releases", StringComparison.Ordinal)
            && directory.Name.Length > 0)
            return directory.Name;

        var assembly = typeof(RunnerReleaseIdentity).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
               ?? assembly.GetName().Version?.ToString(3)
               ?? "unknown";
    }
}
