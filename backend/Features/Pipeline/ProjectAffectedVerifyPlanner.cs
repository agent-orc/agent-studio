using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>Adapts the shared repository impact rule to executable gate commands.</summary>
public static class ProjectAffectedVerifyPlanner
{
    public static StagedVerifyPlan? TryPlan(string repositoryPath, VerifyPlan plan,
        IReadOnlyList<string>? paths, string? lane, string? level)
    {
        var read = ProjectDefinitionReader.ReadWorkspace(repositoryPath);
        if (!read.IsValid || read.Definition is not { TestSelection.Count: > 0 } definition
            || plan.Source != VerifyPlan.SourceProjectDefinition) return null;
        if (level == TestExecutionLevels.Continuous) return null;
        if (level == TestExecutionLevels.BatchPreCheck)
        {
            var affected = TryPlan(repositoryPath, plan, paths, lane, TestExecutionLevels.WorkPackage)!;
            var bounded = BatchPreCheckPlanner.Plan(repositoryPath, plan with { Commands = affected.Commands }, paths ?? [], lane);
            var precheckCommands = (bounded.Commands.Count == 0 ? affected.Commands : bounded.Commands)
                .Select(command => command.TestScope == TestExecutionLevels.BatchPreCheck
                    ? command with { TestScope = GuardFirstGatePlan.GuardScope } : command).ToArray();
            var startedSuites = affected.Audit.SuiteCommands.Where(suite => precheckCommands.Any(command =>
                command.Kind == VerifyCommandKind.Test && command.Command == suite.Value)).Select(suite => suite.Key).ToArray();
            return DeterministicTestScope.WithDigest(new(precheckCommands, affected.Audit with
            {
                Level = TestExecutionLevels.BatchPreCheck,
                FullSuiteRequired = false,
                SelectedCommands = precheckCommands.Where(command => command.Kind == VerifyCommandKind.Test)
                    .Select(TestSelectionPlanner.Describe).ToArray(),
                SelectedSuites = startedSuites,
                OmittedTestCommands = plan.Commands.Where(command => command.Kind == VerifyCommandKind.Test
                    && !precheckCommands.Any(selected => selected.Command == command.Command))
                    .Select(TestSelectionPlanner.Describe).ToArray(),
                OmittedSuites = affected.Audit.SuiteCommands.Keys.Except(startedSuites).ToArray(),
                Reasons = [.. affected.Audit.Reasons, "Batch admission runs only selected builds, guards and frontend lint; full suites stay with the batch."],
            }) { PreparationCommands = affected.Audit.FullSuiteRequired ? null : affected.PreparationCommands });
        }
        var selection = ProjectTestSelectionPolicy.Select(definition, paths, level == TestExecutionLevels.Full);
        var compileOnly = level is TestExecutionLevels.CompileOnly or TestExecutionLevels.BuildOnly;
        var selectedTests = selection.Full ? definition.Commands.Test
            : definition.TestSuites.Where(suite => selection.Suites.Contains(suite.Id)).Select(suite => suite.Command).ToArray();
        var builds = selection.Build.Select(index => definition.Commands.Build[index]).ToHashSet(StringComparer.Ordinal);
        var lints = selection.Lint.Select(index => definition.Commands.Lint[index]).ToHashSet(StringComparer.Ordinal);
        var commands = plan.Commands.Where(command => command.Kind switch
        {
            VerifyCommandKind.Build => builds.Contains(command.Command),
            VerifyCommandKind.Lint => !compileOnly && lints.Contains(command.Command),
            VerifyCommandKind.Test => !compileOnly && selectedTests.Contains(command.Command),
            _ => false,
        }).Select(command => command with
        {
            TestScope = command.Kind == VerifyCommandKind.Test
                ? selection.Full ? TestExecutionLevels.Full : TestExecutionLevels.WorkPackage : command.TestScope,
            SelectionReason = string.Join(" ", selection.Reasons),
        }).ToArray();
        // Documentation checks still run in a compile-only integration: they are
        // the only applicable verification, and require no compiled dependencies.
        if (compileOnly && selection.Build.Count == 0 && !selection.Full)
            commands = plan.Commands.Where(command => command.Kind == VerifyCommandKind.Test
                && selectedTests.Contains(command.Command)).ToArray();
        var tests = commands.Where(command => command.Kind == VerifyCommandKind.Test).ToArray();
        return DeterministicTestScope.WithDigest(new(commands, new TestSelectionAudit
        {
            Level = compileOnly ? level! : selection.Full ? TestExecutionLevels.Full : TestExecutionLevels.WorkPackage,
            Lane = lane ?? "",
            DiffInput = paths ?? [],
            Selector = ProjectTestSelectionPolicy.Selector,
            FullSuiteRequired = selection.Full && !compileOnly,
            SelectedCommands = tests.Select(TestSelectionPlanner.Describe).ToArray(),
            OmittedTestCommands = plan.Commands.Where(command => command.Kind == VerifyCommandKind.Test
                && !tests.Any(test => test.Command == command.Command)).Select(TestSelectionPlanner.Describe).ToArray(),
            SelectedSuites = definition.TestSuites.Where(suite => tests.Any(test => test.Command == suite.Command))
                .Select(suite => suite.Id).ToArray(),
            SuiteCommands = definition.TestSuites.Where(suite => suite.Category == ProjectTestSuiteCategories.Routine)
                .ToDictionary(suite => suite.Id, suite => suite.Command, StringComparer.Ordinal),
            OmittedSuites = definition.TestSuites.Where(suite => suite.Category == ProjectTestSuiteCategories.Routine
                && !tests.Any(test => test.Command == suite.Command)).Select(suite => suite.Id).ToArray(),
            Reasons = selection.Reasons,
        }) { PreparationCommands = selection.Full
            ? [definition.Commands.Prepare] : selection.Prepare });
    }
}
