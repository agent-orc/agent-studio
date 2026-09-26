using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Git;

/// <summary>
/// Fingerprints Git's effective configuration. Git resolves include and
/// worktree rules; this code reads only Git's resolved output and the files
/// Git says contributed to it. Values are never logged or published.
/// </summary>
internal static class GitConfigSignature
{
    internal static GitEffectiveConfig Capture(string repositoryPath)
    {
        if (!Directory.Exists(repositoryPath)
            || GitRefSignature.ResolveGitDirectory(repositoryPath) is null)
            throw new DirectoryNotFoundException("repository-unavailable");

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

        var filePaths = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        string? originUrl = null;
        var fields = result.StandardOutput.Split('\0');
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            var source = fields[i];
            var setting = fields[i + 1];
            if (source.StartsWith("file:", StringComparison.Ordinal))
            {
                var path = source["file:".Length..];
                filePaths.Add(Path.IsPathRooted(path)
                    ? path : Path.GetFullPath(path, repositoryPath));
            }
            var newline = setting.IndexOf('\n');
            if (newline >= 0 && string.Equals(setting[..newline], "remote.origin.url",
                    StringComparison.OrdinalIgnoreCase))
            {
                var value = setting[(newline + 1)..].Trim();
                originUrl = value.Length == 0 ? null : value;
            }
        }
        var files = filePaths.OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new GitConfigFileStamp(path, FileHash(path))).ToArray();
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.StandardOutput)));
        return new GitEffectiveConfig(signature, originUrl, files);
    }

    internal static bool FilesUnchanged(GitEffectiveConfig config)
        => config.Files.All(file => file.Hash is not null
            && string.Equals(file.Hash, FileHash(file.Path), StringComparison.Ordinal));

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
    IReadOnlyList<GitConfigFileStamp> Files);

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
        if (current is { } value && string.Equals(value.Root, NormalizeRoot(root),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            origin = value.Origin;
            return true;
        }
        origin = null;
        return false;
    }

    private static string NormalizeRoot(string root)
        => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
