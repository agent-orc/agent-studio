using System.Text.RegularExpressions;
using AgentStudio.OrchestrationDecisions;

namespace AgentRunner;

/// <summary>
/// RT-3 (RT-D6 B): the repository folders one failing test covers, by static
/// name match as in the Dossier's <c>evidence/test-folder-reach.py</c>. The test
/// source file is found by class name inside the test project. Every identifier
/// of at least eight characters that names a tracked repository type file
/// (<c>Name.cs</c> or <c>Name.Part.cs</c>) outside a test project reaches that
/// file's folder. Types defined in more than two folders and the hub types are
/// ignored. This is not runtime coverage.
/// </summary>
public static partial class PostMergeCoveringFolders
{
    /// <summary>The hub types the Dossier's evidence script ignores.</summary>
    public static readonly IReadOnlySet<string> HubTypes = new HashSet<string>(StringComparer.Ordinal)
        { "Program", "ProjectSettings", "TaskStates", "TaskJob", "RoutePermissionCatalog" };

    public const int MaximumFoldersPerType = 2;

    [GeneratedRegex(@"\b([A-Z][A-Za-z0-9]{7,})\b")]
    private static partial Regex Identifier();

    public sealed record Result(string? TestFile, IReadOnlyList<string> Folders, bool Fallback);

    /// <summary>
    /// <paramref name="trackedFiles"/> are repository-relative paths from
    /// <c>git ls-files</c>; <paramref name="read"/> returns a file's text.
    /// </summary>
    public sealed class Index
    {
        private readonly IReadOnlyList<string> _tracked;
        private readonly Func<string, string?> _read;
        private readonly Dictionary<string, HashSet<string>> _types = new(StringComparer.Ordinal);

        public Index(IReadOnlyList<string> trackedFiles, Func<string, string?> read)
        {
            _tracked = trackedFiles.Select(Normalize).ToArray();
            _read = read;
            foreach (var path in _tracked.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         && !IsTestOrOutputPath(path)))
            {
                var name = Path.GetFileName(path)[..^3].Split('.')[0];
                var slash = path.LastIndexOf('/');
                if (!_types.TryGetValue(name, out var folders)) _types[name] = folders = new(StringComparer.Ordinal);
                folders.Add(slash < 0 ? "" : path[..slash]);
            }
        }

        /// <summary>
        /// <paramref name="test"/> is a fully qualified .NET method name or a
        /// Vitest failure name; <paramref name="testProjectDirectory"/> is the
        /// folder of the suite's test project, or null when unknown.
        /// </summary>
        public Result For(string test, string? testProjectDirectory)
        {
            var vitest = PostMergeAttributor.VitestFile(test);
            if (vitest is not null)
            {
                // Spec files name frontend symbols, not repository type files; the area holds them.
                var spec = _tracked.FirstOrDefault(path => path == vitest || path.EndsWith("/" + vitest, StringComparison.Ordinal));
                return new(spec, [], true);
            }
            var segments = test.Split('.');
            if (segments.Length < 2 || testProjectDirectory is null) return new(null, [], true);
            var className = segments[^2].Split('`', '+')[0];
            var prefix = Normalize(testProjectDirectory).TrimEnd('/');
            var inProject = _tracked.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && (prefix.Length == 0 || path.StartsWith(prefix + "/", StringComparison.Ordinal))
                && !path.Contains("/bin/", StringComparison.Ordinal) && !path.Contains("/obj/", StringComparison.Ordinal))
                .ToArray();
            var declaration = new Regex(@"\b(?:class|record|struct)\s+" + Regex.Escape(className) + @"\b");
            var byName = inProject.Where(path => Path.GetFileName(path)[..^3].Split('.')[0] == className).ToArray();
            string? text = null;
            var file = byName.FirstOrDefault(path => (text = _read(path)) is not null && declaration.IsMatch(text));
            if (file is null)
            {
                text = null;
                file = inProject.FirstOrDefault(path => (text = _read(path)) is not null && declaration.IsMatch(text));
            }
            if (file is null || text is null) return new(null, [], true);
            var reached = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var identifier in Identifier().Matches(text).Select(match => match.Groups[1].Value)
                         .Distinct(StringComparer.Ordinal))
            {
                if (HubTypes.Contains(identifier) || !_types.TryGetValue(identifier, out var folders)
                    || folders.Count > MaximumFoldersPerType) continue;
                reached.UnionWith(folders);
            }
            // A test project's own helpers are part of the test, not code it covers.
            var list = reached.Where(folder => prefix.Length == 0
                || folder != prefix && !folder.StartsWith(prefix + "/", StringComparison.Ordinal)).ToArray();
            return new(file, list, HoldScopePolicy.UsesSuiteAreaFallback(list));
        }
    }

    /// <summary>Test projects, build output and generated folders are not repository types.</summary>
    private static bool IsTestOrOutputPath(string path)
    {
        var segments = path.Split('/');
        return segments.Any(segment => segment is "bin" or "obj" or "node_modules"
            || segment.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            || segment.EndsWith(".Test", StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }
}
