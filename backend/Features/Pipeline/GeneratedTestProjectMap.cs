using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>One <c>ProjectReference</c> of a generated map's project.</summary>
/// <param name="Project">Repo-relative project file.</param>
/// <param name="ProcessLevel">
/// <c>ReferenceOutputAssembly="false"</c>: the referencing tests start the
/// program as a process and cannot name its types.
/// </param>
public sealed record GeneratedProjectReference(string Project, bool ProcessLevel);

/// <summary>
/// The folder-to-test-project map generated at plan time (AGT-3310, pipeline
/// Dossier tier T1). Every project directory maps to the test projects whose
/// transitive <c>ProjectReference</c> closure contains it, process-level
/// references included. A Node package directory maps to the routine test
/// suites of <c>project.yml</c> named after it (<c>frontend/</c> to
/// <c>frontend-unit</c>). Paths outside every row stay unmapped, so
/// <see cref="DeterministicTestScope"/> runs the full suite for them.
/// </summary>
public sealed record GeneratedTestProjectMap
{
    public IReadOnlyList<TestFolderMapping> Rows { get; init; } = [];

    /// <summary>Repo-relative project files of the graph.</summary>
    public IReadOnlyList<string> ProjectFiles { get; init; } = [];

    /// <summary>Direct references per repo-relative project file.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<GeneratedProjectReference>> References { get; init; }
        = new Dictionary<string, IReadOnlyList<GeneratedProjectReference>>();

    /// <summary>Declared test command per mapped test-suite id.</summary>
    public IReadOnlyDictionary<string, string> SuiteCommands { get; init; }
        = new Dictionary<string, string>();

    /// <summary>SHA-256 of the rows and suite commands, recorded in the test-selection audit.</summary>
    public string Digest { get; init; } = "";

    /// <summary>Why no map could be generated; the scope then runs the full suite.</summary>
    public string? Failure { get; init; }

