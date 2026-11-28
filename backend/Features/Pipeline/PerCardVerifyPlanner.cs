namespace AgentStudio.Pipeline;

/// <summary>
/// The per-card verify plan of a project with <c>testing.perCard: affected</c>
/// (AGT-3310, pipeline Dossier tier T1, decision D1 option T-B): the declared
/// build and lint commands, the fast checks of the touched areas, and the
/// tests the generated project map selects, narrowed to classes by type
/// reference where that applies. A configured
/// <see cref="TestExecutionPolicy.FolderToTestProjects"/> map takes the place
/// of the generated one. When the scope falls back to the full suite the
/// returned audit says so and the caller keeps its full plan unchanged.
/// </summary>
public static class PerCardVerifyPlanner
{
    public const string Lane = "remote-review";
    internal const string DotNetReviewScope = "review-dotnet";
    internal const int OptionalDotNetFileBudget = 32;

    internal static StagedVerifyPlan WithDotNetReviewSelection(string repositoryPath, VerifyPlan declared,
        StagedVerifyPlan selection, IReadOnlyList<string>? changedFiles,
        DotNetReferenceSelection? referenceSelection, bool useBudgetedScope)
    {
        if (changedFiles is not null && !DotNetWorkPackagePlanner.TouchesDotNet(changedFiles))
            return selection;

        var reasons = new List<string>();
        IReadOnlyList<DotNetFastReviewCandidates> candidates = [];
        if (changedFiles is null || referenceSelection is null)
            reasons.Add("fast .NET review: immutable delivery diff unavailable; post-merge verification required");
        else
        {
            var map = GeneratedTestProjectMap.Generate(repositoryPath, declared);
            if ((map.Failure ?? map.StaleReason(changedFiles)) is { } problem)
                reasons.Add($"fast .NET review: {problem}; post-merge verification required");
            var sources = changedFiles.Where(DotNetReferenceSelectionPlanner.IsTestIndexPath).ToArray();
            var paths = sources.Where(path => map.OwningProject(path) is not null).ToArray();
            var unmapped = sources.Except(paths, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (unmapped.Length > 0)
                reasons.Add($"fast .NET review: no project owns {string.Join(", ", unmapped)}; post-merge verification required");
            candidates = DotNetReferenceSelectionPlanner.Prepare(repositoryPath, [], paths, referenceSelection, exactPaths: true)
                .FastReviewCandidates(map, reasons);
        }

        var optionalFiles = candidates.SelectMany(project => project.OptionalClassFiles.Values)
            .SelectMany(paths => paths).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal).ToArray();
        var broad = optionalFiles.Length > OptionalDotNetFileBudget;
        var commands = selection.Commands.ToList();
        var selectedClasses = selection.Audit.SelectedTestClasses.ToHashSet(StringComparer.Ordinal);
        var omittedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var canReplace = useBudgetedScope && reasons.Count == 0;
        foreach (var project in candidates)
        {
            var legacy = selection.Audit.ReferenceSelections.FirstOrDefault(entry =>
                entry.TestProject == project.TestProject)?.SelectedClasses ?? [];
            var declaredLegacy = DeterministicTestScope.DotNetProjectCommand(declared, project.TestProject, legacy);
            var generatedLegacy = $"dotnet test \"{project.TestProject}\""
                + TestSelectionPlanner.DotNetFilterSuffix(declared, legacy);
            var existing = legacy.Count == 0 ? [] : commands.Where(command =>
                command.Kind == VerifyCommandKind.Test && (declaredLegacy is not null && command.Command == declaredLegacy.Command
                    && command.WorkingSubdir == declaredLegacy.WorkingSubdir
                    && command.Shell == declaredLegacy.Shell
                    || command.Command == generatedLegacy && command.WorkingSubdir == ""))
                .ToArray();
            if (existing.Length == 0) legacy = [];

            var trial = project.MandatoryClasses.Concat(broad ? [] : project.OptionalClassFiles.Keys)
                .Order(StringComparer.Ordinal).ToArray();
            var legacyOnly = legacy.Except(trial, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var trialOnly = trial.Except(legacy, StringComparer.Ordinal).ToArray();
            reasons.Add($"fast .NET review shadow {project.TestProject}: mandatory [{string.Join(", ", project.MandatoryClasses)}], "
                + $"current-only [{string.Join(", ", legacyOnly)}], pilot-only [{string.Join(", ", trialOnly)}]");

            // Keep the exact prior invocation in shadow so matching immutable
            // receipts can still be reused. Run only the missing class filters.
            var classes = trialOnly;
            if (canReplace && legacyOnly.Length > 0
                && (trial.Length == 0 || DeterministicTestScope.DotNetProjectCommand(
                    declared, project.TestProject, trial, excludeMachineBound: true) is not null))
            {
                commands.RemoveAll(existing.Contains);
                selectedClasses.ExceptWith(legacyOnly);
                classes = trial;
                legacy = [];
                reasons.Add($"fast .NET review: post-merge routine suite obligation for prior reference coverage in "
                    + $"{project.TestProject}: {string.Join(", ", legacyOnly)}");
            }
            if (broad)
                omittedFiles.UnionWith(project.OptionalClassFiles.Where(pair => !legacy.Contains(pair.Key, StringComparer.Ordinal))
                    .SelectMany(pair => pair.Value));
            // Top-level identifiers can be long. Keep mandatory selections
            // intact even if a single composed filter would exceed the limit.
            var batch = new List<string>();
            var commandLength = DeterministicTestScope.DotNetProjectCommand(declared, project.TestProject, [])?.Command.Length ?? 0;
            var batchLength = commandLength;
            foreach (var name in classes)
            {
                var length = "FullyQualifiedName~|".Length + name.Length;
                if (batch.Count > 0 && batchLength + length > DotNetReferenceSelectionPlanner.MaxFilterCharacters)
                {
                    if (AddDotNetReviewCommand(declared, commands, project.TestProject, batch))
                        selectedClasses.UnionWith(batch);
                    batch = [];
                    batchLength = commandLength;
                }
                batch.Add(name);
                batchLength += length;
            }
            if (batch.Count > 0)
            {
                if (AddDotNetReviewCommand(declared, commands, project.TestProject, batch))
                    selectedClasses.UnionWith(batch);
                else
                    reasons.Add($"fast .NET review: no test invocation for {project.TestProject}; post-merge verification required");
            }
        }
        if (broad)
        {
            if (omittedFiles.Count != optionalFiles.Length)
                reasons.Add($"fast .NET review: broad-reference pilot omits {optionalFiles.Length} test-source files "
                    + $"above the {OptionalDotNetFileBudget}-file trial budget: {string.Join(", ", optionalFiles)}");
            if (omittedFiles.Count > 0)
                reasons.Add($"fast .NET review: broad-reference omission defers {omittedFiles.Count} test-source files "
                    + $"to the post-merge routine suite (trial budget: {OptionalDotNetFileBudget} files): "
                    + string.Join(", ", omittedFiles.Order(StringComparer.Ordinal)));
        }
        if (reasons.Count == 0) return selection;

        var tests = commands.Where(command => command.Kind == VerifyCommandKind.Test).ToArray();
        return DeterministicTestScope.WithDigest(selection with
        {
            Commands = commands,
            Audit = selection.Audit with
            {
                Level = commands.Any(command => command.TestScope == DotNetReviewScope)
                    ? TestExecutionLevels.WorkPackage : selection.Audit.Level,
                SelectedTestClasses = selectedClasses.Order(StringComparer.Ordinal).ToArray(),
                SelectedCommands = tests.Select(TestSelectionPlanner.Describe).ToArray(),
                Reasons = [.. selection.Audit.Reasons, .. reasons],
            },
        });
    }

    private static bool AddDotNetReviewCommand(VerifyPlan declared, List<VerifyCommand> commands,
        string project, List<string> classes)
    {
        var command = DeterministicTestScope.DotNetProjectCommand(declared, project, classes, excludeMachineBound: true);
        if (command is null) return false;
        commands.Add(command with
        {
            TestScope = DotNetReviewScope,
            BlocksWorkPackage = true,
            SelectionReason = "fast .NET review: changed tests, companion conventions and budgeted direct references",
        });
        return true;
    }

    /// <summary>Applied only by the fast coding-review entry point, after fallback.</summary>
    internal static StagedVerifyPlan WithReviewBase(string repositoryPath, VerifyPlan declared,
        StagedVerifyPlan selection, IReadOnlyList<string>? changedFiles,
        IReadOnlyDictionary<string, string>? frontendTree = null)
    {
        selection = ReviewBaseChecks.Apply(repositoryPath, declared, selection, changedFiles);
        var angular = FrontendWorkPackagePlanner.Plan(repositoryPath, changedFiles ?? [], frontendTree)
            is { } slice ? slice with { TestScope = FrontendWorkPackagePlanner.ReviewScope } : null;
        if (angular is null)
            return !FrontendWorkPackagePlanner.TouchesFrontend(changedFiles) ? selection
                : DeterministicTestScope.WithDigest(selection with
                {
                    Audit = selection.Audit with
                    {
                        Reasons = [.. selection.Audit.Reasons,
                            "frontend: no Angular invocation could be derived from the test script and matching unit specs; missing execution evidence"],
                    },
                });
        var replaced = selection.Commands.Where(command => FrontendWorkPackagePlanner.IsBroadFrontendTest(command)
            || (command.Kind == VerifyCommandKind.Test && command.WorkingSubdir == "frontend"
                && command.Command.Contains("--include", StringComparison.Ordinal))).ToArray();
        var commands = selection.Commands.Except(replaced).Append(angular).ToArray();
        var tests = commands.Where(command => command.Kind == VerifyCommandKind.Test).ToArray();
        var candidate = new TestSelectionCandidate("review-angular", angular, [angular.SelectionReason!]);
        return DeterministicTestScope.WithDigest(selection with
        {
            Commands = commands,
            Audit = selection.Audit with
            {
                Level = TestExecutionLevels.WorkPackage,
                SelectedCommands = tests.Select(TestSelectionPlanner.Describe).ToArray(),
                SelectedSuites = selection.Audit.SuiteCommands.Where(suite => tests.Any(test => test.Command == suite.Value))
                    .Select(suite => suite.Key).ToArray(),
                OmittedSuites = selection.Audit.SuiteCommands.Where(suite => !tests.Any(test => test.Command == suite.Value))
                    .Select(suite => suite.Key).ToArray(),
                Candidates = [.. selection.Audit.Candidates.Where(item => !replaced.Contains(item.Command)), candidate],
                SelectedCandidateIds = [.. selection.Audit.SelectedCandidateIds.Where(id =>
                    !selection.Audit.Candidates.Any(item => item.Id == id && replaced.Contains(item.Command))), candidate.Id],
                OmittedTestCommands = selection.Audit.OmittedTestCommands.Concat(declared.Commands
                    .Where(FrontendWorkPackagePlanner.IsBroadFrontendTest).Select(TestSelectionPlanner.Describe)).Distinct().ToArray(),
                Reasons = [.. selection.Audit.Reasons, angular.SelectionReason!],
            },
        });
    }

    public static StagedVerifyPlan Plan(
        string repositoryPath,
        VerifyPlan verifyPlan,
        IReadOnlyList<string>? changedFiles,
        IReadOnlyDictionary<string, string>? statuses,
        TestExecutionPolicy? policy,
        DotNetReferenceSelection? referenceSelection)
    {
        var shared = ProjectAffectedVerifyPlanner.TryPlan(repositoryPath, verifyPlan,
            changedFiles, Lane, TestExecutionLevels.WorkPackage);
        if (shared is not null) return shared;
        var generated = policy?.FolderToTestProjects is { Count: > 0 }
            ? null
            : GeneratedTestProjectMap.Generate(repositoryPath, verifyPlan);
        var scoped = DeterministicTestScope.Plan(repositoryPath, verifyPlan, changedFiles, statuses,
            policy, Lane, TestExecutionLevels.WorkPackage, generated, referenceSelection);
        if (scoped.Audit.FullSuiteRequired) return scoped;

        var fast = FastCheckPlanner.Plan(repositoryPath, verifyPlan, changedFiles ?? []);
        var commands = scoped.Commands.Where(command => command.Kind != VerifyCommandKind.Test)
            .Concat(fast.Commands)
            .Concat(scoped.Commands.Where(command => command.Kind == VerifyCommandKind.Test))
            .ToList();
        return DeterministicTestScope.WithDigest(new StagedVerifyPlan(commands, scoped.Audit with
        {
            Reasons = scoped.Audit.Reasons.Concat(fast.Reasons).ToList(),
        }), statuses);
    }
}
