using System.Collections.Concurrent;

namespace AgentStudio.TestSupport;

/// <summary>
/// Starting repositories built once per test process and copied per test
/// (AGT-3419).
///
/// Research card AGT-3347 counted 7,018 Git children in one incomplete backend
/// run. Most of them were fixture setup: every test ran <c>git init</c>,
/// <c>git config</c>, <c>git add</c> and <c>git commit</c> again to arrive at
/// the same starting repository. A fixture that only needs such a starting
/// point names it with a key, builds it with real Git the first time, and gets a
/// plain folder copy every later time. What the test does with its copy still
/// runs real Git.
///
/// <code>
/// GitRepositoryTemplate.CopyTo("batch-pilot:v1", _workspace, root =>
/// {
///     Git(root, "init", "-q", "-b", "develop");
///     ...
/// });
/// </code>
///
/// The template folder lives below the suite temp root and is never handed to a
/// test. Its files and folders are read-only, so a stray write fails instead of
/// changing the next test's start. Copies are real copies, not hard links, so a
/// test writing into its own working tree cannot reach the template either.
/// Absolute paths a recipe writes into Git metadata, such as a remote URL
/// pointing at a sibling bare repository, are rewritten to the destination;
/// a copy that would still point into the template fails.
///
/// One key names one recipe for the whole process. Change the key when the
/// recipe changes shape.
/// </summary>
public static class GitRepositoryTemplate
{
    private static readonly ConcurrentDictionary<string, Lazy<Template>> Templates = new(StringComparer.Ordinal);

