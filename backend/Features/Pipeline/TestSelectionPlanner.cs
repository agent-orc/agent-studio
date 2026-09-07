using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AgentStudio.Pipeline;

public sealed record TestHubHistoryEntry
{
    public string TestId { get; init; } = "";
    public string Command { get; init; } = "";
    public string WorkingSubdir { get; init; } = "";
    public IReadOnlyList<string> RelatedPaths { get; init; } = [];
    public DateTimeOffset? FailedAtUtc { get; init; }
    public string? Failure { get; init; }
}

public sealed record TestSelectionCandidate(
    string Id,
    VerifyCommand Command,
    IReadOnlyList<string> Reasons);

public sealed record TestSelectionAdvice(
    IReadOnlyList<string> CandidateIds,
    string Reason,
    string? Model = null);

public sealed record TestSelectionAudit
{
    public string Level { get; init; } = TestExecutionLevels.WorkPackage;
    public string Lane { get; init; } = "";
    public IReadOnlyList<string> DiffInput { get; init; } = [];
    public IReadOnlyList<TestHubHistoryEntry> HistoryInput { get; init; } = [];
    public IReadOnlyList<TestSelectionCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<string> SelectedCandidateIds { get; init; } = [];
    public IReadOnlyList<string> SelectedCommands { get; init; } = [];
    public IReadOnlyList<string> OmittedTestCommands { get; init; } = [];
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public string Selector { get; init; } = "deterministic";
    public string? SelectorModel { get; init; }
    public string? AdvisorReason { get; init; }
    public bool FullSuiteRequired { get; init; }
    public bool FullSuiteRan { get; init; }
}

public sealed record StagedVerifyPlan(
    IReadOnlyList<VerifyCommand> Commands,
    TestSelectionAudit Audit);

