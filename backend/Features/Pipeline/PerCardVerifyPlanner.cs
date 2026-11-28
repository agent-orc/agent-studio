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
