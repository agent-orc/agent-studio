using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>
/// Opt-in class selection for .NET test projects by type reference (AGT-3085).
/// Only the deferred Remote Review plan passes it: there the batch gate's Full
/// run on the combined candidate is the safety net for tests that reach a
/// change only indirectly. Every other caller keeps the whole impacted project.
/// </summary>
public sealed record DotNetReferenceSelection
{
    /// <summary>
    /// Reads the delivery's changed paths from the subject tree in one call.
    /// Null reads them from the planner's repository path.
    /// </summary>
    public Func<IReadOnlyList<string>, DotNetSourceOverlay>? Overlay { get; init; }

    /// <summary>
    /// Serves the identifier index of one test project (repo-relative project
    /// path) for the checkout's current commit, or builds it. Null builds it on
    /// every plan.
    /// </summary>
    public Func<string, Func<DotNetTestIndex>, DotNetTestIndex>? IndexMemo { get; init; }

    /// <summary>Clock for the plan-build time recorded in the audit.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// The delivery's version of the requested paths. A <c>null</c> content is a
/// path the subject tree does not contain (a deletion). <see cref="Failure"/>
/// is set when the overlay could not be read at all.
/// </summary>
public sealed record DotNetSourceOverlay(
    IReadOnlyDictionary<string, string?> Files,
    string? Failure = null)
{
    public static DotNetSourceOverlay Failed(string reason)
        => new(new Dictionary<string, string?>(StringComparer.Ordinal), reason);
}

/// <summary>
/// Identifier index of the <c>.cs</c> files of one test project, as read from
/// the checkout. The changed paths of a delivery are overlaid per plan.
/// </summary>
public sealed class DotNetTestIndex
{
    internal DotNetTestIndex(IEnumerable<DotNetIndexedFile> files)
        => Files = files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyDictionary<string, DotNetIndexedFile> Files { get; }

    public int FileCount => Files.Count;
}

/// <summary>One indexed source file with its repo-relative path.</summary>
/// <param name="ProcessReferences">
/// The <c>[Trait("ProcessReference", "&lt;ProjectFileName&gt;")]</c> values of each
/// top-level type that carries one at type level.
/// </param>
/// <param name="MachineBoundTypes">Top-level types with a type-level <c>Category=MachineBound</c> trait.</param>
internal sealed record DotNetIndexedFile(
    string Path,
    IReadOnlyList<string> DeclaredTypes,
    IReadOnlyList<string> TestClasses,
    IReadOnlySet<string> Tokens,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ProcessReferences,
    IReadOnlySet<string> MachineBoundTypes);

/// <summary>Per test project evidence of a reference selection.</summary>
public sealed record DotNetReferenceSelectionAudit
{
    public string TestProject { get; init; } = "";
    public IReadOnlyList<string> SeedTypes { get; init; } = [];
    public IReadOnlyList<string> SeedFiles { get; init; } = [];
    public IReadOnlyList<string> SelectedClasses { get; init; } = [];
    public int TotalClasses { get; init; }
    public IReadOnlyList<string> SelectedFiles { get; init; } = [];
    public int ClosureDepth { get; init; }

    /// <summary>Why the whole project runs, or why it gets no command.</summary>
    public string? FallbackReason { get; init; }

    /// <summary>
    /// Process launch selections from traits or an explicit compiled-reference
    /// mapping, with the selected classes named in each line.
    /// </summary>
    public IReadOnlyList<string> ProcessReferences { get; init; } = [];
    public string Summary { get; init; } = "";
}

/// <summary>The outcome for one impacted test project.</summary>
internal enum DotNetReferenceOutcomeKind
{
    Selected,
    WholeProject,
    NoCommand,
}

internal sealed record DotNetReferenceOutcome(
    DotNetReferenceOutcomeKind Kind,
    DotNetReferenceSelectionAudit Audit);

/// <summary>
/// Source scanning shared by the index and the overlay: top-level type
/// declarations, test classes, and identifier tokens of raw file text.
/// </summary>
internal static class DotNetSourceScanner
{
    /// <summary>
    /// The trait a test class carries when it starts the built output of a
    /// project referenced with <c>ReferenceOutputAssembly="false"</c>. Its value
    /// is that project's file name without extension, for example
    /// <c>TaskServer</c>.
    /// </summary>
    public const string ProcessReferenceTrait = "ProcessReference";

