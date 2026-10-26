using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Git;

/// <summary>
/// Fingerprints Git's effective configuration. Git resolves include and
/// worktree rules; this code reads only Git's resolved output and the files
/// Git says contributed to it. Values are never logged or published.
/// <para>
/// AGT-3322: a capture also records the configuration sources that did not
/// exist yet (an include target, <c>config.worktree</c>, the global files), so
/// <see cref="SourcesUnchanged"/> can tell from files alone, without starting
/// Git, whether a new capture could resolve differently.
/// </para>
/// </summary>
internal static class GitConfigSignature
{
    internal static GitEffectiveConfig Capture(string repositoryPath)
    {
        EnsureRepository(repositoryPath);

        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("config");
        start.ArgumentList.Add("--includes");
        start.ArgumentList.Add("--show-origin");
        start.ArgumentList.Add("--null");
        start.ArgumentList.Add("--list");
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        var watch = Stopwatch.StartNew();
        using var processSlot = GitProcessBudget.Acquire();
        var result = GitNetworkProcessRunner.Run(start, cancellationToken: GitProcessBudget.Token);
        watch.Stop();
        GitProcessTelemetry.Record("config", watch.ElapsedMilliseconds, result.ExitCode);
        if (GitProcessBudget.Token.IsCancellationRequested)
            throw new OperationCanceledException(GitProcessBudget.Token);
        if (!result.Success) throw new IOException("repository-config-unavailable");

        var filePaths = new HashSet<string>(FileSystemPathComparer.Instance);
        var candidates = new HashSet<string>(FileSystemPathComparer.Instance);
        string? originUrl = null;
        var dependsOnHead = false;
        var fields = result.StandardOutput.Split('\0');
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            var source = fields[i];
            var setting = fields[i + 1];
            string? sourcePath = null;
            if (source.StartsWith("file:", StringComparison.Ordinal))
            {
                var path = source["file:".Length..];
                sourcePath = Path.IsPathRooted(path)
                    ? Path.GetFullPath(path) : Path.GetFullPath(path, repositoryPath);
                filePaths.Add(sourcePath);
            }
            var newline = setting.IndexOf('\n');
            if (newline < 0) continue;
            var key = setting[..newline];
            var value = setting[(newline + 1)..].Trim();
            if (string.Equals(key, "remote.origin.url", StringComparison.OrdinalIgnoreCase))
                originUrl = value.Length == 0 ? null : value;
            if (IsIncludePath(key))
            {
                dependsOnHead |= key.StartsWith("includeif.onbranch:", StringComparison.OrdinalIgnoreCase);
                if (ResolveIncludePath(value, sourcePath) is { } include) candidates.Add(include);
            }
        }
        foreach (var path in StandardSources(repositoryPath)) candidates.Add(path);
        var files = filePaths.OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new GitConfigFileStamp(path, FileHash(path))).ToArray();
        // A source Git already read is covered by its hash; the rest only
        // matter once they appear.
        var absent = candidates.Where(path => !filePaths.Contains(path) && !File.Exists(path))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.StandardOutput)));
        return new GitEffectiveConfig(signature, originUrl, files, absent, dependsOnHead);
    }

    /// <summary>
    /// Throws the typed <c>repository-unavailable</c> failure of <see cref="Capture"/>
    /// from filesystem facts alone, so the safety sweep can report a missing
    /// checkout without starting Git.
    /// </summary>
    internal static void EnsureRepository(string repositoryPath)
    {
        if (!Directory.Exists(repositoryPath)
            || GitRefSignature.ResolveGitDirectory(repositoryPath) is null)
            throw new DirectoryNotFoundException("repository-unavailable");
    }

    internal static bool FilesUnchanged(GitEffectiveConfig config)
        => config.Files.All(file => file.Hash is not null
            && string.Equals(file.Hash, FileHash(file.Path), StringComparison.Ordinal));

    /// <summary>
    /// True while a new capture would read exactly the same files: every
    /// contributing file is byte-identical and no recorded absent source has
    /// appeared. Reads files only and never starts Git.
    /// </summary>
    internal static bool SourcesUnchanged(GitEffectiveConfig config)
        => FilesUnchanged(config) && (config.AbsentSources ?? []).All(path => !File.Exists(path));

    private static bool IsIncludePath(string key)
        => string.Equals(key, "include.path", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("includeif.", StringComparison.OrdinalIgnoreCase)
                && key.EndsWith(".path", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveIncludePath(string value, string? sourcePath)
    {
        if (value.Length == 0) return null;
        try
        {
            if (value.StartsWith("~/", StringComparison.Ordinal))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrEmpty(home) ? null : Path.GetFullPath(Path.Combine(home, value[2..]));
            }
            if (Path.IsPathRooted(value)) return Path.GetFullPath(value);
            var directory = sourcePath is null ? null : Path.GetDirectoryName(sourcePath);
            return directory is null ? null : Path.GetFullPath(value, directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SilentCatch.Note(ex, "GitConfigSignature: include path not resolvable");
            return null;
        }
    }

    private static IEnumerable<string> StandardSources(string repositoryPath)
    {
        if (GitRefSignature.ResolveGitDirectory(repositoryPath) is { } gitDirectory)
        {
            yield return Path.GetFullPath(Path.Combine(gitDirectory, "config.worktree"));
            var common = GitRefSignature.ResolveCommonGitDirectory(gitDirectory);
            yield return Path.GetFullPath(Path.Combine(common, "config"));
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) yield return Path.GetFullPath(Path.Combine(home, ".gitconfig"));
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = !string.IsNullOrWhiteSpace(xdg) ? xdg
            : string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".config");
        if (configHome is not null) yield return Path.GetFullPath(Path.Combine(configHome, "git", "config"));
    }

    private static string? FileHash(string path)
    {
        try { return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "GitConfigSignature: config dependency read failed");
            return null;
        }
    }
}

internal sealed record GitConfigFileStamp(string Path, string? Hash);
internal sealed record GitEffectiveConfig(string Signature, string? OriginUrl,
    IReadOnlyList<GitConfigFileStamp> Files,
    IReadOnlyList<string>? AbsentSources = null,
    bool DependsOnHead = false);

/// <summary>Shares the one Git-resolved origin lookup across an index run.</summary>
internal static class GitConfigScope
{
    private static readonly AsyncLocal<(string Root, string? Origin)?> Current = new();

    internal static IDisposable Begin(string root, string? origin)
    {
        var previous = Current.Value;
        Current.Value = (NormalizeRoot(root), origin);
        return new Scope(() => Current.Value = previous);
    }

    internal static bool TryReadOrigin(string root, out string? origin)
    {
        var current = Current.Value;
        if (current is { } value && FileSystemPathComparer.Instance.Equals(value.Root, NormalizeRoot(root)))
        {
            origin = value.Origin;
            return true;
        }
        origin = null;
        return false;
    }

    internal static string NormalizeRoot(string root)
        => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
