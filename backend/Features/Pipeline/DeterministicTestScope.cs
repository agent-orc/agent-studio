using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>Fail-closed, configured folder ownership for scoped gate tests.</summary>
public static class DeterministicTestScope
{
    public static StagedVerifyPlan Plan(
        string repositoryPath,
        VerifyPlan verifyPlan,
        IReadOnlyList<string>? changedFiles,
        IReadOnlyDictionary<string, string>? statuses,
        TestExecutionPolicy? policy,
        string? lane,
        string? requiredLevel)
    {
        var level = TestSelectionPlanner.ResolveLevel(policy, lane, requiredLevel);
        if (level is TestExecutionLevels.BuildOnly or TestExecutionLevels.CompileOnly
            or TestExecutionLevels.Continuous or TestExecutionLevels.Full)
            return WithDigest(TestSelectionPlanner.Plan(repositoryPath, verifyPlan, changedFiles,
                policy, lane, requiredLevel), statuses);

        var files = (changedFiles ?? []).Select(Normalize).Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var map = policy?.FolderToTestProjects ?? [];
        IReadOnlyList<string> staleDirectories = map.Count == 0
            ? [] : UnmappedSourceDirectories(repositoryPath, policy);
        var selectedRows = new List<TestFolderMapping>();
        var reasons = new List<string>();
        var full = files.Length == 0 || map.Count == 0;
        if (files.Length == 0) reasons.Add("changed-file input is unavailable or empty");
        if (map.Count == 0) reasons.Add("folder-to-test-project map is missing");
        if (staleDirectories.Count > 0)
        {
            full = true;
            reasons.Add("stale folder-to-test-project map: " + string.Join(", ", staleDirectories));
        }

        foreach (var file in files)
        {
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
                else reasons.Add($"new test file included by its whole project: {file}");
            }
        }

        var modules = selectedRows.Select(row => string.IsNullOrWhiteSpace(row.Module)
                ? Normalize(row.Folder) : row.Module.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (modules.Length > 1)
        {
            full = true;
            reasons.Add($"change spans modules: {string.Join(", ", modules)}");
        }

        var testCommands = new List<VerifyCommand>();
        if (!full)
        {
            foreach (var project in selectedRows.SelectMany(row => row.TestProjects)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var command = ResolveProject(repositoryPath, verifyPlan, project);
                if (command is null)
                {
                    full = true;
                    reasons.Add($"mapped test project is unavailable: {project}");
                    break;
                }
                testCommands.Add(command with
                {
                    TestScope = TestExecutionLevels.WorkPackage,
                    BlocksWorkPackage = true,
                    SelectionReason = $"folder-to-test-project map: {string.Join(", ", modules)}",
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
            Reasons = reasons.Append($"mapped module: {modules[0]}").ToArray(),
            Selector = "deterministic-folder-map",
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
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)))
            .ToLowerInvariant();
        return plan with { Audit = audit with { Digest = digest } };
    }

    private static VerifyCommand? ResolveProject(string root, VerifyPlan plan, string project)
    {
        var normalized = Normalize(project);
        if (!SafeRelative(normalized)) return null;
        if (normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(root, normalized))) return null;
            var full = plan.Commands.FirstOrDefault(command => command.Kind == VerifyCommandKind.Test
                && command.Ecosystem == VerifyEcosystem.DotNet);
            if (full is null) return null;
            return new VerifyCommand(VerifyEcosystem.DotNet, VerifyCommandKind.Test, "",
                $"dotnet test \"{normalized}\"{TestSelectionPlanner.DotNetFilterSuffix(plan, null)}");
        }
        if (!Directory.Exists(Path.Combine(root, normalized))) return null;
        return plan.Commands.FirstOrDefault(command => command.Kind == VerifyCommandKind.Test
            && string.Equals(Normalize(command.WorkingSubdir), normalized,
                StringComparison.OrdinalIgnoreCase));
    }

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