    /// <summary>
    /// Copies the template named <paramref name="key"/> into
    /// <paramref name="destination"/>, building it with <paramref name="build"/>
    /// first if this process has not built it yet. <paramref name="build"/>
    /// receives an empty folder and must not keep a reference to it. The
    /// destination may already exist; template entries are added to it.
    /// </summary>
    public static void CopyTo(string key, string destination, Action<string> build)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        Resolve(key, build).CopyTo([("", Path.GetFullPath(destination))]);
    }

    /// <summary>
    /// Copies each named top-level folder of the template to its own
    /// destination, for fixtures whose repository and origin sit at
    /// caller-chosen paths. Paths into one placed folder are rewritten to its
    /// destination; template entries outside every placement are not copied.
    /// </summary>
    public static void CopyTo(string key, Action<string> build, params (string Folder, string Destination)[] placements)
    {
        ArgumentOutOfRangeException.ThrowIfZero(placements.Length);
        foreach (var (folder, destination) in placements)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(folder);
            ArgumentException.ThrowIfNullOrWhiteSpace(destination);
            if (folder.IndexOfAny(['/', '\\']) >= 0 || folder is "." or "..")
                throw new ArgumentException($"Template folder must be one top-level name: {folder}", nameof(placements));
        }
        Resolve(key, build).CopyTo(placements
            .Select(placement => (placement.Folder, Path.GetFullPath(placement.Destination)))
            .ToArray());
    }

    private static Template Resolve(string key, Action<string> build)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(build);
        return Templates.GetOrAdd(key, _ => new Lazy<Template>(
            () => Template.Build(build), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private sealed class Template
    {
        private readonly IReadOnlyList<Entry> _entries;

        private Template(string root, IReadOnlyList<Entry> entries)
        {
            Root = root;
            _entries = entries;
        }

        public string Root { get; }

        public static Template Build(Action<string> build)
        {
            var root = TestTempRoot.Create("git-template");
            try
            {
                build(root);
            }
            catch
            {
                TestTempRoot.TryDelete(root);
                throw;
            }

            var entries = new List<Entry>();
            Collect(root, root, entries);
            foreach (var entry in entries)
                MakeReadOnly(Path.Combine(root, entry.RelativePath), entry.IsDirectory);
            MakeReadOnly(root, isDirectory: true);
            return new Template(root, entries);
        }

        /// <param name="placements">
        /// Template folder (relative to the root, <c>""</c> for the root itself)
        /// and the absolute destination it is copied to.
        /// </param>
        public void CopyTo(IReadOnlyList<(string Folder, string Destination)> placements)
        {
            // Longest source first, so a placed folder's own path is rewritten
            // before a shorter prefix of it could be. FETCH_HEAD names a remote
            // without its `.git` suffix, so that form is rewritten as well.
            var rewrites = placements
                .Select(placement => (Source: placement.Folder.Length == 0 ? Root : Path.Combine(Root, placement.Folder),
                    placement.Destination))
                .SelectMany(rewrite => new[]
                {
                    rewrite,
                    (Source: TrimGitSuffix(rewrite.Source), Destination: TrimGitSuffix(rewrite.Destination)),
                })
                .Distinct()
                .OrderByDescending(rewrite => rewrite.Source.Length)
                .ToArray();

            foreach (var (folder, destination) in placements)
            {
                Directory.CreateDirectory(destination);
                foreach (var entry in _entries)
                {
                    if (!TryRelativeTo(folder, entry.RelativePath, out var relative)) continue;
                    var source = Path.Combine(Root, entry.RelativePath);
                    var target = Path.Combine(destination, relative);
                    if (entry.IsDirectory)
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    if (entry.RewritePaths)
                    {
                        var text = File.ReadAllText(source);
                        foreach (var rewrite in rewrites)
                        {
                            text = text.Replace(rewrite.Source, rewrite.Destination, StringComparison.Ordinal);
                            text = text.Replace(Slashed(rewrite.Source), Slashed(rewrite.Destination), StringComparison.Ordinal);
                        }
                        if (text.Contains(Root, StringComparison.Ordinal)
                            || text.Contains(Slashed(Root), StringComparison.Ordinal))
                            throw new InvalidOperationException(
                                $"Template file {entry.RelativePath} still points into the template after placement; place every folder it references.");
                        File.WriteAllText(target, text);
                    }
                    else
                    {
                        File.Copy(source, target, overwrite: false);
                    }
                    RestoreMode(target, entry);
                }
            }
        }

        private static string TrimGitSuffix(string path)
            => path.EndsWith(".git", StringComparison.Ordinal) ? path[..^".git".Length] : path;

        // Git for Windows may write a path it was given with forward slashes.
        private static string Slashed(string path) => path.Replace('\\', '/');

        private static bool TryRelativeTo(string folder, string entryPath, out string relative)
        {
            relative = entryPath;
            if (folder.Length == 0) return true;
            if (entryPath.Length <= folder.Length + 1
                || !entryPath.StartsWith(folder, StringComparison.Ordinal)
                || entryPath[folder.Length] != Path.DirectorySeparatorChar)
                return false;
            relative = entryPath[(folder.Length + 1)..];
            return true;
        }

        private static void Collect(string root, string directory, List<Entry> entries)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException(
                        $"Git repository template entries must be plain files or folders: {path}");

                var relative = Path.GetRelativePath(root, path);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    entries.Add(new Entry(relative, IsDirectory: true, RewritePaths: false, null, false));
                    Collect(root, path, entries);
                    continue;
                }

                entries.Add(new Entry(
                    relative,
                    IsDirectory: false,
                    RewritePaths: CarriesAbsolutePaths(path, root),
                    OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path),
                    (attributes & FileAttributes.ReadOnly) != 0));
            }
        }

        // Git keeps absolute paths in config (remote URLs), in FETCH_HEAD, and
        // in a worktree's `.git` file and `gitdir`. Object and index files are
        // binary and stay byte copies.
        private static bool CarriesAbsolutePaths(string path, string root)
        {
            var name = Path.GetFileName(path);
            if (name is not ("config" or "FETCH_HEAD" or ".git" or "gitdir")) return false;
            var text = File.ReadAllText(path);
            return text.Contains(root, StringComparison.Ordinal)
                || text.Contains(Slashed(root), StringComparison.Ordinal);
        }

        // A read-only folder is what stops Git itself: it writes refs, the
        // index and new objects through a lock file and a rename, which a
        // read-only file alone does not prevent. Windows ignores the attribute
        // on folders; there only the files are protected.
        private static void MakeReadOnly(string path, bool isDirectory)
        {
            if (OperatingSystem.IsWindows())
            {
                if (!isDirectory) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                return;
            }
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        }

        private static void RestoreMode(string path, Entry entry)
        {
            if (OperatingSystem.IsWindows())
            {
                var attributes = File.GetAttributes(path);
                File.SetAttributes(path, entry.WasReadOnly
                    ? attributes | FileAttributes.ReadOnly
                    : attributes & ~FileAttributes.ReadOnly);
                return;
            }
            File.SetUnixFileMode(path, entry.Mode!.Value);
        }
    }

    private sealed record Entry(
        string RelativePath,
        bool IsDirectory,
        bool RewritePaths,
        UnixFileMode? Mode,
        bool WasReadOnly);
}
