using System.Diagnostics;
using System.Text;
using AgentStudio.Git;

namespace AgentStudio.Readiness;

public sealed record ReadinessGitResult(int ExitCode, string Output, string Error, bool Ran = true)
{
    public bool Success => Ran && ExitCode == 0;
}

/// <summary>The Git calls the readiness collector makes. Tests count them.</summary>
public interface IReadinessGit
{
    /// <summary>Runs git; an <paramref name="environment"/> value of null removes that variable.</summary>
    ReadinessGitResult Run(string workingDirectory, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null);
}

internal sealed class ProcessReadinessGit : IReadinessGit
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public ReadinessGitResult Run(string workingDirectory, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null) start.Environment.Remove(key);
            else start.Environment[key] = value;
        }
        var result = GitProcessBoundary.Run(start, timeout: Timeout);
        return result.FailureKind == GitProcessFailureKind.None
            ? new ReadinessGitResult(result.ExitCode, result.StandardOutput, result.StandardError)
            : new ReadinessGitResult(-1, "", string.IsNullOrWhiteSpace(result.StandardError)
                ? $"git {arguments.FirstOrDefault()} {result.FailureKind}" : result.StandardError, Ran: false);
    }
}

/// <summary>
/// The Git facts of the readiness report, each cached for
/// <see cref="CacheLifetime"/>: however often the page or the move tool asks,
/// Git runs at most once per repository per minute (AGT-3412). The service-user
/// probe (identity and <c>commit-tree</c>) counts as one repository: it runs in
/// a scratch repository the probe creates and removes.
/// </summary>
public sealed class ReadinessGitFacts(IReadinessGit git, TimeProvider clock)
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);
    internal const string ProbeAuthorName = "Agent Studio readiness probe";
    internal const string ProbeAuthorEmail = "readiness@agent-studio.invalid";
    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    private const string ServiceUserKey = "service-user";

    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset At, object Value)> _cache = new(StringComparer.Ordinal);

    public (GitIdentityFact Identity, CommitProbeFact Commit) ServiceUser()
        => Cached(ServiceUserKey, ProbeServiceUser);

    public RepositoryRefsFact Refs(string repositoryPath)
    {
        var path = Path.GetFullPath(repositoryPath);
        return Cached("repository:" + path, () => ReadRefs(path));
    }

    private T Cached<T>(string key, Func<T> read) where T : notnull
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_cache.TryGetValue(key, out var entry) && now - entry.At < CacheLifetime) return (T)entry.Value;
            var value = read();
            _cache[key] = (now, value);
            return value;
        }
    }

    private (GitIdentityFact, CommitProbeFact) ProbeServiceUser()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "agent-studio-readiness-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(scratch);
            var init = git.Run(scratch, ["init", "-q", "--object-format=sha1"]);
            if (!init.Success)
            {
                var error = FirstLine(init.Error) ?? "git init failed";
                return (new GitIdentityFact(null, null, null, null, error), new CommitProbeFact(false, false, error));
            }
            var config = git.Run(scratch, ["config", "--show-origin", "--get-regexp", @"^user\.(name|email)$"]);
            // Exit 1 is "no such key": an answer, not an error.
            var identity = config.Ran && config.ExitCode is 0 or 1
                ? ParseIdentity(config.Output)
                : new GitIdentityFact(null, null, null, null, FirstLine(config.Error) ?? "git config failed");
            // The author comes from the environment, like GitService's bound-index commit:
            // the probe proves the committer identity Git takes from its configuration.
            var commit = git.Run(scratch, ["commit-tree", EmptyTree, "-m", "readiness probe"],
                new Dictionary<string, string?>
                {
                    ["GIT_AUTHOR_NAME"] = ProbeAuthorName,
                    ["GIT_AUTHOR_EMAIL"] = ProbeAuthorEmail,
                });
            var probe = commit.Ran
                ? new CommitProbeFact(true, commit.Success, commit.Success ? commit.Output.Trim() : commit.Error.Trim())
                : new CommitProbeFact(false, false, FirstLine(commit.Error));
            return (identity, probe);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (new GitIdentityFact(null, null, null, null, error.Message), new CommitProbeFact(false, false, error.Message));
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                AgentStudio.Diagnostics.SilentCatch.Note(error, "ReadinessGitFacts: scratch repository left behind");
            }
        }
    }

    /// <summary>Parses <c>git config --show-origin --get-regexp</c>: <c>origin TAB key SPACE value</c> per line. The last value wins.</summary>
    internal static GitIdentityFact ParseIdentity(string output)
    {
        string? name = null, nameOrigin = null, email = null, emailOrigin = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var origin = line[..tab];
            var entry = line[(tab + 1)..].TrimEnd('\r');
            var space = entry.IndexOf(' ');
            var key = space < 0 ? entry : entry[..space];
            var value = space < 0 ? "" : entry[(space + 1)..];
            if (key.Equals("user.name", StringComparison.OrdinalIgnoreCase)) (name, nameOrigin) = (value, origin);
            else if (key.Equals("user.email", StringComparison.OrdinalIgnoreCase)) (email, emailOrigin) = (value, origin);
        }
        return new GitIdentityFact(name, nameOrigin, email, emailOrigin);
    }

    private RepositoryRefsFact ReadRefs(string path)
    {
        if (!Directory.Exists(path)) return RepositoryRefsFact.Missing;
        var dotGit = Path.Combine(path, ".git");
        if (!Directory.Exists(dotGit) && !File.Exists(dotGit)) return RepositoryRefsFact.NotGit;
        var result = git.Run(path, ["for-each-ref", "--format=%(refname)%09%(symref)",
            "refs/heads", "refs/remotes/origin", "refs/agent-studio/integration"]);
        return result.Success
            ? ParseRefs(result.Output)
            : new RepositoryRefsFact(true, true, new HashSet<string>(), null, FirstLine(result.Error) ?? $"exit {result.ExitCode}");
    }

    internal static RepositoryRefsFact ParseRefs(string output)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal);
        string? originHead = null;
        const string remote = "refs/remotes/origin/";
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.TrimEnd('\r').Split('\t');
            var name = parts[0];
            if (name == remote + "HEAD")
            {
                if (parts.Length > 1 && parts[1].StartsWith(remote, StringComparison.Ordinal))
                    originHead = parts[1][remote.Length..];
                continue;
            }
            refs.Add(name);
        }
        return new RepositoryRefsFact(true, true, refs, originHead);
    }

    private static string? FirstLine(string? text)
        => text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