    /// <summary>
    /// A top-level type declaration at column zero, the house style of a
    /// file-scoped namespace. Nested types are indented and never match, so a
    /// generic nested name such as <c>Options</c> does not become a seed.
    /// </summary>
    private static readonly Regex TopLevelTypeDeclaration = new(
        @"^(?<attributes>(?:\[[^\]\r\n]*\]\s*)*)"
        + @"(?<modifiers>(?:(?:public|internal|private|protected|file|sealed|abstract|static|partial|unsafe|readonly|ref|new)\s+)*)"
        + @"(?<kind>class|interface|enum|struct|record(?:\s+(?:class|struct))?)\s+"
        + @"(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex ProcessReferenceAttribute = new(
        @"\bTrait(?:Attribute)?\s*\(\s*""" + ProcessReferenceTrait + @"""\s*,\s*""(?<project>[^""]+)""\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MachineBoundAttribute = new(
        @"\bTrait(?:Attribute)?\s*\(\s*""Category""\s*,\s*""MachineBound""\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TestAttribute = new(
        @"[\[,]\s*(?:Xunit\.)?(?:Skippable)?(?:Fact|Theory)(?:Attribute)?\s*[\]\(,]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static DotNetIndexedFile Scan(string relativePath, string text)
    {
        var declarations = TopLevelTypeDeclaration.Matches(text)
            .Select(match => (
                match.Index,
                Name: match.Groups["name"].Value,
                Attributes: match.Groups["attributes"].Value,
                IsClass: match.Groups["kind"].Value == "class",
                Runnable: !Regex.IsMatch(match.Groups["modifiers"].Value, @"\b(?:abstract|static)\b")))
            .ToList();
        var attributed = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match attribute in TestAttribute.Matches(text))
        {
            var owner = declarations.LastOrDefault(declaration => declaration.Index < attribute.Index);
            if (owner.Name is not null) attributed.Add(owner.Name);
        }
        var testClasses = declarations
            .Where(declaration => declaration.IsClass && declaration.Runnable
                && (attributed.Contains(declaration.Name) || IsTestClassName(declaration.Name)))
            .Select(declaration => declaration.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var processReferences = declarations
            .GroupBy(declaration => declaration.Name, StringComparer.Ordinal)
            .Select(group => (Name: group.Key, Projects: group
                .SelectMany(declaration => ProcessReferenceAttribute.Matches(declaration.Attributes))
                .Select(match => match.Groups["project"].Value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()))
            .Where(entry => entry.Projects.Count > 0)
            .ToDictionary(entry => entry.Name, entry => (IReadOnlyList<string>)entry.Projects, StringComparer.Ordinal);
        return new DotNetIndexedFile(
            relativePath,
            declarations.Select(declaration => declaration.Name).Distinct(StringComparer.Ordinal).ToList(),
            testClasses,
            Tokens(text),
            processReferences,
            declarations
                .Where(declaration => MachineBoundAttribute.IsMatch(declaration.Attributes))
                .Select(declaration => declaration.Name)
                .ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Identifier tokens of the raw text. Comments and strings are not
    /// stripped: a mention there selects a file, and a superset is acceptable.
    /// </summary>
    public static HashSet<string> Tokens(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var lookup = tokens.GetAlternateLookup<ReadOnlySpan<char>>();
        var index = 0;
        while (index < text.Length)
        {
            var current = text[index];
            if (char.IsLetter(current) || current == '_')
            {
                var start = index++;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'))
                    index++;
                lookup.Add(text.AsSpan(start, index - start));
            }
            else if (char.IsDigit(current))
            {
                // A numeric literal such as 0x1F must not yield the token x1F.
                index++;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'))
                    index++;
            }
            else
            {
                index++;
            }
        }
        return tokens;
    }

    private static bool IsTestClassName(string name)
        => name.EndsWith("Tests", StringComparison.Ordinal)
            || name.EndsWith("Test", StringComparison.Ordinal);
}

/// <summary>
/// The pure reference selection for one plan. Rules, in the order they are
/// checked for an impacted test project:
/// <list type="number">
/// <item>A whole-project fallback names its reason: a repository-level build
/// input, a changed non-C# file, a composition root, an unreadable or deleted
/// file, a changed file without a top-level type.</item>
/// <item>Seeds are the top-level types of the changed <c>.cs</c> files of the
/// referenced production projects and of the test project itself.</item>
/// <item>A test-project file is selected when one of its identifier tokens
/// names a seed; its own top-level types become seeds, to a fixpoint.</item>
/// <item>The changed test files' directory siblings are added, as in the
/// test-only slice.</item>
/// <item>A changed project the test project references only at process level
/// (<c>ReferenceOutputAssembly="false"</c>) adds the test classes whose
/// <c>ProcessReference</c> trait names it, except those that are MachineBound
/// at class level (AGT-3108).</item>
/// <item>The backend test project's compiled Task Server reference also selects
/// <c>ConnectorNegativeMatrixTests</c>, which starts the Task Server process.</item>
/// <item>An empty combined selection gets no command; a combined selection
/// above half of the project's classes or a filter above 16 KB runs the whole
/// project.</item>
/// </list>
/// </summary>
internal sealed class DotNetReferenceSelectionPlanner
{
    /// <summary>More than half of the classes: the slice saves too little.</summary>
    internal const double MaxSelectedShare = 0.5;

    /// <summary>
    /// The composed <c>--filter</c> value keeps a 2x margin under the Windows
    /// command-line limit of 32,767 characters.
    /// </summary>
    internal const int MaxFilterCharacters = 16_384;

    /// <summary>Same bound as the test-only slice's directory expansion.</summary>
    private const int MaxSiblingFiles = 20;

    internal const string EmptySelectionReason =
        "no test class references the changed types; the batch gate runs the full suite";

    private static readonly HashSet<string> RepositoryBuildInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp",
        "Directory.Packages.props", "global.json", "NuGet.config",
    };

    private static readonly HashSet<string> CompositionRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program.cs", "Startup.cs",
    };

    private readonly string _repositoryPath;
    private readonly DotNetReferenceSelection _options;
    private readonly string? _buildInput;
    private readonly IReadOnlyDictionary<string, string?> _contents;
    private readonly string? _overlayFailure;
    private readonly IReadOnlySet<string> _requested;

    private DotNetReferenceSelectionPlanner(
        string repositoryPath,
        DotNetReferenceSelection options,
        string? buildInput,
        IReadOnlyDictionary<string, string?> contents,
        string? overlayFailure,
        IReadOnlySet<string> requested)
    {
        _repositoryPath = repositoryPath;
        _options = options;
        _buildInput = buildInput;
        _contents = contents;
        _overlayFailure = overlayFailure;
        _requested = requested;
    }

    /// <summary>
    /// Reads the changed <c>.cs</c> paths of every impacted project once: one
    /// overlay call per plan, never one per test project.
    /// </summary>
    /// <param name="readPaths">Repo-relative changed <c>.cs</c> paths inside impacted projects.</param>
    public static DotNetReferenceSelectionPlanner Prepare(
        string repositoryPath,
        IReadOnlyList<string> changedFiles,
        IReadOnlyCollection<string> readPaths,
        DotNetReferenceSelection options)
    {
        var buildInput = changedFiles.FirstOrDefault(path =>
            !IsGeneratedPath(path) && RepositoryBuildInputs.Contains(FileName(path)));
        // A build input change keeps every impacted project whole, so nothing
        // needs to be read.
        var paths = (buildInput is null ? readPaths : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var requested = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
            return new(repositoryPath, options, buildInput,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase), null, requested);

        if (options.Overlay is null)
        {
            var local = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var path in paths)
                {
                    var absolute = Absolute(repositoryPath, path);
                    local[path] = File.Exists(absolute) ? File.ReadAllText(absolute) : null;
                }
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "DotNetReferenceSelectionPlanner: changed file read");
                return new(repositoryPath, options, buildInput, local, ex.Message, requested);
            }
            return new(repositoryPath, options, buildInput, local, null, requested);
        }

        DotNetSourceOverlay overlay;
        try
        {
            overlay = options.Overlay(paths);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "DotNetReferenceSelectionPlanner: overlay read");
            overlay = DotNetSourceOverlay.Failed(ex.Message);
        }
        return new(repositoryPath, options, buildInput,
            new Dictionary<string, string?>(overlay.Files, StringComparer.OrdinalIgnoreCase),
            overlay.Failure, requested);
    }

    /// <summary>
    /// The outcome for one impacted test project.
    /// </summary>
    /// <param name="testProject">Repo-relative test project file.</param>
    /// <param name="ownChangedFiles">Repo-relative changed files owned by the test project.</param>
    /// <param name="productionChanges">
    /// Changed files of each touched referenced project, keyed by the
    /// repo-relative project file.
    /// </param>
    /// <param name="processLevelChanges">
    /// Touched projects the test project references with
    /// <c>ReferenceOutputAssembly="false"</c>. Their files are never read: the
    /// tests cannot name their types, so they select by trait instead.
    /// </param>
    /// <param name="filterExpression">The project's own category filter.</param>
    public DotNetReferenceOutcome Plan(
        string testProject,
        IReadOnlyList<string> ownChangedFiles,
        IReadOnlyDictionary<string, IReadOnlyList<string>> productionChanges,
        IReadOnlyList<string> processLevelChanges,
        string filterExpression)
    {
        if (_buildInput is not null)
            return Whole(testProject, $"repository-level build input changed: {_buildInput}");

        var seeds = new SortedSet<string>(StringComparer.Ordinal);
        var seedFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var testChanges = new Dictionary<string, DotNetIndexedFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var (project, changed) in productionChanges.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var projectDirectory = DirectoryOf(project);
            foreach (var path in changed.Where(path => !IsGeneratedPath(path)))
            {
                var inHost = RelativeTo(projectDirectory, path).Split('/')[..^1]
                    .Any(part => part.Equals("Host", StringComparison.OrdinalIgnoreCase));
                if (inHost)
                    return Whole(testProject, $"composition root changed: {path}");
                if (ReadChanged(path, out var scanned) is { } reason)
                    return Whole(testProject, reason);
                foreach (var name in scanned!.DeclaredTypes) seeds.Add(name);
                seedFiles.Add(path);
            }
        }

        foreach (var path in ownChangedFiles.Where(path => !IsGeneratedPath(path)))
        {
            if (ReadChanged(path, out var scanned) is { } reason)
                return Whole(testProject, reason);
            foreach (var name in scanned!.DeclaredTypes) seeds.Add(name);
            seedFiles.Add(path);
            testChanges[path] = scanned;
        }

        DotNetTestIndex index;
        try
        {
            index = _options.IndexMemo is null
                ? BuildIndex(testProject)
                : _options.IndexMemo(testProject, () => BuildIndex(testProject));
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "DotNetReferenceSelectionPlanner: identifier index");
            return Whole(testProject, $"test project index could not be read: {ex.Message}");
        }

        var files = index.Files.Values
            .Where(file => !testChanges.ContainsKey(file.Path))
            .Concat(testChanges.Values)
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();
        var totalClasses = files.SelectMany(file => file.TestClasses).Distinct(StringComparer.Ordinal).Count();

        var (closure, depth) = Closure(files, seeds);
        var selectedFiles = closure
            .Concat(SiblingFiles(files, testChanges.Keys))
            .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();
        var classes = new SortedSet<string>(
            selectedFiles.SelectMany(file => file.TestClasses), StringComparer.Ordinal);

        var processLines = new List<string>();
        foreach (var project in processLevelChanges
                     .Select(ProjectName)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.Ordinal))
        {
            var (line, traitClasses) = ProcessReferenceSelection(files, project);
            processLines.Add(line);
            classes.UnionWith(traitClasses);
            selectedFiles = selectedFiles
                .Concat(files.Where(file => file.TestClasses.Any(traitClasses.Contains)))
                .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .ToList();
        }
        if (testProject.Equals("backend.Tests/OrchestratorApi.Tests.csproj", StringComparison.OrdinalIgnoreCase)
            && productionChanges.Keys.Any(project =>
                project.Equals("task-server/TaskServer.csproj", StringComparison.OrdinalIgnoreCase)))
        {
            const string processTest = "ConnectorNegativeMatrixTests";
            var mappedFiles = files.Where(file => file.TestClasses.Contains(processTest, StringComparer.Ordinal))
                .ToList();
            var runnable = mappedFiles.Count > 0
                && mappedFiles.All(file => !file.MachineBoundTypes.Contains(processTest));
            if (runnable)
            {
                classes.Add(processTest);
                selectedFiles = selectedFiles.Concat(mappedFiles)
                    .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
                processLines.Add("compiled TaskServer process launch: ConnectorNegativeMatrixTests");
            }
        }
        var fileCount = selectedFiles.Count;

        var audit = new DotNetReferenceSelectionAudit
        {
            TestProject = testProject,
            SeedTypes = seeds.ToList(),
            SeedFiles = seedFiles.ToList(),
            SelectedClasses = classes.ToList(),
            TotalClasses = totalClasses,
            SelectedFiles = selectedFiles.Select(file => file.Path).ToList(),
            ClosureDepth = depth,
            ProcessReferences = processLines,
        };

        if (classes.Count == 0)
        {
            var reason = processLines.Count == 0
                ? EmptySelectionReason
                : seeds.Count == 0
                    ? $"{string.Join("; ", processLines)}; no command"
                    : $"{string.Join("; ", processLines)}; {EmptySelectionReason}";
            return new(DotNetReferenceOutcomeKind.NoCommand, audit with
            {
                SelectedClasses = [],
                FallbackReason = reason,
                Summary = $"{testProject}: {reason}",
            });
        }

        if (classes.Count > totalClasses * MaxSelectedShare)
        {
            return Whole(testProject,
                $"{classes.Count} of {totalClasses} test classes selected, more than half "
                + "of the project", audit);
        }

        var filterLength = FilterValue(filterExpression, classes).Length;
        if (filterLength > MaxFilterCharacters)
        {
            return Whole(testProject,
                $"filter of {classes.Count} classes is {filterLength} characters, above "
                + $"{MaxFilterCharacters}", audit);
        }

        var summary = $"work package: {classes.Count} of {totalClasses} test classes selected "
            + $"({fileCount} files, closure depth {depth})";
        if (seeds.Count > 0)
            summary += $" by reference to {seeds.Count} changed types in {seedFiles.Count} files";
        if (processLines.Count > 0)
            summary += $"; {string.Join("; ", processLines)}";
        return new(DotNetReferenceOutcomeKind.Selected, audit with { Summary = summary });
    }

    /// <summary>
    /// The test classes whose type-level <c>ProcessReference</c> trait names
    /// <paramref name="project"/>, without those that are MachineBound at type
    /// level: every review and gate command excludes that category, so they
    /// would run nothing. A partial class counts its traits from every part.
    /// </summary>
    private static (string Line, IReadOnlySet<string> Classes) ProcessReferenceSelection(
        IReadOnlyList<DotNetIndexedFile> files,
        string project)
    {
        var traitClasses = files
            .SelectMany(file => file.TestClasses.Select(name => (Name: name, File: file)))
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .Where(group => group.Any(entry =>
                entry.File.ProcessReferences.TryGetValue(group.Key, out var projects)
                && projects.Contains(project, StringComparer.OrdinalIgnoreCase)))
            .Select(group => (Name: group.Key,
                MachineBound: group.Any(entry => entry.File.MachineBoundTypes.Contains(group.Key))))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToList();
        var kept = traitClasses.Where(entry => !entry.MachineBound).Select(entry => entry.Name).ToList();
        var dropped = traitClasses.Where(entry => entry.MachineBound).Select(entry => entry.Name).ToList();
        var prefix = $"process-level reference {project}: ";
        if (traitClasses.Count == 0)
            return (prefix + $"no test class carries [Trait(\"{DotNetSourceScanner.ProcessReferenceTrait}\", "
                + $"\"{project}\")]", new HashSet<string>(StringComparer.Ordinal));
        if (kept.Count == 0)
            return (prefix + $"only MachineBound classes ({string.Join(", ", dropped)})",
                new HashSet<string>(StringComparer.Ordinal));
        var line = prefix + $"{kept.Count} {(kept.Count == 1 ? "class" : "classes")} ({string.Join(", ", kept)})";
        if (dropped.Count > 0)
            line += $", {dropped.Count} MachineBound dropped ({string.Join(", ", dropped)})";
        return (line, kept.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// The value a <c>ProcessReference</c> trait uses for a project: its file
    /// name without extension.
    /// </summary>
    internal static string ProjectName(string projectPath)
        => Path.GetFileNameWithoutExtension(FileName(projectPath));

    /// <summary>The composed filter value, as <see cref="TestSelectionPlanner.DotNetFilterSuffix"/> writes it.</summary>
    internal static string FilterValue(string expression, IEnumerable<string> classes)
        => $"({expression})&({string.Join("|", classes.Select(name => $"FullyQualifiedName~{name}"))})";

    /// <summary>
    /// Builds the identifier index of one test project from the checkout.
    /// <c>bin/</c> and <c>obj/</c> are skipped, as the project inventory does.
    /// </summary>
    internal static DotNetTestIndex BuildIndex(string repositoryPath, string testProject)
    {
        var directory = Absolute(repositoryPath, DirectoryOf(testProject));
        if (!Directory.Exists(directory)) return new DotNetTestIndex([]);
        var files = new List<DotNetIndexedFile>();
        foreach (var absolute in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Normalize(Path.GetRelativePath(repositoryPath, absolute));
            if (IsGeneratedPath(Path.GetRelativePath(directory, absolute))) continue;
            files.Add(DotNetSourceScanner.Scan(relative, File.ReadAllText(absolute)));
        }
        return new DotNetTestIndex(files);
    }

    private DotNetTestIndex BuildIndex(string testProject) => BuildIndex(_repositoryPath, testProject);

    /// <summary>
    /// Reads and scans one changed file. Returns the whole-project reason when
    /// the file cannot carry seeds.
    /// </summary>
    private string? ReadChanged(string path, out DotNetIndexedFile? scanned)
    {
        scanned = null;
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return $"non-C# input changed: {path}";
        if (CompositionRootFiles.Contains(FileName(path)))
            return $"composition root changed: {path}";
        if (_overlayFailure is not null && _requested.Contains(path))
            return $"delivery overlay could not be read: {_overlayFailure}";
        if (!_contents.TryGetValue(path, out var content))
            return $"delivery overlay could not read {path}";
        if (content is null)
            return $"changed file was deleted: {path}";
        scanned = DotNetSourceScanner.Scan(path, content);
        if (scanned.DeclaredTypes.Count == 0)
            return $"changed file declares no top-level type: {path}";
        return null;
    }

    private static (IReadOnlyList<DotNetIndexedFile> Files, int Depth) Closure(
        IReadOnlyList<DotNetIndexedFile> files,
        IReadOnlyCollection<string> initialSeeds)
    {
        var seeds = new HashSet<string>(initialSeeds, StringComparer.Ordinal);
        var frontier = initialSeeds.ToList();
        var selected = new List<DotNetIndexedFile>();
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var depth = 0;
        while (frontier.Count > 0)
        {
            var tokens = frontier.SelectMany(MatchTokens).ToHashSet(StringComparer.Ordinal);
            var found = files
                .Where(file => !selectedPaths.Contains(file.Path) && tokens.Overlaps(file.Tokens))
                .ToList();
            if (found.Count == 0) break;
            depth++;
            frontier = [];
            foreach (var file in found)
            {
                selected.Add(file);
                selectedPaths.Add(file.Path);
                foreach (var name in file.DeclaredTypes)
                    if (seeds.Add(name)) frontier.Add(name);
            }
        }
        return (selected, depth);
    }

    /// <summary>A seed <c>FooAttribute</c> also matches its short form <c>[Foo]</c>.</summary>
    private static IEnumerable<string> MatchTokens(string seed)
    {
        yield return seed;
        const string suffix = "Attribute";
        if (seed.Length > suffix.Length && seed.EndsWith(suffix, StringComparison.Ordinal))
            yield return seed[..^suffix.Length];
    }

    /// <summary>
    /// The test-only slice's directory expansion: every file in a changed test
    /// file's directory, unless the directory is a flat root.
    /// </summary>
    private static IEnumerable<DotNetIndexedFile> SiblingFiles(
        IReadOnlyList<DotNetIndexedFile> files,
        IEnumerable<string> changedTestFiles)
    {
        foreach (var directory in changedTestFiles.Select(DirectoryOf).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var siblings = files
                .Where(file => string.Equals(DirectoryOf(file.Path), directory, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (siblings.Count > MaxSiblingFiles) continue;
            foreach (var file in siblings) yield return file;
        }
    }

    private DotNetReferenceOutcome Whole(
        string testProject,
        string reason,
        DotNetReferenceSelectionAudit? audit = null)
        => new(DotNetReferenceOutcomeKind.WholeProject, (audit ?? new DotNetReferenceSelectionAudit
        {
            TestProject = testProject,
        }) with
        {
            SelectedClasses = [],
            FallbackReason = reason,
            Summary = $"work package: whole test project {testProject} ({reason})",
        });

    private static string Absolute(string repositoryPath, string relative)
        => Path.GetFullPath(Path.Combine(repositoryPath, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string DirectoryOf(string path)
    {
        var normalized = Normalize(path);
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? "" : normalized[..slash];
    }

    private static string RelativeTo(string directory, string path)
    {
        var normalized = Normalize(path);
        return directory.Length > 0 && normalized.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase)
            ? normalized[(directory.Length + 1)..]
            : normalized;
    }

    private static string FileName(string path)
    {
        var normalized = Normalize(path);
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    private static bool IsGeneratedPath(string path)
        => Normalize(path).Split('/').Any(part =>
            part.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || part.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
