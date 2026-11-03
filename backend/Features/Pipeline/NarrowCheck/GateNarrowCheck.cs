namespace AgentStudio.Pipeline;

/// <summary>Which part of a red gate a narrow check repeats.</summary>
public static class GateNarrowCheckKinds
{
    /// <summary>The guard-first step: architecture and guard tests.</summary>
    public const string GuardStep = "guard-step";

    /// <summary>The exact failed test names of one red test command.</summary>
    public const string FailedTests = "failed-tests";

    /// <summary>The build commands.</summary>
    public const string Build = "build";
}

/// <summary>
/// AGT-3433: repeat only what failed instead of the whole suite. The batch
/// gate asks this of the base SHA after a red candidate: a guard violation
/// runs the guard step, a red test command runs its failed tests, a build
/// failure runs the build. <paramref name="Command"/> is the red test command
/// as it ran on the candidate; only that command is re-pointed at
/// <paramref name="Tests"/>.
/// </summary>
public sealed record GateNarrowCheck(string Kind, IReadOnlyList<string> Tests, string? Command = null);

/// <summary>
/// Pure narrowing of a resolved verify plan to one <see cref="GateNarrowCheck"/>.
/// Builds stay in front of every narrowed test step, because test commands
/// may run with <c>--no-build</c>. Null means the plan holds nothing the check
/// can repeat; the caller must then not read the run as an answer.
/// </summary>
public static class GateNarrowCheckPlan
{
    public static IReadOnlyList<VerifyCommand>? Apply(
        IReadOnlyList<VerifyCommand> commands, GateNarrowCheck check)
    {
        var builds = commands.Where(command => command.Kind == VerifyCommandKind.Build).ToArray();
        IReadOnlyList<VerifyCommand> narrowed = check.Kind switch
        {
            GateNarrowCheckKinds.GuardStep => GuardFirstGatePlan.Apply(commands)
                .Where(GuardFirstGatePlan.IsGuardStep).ToArray(),
            GateNarrowCheckKinds.FailedTests when check.Tests.Count > 0 => commands
                .Where(command => command.Kind == VerifyCommandKind.Test
                                  && !GuardFirstGatePlan.IsGuardStep(command)
                                  && GateFlakyRerunPolicy.IsTargetableDotNetTest(command.Command)
                                  && (check.Command is null || string.Equals(
                                      command.Command.Trim(), check.Command.Trim(), StringComparison.Ordinal)))
                .Select(command => command with
                {
                    Command = GateFlakyRerunPolicy.TargetCommand(command.Command, check.Tests),
                    SelectionReason = "narrow check: the failed tests of the red candidate",
                })
                .ToArray(),
            _ => [],
        };
        if (check.Kind == GateNarrowCheckKinds.Build)
            return builds.Length == 0 ? null : builds;
        return narrowed.Count == 0 ? null : [.. builds, .. narrowed];
    }
}
