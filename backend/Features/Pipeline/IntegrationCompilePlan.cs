using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>Repository-declared scope for compile checks. Reviews and release gates keep their plans.</summary>
internal static class IntegrationCompilePlan
{
    internal static StagedVerifyPlan? TryPlan(string repository, BuildTestGateRequest request,
        VerifyPlan plan, IReadOnlyList<string>? paths)
    {
        if (!request.IntegrationPriority || request.RequiredTestLevel != TestExecutionLevels.CompileOnly
            || plan.Source != VerifyPlan.SourceProjectDefinition || paths is not { Count: > 0 }) return null;
        var read = ProjectDefinitionReader.ReadWorkspace(repository);
        if (!read.IsValid || read.Definition is not { IntegrationBuild.Count: > 0 } definition) return null;
        var rows = definition.IntegrationBuild;
        if (rows.Any(row => row.Paths.Count == 0 || row.Paths.Any(path => string.IsNullOrWhiteSpace(path)
                || path.StartsWith('/') || path.Contains('\\') || path.Split('/').Contains(".."))
            || row.Build.Count == 0 || row.Build.Any(index => index < 0 || index >= definition.Commands.Build.Count)
            || row.Prepare.Count == 0)) return null;
        var selected = new List<ProjectTestSelectionArea>();
        foreach (var path in paths)
        {
            // Every path needs an explicit repository-owned scope; unknown/shared inputs keep the full build.
            var matches = rows.Where(row => row.Paths.Any(prefix => ProjectTestSelectionPolicy.Matches(path, prefix))).ToArray();
            if (matches.Length == 0) return null;
            selected.AddRange(matches);
        }
        var builds = selected.SelectMany(row => row.Build).Distinct()
            .Select(index => definition.Commands.Build[index]).ToHashSet(StringComparer.Ordinal);
        return new(plan.Commands.Where(command => command.Kind == VerifyCommandKind.Build
            && builds.Contains(command.Command)).ToArray(), new TestSelectionAudit
        {
            Level = TestExecutionLevels.CompileOnly, Selector = "integration-build", DiffInput = paths,
            Reasons = ["Repository integrationBuild map selects build and preparation commands for the exact merge diff."],
        }) { PreparationCommands = selected.SelectMany(row => row.Prepare).Distinct(StringComparer.Ordinal).ToArray() };
    }
}
