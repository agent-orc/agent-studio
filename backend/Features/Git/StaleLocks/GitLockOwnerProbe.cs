using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Diagnostics;

namespace AgentStudio.Git;

/// <summary>
/// The directories a git write on one repository can take locks in: the
/// working tree, its private git directory, and the shared common directory
/// (they differ for a linked worktree). <see cref="SharedWorkTrees"/> lists
/// every other working tree on the same common directory (the main checkout
/// and its linked worktrees): git running from any of them can hold a shared
/// ref lock without naming this path. When that list could not be read
/// completely, <see cref="SharedWorkTreesComplete"/> is false and ownership
/// cannot be disproven.
/// </summary>
public sealed record GitLockScope(
    string WorkTree,
    string GitDirectory,
    string CommonDirectory,
    IReadOnlyList<string>? SharedWorkTrees = null,
    bool SharedWorkTreesComplete = true)
{
    public IEnumerable<string> Paths()
        => new[] { WorkTree, GitDirectory, CommonDirectory }
            .Concat(SharedWorkTrees ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Answers whether any running git process may hold a lock in a repository.</summary>
public interface IGitLockOwnerProbe
{
    GitLockOwnership Probe(GitLockScope scope);
}

/// <summary>
/// Live git children of this server, keyed by their working directory. Every
/// backend spawn through <see cref="GitNetworkProcessRunner"/> registers here,
/// so the stale-lock guard can tell "our own git is still running in that
/// repository" without an OS inventory (on Windows the inventory cannot read
/// another process's working directory).
/// </summary>
public static class GitChildProcessRegistry
{
    private static readonly ConcurrentDictionary<long, string> Live = new();
    private static long _next;

    public static IDisposable Track(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)) return NoopScope.Instance;
        var id = Interlocked.Increment(ref _next);
        Live[id] = workingDirectory;
        return new Scope(id);
    }

    public static IReadOnlyList<string> WorkingDirectories() => Live.Values.ToArray();

    private sealed class Scope(long id) : IDisposable
    {
        public void Dispose() => Live.TryRemove(id, out _);
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Production probe. A lock is owned when a live server child runs git with its
/// working directory inside the repository, or when the OS process list shows
/// a git process tied to the repository: on Linux by <c>/proc/&lt;pid&gt;/cwd</c>
/// or command line, on Windows by a <c>git*.exe</c> whose command line names the
/// path. A process whose working directory cannot be read and whose command
/// line does not identify this repository leaves ownership unknown.
/// </summary>
public sealed class GitProcessLockOwnerProbe : IGitLockOwnerProbe
{
    private readonly Func<IReadOnlyList<string>> _serverChildren;
    private readonly Func<IReadOnlyList<GitProcessObservation>?> _inventory;

    public GitProcessLockOwnerProbe()
        : this(GitChildProcessRegistry.WorkingDirectories, ReadInventory)
    {
    }

    internal GitProcessLockOwnerProbe(
        Func<IReadOnlyList<string>> serverChildren,
        Func<IReadOnlyList<GitProcessObservation>?> inventory)
    {
        _serverChildren = serverChildren;
        _inventory = inventory;
    }

    public GitLockOwnership Probe(GitLockScope scope)
    {
        var paths = scope.Paths().Select(NormalizeDirectory).ToArray();
        if (_serverChildren().Any(cwd => IsInside(cwd, paths)))
            return GitLockOwnership.Owned;

        // A sibling working tree we could not resolve may host the owner.
        if (!scope.SharedWorkTreesComplete) return GitLockOwnership.Unknown;

        var processes = _inventory();
        if (processes is null) return GitLockOwnership.Unknown;
        if (processes.Any(process => IsTiedTo(process, paths)))
            return GitLockOwnership.Owned;
        // Win32_Process does not expose cwd. `git add` started inside this
        // repository can have no repository path in its command line, so an
        // unmatched process without cwd cannot prove the lock is unowned.
        return processes.Any(process => process.WorkingDirectory is null)
            ? GitLockOwnership.Unknown
            : GitLockOwnership.None;
    }

    internal static bool IsTiedTo(GitProcessObservation process, IReadOnlyList<string> normalizedPaths)
    {
        if (process.WorkingDirectory is not null && IsInside(process.WorkingDirectory, normalizedPaths))
            return true;
        if (string.IsNullOrWhiteSpace(process.CommandLine)) return false;
        var commandLine = process.CommandLine.Replace('\\', '/');
        return normalizedPaths.Any(path => commandLine.Contains(path, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInside(string candidate, IReadOnlyList<string> normalizedPaths)
    {
        var normalized = NormalizeDirectory(candidate);
        return normalizedPaths.Any(path =>
            string.Equals(normalized, path, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDirectory(string path)
    {
        try { path = Path.GetFullPath(path); }
        catch (Exception ex) { SilentCatch.Note(ex, "GitProcessLockOwnerProbe: path normalisation is best-effort"); }
        return path.Replace('\\', '/').TrimEnd('/');
    }

    private static IReadOnlyList<GitProcessObservation>? ReadInventory()
    {
        if (OperatingSystem.IsLinux()) return ReadLinuxInventory();
        if (OperatingSystem.IsWindows()) return ReadWindowsInventory();
        // No portable inventory elsewhere; server children were checked above.
        return [];
    }

    private static IReadOnlyList<GitProcessObservation>? ReadLinuxInventory()
        => ReadLinuxInventory("/proc", File.ReadAllText, path => new FileInfo(path).LinkTarget);

    internal static IReadOnlyList<GitProcessObservation>? ReadLinuxInventory(
        string procRoot,
        Func<string, string> readText,
        Func<string, string?> readLinkTarget)
    {
        try
        {
            var result = new List<GitProcessObservation>();
            foreach (var dir in Directory.EnumerateDirectories(procRoot))
            {
                var name = Path.GetFileName(dir);
                if (!int.TryParse(name, out var pid) || pid == Environment.ProcessId) continue;
                try
                {
                    var comm = readText(Path.Combine(dir, "comm")).Trim();
                    if (!comm.StartsWith("git", StringComparison.Ordinal)) continue;
                    var cwd = readLinkTarget(Path.Combine(dir, "cwd"));
                    var cmdline = readText(Path.Combine(dir, "cmdline")).Replace('\0', ' ').Trim();
                    result.Add(new GitProcessObservation(pid, comm, cwd, cmdline));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A process that vanished while /proc was being read
                    // cannot own the lock. A still-present unreadable entry
                    // may be git in this repository, so fail closed.
                    if (!Directory.Exists(dir) && IsDefinitelyGone(pid)) continue;
                    SilentCatch.Note(ex, "GitProcessLockOwnerProbe: unreadable /proc entry makes ownership unknown");
                    return null;
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "GitProcessLockOwnerProbe: /proc inventory unavailable");
            return null;
        }
    }

    private static bool IsDefinitelyGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "GitProcessLockOwnerProbe: process exit check is inconclusive");
            return false;
        }
    }

    private static IReadOnlyList<GitProcessObservation>? ReadWindowsInventory()
    {
        const string script =
            "$ErrorActionPreference='Stop'; " +
            "@(Get-CimInstance Win32_Process -Filter \"Name LIKE 'git%'\" | Select-Object ProcessId,Name,CommandLine) | ConvertTo-Json -Compress";
        var json = ReadProcessOutput(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }, TimeSpan.FromSeconds(15));
        if (json is null) return null;
        try { return ParseWindowsInventory(json); }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "GitProcessLockOwnerProbe: Win32_Process inventory unavailable");
            return null;
        }
    }

    // The output pipes can stay open even after the parent exits (for example,
    // when a descendant inherits them). Bound both reads and exit together.
    internal static string? ReadProcessOutput(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        try { return ReadProcessOutputAsync(startInfo, timeout).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "GitProcessLockOwnerProbe: process inventory unavailable");
            return null;
        }
    }

    private static async Task<string?> ReadProcessOutputAsync(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        using var process = Process.Start(startInfo);
        if (process is null) return null;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var exit = process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr, exit).WaitAsync(timeout).ConfigureAwait(false);
            return process.ExitCode == 0 ? await stdout.ConfigureAwait(false) : null;
        }
        catch (TimeoutException ex)
        {
            SilentCatch.Note(ex, "GitProcessLockOwnerProbe: process inventory timed out");
            return null;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "GitProcessLockOwnerProbe: inventory kill is best-effort"); }
            }
        }
    }

    internal static IReadOnlyList<GitProcessObservation> ParseWindowsInventory(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var document = JsonDocument.Parse(json);
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];
        return elements.Select(element => new GitProcessObservation(
                element.GetProperty("ProcessId").GetInt32(),
                GetString(element, "Name"),
                WorkingDirectory: null,
                GetString(element, "CommandLine")))
            .ToArray();
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>One running git process as the OS inventory reports it.</summary>
public sealed record GitProcessObservation(
    int ProcessId,
    string? Name,
    string? WorkingDirectory,
    string? CommandLine);
