namespace AgentStudio.Git;

/// <summary>What <see cref="GitRefFiles"/> could tell from the files alone.</summary>
internal enum GitRefFileAnswer
{
    /// <summary>The files do not settle the question; ask Git.</summary>
    Unknown,
    /// <summary>Git would report the name as missing.</summary>
    Absent,
    /// <summary>Git would print the object ID returned with this answer.</summary>
    Found,
}

/// <summary>
/// Answers the small repository reads that <see cref="GitService"/> asked one
/// Git process each for from the repository's files (AGT-3567):
/// <c>rev-parse --verify --quiet</c> of a full ref or a pseudo-ref,
/// <c>rev-parse HEAD</c>, <c>rev-parse --abbrev-ref HEAD</c>,
/// <c>--git-dir</c>, <c>--git-common-dir</c>, the linked-worktree check and
/// whether a remote is configured. The integration path asks them dozens of
/// times per merge; in <c>MergeIntoDevelopRunnerTests</c> they were about half
/// of all Git processes. Like <see cref="GitRefObjectIds"/> it reads loose ref
/// files and <c>packed-refs</c> and never starts Git.
///
/// <para>Only the plain files-backend case is answered: a work tree root with
/// its own <c>.git</c> directory or <c>gitdir:</c> file, a full ref name under
/// <c>refs/</c> outside the per-worktree namespaces, and a loose ref that holds
/// an object ID. Anything else - a reftable repository, a symbolic ref, a
/// subdirectory, a bare repository, <c>GIT_DIR</c> in the environment, an
/// unreadable file - is <see cref="GitRefFileAnswer.Unknown"/>, and the caller
/// runs Git exactly as before. Git itself does not check that the object
/// exists for these reads, so neither does this reader.</para>
/// </summary>
internal static class GitRefFiles
{
    private static readonly string[] PerWorktreePrefixes = ["refs/bisect/", "refs/worktree/", "refs/rewritten/"];

    /// <summary>
    /// What <c>git rev-parse --verify --quiet &lt;name&gt;</c> answers in
    /// <paramref name="repoRoot"/> for a full name under <c>refs/</c> or a
    /// branch-like short name, trying Git's spellings of it
    /// (<c>refs/heads/&lt;name&gt;</c>, <c>refs/remotes/&lt;name&gt;</c> and so on)
    /// in Git's order.
    /// </summary>
    internal static GitRefFileAnswer TryResolve(string repoRoot, string name, out string? objectId)
    {
        objectId = null;
        var shortName = !name.StartsWith("refs/", StringComparison.Ordinal);
        if (shortName ? !IsPlainShortName(name) : !IsPlainRefName(name)) return GitRefFileAnswer.Unknown;
        if (ResolveLayout(repoRoot) is not { } layout) return GitRefFileAnswer.Unknown;
        // Git first reads a short name as written, from the Git directory.
        if (shortName && (File.Exists(Path.Combine(layout.GitDirectory, name))
                          || File.Exists(Path.Combine(layout.Common, name))))
            return GitRefFileAnswer.Unknown;
        string[] candidates =
        [
            name, "refs/" + name, "refs/tags/" + name, "refs/heads/" + name,
            "refs/remotes/" + name, "refs/remotes/" + name + "/HEAD",
        ];
        foreach (var candidate in shortName ? candidates[1..] : candidates)
        {
            if (IsPerWorktree(candidate)) return GitRefFileAnswer.Unknown;
            var answer = ReadExact(layout.Common, candidate, out objectId);
            if (answer != GitRefFileAnswer.Absent) return answer;
        }
        return GitRefFileAnswer.Absent;
    }

    /// <summary>What <c>git rev-parse HEAD</c> prints in <paramref name="repoRoot"/>.</summary>
    internal static GitRefFileAnswer TryResolveHead(string repoRoot, out string? objectId)
    {
        objectId = null;
        if (ResolveLayout(repoRoot) is not { } layout) return GitRefFileAnswer.Unknown;
        var head = ReadFirstLine(Path.Combine(layout.GitDirectory, "HEAD"), out var unreadable);
        if (unreadable || head is null) return GitRefFileAnswer.Unknown;
        if (IsObjectId(head))
        {
            objectId = head;
            return GitRefFileAnswer.Found;
        }
        const string symbolic = "ref: ";
        if (!head.StartsWith(symbolic, StringComparison.Ordinal)) return GitRefFileAnswer.Unknown;
        var target = head[symbolic.Length..].Trim();
        if (!IsPlainRefName(target) || IsPerWorktree(target)) return GitRefFileAnswer.Unknown;
        // An unborn branch: git rev-parse HEAD fails.
        return ReadExact(layout.Common, target, out objectId);
    }

