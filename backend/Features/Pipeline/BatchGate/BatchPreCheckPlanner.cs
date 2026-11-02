using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>Cheap checks of one reviewed delivery before batch admission.</summary>
public static class BatchPreCheckPlanner
{
    private const string GuardFilter = "(Category!=MachineBound)&(FullyQualifiedName~.Architecture.|Category=Guard)";

    public static StagedVerifyPlan Plan(string repositoryPath, VerifyPlan verifyPlan,
        IReadOnlyList<string> changedFiles, string? lane)
    {
        var commands = verifyPlan.Commands.Where(command => command.Kind == VerifyCommandKind.Build).ToList();
        var guardCommands = verifyPlan.Commands.Where(command => command.Kind == VerifyCommandKind.Test
            && Regex.IsMatch(command.Command, @"\bdotnet\s+test\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Select(command => command with
            {
                Command = GuardCommand(command.Command),
                TestScope = TestExecutionLevels.BatchPreCheck,
                SelectionReason = "batch admission guard tests",
            }).ToList();
        commands.AddRange(guardCommands);
        if (changedFiles.Any(path => path.StartsWith("frontend/", StringComparison.OrdinalIgnoreCase)))
        {
            commands.AddRange(verifyPlan.Commands.Where(command => command.Kind == VerifyCommandKind.Lint));
            commands.AddRange(FastCheckPlanner.Plan(repositoryPath, verifyPlan, changedFiles).Commands
                .Where(command => command.SelectionReason?.Contains("format", StringComparison.Ordinal) == true));
        }
        return new StagedVerifyPlan(commands, new TestSelectionAudit
        {
            Level = TestExecutionLevels.BatchPreCheck,
            Lane = lane ?? "",
            DiffInput = changedFiles,
            SelectedCommands = commands.Select(command => command.Command).ToArray(),
            OmittedTestCommands = verifyPlan.Commands.Where(command => command.Kind == VerifyCommandKind.Test
                && !Regex.IsMatch(command.Command, @"\bdotnet\s+test\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .Select(command => command.Command).ToArray(),
            Reasons = ["build, guard tests, and touched frontend lint and format checks"],
            Selector = "batch-pre-check",
        });
    }

    private static string GuardCommand(string command)
    {
        var withoutFilter = Regex.Replace(command, @"\s+--filter\s+(?:'[^']*'|""[^""]*""|\S+)", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return withoutFilter + " --filter '" + GuardFilter + "'";
    }
}