/// <summary>
/// Pure staged-test planner. It never invents an LLM command: model advice may
/// select only stable candidate ids produced from repository inventory,
/// explicit impact rules, or Test Hub history.
/// </summary>
public static class TestSelectionPlanner
{
    public static string ResolveLevel(
        TestExecutionPolicy? policy,
        string? lane,
        string? requiredLevel)
    {
        if (!string.IsNullOrWhiteSpace(requiredLevel))
            return TestExecutionLevels.Normalize(requiredLevel);
        if (!string.IsNullOrWhiteSpace(lane)
            && policy?.LaneLevels is { } laneLevels)
        {
            if (laneLevels.TryGetValue(lane, out var configured))
                return TestExecutionLevels.Normalize(configured);
            var caseInsensitiveMatch = laneLevels.FirstOrDefault(pair =>
                string.Equals(pair.Key, lane, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(caseInsensitiveMatch.Key))
                return TestExecutionLevels.Normalize(caseInsensitiveMatch.Value);
        }
        return TestExecutionLevels.WorkPackage;
    }

    public static StagedVerifyPlan Plan(
        string repositoryPath,
        VerifyPlan verifyPlan,
        IReadOnlyList<string>? changedFiles,
        TestExecutionPolicy? policy,
        string? lane,
        string? requiredLevel,
        TestSelectionAdvice? advice = null)
    {
        var level = ResolveLevel(policy, lane, requiredLevel);
        var fullSuiteRequired = TestExecutionLevels.Normalize(requiredLevel, "") == TestExecutionLevels.Full;
        var conservativeFullSuite = changedFiles is null && level == TestExecutionLevels.WorkPackage;
        if (conservativeFullSuite) level = TestExecutionLevels.Full;
        var diff = (changedFiles ?? [])
            .Select(NormalizePath)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var history = ReadHistory(repositoryPath, policy?.TestHubHistoryPath);
        var fullTests = verifyPlan.Commands.Where(command => command.Kind == VerifyCommandKind.Test).ToList();
        var nonTests = verifyPlan.Commands.Where(command => command.Kind != VerifyCommandKind.Test).ToList();

        // Build-only stage (the pre-develop merge gate): compile evidence without
        // any test command - not the impacted selection, not the continuous
        // baseline. The omitted inventory stays in the audit so the evidence log
        // says plainly what was NOT run.
        if (level == TestExecutionLevels.BuildOnly)
        {
            const string buildOnlyReason =
                "build-only stage: build commands only, the test suite stays with the auto-review gate";
            return new StagedVerifyPlan(nonTests, new TestSelectionAudit
            {
                Level = level,
                Lane = lane ?? "",
                DiffInput = diff,
                HistoryInput = history,
                SelectedCommands = [],
                OmittedTestCommands = fullTests.Select(Describe).ToList(),
                Reasons = [buildOnlyReason],
                Selector = "build-only",
                FullSuiteRequired = false,
                FullSuiteRan = false,
            });
        }

        if (level == TestExecutionLevels.Full)
        {
            var fullReason = fullSuiteRequired
                ? "mandatory full suite before main"
                : conservativeFullSuite
                    ? "diff input is unavailable; conservative fallback runs the full suite"
                    : "lane policy selected the full suite";
            var fullBaseline = ContinuousCommands(
                policy, blocksWorkPackage: true, omitBroadFrontendTests: false);
            var selected = MergeTestCommands(fullBaseline.Concat(fullTests.Select(command => command with
            {
                TestScope = TestExecutionLevels.Full,
                BlocksWorkPackage = true,
                SelectionReason = fullReason,
            })));
            var commands = nonTests.Concat(selected).ToList();
            return new StagedVerifyPlan(commands, new TestSelectionAudit
            {
                Level = level,
                Lane = lane ?? "",
                DiffInput = diff,
                HistoryInput = history,
                SelectedCommands = selected.Select(Describe).ToList(),
                Reasons = [fullReason],
                Selector = fullSuiteRequired
                    ? "mandatory-full-suite"
                    : conservativeFullSuite ? "conservative-full-suite" : "lane-full-suite",
                FullSuiteRequired = fullSuiteRequired,
                // Planning selects the full inventory. The runner flips this
                // only after execution evidence shows that every selected test
                // command was actually attempted.
                FullSuiteRan = false,
            });
        }

        var frontendWorkPackage = FrontendWorkPackagePlanner.Plan(repositoryPath, diff);
        var frontendTouched = FrontendWorkPackagePlanner.TouchesFrontend(diff);
        var candidates = BuildCandidates(
            repositoryPath, verifyPlan, diff, policy, history, frontendWorkPackage);
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        var reasons = new List<string>();

        foreach (var candidate in candidates)
        {
            if (candidate.Reasons.Count > 0)
                selectedIds.Add(candidate.Id);
        }

        if (advice is not null)
        {
            var allowed = candidates.Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in advice.CandidateIds.Where(allowed.Contains)) selectedIds.Add(id);
            reasons.Add("LLM adviser may add allowlisted candidates but cannot remove deterministic selections");
        }

        var selectedCandidates = level == TestExecutionLevels.Continuous
            ? []
            : candidates.Where(candidate => selectedIds.Contains(candidate.Id)).ToList();
        var advisedIds = advice?.CandidateIds.ToHashSet(StringComparer.Ordinal) ?? [];
        var continuous = ContinuousCommands(
            policy,
            blocksWorkPackage: false,
            omitBroadFrontendTests: frontendTouched);

        var selectedTests = level == TestExecutionLevels.Continuous
            ? new List<VerifyCommand>()
            : selectedCandidates.Select(candidate => candidate.Command with
            {
                TestScope = TestExecutionLevels.WorkPackage,
                BlocksWorkPackage = true,
                SelectionReason = string.Join("; ", candidate.Reasons.Concat(
                    advisedIds.Contains(candidate.Id)
                        ? [$"LLM adviser: {advice!.Reason}"]
                        : [])),
            }).ToList();
        var mergedTests = MergeTestCommands(continuous.Concat(selectedTests));
        var commandsForRun = nonTests.Concat(mergedTests).ToList();

        if (continuous.Count == 0) reasons.Add("continuous baseline not configured");
        if (level == TestExecutionLevels.WorkPackage && selectedTests.Count == 0)
            reasons.Add("no impacted test command could be derived; this coverage gap is explicit");

        return new StagedVerifyPlan(commandsForRun, new TestSelectionAudit
        {
            Level = level,
            Lane = lane ?? "",
            DiffInput = diff,
            HistoryInput = history,
            Candidates = candidates,
            SelectedCandidateIds = selectedCandidates.Select(candidate => candidate.Id).ToList(),
            SelectedCommands = mergedTests.Select(Describe).ToList(),
            OmittedTestCommands = fullTests
                .Select(Describe)
                .Except(mergedTests.Select(Describe), StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Reasons = reasons,
            Selector = advice is null ? "deterministic" : "deterministic+llm",
            SelectorModel = advice?.Model,
            AdvisorReason = advice?.Reason,
            FullSuiteRequired = false,
            FullSuiteRan = false,
        });
    }

    public static IReadOnlyList<TestSelectionCandidate> BuildCandidates(
        string repositoryPath,
        VerifyPlan verifyPlan,
        IReadOnlyList<string> changedFiles,
        TestExecutionPolicy? policy,
        IReadOnlyList<TestHubHistoryEntry> history,
        VerifyCommand? frontendWorkPackage = null)
    {
        var map = new Dictionary<string, CandidateBuilder>(StringComparer.OrdinalIgnoreCase);
        frontendWorkPackage ??= FrontendWorkPackagePlanner.Plan(repositoryPath, changedFiles);
        var omitBroadFrontendTests = FrontendWorkPackagePlanner.TouchesFrontend(changedFiles);

        // Build a safe inventory first. Deterministic matching and the optional
        // adviser may select from this inventory, but neither may invent a shell
        // command. Candidates without reasons remain unselected unless the LLM
        // explicitly adds their stable id.
        foreach (var command in verifyPlan.Commands.Where(command =>
                     command.Kind == VerifyCommandKind.Test
                     && (!omitBroadFrontendTests
                         || !FrontendWorkPackagePlanner.IsBroadFrontendTest(command))))
            Add(map, command);

        foreach (var command in verifyPlan.Commands.Where(command =>
                     command.Kind == VerifyCommandKind.Test
                     && command.Ecosystem == VerifyEcosystem.Node
                     && (!omitBroadFrontendTests
                         || !FrontendWorkPackagePlanner.IsBroadFrontendTest(command))))
        {
            var replacedByFocusedAngularSlice = FrontendWorkPackagePlanner.TouchesFrontend(changedFiles)
                && string.Equals(command.WorkingSubdir, "frontend", StringComparison.OrdinalIgnoreCase);
            Add(map, command, replacedByFocusedAngularSlice
                ? null
                : (string.IsNullOrEmpty(command.WorkingSubdir)
                    || PathMatches(changedFiles, command.WorkingSubdir))
                        ? "diff touches this package/component"
                        : null);
        }

        if (frontendWorkPackage is not null)
            Add(map, frontendWorkPackage, frontendWorkPackage.SelectionReason);

        foreach (var candidate in DotNetTestInventory(repositoryPath, verifyPlan, changedFiles))
            Add(map, candidate.Command, candidate.Impacted
                ? "diff touches this test project or a referenced production project"
                : null);

        foreach (var rule in policy?.ImpactRules ?? [])
        {
            var matched = rule.PathPrefixes.Any(prefix => PathMatches(changedFiles, prefix));
            foreach (var raw in rule.TestCommands.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var command = new VerifyCommand(
                    VerifyEcosystem.Custom, VerifyCommandKind.Test, "", raw.Trim());
                if (!omitBroadFrontendTests
                    || !FrontendWorkPackagePlanner.IsBroadFrontendTest(command))
                {
                    Add(map, command,
                        matched ? rule.Reason ?? "configured impact rule matched the diff" : null);
                }
            }
        }

        // History may only select commands already present in the safe inventory.
        // This prevents a writable JSONL file from becoming an arbitrary shell
        // execution surface.
        foreach (var entry in history.Where(entry => PathMatches(changedFiles, entry.RelatedPaths)))
        {
            var key = CommandKey(entry.Command, entry.WorkingSubdir);
            if (!map.TryGetValue(key, out var existing) && string.IsNullOrWhiteSpace(entry.WorkingSubdir))
            {
                existing = map.Values.FirstOrDefault(candidate =>
                    string.Equals(candidate.Command.Command.Trim(), entry.Command.Trim(),
                        StringComparison.OrdinalIgnoreCase));
            }
            if (existing is not null)
                existing.Reasons.Add($"Test Hub history: {entry.TestId} failed here before");
        }

        return map.Values
            .OrderBy(candidate => candidate.Command.Command, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new TestSelectionCandidate(
                StableId(candidate.Command), candidate.Command,
                candidate.Reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
    }

    private static IEnumerable<(VerifyCommand Command, bool Impacted)> DotNetTestInventory(
        string repositoryPath,
        VerifyPlan verifyPlan,
        IReadOnlyList<string> changedFiles)
    {
        if (!Directory.Exists(repositoryPath)) yield break;
        var projects = Directory.EnumerateFiles(repositoryPath, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(Path.GetRelativePath(repositoryPath, path)))
            .ToList();
        var testProjects = projects.Where(IsTestProject).ToList();
        var touchedProjects = changedFiles
            .Select(file => OwningProject(repositoryPath, file, projects))
            .Where(path => path is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var testProject in testProjects)
        {
            var references = ProjectReferences(testProject);
            var impacted = touchedProjects.Contains(testProject)
                || references.Any(touchedProjects.Contains);
            var relative = NormalizePath(Path.GetRelativePath(repositoryPath, testProject));
            yield return (new VerifyCommand(
                VerifyEcosystem.DotNet,
                VerifyCommandKind.Test,
                "",
                $"dotnet test \"{relative}\"{DotNetFilterSuffix(verifyPlan)}"), impacted);
        }
    }

    private static string DotNetFilterSuffix(VerifyPlan verifyPlan)
    {
        foreach (var command in verifyPlan.Commands.Where(command => command.Kind == VerifyCommandKind.Test))
        {
            if (!command.Command.TrimStart().StartsWith("dotnet test", StringComparison.OrdinalIgnoreCase))
                continue;
            var match = Regex.Match(command.Command,
                "(?:^|\\s)(?:--filter|-f)\\s+(?:\\\"[^\\\"]*\\\"|'[^']*'|\\S+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success) return " " + match.Value.Trim();
        }
        // Repository-wide routine gates exclude machine-bound and Windows-host
        // process/timing families, and live-CLI tests that spend account quota.
        // Preserve an explicit project filter when one exists; otherwise apply
        // the canonical exclusion to every generated work-package test-project
        // command.
        return GateTestCategoryFilter.DotNetSuffix;
    }

    private static string? OwningProject(string root, string changedFile, IReadOnlyList<string> projects)
    {
        var full = Path.GetFullPath(Path.Combine(root, changedFile.Replace('/', Path.DirectorySeparatorChar)));
        return projects
            .Where(project => full.StartsWith(
                Path.GetDirectoryName(project)! + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(project => Path.GetDirectoryName(project)!.Length)
            .FirstOrDefault();
    }

    private static bool IsTestProject(string project)
    {
        if (Path.GetFileNameWithoutExtension(project).Contains("Test", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var doc = XDocument.Load(project);
            return doc.Descendants().Any(element =>
                element.Name.LocalName == "IsTestProject"
                && string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "TestSelectionPlanner: test-project inventory");
            return false;
        }
    }

    private static IReadOnlyList<string> ProjectReferences(string project)
    {
        try
        {
            var dir = Path.GetDirectoryName(project)!;
            return XDocument.Load(project).Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.GetFullPath(Path.Combine(dir, value!)))
                .ToList();
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "TestSelectionPlanner: project-reference inventory");
            return [];
        }
    }

    internal static IReadOnlyList<TestHubHistoryEntry> ReadHistory(string root, string? configuredPath)
    {
        var relative = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(".test-hub", "history.jsonl")
            : configuredPath.Trim();
        var path = Path.IsPathRooted(relative) ? relative : Path.Combine(root, relative);
        if (!File.Exists(path)) return [];
        var entries = new List<TestHubHistoryEntry>();
        try
        {
            foreach (var line in File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(500))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<TestHubHistoryEntry>(line,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (entry is not null && !string.IsNullOrWhiteSpace(entry.Command)) entries.Add(entry);
                }
                catch (JsonException ex)
                {
                    SilentCatch.Note(ex, "TestSelectionPlanner: malformed Test Hub history row");
                }
            }
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "TestSelectionPlanner: Test Hub history read");
        }
        return entries;
    }

    private static IReadOnlyList<VerifyCommand> ContinuousCommands(
        TestExecutionPolicy? policy,
        bool blocksWorkPackage,
        bool omitBroadFrontendTests)
        => (policy?.ContinuousCommands ?? [])
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .Select(command => new VerifyCommand(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", command.Trim())
            {
                TestScope = TestExecutionLevels.Continuous,
                BlocksWorkPackage = blocksWorkPackage,
                SelectionReason = "configured fixed continuous baseline",
            })
            .Where(command => !omitBroadFrontendTests
                || !FrontendWorkPackagePlanner.IsBroadFrontendTest(command))
            .ToList();

    private static IReadOnlyList<VerifyCommand> MergeTestCommands(IEnumerable<VerifyCommand> commands)
    {
        var merged = new List<VerifyCommand>();
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands)
        {
            var key = CommandKey(command.Command, command.WorkingSubdir);
            if (!indexes.TryGetValue(key, out var index))
            {
                indexes[key] = merged.Count;
                merged.Add(command);
                continue;
            }

            var existing = merged[index];
            // One physical command can be both part of the fixed baseline and
            // selected for this diff. In that case the stricter classification
            // must win, otherwise an impacted regression would be mislabeled as
            // unrelated debt and would not block the card.
            if ((!existing.BlocksWorkPackage && command.BlocksWorkPackage)
                || TestScopeRank(command.TestScope) > TestScopeRank(existing.TestScope))
            {
                merged[index] = command;
            }
        }
        return merged;
    }

    private static int TestScopeRank(string scope)
        => scope switch
        {
            TestExecutionLevels.Full => 3,
            TestExecutionLevels.WorkPackage => 2,
            TestExecutionLevels.Continuous => 1,
            _ => 0,
        };

    private static void Add(Dictionary<string, CandidateBuilder> map, VerifyCommand command, string? reason = null)
    {
        var key = CommandKey(command.Command, command.WorkingSubdir);
        if (!map.TryGetValue(key, out var candidate))
        {
            candidate = new CandidateBuilder(command);
            map[key] = candidate;
        }
        if (!string.IsNullOrWhiteSpace(reason)) candidate.Reasons.Add(reason);
    }

    private static bool PathMatches(IReadOnlyList<string> changedFiles, IEnumerable<string> prefixes)
        => prefixes.Any(prefix => PathMatches(changedFiles, prefix));

    private static bool PathMatches(IReadOnlyList<string> changedFiles, string prefix)
    {
        var normalized = NormalizePath(prefix).TrimEnd('/');
        return normalized.Length > 0 && changedFiles.Any(file =>
            string.Equals(file, normalized, StringComparison.OrdinalIgnoreCase)
            || file.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGeneratedPath(string path)
        => NormalizePath(path).Split('/').Any(part =>
            part.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || part.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || part.Equals("node_modules", StringComparison.OrdinalIgnoreCase));

    private static string StableId(VerifyCommand command)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(CommandKey(command.Command, command.WorkingSubdir)));
        return "test-" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static string CommandKey(string command, string subdir)
        => $"{NormalizePath(subdir).TrimEnd('/')}\n{command.Trim()}";

    private static string NormalizePath(string value) => value.Replace('\\', '/').TrimStart('.', '/');
    internal static string Describe(VerifyCommand command)
        => string.IsNullOrWhiteSpace(command.WorkingSubdir)
            ? command.Command
            : $"({NormalizePath(command.WorkingSubdir)}) {command.Command}";

    private sealed record CandidateBuilder(VerifyCommand Command)
    {
        public List<string> Reasons { get; } = [];
    }
}