    /// <summary>
    /// What <c>git rev-parse --abbrev-ref HEAD</c> prints in
    /// <paramref name="repoRoot"/>: <c>HEAD</c> when detached, otherwise the
    /// branch name. A branch whose short name could also mean another ref (a
    /// tag of the same name, say) is left to Git, which would print a longer
    /// form; an unborn branch is <see cref="GitRefFileAnswer.Absent"/>, because
    /// Git fails there.
    /// </summary>
    internal static GitRefFileAnswer TryReadAbbreviatedHead(string repoRoot, out string? name)
    {
        name = null;
        if (ResolveLayout(repoRoot) is not { } layout) return GitRefFileAnswer.Unknown;
        var head = ReadFirstLine(Path.Combine(layout.GitDirectory, "HEAD"), out var unreadable);
        if (unreadable || head is null) return GitRefFileAnswer.Unknown;
        if (IsObjectId(head))
        {
            name = "HEAD";
            return GitRefFileAnswer.Found;
        }
        const string symbolic = "ref: refs/heads/";
        if (!head.StartsWith(symbolic, StringComparison.Ordinal)) return GitRefFileAnswer.Unknown;
        var branch = head[symbolic.Length..].Trim();
        if (!IsPlainRefName("refs/heads/" + branch)) return GitRefFileAnswer.Unknown;
        var tip = ReadExact(layout.Common, "refs/heads/" + branch, out _);
        if (tip != GitRefFileAnswer.Found) return tip;
        // Git shortens to the branch name only when no other spelling of it names a ref.
        if (File.Exists(Path.Combine(layout.GitDirectory, branch)) || File.Exists(Path.Combine(layout.Common, branch)))
            return GitRefFileAnswer.Unknown;
        foreach (var other in new[] { "refs/" + branch, "refs/tags/" + branch, "refs/remotes/" + branch,
                     "refs/remotes/" + branch + "/HEAD" })
        {
            if (ReadExact(layout.Common, other, out _) != GitRefFileAnswer.Absent) return GitRefFileAnswer.Unknown;
        }
        name = branch;
        return GitRefFileAnswer.Found;
    }

    /// <summary>
    /// What <c>git rev-parse --verify --quiet &lt;name&gt;</c> answers for a
    /// pseudo-ref such as <c>MERGE_HEAD</c>, which lives in the work tree's own
    /// Git directory, including Git's other spellings of the name.
    /// </summary>
    internal static GitRefFileAnswer TryResolvePseudoRef(string repoRoot, string name, out string? objectId)
    {
        objectId = null;
        if (name.Length == 0 || !name.All(c => c is >= 'A' and <= 'Z' or '_'))
            return GitRefFileAnswer.Unknown;
        if (ResolveLayout(repoRoot) is not { } layout) return GitRefFileAnswer.Unknown;
        var value = ReadFirstLine(Path.Combine(layout.GitDirectory, name), out var unreadable);
        if (unreadable) return GitRefFileAnswer.Unknown;
        if (value is not null)
        {
            if (!IsObjectId(value)) return GitRefFileAnswer.Unknown;
            objectId = value;
            return GitRefFileAnswer.Found;
        }
        foreach (var candidate in new[] { "refs/" + name, "refs/tags/" + name, "refs/heads/" + name,
                     "refs/remotes/" + name, "refs/remotes/" + name + "/HEAD" })
        {
            var answer = ReadExact(layout.Common, candidate, out objectId);
            if (answer != GitRefFileAnswer.Absent) return answer;
        }
        return GitRefFileAnswer.Absent;
    }

