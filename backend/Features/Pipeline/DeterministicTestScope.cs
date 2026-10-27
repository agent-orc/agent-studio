using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>
/// Fail-closed folder ownership for scoped gate tests. The map is the
/// configured one, or the one generated at plan time from the project graph
/// (AGT-3310). A change across several mapped modules runs the union of their
/// mapped test commands; global inputs, unmapped paths, a stale map and a
/// missing diff run the full suite.
/// </summary>
public static partial class DeterministicTestScope
{
    /// <param name="generatedMap">
    /// Replaces the configured map. Its own staleness rule applies instead of
    /// <see cref="UnmappedSourceDirectories"/>, and its digest is recorded in the audit.
    /// </param>
    /// <param name="referenceSelection">
    /// Narrows a selected .NET test project to the classes that reference the
    /// changed types (AGT-3085). Applies only with <paramref name="generatedMap"/>,
    /// whose reference graph tells a direct reference from a transitive one.
    /// </param>
    public static StagedVerifyPlan Plan(
        string repositoryPath,
        VerifyPlan verifyPlan,
        IReadOnlyList<string>? changedFiles,
        IReadOnlyDictionary<string, string>? statuses,
        TestExecutionPolicy? policy,
        string? lane,
        string? requiredLevel,
        GeneratedTestProjectMap? generatedMap = null,
        DotNetReferenceSelection? referenceSelection = null)
    {
        var level = TestSelectionPlanner.ResolveLevel(policy, lane, requiredLevel);
        if (level is TestExecutionLevels.BuildOnly or TestExecutionLevels.CompileOnly
            or TestExecutionLevels.Continuous or TestExecutionLevels.Full)
            return WithDigest(TestSelectionPlanner.Plan(repositoryPath, verifyPlan, changedFiles,
                policy, lane, requiredLevel), statuses);

        var files = (changedFiles ?? []).Select(Normalize).Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var map = generatedMap?.Rows ?? policy?.FolderToTestProjects ?? [];
        IReadOnlyList<string> staleDirectories = map.Count == 0 || generatedMap is not null
            ? [] : UnmappedSourceDirectories(repositoryPath, policy);
        var selectedRows = new List<TestFolderMapping>();
        var reasons = new List<string>();
        var full = files.Length == 0 || map.Count == 0;
        if (files.Length == 0) reasons.Add("changed-file input is unavailable or empty");
        if (generatedMap?.Failure is { } failure) reasons.Add("project map could not be generated: " + failure);
        else if (map.Count == 0) reasons.Add("folder-to-test-project map is missing");
        if (staleDirectories.Count > 0)
        {
            full = true;
            reasons.Add("stale folder-to-test-project map: " + string.Join(", ", staleDirectories));
        }
        if (generatedMap?.StaleReason(files) is { } stale)
        {
            full = true;
            reasons.Add(stale);
        }

        foreach (var file in files)
        {
            if (GeneratedTestProjectMap.GlobalInput(file) is { } global)
            {
                full = true;
                reasons.Add($"global input changed: {file} ({global})");
                continue;
            }
            var matches = map.Where(row => Matches(file, Normalize(row.Folder)))
                .OrderByDescending(row => Normalize(row.Folder).Length).ToArray();
            if (matches.Length == 0)
            {
                full = true;
                reasons.Add($"unmapped changed path: {file}");
                continue;
            }
            var row = matches[0];
            if (row.TestProjects.Count == 0)
            {
                full = true;
                reasons.Add($"mapping has no test project: {row.Folder}");
            }
            selectedRows.Add(row);
            if (statuses?.TryGetValue(file, out var status) == true
                && status.StartsWith('A') && IsTestFile(file))
            {
                var ownsFile = row.TestProjects.Any(project => Matches(file,
                    project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                        ? Normalize(Path.GetDirectoryName(project) ?? "")
                        : Normalize(project)));
                if (!ownsFile)
                {
                    full = true;
                    reasons.Add($"new test file lacks a mapped owning project: {file}");
                }
                else reasons.Add(referenceSelection is not null && generatedMap is not null
                    ? $"new test file included with its classes: {file}"
                    : $"new test file included by its whole project: {file}");
            }
        }

        var modules = selectedRows.Select(ModuleOf)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (modules.Length > 1)
            reasons.Add($"change spans modules: {string.Join(", ", modules)}; "
                + "the union of their mapped test commands runs");

        var testCommands = new List<VerifyCommand>();
        var classSelection = new ClassSelection();
        if (!full)
        {
            var projects = selectedRows.SelectMany(row => row.TestProjects)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (referenceSelection is not null && generatedMap is not null)
                classSelection = SelectClasses(repositoryPath, verifyPlan, files, projects,
                    generatedMap, referenceSelection, reasons);
            foreach (var project in projects)
            {
                IReadOnlyList<string>? classes = null;
                if (classSelection.Outcomes.TryGetValue(project, out var outcome))
                {
                    if (outcome.Kind == DotNetReferenceOutcomeKind.NoCommand) continue;
                    if (outcome.Kind == DotNetReferenceOutcomeKind.Selected) classes = outcome.Audit.SelectedClasses;
                }
                var command = ResolveProject(repositoryPath, verifyPlan, project, generatedMap, classes);
                if (command is null)
                {
                    full = true;
                    reasons.Add($"mapped test project is unavailable: {project}");
                    break;
                }
                var owners = selectedRows.Where(row => row.TestProjects.Contains(project, StringComparer.OrdinalIgnoreCase))
                    .Select(ModuleOf).Distinct(StringComparer.OrdinalIgnoreCase);
                testCommands.Add(command with
                {
                    TestScope = TestExecutionLevels.WorkPackage,
                    BlocksWorkPackage = true,
                    SelectionReason = $"folder-to-test-project map: {string.Join(", ", owners)}"
                        + (classes is { Count: > 0 } ? $"; {outcome!.Audit.Summary}" : ""),
                });
            }
        }

        if (full)
        {
            var fallback = TestSelectionPlanner.Plan(repositoryPath, verifyPlan, changedFiles,
                policy, lane, TestExecutionLevels.Full);
            return WithDigest(fallback with { Audit = fallback.Audit with
            {
                FullSuiteRequired = true,
                UnmappedSourceDirectories = staleDirectories,
                ProjectMapDigest = NullIfEmpty(generatedMap?.Digest),
                Selector = "deterministic-map-full-fallback",
                Reasons = reasons.Count == 0 ? ["conservative full-suite fallback"] : reasons,
            } }, statuses);
        }

        var selected = testCommands.DistinctBy(command =>
            (command.WorkingSubdir, command.Command)).ToArray();
        var fullTests = verifyPlan.Commands.Where(command => command.Kind == VerifyCommandKind.Test)
            .Select(TestSelectionPlanner.Describe).ToArray();
        var audit = new TestSelectionAudit
        {
            Level = TestExecutionLevels.WorkPackage,
            Lane = lane ?? "",
            DiffInput = files,
            SelectedCommands = selected.Select(TestSelectionPlanner.Describe).ToArray(),
            OmittedTestCommands = fullTests.Except(selected.Select(TestSelectionPlanner.Describe),
                StringComparer.OrdinalIgnoreCase).ToArray(),
            Reasons = reasons.Append(modules.Length == 1
                ? $"mapped module: {modules[0]}"
                : $"mapped modules: {string.Join(", ", modules)}").ToArray(),
            Selector = generatedMap is null ? "deterministic-folder-map" : "generated-project-map",
            ProjectMapDigest = NullIfEmpty(generatedMap?.Digest),
            SelectedTestClasses = classSelection.Audits.SelectMany(audit => audit.SelectedClasses)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            ReferenceSelections = classSelection.Audits,
            PlanBuildTime = classSelection.PlanBuildTime,
        };
        return WithDigest(new StagedVerifyPlan(
            verifyPlan.Commands.Where(command => command.Kind != VerifyCommandKind.Test)
                .Concat(selected).ToArray(), audit), statuses);
    }

