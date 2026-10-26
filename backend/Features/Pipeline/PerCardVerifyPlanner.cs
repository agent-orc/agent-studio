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

    public static StagedVerifyPlan Plan(
        string repositoryPath,
        VerifyPlan verifyPlan,
        IReadOnlyList<string>? changedFiles,
        IReadOnlyDictionary<string, string>? statuses,
        TestExecutionPolicy? policy,
        DotNetReferenceSelection? referenceSelection)
    {
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
