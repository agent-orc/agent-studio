using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>
/// The bounded class slice of one .NET test project plus the provenance line
/// that explains it in the gate log.
/// </summary>
internal sealed record DotNetTestSlice(IReadOnlyList<string> Classes, string Reason);

/// <summary>
/// Derives the bounded .NET unit-test slice used when an integration merge
/// changes managed sources. It is the backend counterpart of
/// <see cref="FrontendWorkPackagePlanner"/>: the slice is built only from test
/// class names declared in repository-owned files, never from executable text
/// in the diff.
///
/// <para>
/// A diff confined to a test project can be bounded to the classes it touches.
/// A diff that also changes production code that the test project references
/// cannot: no convention maps a production type to the test classes that cover
/// it, so that case keeps the whole test project and <see cref="PlanSlice"/>
/// returns <c>null</c>.
/// </para>
/// </summary>
internal static class DotNetWorkPackagePlanner
{
    /// <summary>
    /// Upper bound for the sibling expansion of one directory. A cohesive test
    /// folder is expanded like the Angular touched-folder glob; a flat test
    /// project root holding hundreds of files carries no such signal, so the
    /// slice stays at the changed files' own classes and says so in the audit.
    /// </summary>
    private const int MaxSiblingFiles = 20;

    private static readonly string[] DotNetPathSuffixes =
    [
        ".cs", ".csproj", ".fsproj", ".vbproj", ".props", ".targets",
        ".sln", ".slnx", ".razor", ".cshtml", ".resx",
    ];

    /// <summary>
    /// A top-level class declaration. Anchored at column zero so nested helper
    /// types inside a test class are not mistaken for a peer type; a file-scoped
    /// namespace keeps its types at that indentation, which is the house style.
    /// </summary>
    private static readonly Regex TopLevelClassDeclaration = new(
        @"^(?:(?:public|internal|file|sealed|abstract|static|partial|unsafe)\s+)*"
        + @"class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    /// True when the diff contains a managed compile or project input, i.e. the
    /// merge result can change what a .NET test asserts. Documentation, scripts,
    /// and workflow files alone do not.
    /// </summary>
    public static bool TouchesDotNet(IEnumerable<string>? changedFiles)
        => changedFiles?.Any(path => DotNetPathSuffixes.Any(suffix =>
            Normalize(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))) == true;

    /// <summary>
    /// The class slice for <paramref name="testProjectPath"/>, or <c>null</c>
    /// when the diff cannot be bounded and the whole test project must run.
    /// </summary>
    /// <param name="changedFilesInsideProject">
    /// Repo-relative changed files owned by this test project.
    /// </param>
    /// <param name="referencedProductionTouched">
    /// True when the diff also changed a production project this test project
    /// references. Such a change has no class-level mapping.
    /// </param>
    public static DotNetTestSlice? PlanSlice(
        string repositoryPath,
        string testProjectPath,
        IReadOnlyList<string> changedFilesInsideProject,
        bool referencedProductionTouched)
    {
        if (referencedProductionTouched || changedFilesInsideProject.Count == 0) return null;

        var classes = new SortedSet<string>(StringComparer.Ordinal);
        var expanded = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var tooWide = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var changedFile in changedFilesInsideProject)
        {
            if (!changedFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return null;
            var absolute = Path.GetFullPath(Path.Combine(
                repositoryPath, changedFile.Replace('/', Path.DirectorySeparatorChar)));

            // A file that is not purely test classes is a shared fixture, a
            // helper, a base type, or a deletion. Which tests it feeds is
            // unknown, so the slice must not pretend to cover the project.
            var declared = DeclaredTestClasses(absolute);
            if (declared is null || declared.Count == 0) return null;
            foreach (var name in declared) classes.Add(name);

            var directory = Path.GetDirectoryName(absolute);
            if (directory is null) continue;
            var siblings = SiblingFiles(directory);
            var relativeDirectory = Normalize(Path.GetRelativePath(repositoryPath, directory));
            if (siblings.Count > MaxSiblingFiles)
            {
                tooWide.Add(relativeDirectory);
                continue;
            }
            foreach (var sibling in siblings)
                foreach (var name in DeclaredTestClasses(sibling) ?? []) classes.Add(name);
            expanded.Add(relativeDirectory);
        }

        if (classes.Count == 0) return null;

        var reason = $"work package: {classes.Count} test class(es) selected from the changed files";
        if (expanded.Count > 0)
            reason += $" and their directories ({string.Join(", ", expanded)})";
        if (tooWide.Count > 0)
            reason += $"; directory expansion skipped for {string.Join(", ", tooWide)} " +
                      $"(more than {MaxSiblingFiles} files)";
        reason += $": {string.Join(", ", classes)}";
        return new DotNetTestSlice(classes.ToList(), reason);
    }

    /// <summary>
    /// The test classes <paramref name="path"/> declares, or <c>null</c> when it
    /// also declares a top-level type that is not a test class. Such a type can
    /// be a base class or a fixture for tests in other files, and a filter built
    /// from its file would silently skip them.
    /// </summary>
    private static IReadOnlyList<string>? DeclaredTestClasses(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var declared = TopLevelClassDeclaration.Matches(File.ReadAllText(path))
                .Select(match => match.Groups["name"].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return declared.All(IsTestClassName) ? declared : null;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "DotNetWorkPackagePlanner: test class scan");
            return null;
        }
    }

    private static bool IsTestClassName(string name)
        => name.EndsWith("Tests", StringComparison.Ordinal)
            || name.EndsWith("Test", StringComparison.Ordinal);

    private static IReadOnlyList<string> SiblingFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "DotNetWorkPackagePlanner: sibling scan");
            return [];
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