    /// <summary>CI/maintenance guard. It is separate from runtime fallback so new paths still run the full suite.</summary>
    public static IReadOnlyList<string> UnmappedSourceDirectories(
        string repositoryPath, TestExecutionPolicy? policy)
    {
        var map = policy?.FolderToTestProjects ?? [];
        var missing = new List<string>();
        if (map.Count > 0 && policy?.MappedSourceRoots is not { Count: > 0 })
            missing.Add("mappedSourceRoots is missing");
        foreach (var root in policy?.MappedSourceRoots ?? [])
        {
            var normalized = Normalize(root);
            if (!SafeRelative(normalized))
            {
                missing.Add($"invalid source root: {root}");
                continue;
            }
            var absolute = Path.Combine(repositoryPath, normalized);
            if (!Directory.Exists(absolute)) continue;
            foreach (var directory in Directory.EnumerateDirectories(absolute))
            {
                var relative = Normalize(Path.GetRelativePath(repositoryPath, directory));
                if (Path.GetFileName(directory) is "bin" or "obj" or "node_modules") continue;
                if (!map.Any(row => Matches(Normalize(row.Folder), relative)
                    || Matches(relative, Normalize(row.Folder))))
                    missing.Add(relative);
            }
        }
        return missing.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    public static void AssertMapCurrent(string repositoryPath, TestExecutionPolicy? policy)
    {
        var missing = UnmappedSourceDirectories(repositoryPath, policy);
        if (missing.Count > 0)
            throw new InvalidOperationException(
                "Folder-to-test-project map is stale; unmapped source directories: "
                + string.Join(", ", missing));
    }

    public static StagedVerifyPlan WithDigest(StagedVerifyPlan plan,
        IReadOnlyDictionary<string, string>? statuses = null)
    {
        var normalizedStatuses = (statuses ?? new Dictionary<string, string>())
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Value} {Normalize(pair.Key)}").ToArray();
        var audit = plan.Audit with { DiffStatuses = normalizedStatuses };
        var data = JsonSerializer.Serialize(new
        {
            audit.Level, audit.Selector, audit.DiffInput, audit.DiffStatuses,
            audit.SelectedCommands, audit.AttemptedTestCommands,
            audit.OmittedTestCommands, audit.Reasons,
            audit.UnmappedSourceDirectories, audit.FullSuiteRequired, audit.FullSuiteRan,
        });
        // A generated map's digest joins the hash only when present, so the
        // digest of a configured-map plan stays what it was.
        if (audit.ProjectMapDigest is { } mapDigest) data += "\nproject-map:" + mapDigest;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)))
            .ToLowerInvariant();
        return plan with { Audit = audit with { Digest = digest } };
    }

    private static VerifyCommand? ResolveProject(string root, VerifyPlan plan, string project,
        GeneratedTestProjectMap? generatedMap, IReadOnlyList<string>? classes)
    {
        var normalized = Normalize(project);
        if (generatedMap?.SuiteCommands.TryGetValue(normalized, out var suiteCommand) == true)
            return plan.Commands.FirstOrDefault(command => command.Kind == VerifyCommandKind.Test
                && string.Equals(command.Command.Trim(), suiteCommand, StringComparison.Ordinal));
        if (!SafeRelative(normalized)) return null;
        if (normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(root, normalized))) return null;
            return DotNetProjectCommand(plan, normalized, classes);
        }
        if (!Directory.Exists(Path.Combine(root, normalized))) return null;
        return plan.Commands.FirstOrDefault(command => command.Kind == VerifyCommandKind.Test
            && string.Equals(Normalize(command.WorkingSubdir), normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The test command for one .NET test project. A declared command that
    /// already targets the project is reused as written, so its flags (for
    /// example <c>--no-build</c>) and its omitted-inventory identity hold.
    /// Any other project inherits the flags of the first declared
    /// <c>dotnet test</c> command. <paramref name="classes"/> narrows the filter.
    /// </summary>
    internal static VerifyCommand? DotNetProjectCommand(
        VerifyPlan plan, string project, IReadOnlyList<string>? classes)
    {
        var dotnet = plan.Commands.Where(command => command.Kind == VerifyCommandKind.Test
                && (command.Ecosystem == VerifyEcosystem.DotNet || IsDotNetTest(command.Command)))
            .ToList();
        if (dotnet.Count == 0) return null;
        var declared = dotnet.FirstOrDefault(command => Tokens(command.Command).Skip(2)
            .Any(token => string.Equals(Normalize(token.Trim('"', '\'')), project, StringComparison.OrdinalIgnoreCase)));
        if (declared is not null)
        {
            if (classes is not { Count: > 0 }) return declared;
            var filter = FilterArgument().Match(declared.Command);
            var expression = filter.Success
                ? filter.Groups["value"].Value.Trim().Trim('"', '\'')
                : TestSelectionPlanner.DotNetFilterExpression(plan).Expression;
            var value = $"\"{DotNetReferenceSelectionPlanner.FilterValue(expression, classes)}\"";
            return declared with
            {
                Command = filter.Success
                    ? declared.Command[..filter.Groups["value"].Index] + value
                        + declared.Command[(filter.Groups["value"].Index + filter.Groups["value"].Length)..]
                    : $"{declared.Command.TrimEnd()} --filter {value}",
            };
        }

        var template = dotnet[0];
        var inherited = new List<string>();
        var arguments = Tokens(template.Command).Skip(2).ToList();
        for (var index = 0; index < arguments.Count; index++)
        {
            var token = arguments[index];
            if (token == "--filter") { index++; continue; }
            if (token.StartsWith("--filter=", StringComparison.Ordinal)) continue;
            var bare = token.Trim('"', '\'');
            if (bare.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || bare.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || bare.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) continue;
            inherited.Add(token);
        }
        return new VerifyCommand(VerifyEcosystem.DotNet, VerifyCommandKind.Test, "",
            $"dotnet test \"{project}\"{string.Concat(inherited.Select(token => " " + token))}"
            + TestSelectionPlanner.DotNetFilterSuffix(plan, classes))
        {
            Shell = template.Shell,
        };
    }

    private sealed record ClassSelection
    {
        public Dictionary<string, DotNetReferenceOutcome> Outcomes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<DotNetReferenceSelectionAudit> Audits { get; } = [];
        public TimeSpan? PlanBuildTime { get; set; }
    }

    /// <summary>
    /// Class selection inside each selected .NET test project. Rules: the
    /// test project's own changes and the changes of the projects it
    /// references directly seed the selection; a process-level reference
    /// selects by trait; a change that reaches the test project only through
    /// another project keeps the whole project, because no type reference
    /// in the tests names it. A project without an outcome runs whole.
    /// </summary>
    private static ClassSelection SelectClasses(
        string repositoryPath,
        VerifyPlan verifyPlan,
        IReadOnlyList<string> files,
        IReadOnlyList<string> testProjects,
        GeneratedTestProjectMap map,
        DotNetReferenceSelection selection,
        List<string> reasons)
    {
        var result = new ClassSelection();
        var started = selection.Time.GetTimestamp();
        var owners = files.Select(file => (File: file, Project: map.OwningProject(file)))
            .Where(entry => entry.Project is not null).ToList();
        var inputs = new List<(string TestProject, List<string> Own,
            Dictionary<string, IReadOnlyList<string>> Production, List<string> Process)>();
        foreach (var testProject in testProjects.Where(project =>
                     project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var direct = map.References.TryGetValue(testProject, out var references) ? references : [];
            var own = new List<string>();
            var production = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var process = new List<string>();
            string? transitive = null;
            foreach (var (file, project) in owners)
            {
                if (string.Equals(project, testProject, StringComparison.OrdinalIgnoreCase))
                {
                    own.Add(file);
                    continue;
                }
                var reference = direct.FirstOrDefault(entry =>
                    string.Equals(entry.Project, project, StringComparison.OrdinalIgnoreCase));
                if (reference is null)
                {
                    if (map.Covers(project!, testProject)) transitive ??= project;
                    continue;
                }
                if (reference.ProcessLevel) process.Add(project!);
                else
                {
                    if (!production.TryGetValue(project!, out var changed)) production[project!] = changed = [];
                    changed.Add(file);
                }
            }
            if (transitive is not null)
            {
                reasons.Add($"{testProject}: whole project, {transitive} reaches it only through another project");
                continue;
            }
            inputs.Add((testProject, own, production.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value, StringComparer.OrdinalIgnoreCase),
                process.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }
        if (inputs.Count == 0) return result;

        var planner = DotNetReferenceSelectionPlanner.Prepare(
            repositoryPath,
            files,
            inputs.SelectMany(input => input.Own.Concat(input.Production.Values.SelectMany(changed => changed)))
                .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            selection);
        var expression = TestSelectionPlanner.DotNetFilterExpression(verifyPlan).Expression;
        foreach (var (testProject, own, production, process) in inputs)
        {
            var outcome = planner.Plan(testProject, own, production, process, expression);
            result.Outcomes[testProject] = outcome;
            result.Audits.Add(outcome.Audit);
            reasons.Add(outcome.Kind == DotNetReferenceOutcomeKind.NoCommand
                ? $"{testProject}: no per-card command, {outcome.Audit.FallbackReason}"
                : outcome.Audit.Summary);
        }
        result.PlanBuildTime = selection.Time.GetElapsedTime(started);
        return result;
    }

    private static string ModuleOf(TestFolderMapping row)
        => string.IsNullOrWhiteSpace(row.Module) ? Normalize(row.Folder) : row.Module.Trim();

    private static bool IsDotNetTest(string command)
        => command.TrimStart().StartsWith("dotnet test", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Tokens(string command)
        => CommandToken().Matches(command).Select(match => match.Value);

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    [GeneratedRegex("\"[^\"]*\"|'[^']*'|\\S+", RegexOptions.CultureInvariant)]
    private static partial Regex CommandToken();

    [GeneratedRegex("(?:^|\\s)--filter(?:\\s+|=)(?<value>\"[^\"]*\"|'[^']*'|\\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FilterArgument();

    private static bool IsTestFile(string path)
        => path.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".test.ts", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".test.js", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Contains("Test", StringComparison.OrdinalIgnoreCase)
               && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static bool Matches(string path, string prefix)
        => prefix.Length > 0 && (string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));

    private static bool SafeRelative(string path)
        => path.Length > 0 && !Path.IsPathRooted(path)
            && path.Split('/').All(part => part is not "" and not "." and not "..")
            && path.All(character => char.IsLetterOrDigit(character)
                || character is '/' or '_' or '-' or '.');

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized.TrimEnd('/');
    }
}
