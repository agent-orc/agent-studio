using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>Repository-owned fixed checks, narrowed from declared routine invocations.</summary>
internal static class ReviewBaseChecks
{
    internal const string InventoryPath = ".agent-studio/review-base.json";
    internal const string Scope = "review-base";

    private sealed record Check(string Suite, string[] Paths, string? Category, string[]? Classes);

    internal static StagedVerifyPlan Apply(string repositoryPath, VerifyPlan declared,
        StagedVerifyPlan selection, IReadOnlyList<string>? changedFiles)
    {
        if (!DotNetWorkPackagePlanner.TouchesDotNet(changedFiles)) return selection;
        var path = Path.Combine(repositoryPath, InventoryPath);
        if (!File.Exists(path) || declared.Source != VerifyPlan.SourceProjectDefinition) return selection;
        if (new FileInfo(path).Length > 64_000)
            throw new InvalidDataException($"{InventoryPath} exceeds 64000 bytes.");
        var checks = JsonSerializer.Deserialize<Check[]>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        }) ?? throw new InvalidDataException($"{InventoryPath} must contain an array.");
        var read = ProjectDefinitionReader.ReadWorkspace(repositoryPath);
        if (!read.IsValid || read.Definition is not { } definition)
            throw new InvalidDataException($"{InventoryPath} requires a valid project definition.");
        var fixedFilters = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var fixedClasses = new List<string>();
        foreach (var check in checks)
        {
            var suite = definition.TestSuites.SingleOrDefault(suite => suite.Id == check.Suite
                && suite.Category == ProjectTestSuiteCategories.Routine
                && definition.Commands.Test.Contains(suite.Command));
            if (suite is null || !GateFlakyRerunPolicy.IsTargetableDotNetTest(suite.Command)
                || ProjectArgument.Matches(ExistingFilter.Replace(suite.Command, string.Empty)).Count != 1
                || check.Paths is not { Length: > 0 } || check.Paths.Any(prefix => !SafePath(prefix))
                || (check.Category is null && check.Classes is not { Length: > 0 })
                || (check.Category is not null && !SafeName(check.Category))
                || check.Classes?.Any(name => !SafeName(name)) == true)
                throw new InvalidDataException($"{InventoryPath}: invalid routine suite, paths or selectors for {check.Suite}.");
            if (!changedFiles!.Any(file => DotNetWorkPackagePlanner.TouchesDotNet([file])
                    && check.Paths.Any(prefix => prefix == "*" || ProjectTestSelectionPolicy.Matches(file, prefix))))
                continue;
            if (!fixedFilters.TryGetValue(suite.Command, out var filters))
                fixedFilters[suite.Command] = filters = [];
            if (check.Category is not null) filters.Add($"Category={check.Category}");
            filters.AddRange((check.Classes ?? []).Select(name => $"FullyQualifiedName~{name}"));
            fixedClasses.AddRange(check.Classes ?? []);
        }
        if (fixedFilters.Count == 0) return selection;

        // Use the same guard inventory on mapped and legacy reviews. A project's
        // fixed selectors share one invocation with its guards, so RDG runs once.
        var guards = GuardFirstGatePlan.Apply(declared.Commands).Where(GuardFirstGatePlan.IsGuardStep).ToArray();
        var coverage = new List<(VerifyCommand Invocation, string Filter, string Exclusions)>();
        var bounded = guards.Select(guard =>
        {
            var original = declared.Commands.First(command => command.Kind == VerifyCommandKind.Test
                && command.WorkingSubdir == guard.WorkingSubdir && command.Shell == guard.Shell
                && GuardFirstGatePlan.GuardCommand(command.Command) == guard.Command);
            var filters = fixedFilters.GetValueOrDefault(original.Command) ?? [];
            var exclusions = string.Join('&',
                new[] { "Category!=Guard", "FullyQualifiedName!~.Architecture." }
                    .Concat(filters.Distinct().Select(filter => filter.Replace("=", "!=", StringComparison.Ordinal)
                        .Replace("~", "!~", StringComparison.Ordinal))));
            coverage.Add((original, FilterExpression(original.Command), exclusions));
            return guard with
            {
                Command = Narrow(original.Command, string.Join('|', new[] { GuardFirstGatePlan.GuardFilter }.Concat(filters))),
                TestScope = Scope,
                SelectionReason = "fast review: applicable guards and repository fixed checks",
            };
        }).ToArray();
        var commands = selection.Commands.Where(command => command.Kind != VerifyCommandKind.Test).ToList();
        commands.AddRange(bounded);
        foreach (var command in selection.Commands.Where(command => command.Kind == VerifyCommandKind.Test))
        {
            var exclusions = string.Join('&', coverage.Where(replacement =>
                    InvocationKey(replacement.Invocation) == InvocationKey(command)
                    && FilterIsCovered(command.Command, replacement.Filter))
                .Select(replacement => replacement.Exclusions).Distinct());
            // Whole .NET invocations are post-merge obligations on fast reviews.
            if ((GateFlakyRerunPolicy.IsTargetableDotNetTest(command.Command)
                    && declared.Commands.Any(full => full.Kind == VerifyCommandKind.Test && full.Command == command.Command
                        && InvocationKey(full) == InvocationKey(command)))
                || (exclusions.Length > 0 && (GuardFirstGatePlan.IsGuardStep(command) || command.TestScope == Scope))) continue;
            // Remove cases only where the matching invocation runs their replacement.
            // Other projects, directories, build options and filters can select different cases.
            commands.Add(exclusions.Length > 0 && GateFlakyRerunPolicy.IsTargetableDotNetTest(command.Command)
                ? command with { Command = Narrow(command.Command, exclusions) } : command);
        }
        var tests = commands.Where(command => command.Kind == VerifyCommandKind.Test).ToArray();
        var routines = definition.TestSuites.Where(suite => suite.Category == ProjectTestSuiteCategories.Routine)
            .ToDictionary(suite => suite.Id, suite => suite.Command, StringComparer.Ordinal);
        var candidates = tests.Select((command, index) => new TestSelectionCandidate(
            $"review-base-{index + 1}", command, [command.SelectionReason ?? "fast review: retained affected check"])).ToArray();
        return DeterministicTestScope.WithDigest(selection with
        {
            Commands = commands,
            Audit = selection.Audit with
            {
                FullSuiteRequired = false,
                Level = TestExecutionLevels.WorkPackage,
                Candidates = [.. selection.Audit.Candidates, .. candidates],
                SelectedCandidateIds = candidates.Select(candidate => candidate.Id).ToArray(),
                SelectedTestClasses = selection.Audit.SelectedTestClasses.Concat(fixedClasses).Distinct().ToArray(),
                SuiteCommands = routines,
                SelectedSuites = routines.Where(suite => tests.Any(test => test.Command == suite.Value))
                    .Select(suite => suite.Key).ToArray(),
                OmittedSuites = routines.Where(suite => !tests.Any(test => test.Command == suite.Value))
                    .Select(suite => suite.Key).ToArray(),
                SelectedCommands = tests.Select(TestSelectionPlanner.Describe).ToArray(),
                OmittedTestCommands = declared.Commands.Where(command => command.Kind == VerifyCommandKind.Test
                    && !tests.Any(test => test.Command == command.Command && test.WorkingSubdir == command.WorkingSubdir))
                    .Select(TestSelectionPlanner.Describe).ToArray(),
                Reasons = [.. selection.Audit.Reasons, .. bounded.Select(command => command.SelectionReason + ": " + command.Command),
                    "Fixed checks are selected, not attempted; execution receipts supply attempted and missing evidence. Full routine suites remain post-merge obligations."],
            },
        });
    }

    private static (string, string, VerifyCommandShell) InvocationKey(VerifyCommand command)
        => (command.WorkingSubdir, ExistingFilter.Replace(command.Command, string.Empty).Trim(), command.Shell);

    private static bool SafeName(string name) => Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.CultureInvariant);

    private static bool SafePath(string path) => path == "*" || (!string.IsNullOrWhiteSpace(path)
        && !path.StartsWith('/') && !path.Contains('\\') && !path.Split('/').Contains("..")
        && Regex.IsMatch(path, @"^[A-Za-z0-9_./-]+$", RegexOptions.CultureInvariant));

    private static string Narrow(string command, string filter)
        => $"{ExistingFilter.Replace(command, string.Empty).Trim()} --filter \"({FilterExpression(command)})&({filter})\"";

    private static string FilterExpression(string command)
    {
        var match = ExistingFilter.Match(command);
        return match.Success ? match.Groups["value"].Value.Trim('"', '\'') : "Category!=MachineBound";
    }

    private static bool FilterIsCovered(string command, string replacementFilter)
    {
        if (!ExistingFilter.IsMatch(command)) return false;
        var selected = FilterExpression(command);
        // Recognize the selector's simple conjunction, not an arbitrary Boolean
        // expression. Unknown shapes keep their cases rather than assume coverage.
        return selected == replacementFilter || Regex.IsMatch(selected,
            @"\A\(" + Regex.Escape(replacementFilter) + @"\)&\([^()]+\)\z", RegexOptions.CultureInvariant);
    }

    private static readonly Regex ExistingFilter = new(
        @"\s--filter(?:\s+|=)(?<value>""[^""]*""|'[^']*'|\S+)", RegexOptions.CultureInvariant);

    private static readonly Regex ProjectArgument = new(
        @"(?:^|\s)(?:""[^""]+\.csproj""|'[^']+\.csproj'|[^\s""']+\.csproj)(?=\s|$)", RegexOptions.CultureInvariant);
}
