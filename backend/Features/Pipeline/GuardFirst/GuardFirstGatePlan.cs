using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-W57 section 4 (guard tests first, full suite after): architecture and
/// guard tests run as their own gate step ahead of every other test command, so
/// a guard violation returns a verdict in seconds instead of after the full
/// suite. Pure ordering; the gate's existing stop-at-first-red loop does the rest.
/// </summary>
public static class GuardFirstGatePlan
{
    /// <summary>Test scope stamped on the derived guard step.</summary>
    public const string GuardScope = "guard";

    /// <summary>
    /// Convention: tests in an <c>Architecture</c> namespace or tagged
    /// <c>Category=Guard</c>. A project without such tests gets a guard step
    /// that matches nothing and exits green.
    /// </summary>
    public const string GuardFilter = "FullyQualifiedName~.Architecture.|Category=Guard";

    /// <summary>
    /// Inserts one guard step per blocking, filterable <c>dotnet test</c>
    /// command directly before the first test command. Build and lint order is
    /// unchanged; the original commands keep running afterwards.
    /// </summary>
    public static IReadOnlyList<VerifyCommand> Apply(IReadOnlyList<VerifyCommand> commands)
    {
        var guards = commands
            .Where(command => command.Kind == VerifyCommandKind.Test
                              && command.BlocksWorkPackage
                              && command.TestScope != GuardScope
                              && GateFlakyRerunPolicy.IsTargetableDotNetTest(command.Command))
            .Select(command => command with
            {
                Command = GuardCommand(command.Command),
                TestScope = GuardScope,
                SelectionReason = "guard-first: architecture and guard tests before the full suite",
            })
            .ToArray();
        if (guards.Length == 0) return commands;

        var firstTest = commands.ToList().FindIndex(command => command.Kind == VerifyCommandKind.Test);
        var ordered = new List<VerifyCommand>(commands.Count + guards.Length);
        ordered.AddRange(commands.Take(firstTest));
        ordered.AddRange(guards);
        ordered.AddRange(commands.Skip(firstTest));
        return ordered;
    }

    public static bool IsGuardStep(VerifyCommand command) => command.TestScope == GuardScope;

    /// <summary>
    /// The same invocation narrowed to the guard filter. An existing selection
    /// filter is kept and intersected, so the guard step never runs a test the
    /// full-suite command would have excluded.
    /// </summary>
    public static string GuardCommand(string command)
    {
        var match = ExistingFilter.Match(command);
        if (!match.Success) return $"{command.Trim()} --filter \"{GuardFilter}\"";
        var existing = match.Groups["value"].Value.Trim('"', '\'');
        var stripped = ExistingFilter.Replace(command, string.Empty).Trim();
        return $"{stripped} --filter \"({existing})&({GuardFilter})\"";
    }

    private static readonly Regex ExistingFilter = new(
        @"\s--filter(?:\s+|=)(?<value>""[^""]*""|'[^']*'|\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