    /// <summary>
    /// True when the repository configuration file names
    /// <paramref name="remote"/> with a URL, so <c>git remote</c> lists it; null
    /// otherwise. Other configuration files (global, included, per worktree)
    /// can only add remotes, so a true answer stands and anything else is left
    /// to Git.
    /// </summary>
    internal static bool? DefinesRemote(string repoRoot, string remote)
    {
        if (remote.Length == 0 || !remote.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            return null;
        if (ResolveLayout(repoRoot) is not { } layout) return null;
        try
        {
            var config = Path.Combine(layout.Common, "config");
            if (!File.Exists(config)) return null;
            var inSection = false;
            foreach (var raw in File.ReadLines(config))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    var close = line.IndexOf(']');
                    if (close < 0) return null;
                    inSection = IsRemoteSection(line[1..close], remote);
                    line = line[(close + 1)..].Trim();
                }
                if (!inSection || line.Length == 0 || line[0] is '#' or ';') continue;
                var equals = line.IndexOf('=');
                if (equals <= 0) continue;
                if (string.Equals(line[..equals].Trim(), "url", StringComparison.OrdinalIgnoreCase)
                    && line[(equals + 1)..].Trim().Length > 0)
                    return true;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "GitRefFiles: repository config read failed");
            return null;
        }
    }

    /// <summary>
    /// <c>remote "&lt;name&gt;"</c>: the section name is case-insensitive, the
    /// quoted subsection is not. The legacy <c>[remote.name]</c> form is not
    /// matched and leaves the answer to Git.
    /// </summary>
    private static bool IsRemoteSection(string header, string remote)
    {
        header = header.Trim();
        const string section = "remote";
        if (header.Length <= section.Length || !header.StartsWith(section, StringComparison.OrdinalIgnoreCase)
            || !char.IsWhiteSpace(header[section.Length]))
            return false;
        return string.Equals(header[section.Length..].Trim(), $"\"{remote}\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// What <c>git rev-parse --show-toplevel</c> prints for
    /// <paramref name="path"/> when it is a work tree root itself, or null when
    /// Git might print something else: a subdirectory, a configured
    /// <c>core.worktree</c>, a bare repository, a per-worktree configuration, a
    /// symbolic link anywhere in the path (Git prints the resolved path), or
    /// Windows path spelling.
    /// </summary>
    internal static string? WorkTreeRoot(string path)
    {
        if (OperatingSystem.IsWindows()
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_WORK_TREE")))
            return null;
        if (ResolveLayout(path) is not { } layout) return null;
        try
        {
            if (File.Exists(Path.Combine(layout.GitDirectory, "config.worktree"))) return null;
            foreach (var config in new[] { Path.Combine(layout.GitDirectory, "config"), Path.Combine(layout.Common, "config") }.Distinct())
            {
                if (!File.Exists(config)) continue;
                foreach (var raw in File.ReadLines(config))
                {
                    var line = raw.Trim();
                    var equals = line.IndexOf('=');
                    var key = (equals < 0 ? line : line[..equals]).Trim();
                    if (key.Equals("worktree", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("bare", StringComparison.OrdinalIgnoreCase)
                           && !line[(equals + 1)..].Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
                        return null;
                }
            }
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            {
                if (directory.LinkTarget is not null) return null;
            }
            return root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SilentCatch.Note(ex, "GitRefFiles: work tree root resolution failed");
            return null;
        }
    }

    /// <summary>
    /// The absolute Git directory of a work tree root (Git's
    /// <c>--git-dir</c>), or null when the files do not settle it.
    /// </summary>
    internal static string? GitDirectory(string repoRoot) => ResolveLayout(repoRoot)?.GitDirectory;

    /// <summary>
    /// The absolute common Git directory of a work tree root (Git's
    /// <c>--git-common-dir</c>), or null when the files do not settle it.
    /// </summary>
    internal static string? CommonDirectory(string repoRoot) => ResolveLayout(repoRoot)?.Common;

    /// <summary>
    /// True for a linked worktree, false for a primary checkout (Git's
    /// <c>--git-dir</c> differs from <c>--git-common-dir</c>), null when the
    /// files do not settle it.
    /// </summary>
    internal static bool? IsLinkedWorktree(string repoRoot)
        => ResolveLayout(repoRoot) is { } layout
            ? !string.Equals(layout.GitDirectory, layout.Common, PathComparison)
            : null;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static GitRefFileAnswer ReadExact(string common, string name, out string? objectId)
    {
        objectId = null;
        var loose = Path.Combine(common, name.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(loose))
        {
            var value = ReadFirstLine(loose, out var unreadable);
            if (unreadable) return GitRefFileAnswer.Unknown;
            if (value is not null)
            {
                if (!IsObjectId(value)) return GitRefFileAnswer.Unknown;
                objectId = value;
                return GitRefFileAnswer.Found;
            }
        }
        // Git writes packed-refs before it removes the loose copy, so a ref
        // that is being packed is always in one of the two.
        return ReadPacked(Path.Combine(common, "packed-refs"), name, out objectId);
    }

    private static GitRefFileAnswer ReadPacked(string path, string name, out string? objectId)
    {
        objectId = null;
        try
        {
            if (!File.Exists(path)) return GitRefFileAnswer.Absent;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || line[0] is '#' or '^') continue;
                var space = line.IndexOf(' ');
                if (space <= 0 || !line.AsSpan(space + 1).SequenceEqual(name)) continue;
                var value = line[..space];
                if (!IsObjectId(value)) return GitRefFileAnswer.Unknown;
                objectId = value;
                return GitRefFileAnswer.Found;
            }
            return GitRefFileAnswer.Absent;
        }
        catch (FileNotFoundException)
        {
            return GitRefFileAnswer.Absent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "GitRefFiles: packed-refs read failed");
            return GitRefFileAnswer.Unknown;
        }
    }

    /// <summary>The first line, trimmed; null when the file is missing.</summary>
    private static string? ReadFirstLine(string path, out bool unreadable)
    {
        unreadable = false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadLine()?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "GitRefFiles: ref file read failed");
            unreadable = true;
            return null;
        }
    }

    private sealed record Layout(string GitDirectory, string Common);

    /// <summary>
    /// The Git directory and common directory of a work tree root, as Git
    /// discovers them without environment overrides; null otherwise.
    /// </summary>
    private static Layout? ResolveLayout(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot)) return null;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_DIR"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_COMMON_DIR")))
            return null;
        try
        {
            var root = Path.GetFullPath(repoRoot);
            var dotGit = Path.Combine(root, ".git");
            string gitDirectory;
            if (Directory.Exists(dotGit))
            {
                gitDirectory = dotGit;
            }
            else
            {
                var marker = ReadFirstLine(dotGit, out var unreadable);
                const string prefix = "gitdir:";
                if (unreadable || marker is null || !marker.StartsWith(prefix, StringComparison.Ordinal))
                    return null;
                gitDirectory = Path.GetFullPath(marker[prefix.Length..].Trim(), root);
            }
            if (!File.Exists(Path.Combine(gitDirectory, "HEAD"))) return null;

            var common = gitDirectory;
            var commonFile = Path.Combine(gitDirectory, "commondir");
            if (File.Exists(commonFile))
            {
                var configured = File.ReadAllText(commonFile).Trim();
                if (configured.Length == 0) return null;
                common = Path.GetFullPath(configured, gitDirectory);
            }
            if (Directory.Exists(Path.Combine(common, "reftable"))
                || !Directory.Exists(Path.Combine(common, "refs")))
                return null;
            return new Layout(Path.TrimEndingDirectorySeparator(gitDirectory),
                Path.TrimEndingDirectorySeparator(common));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            SilentCatch.Note(ex, "GitRefFiles: repository layout resolution failed");
            return null;
        }
    }

    private static bool IsPerWorktree(string name)
        => PerWorktreePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// A full ref name whose loose file path is unambiguous: <c>refs/</c>
    /// followed by segments of letters, digits, <c>-</c>, <c>_</c>,
    /// <c>.</c> and <c>+</c> that Git's ref-format rules accept.
    /// </summary>
    internal static bool IsPlainRefName(string? name)
    {
        if (name is null || !name.StartsWith("refs/", StringComparison.Ordinal) || name.Length > 1024)
            return false;
        foreach (var segment in name.Split('/'))
        {
            if (segment.Length == 0 || segment[0] == '.' || segment.EndsWith('.')
                || segment.EndsWith(".lock", StringComparison.Ordinal))
                return false;
            foreach (var c in segment)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '+')) return false;
        }
        return !name.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>
    /// A branch-like short name (<c>develop</c>, <c>origin/develop</c>,
    /// <c>task/20</c>). Names Git could also read as an abbreviated object ID
    /// or a pseudo-ref such as <c>HEAD</c> are not plain.
    /// </summary>
    private static bool IsPlainShortName(string name)
        => name.Length > 0
           && !name.All(char.IsAsciiHexDigitLower)
           && name.Any(char.IsAsciiLetterLower)
           && IsPlainRefName("refs/" + name);

    private static bool IsObjectId(string value)
        => value.Length is 40 or 64 && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}