    /// <summary>
    /// Inputs that reach every test project without a <c>ProjectReference</c>:
    /// the shared build props, the SDK pin, the project definition, and the
    /// shared test support. A changed global input always runs the full suite.
    /// </summary>
    public static string? GlobalInput(string path)
    {
        var normalized = Normalize(path);
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        if (name.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase)) return "Directory.Build.*";
        if (normalized.Equals("global.json", StringComparison.OrdinalIgnoreCase)) return "global.json";
        if (normalized.StartsWith(".agent-studio/", StringComparison.OrdinalIgnoreCase)) return ".agent-studio/**";
        if (normalized.StartsWith("testsupport/", StringComparison.OrdinalIgnoreCase)) return "testsupport/**";
        return null;
    }

    /// <summary>
    /// The map is generated from the planning checkout. A diff that edits a
    /// project file changes the graph the map was built from, so the map is
    /// stale for that diff.
    /// </summary>
    public string? StaleReason(IReadOnlyList<string> changedFiles)
        => changedFiles.FirstOrDefault(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            is { } project
                ? $"stale generated project map: the diff changes the project graph ({Normalize(project)})"
                : null;

    /// <summary>True when <paramref name="project"/>'s row maps to <paramref name="testProject"/>.</summary>
    public bool Covers(string project, string testProject)
        => Rows.Any(row => row.Folder.Equals(DirectoryOf(Normalize(project)), StringComparison.OrdinalIgnoreCase)
            && row.TestProjects.Contains(Normalize(testProject), StringComparer.OrdinalIgnoreCase));

    /// <summary>The project file whose directory owns <paramref name="path"/>, or null.</summary>
    public string? OwningProject(string path)
    {
        var normalized = Normalize(path);
        return ProjectFiles
            .Where(project => DirectoryOf(project) is { Length: > 0 } directory
                && normalized.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(project => DirectoryOf(project).Length)
            .FirstOrDefault();
    }

    public static GeneratedTestProjectMap Generate(string repositoryPath, VerifyPlan verifyPlan)
    {
        var tracked = TrackedRepositoryFiles.Read(repositoryPath);
        var projects = ProjectFilesOf(repositoryPath, tracked);
        if (projects.FirstOrDefault(project => DirectoryOf(project).Length == 0) is { } rootProject)
            return new GeneratedTestProjectMap
            {
                Failure = $"a project file sits at the repository root ({rootProject}); every path belongs to it",
            };

        var known = projects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var references = projects.ToDictionary(
            project => project,
            project => (IReadOnlyList<GeneratedProjectReference>)TestSelectionPlanner
                .ProjectReferenceEntries(Path.Combine(repositoryPath, project))
                .Select(entry => new GeneratedProjectReference(
                    Normalize(Path.GetRelativePath(repositoryPath, entry.Path)), entry.ProcessLevel))
                .Where(entry => known.Contains(entry.Project))
                .DistinctBy(entry => entry.Project, StringComparer.OrdinalIgnoreCase)
                .OrderBy(entry => entry.Project, StringComparer.Ordinal)
                .ToList(),
            StringComparer.OrdinalIgnoreCase);

        var coveredBy = projects.ToDictionary(project => project,
            _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
        foreach (var testProject in projects.Where(project =>
                     TestSelectionPlanner.IsTestProject(Path.Combine(repositoryPath, project))))
        {
            foreach (var reached in Closure(testProject, references))
                coveredBy[reached].Add(testProject);
        }

        var rows = projects
            .GroupBy(DirectoryOf, StringComparer.OrdinalIgnoreCase)
            .Select(group => new TestFolderMapping
            {
                Folder = group.Key,
                Module = group.Key,
                TestProjects = group.SelectMany(project => coveredBy[project])
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList(),
            })
            .ToList();

        var suites = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var definition = ProjectDefinitionReader.ReadWorkspace(repositoryPath);
        var declaredTests = verifyPlan.Commands.Where(command => command.Kind == VerifyCommandKind.Test)
            .Select(command => command.Command.Trim()).ToHashSet(StringComparer.Ordinal);
        if (definition.IsValid && definition.Definition is { } execution)
        {
            foreach (var package in VerifyCommandPlanner.NodePackageDirs(repositoryPath, tracked)
                         .Where(package => package.Length > 0
                             && !rows.Any(row => row.Folder.Equals(package, StringComparison.OrdinalIgnoreCase))))
            {
                var mapped = execution.TestSuites
                    .Where(suite => suite.Category.Equals("routine", StringComparison.OrdinalIgnoreCase)
                        && suite.Id.StartsWith(package + "-", StringComparison.OrdinalIgnoreCase)
                        && declaredTests.Contains(suite.Command.Trim()))
                    .OrderBy(suite => suite.Id, StringComparer.Ordinal)
                    .ToList();
                if (mapped.Count == 0) continue;
                foreach (var suite in mapped) suites[suite.Id] = suite.Command.Trim();
                rows.Add(new TestFolderMapping
                {
                    Folder = package,
                    Module = package,
                    TestProjects = mapped.Select(suite => suite.Id).ToList(),
                });
            }
        }

        rows = rows.OrderBy(row => row.Folder, StringComparer.Ordinal).ToList();
        var canonical = new StringBuilder();
        foreach (var row in rows)
            canonical.Append(row.Folder).Append('\t').AppendJoin(',', row.TestProjects).Append('\n');
        foreach (var (id, command) in suites)
            canonical.Append("suite\t").Append(id).Append('\t').Append(command).Append('\n');
        return new GeneratedTestProjectMap
        {
            Rows = rows,
            ProjectFiles = projects,
            References = references,
            SuiteCommands = suites,
            Digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))),
        };
    }

    private static IEnumerable<string> Closure(
        string start, IReadOnlyDictionary<string, IReadOnlyList<GeneratedProjectReference>> references)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var pending = new Stack<string>([start]);
        while (pending.TryPop(out var project))
        {
            foreach (var reference in references.TryGetValue(project, out var direct) ? direct : [])
                if (seen.Add(reference.Project)) pending.Push(reference.Project);
        }
        return seen;
    }

    private static IReadOnlyList<string> ProjectFilesOf(string repositoryPath, IReadOnlySet<string>? tracked)
    {
        IEnumerable<string> candidates = tracked is not null
            ? tracked.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(repositoryPath, path)))
            : Directory.Exists(repositoryPath)
                ? Directory.EnumerateFiles(repositoryPath, "*.csproj", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(repositoryPath, path))
                : [];
        return candidates.Select(Normalize)
            .Where(path => !path.Split('/').Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || part.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || part.Equals("node_modules", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string DirectoryOf(string project)
    {
        var index = project.LastIndexOf('/');
        return index < 0 ? "" : project[..index];
    }

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized.TrimEnd('/');
    }
}
