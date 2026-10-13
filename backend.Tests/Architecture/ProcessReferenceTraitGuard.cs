using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// Keeps the <c>ProcessReference</c> traits that the reference selection reads
/// (AGT-3108) true to the code. For one test project:
/// <list type="bullet">
/// <item>A test class that names the output assembly of a project the test
/// project references with <c>ReferenceOutputAssembly="false"</c>, directly or
/// through a helper type it names, must carry
/// <c>[Trait("ProcessReference", "&lt;ProjectFileName&gt;")]</c>.</item>
/// <item>A test class that calls <c>BuiltProcessLauncher.StartBuilt</c>,
/// directly or through a helper, must name the assembly it starts as a string
/// literal, so the first rule can match it.</item>
/// <item>A trait for a project the test project does not reference at process
/// level is stale.</item>
/// </list>
/// Calls and names are read from code only: comments and the source text
/// inside a fixture's string literal do not count. A helper is any top-level
/// type of the test project that is not a test class, so a test class named by
/// another test class lends it nothing.
/// </summary>
internal static partial class ProcessReferenceTraitGuard
{
    private static readonly string[] LauncherCalls = ["StartBuilt", "StartBuiltIn"];

    /// <param name="testProject">Repo-relative test project file.</param>
    public static IReadOnlyList<string> Check(string repositoryPath, string testProject)
    {
        var absolute = Path.GetFullPath(Path.Combine(repositoryPath, testProject.Replace('/', Path.DirectorySeparatorChar)));
        var processProjects = TestSelectionPlanner.ProjectReferenceEntries(absolute)
            .Where(reference => reference.ProcessLevel)
            .ToDictionary(
                reference => DotNetReferenceSelectionPlanner.ProjectName(reference.Path),
                reference => AssemblyFileName(reference.Path),
                StringComparer.OrdinalIgnoreCase);
        // bin/ and obj/ are skipped below the project only, so a fixture under
        // any temp root is read in full.
        var projectDirectory = Path.GetDirectoryName(absolute)!;
        var sources = Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(projectDirectory, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .Select(file => (Path: CSharpSourceScanner.Relative(repositoryPath, file), Text: CSharpSourceScanner.Read(file)))
            .ToList();
        return Check(testProject, processProjects, sources);
    }

    /// <param name="processProjects">
    /// Project file name without extension to output assembly file name, for
    /// every process-level reference of the test project.
    /// </param>
    private static IReadOnlyList<string> Check(
        string testProject,
        IReadOnlyDictionary<string, string> processProjects,
        IReadOnlyList<(string Path, string Text)> sources)
    {
        var projectByAssembly = processProjects.ToDictionary(
            pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);
        var files = sources
            .OrderBy(source => source.Path, StringComparer.Ordinal)
            .Select(source => new SourceFile(
                DotNetSourceScanner.Scan(source.Path, source.Text),
                DotNetSourceScanner.Tokens(CSharpSourceScanner.MaskCommentsAndLiterals(source.Text)),
                CSharpSourceScanner.StringLiterals(source.Text)
                    .Select(literal => AssemblyLiteral().Match(literal))
                    .Where(match => match.Success)
                    .Select(match => match.Groups["name"].Value)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .ToList();
        var testClasses = files.SelectMany(file => file.Scan.TestClasses).ToHashSet(StringComparer.Ordinal);
        var helpers = files
            .SelectMany(file => file.Scan.DeclaredTypes
                .Where(name => !testClasses.Contains(name))
                .Select(name => (Name: name, File: file)))
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.File).ToList(), StringComparer.Ordinal);

        // A helper's reach is the union of its files' own reach and the reach of
        // every helper those files name, to a fixpoint.
        var reach = helpers.Keys.ToDictionary(name => name, _ => Reach.None, StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var (name, declaring) in helpers)
            {
                var next = declaring.Aggregate(reach[name], (current, file) => current.With(FileReach(file)));
                if (next.SameAs(reach[name])) continue;
                reach[name] = next;
                changed = true;
            }
        }
        while (changed);

        Reach FileReach(SourceFile file)
        {
            var own = new Reach(
                file.Assemblies.Where(projectByAssembly.ContainsKey).ToHashSet(StringComparer.OrdinalIgnoreCase),
                LauncherCalls.Any(file.CodeTokens.Contains),
                file.Assemblies.Count > 0);
            return reach.Where(pair => file.CodeTokens.Contains(pair.Key))
                .Aggregate(own, (current, pair) => current.With(pair.Value));
        }

        var violations = new List<string>();
        foreach (var group in files
                     .SelectMany(file => file.Scan.TestClasses.Select(name => (Name: name, File: file)))
                     .GroupBy(entry => entry.Name, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var name = group.Key;
            var location = string.Join(", ", group.Select(entry => entry.File.Scan.Path).Distinct(StringComparer.OrdinalIgnoreCase));
            var classReach = group.Aggregate(Reach.None, (current, entry) => current.With(FileReach(entry.File)));
            var traits = group
                .SelectMany(entry => entry.File.Scan.ProcessReferences.TryGetValue(name, out var projects) ? projects : [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var assembly in classReach.Assemblies.Order(StringComparer.OrdinalIgnoreCase))
            {
                var project = projectByAssembly[assembly];
                if (!traits.Contains(project))
                    violations.Add($"{location}: {name} starts {assembly} without "
                        + $"[Trait(\"{DotNetSourceScanner.ProcessReferenceTrait}\", \"{project}\")]");
            }
            if (classReach.CallsLauncher && !classReach.NamesAssembly)
                violations.Add($"{location}: {name} calls BuiltProcessLauncher.StartBuilt but names no "
                    + "output assembly literal, so its ProcessReference trait cannot be checked");
            foreach (var trait in traits.Where(trait => !processProjects.ContainsKey(trait)).Order(StringComparer.Ordinal))
                violations.Add($"{location}: {name} carries a stale "
                    + $"[Trait(\"{DotNetSourceScanner.ProcessReferenceTrait}\", \"{trait}\")]; {testProject} "
                    + "does not reference that project with ReferenceOutputAssembly=\"false\"");
        }
        return violations;
    }

    /// <summary>The project's <c>AssemblyName</c> plus <c>.dll</c>, defaulting to its file name.</summary>
    private static string AssemblyFileName(string projectPath)
    {
        var name = XDocument.Load(projectPath).Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "AssemblyName")?.Value.Trim();
        return (string.IsNullOrEmpty(name) ? DotNetReferenceSelectionPlanner.ProjectName(projectPath) : name) + ".dll";
    }

    /// <summary>A whole regular or verbatim literal that is one assembly file name.</summary>
    [GeneratedRegex(@"^@?""(?<name>[A-Za-z0-9_.\-]+\.dll)""$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssemblyLiteral();

    private sealed record SourceFile(DotNetIndexedFile Scan, IReadOnlySet<string> CodeTokens, IReadOnlySet<string> Assemblies);

    private sealed record Reach(IReadOnlySet<string> Assemblies, bool CallsLauncher, bool NamesAssembly)
    {
        public static readonly Reach None = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

        public Reach With(Reach other)
            => new(Assemblies.Union(other.Assemblies, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase),
                CallsLauncher || other.CallsLauncher,
                NamesAssembly || other.NamesAssembly);

        public bool SameAs(Reach other)
            => CallsLauncher == other.CallsLauncher
                && NamesAssembly == other.NamesAssembly
                && Assemblies.SetEquals(other.Assemblies);
    }
}
