using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>Why a red test step did or did not earn its one targeted re-run.</summary>
public static class GateFlakyRerunReasons
{
    /// <summary>The step qualifies; <see cref="GateFlakyRerunDecision.Command"/> is the re-run.</summary>
    public const string Eligible = "eligible";

    /// <summary>The red step was not a filterable <c>dotnet test</c> invocation.</summary>
    public const string NotATestCommand = "not-a-test-command";

    /// <summary>The failure was infrastructure, a timeout, or a toolchain crash, not a red test.</summary>
    public const string NotAProductFailure = "not-a-product-failure";

    /// <summary>The gate-run budget is spent, so the original red stands.</summary>
    public const string NoBudgetLeft = "no-budget-left";

    /// <summary>No test name could be read out of the output, so nothing can be targeted.</summary>
    public const string NoParsedTestNames = "no-parsed-test-names";

    /// <summary>More failures than a flake pattern; a broad red is a real red.</summary>
    public const string TooManyFailures = "too-many-failures";

    /// <summary>A failed name carries characters a <c>FullyQualifiedName=</c> filter cannot express verbatim.</summary>
    public const string UnfilterableTestName = "unfilterable-test-name";
}

/// <param name="ShouldRerun">Whether the gate spends one targeted re-run.</param>
/// <param name="Reason">One <see cref="GateFlakyRerunReasons"/> value, recorded either way.</param>
/// <param name="FailedTests">The exact failed test names read out of the red run.</param>
/// <param name="Command">The re-run command line; null unless <paramref name="ShouldRerun"/>.</param>
public sealed record GateFlakyRerunDecision(
    bool ShouldRerun,
    string Reason,
    IReadOnlyList<string> FailedTests,
    string? Command)
{
    public static GateFlakyRerunDecision No(string reason, IReadOnlyList<string>? failedTests = null)
        => new(false, reason, failedTests ?? [], null);
}

/// <summary>
/// AGT-2853: pure decision for the one targeted re-run the Windows integration
/// gate spends on a red test step before it rolls a green delivery back.
///
/// <para>
/// Between 20:00 and 01:35 on 16./17.09.2026 six pre-develop gate runs rolled
/// back green deliveries because one to three tests of the ~6.700-test backend
/// suite failed inside the full run and passed when the same class was run
/// alone on the same merge candidate. Each false red cost a full remote
/// re-review plus another 25-minute gate, and the operator had to prove the
/// flake by hand.
/// </para>
///
/// <para>
/// The re-run is deliberately narrow: same build (<c>--no-build</c>), same
/// workspace, only the exact failed test names. A green re-run does not hide
/// the failure - the names are recorded as flaky in the gate evidence and on
/// the card timeline under the same
/// <see cref="TaskServer.Contracts.ReviewFlakyQuarantine.Classification"/> the
/// remote review executor writes. A second red is the verdict.
/// </para>
/// </summary>
public static class GateFlakyRerunPolicy
{
    /// <summary>
    /// Above this many distinct failures the run is a broad breakage rather
    /// than the one-to-three-test flake pattern this policy exists for, and a
    /// targeted re-run would only spend budget confirming a real red.
    /// </summary>
    public const int MaxTargetedTests = 10;

    /// <summary>
    /// Boundary check, then the decision. <paramref name="remainingBudget"/> is
    /// what is left of the gate-run budget at the moment the step went red; the
    /// re-run is charged to that same budget, so an exhausted budget leaves the
    /// original red standing.
    /// </summary>
    public static GateFlakyRerunDecision Decide(
        VerifyCommandKind commandKind,
        BuildTestGateFailureKind failureKind,
        string command,
        string output,
        TimeSpan remainingBudget)
    {
        if (failureKind != BuildTestGateFailureKind.Code)
            return GateFlakyRerunDecision.No(GateFlakyRerunReasons.NotAProductFailure);
        if (commandKind != VerifyCommandKind.Test || !IsTargetableDotNetTest(command))
            return GateFlakyRerunDecision.No(GateFlakyRerunReasons.NotATestCommand);

        var failed = ParseFailedTests(output);
        if (failed.Count == 0)
            return GateFlakyRerunDecision.No(GateFlakyRerunReasons.NoParsedTestNames);
        if (failed.Count > MaxTargetedTests)
            return GateFlakyRerunDecision.No(GateFlakyRerunReasons.TooManyFailures, failed);
        if (!failed.All(IsFilterable))
            return GateFlakyRerunDecision.No(GateFlakyRerunReasons.UnfilterableTestName, failed);
        if (remainingBudget <= TimeSpan.Zero)
            return GateFlakyRerunDecision.No(GateFlakyRerunReasons.NoBudgetLeft, failed);

        return new GateFlakyRerunDecision(
            true,
            GateFlakyRerunReasons.Eligible,
            failed,
            RerunCommand(command, failed));
    }

    /// <summary>
    /// The exact failed test names a VSTest run printed, deduplicated and
    /// ordered. Both shapes the .NET test loggers emit are read: the console
    /// logger's <c>Failed Name [12 ms]</c> and the xUnit diagnostic
    /// <c>Name [FAIL]</c>.
    /// </summary>
    public static IReadOnlyList<string> ParseFailedTests(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        var failures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = AnsiEscapeSequence.Replace(line, string.Empty);
            var match = DotNetFailedPrefix.Match(normalized);
            if (!match.Success) match = DotNetFailSuffix.Match(normalized);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value.Trim();
            if (name.Length > 0) failures.Add(name);
        }
        return failures.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// <c>FullyQualifiedName=A|FullyQualifiedName=B</c> over the exact failed
    /// names, in the order <see cref="ParseFailedTests"/> returned them.
    /// </summary>
    public static string FilterExpression(IReadOnlyList<string> failedTests)
        => string.Join("|", failedTests.Select(name => $"FullyQualifiedName={name}"));

    /// <summary>
    /// The red command re-pointed at only its own failures: any selection
    /// filter the staged planner had added is replaced by the targeted one, and
    /// the run reuses the artifacts the red run already produced
    /// (<c>--no-build</c>) so the re-run costs a test pass, not a build.
    /// </summary>
    public static string RerunCommand(string command, IReadOnlyList<string> failedTests)
    {
        var stripped = ExistingFilter.Replace(command, string.Empty).Trim();
        if (!NoBuildOption.IsMatch(stripped)) stripped += " --no-build";
        return $"{stripped} --filter \"{FilterExpression(failedTests)}\"";
    }

    /// <summary>
    /// A single <c>dotnet test</c> invocation. A chained or piped command line
    /// is left alone: rewriting its filter would change a part of it this
    /// policy never inspected.
    /// </summary>
    internal static bool IsTargetableDotNetTest(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        // The selection filter is inspected and replaced, so its own operators
        // (`Category!=MachineBound&Category!=Flaky`) are not shell structure.
        var withoutFilter = ExistingFilter.Replace(command, string.Empty);
        if (CommandSeparator.IsMatch(withoutFilter)) return false;
        var tokens = withoutFilter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return false;
        var tool = Path.GetFileNameWithoutExtension(tokens[0].Trim('"', '\''));
        if (!string.Equals(tool, "dotnet", StringComparison.OrdinalIgnoreCase)) return false;
        var verb = tokens.Skip(1).FirstOrDefault(token => !token.StartsWith('-'));
        return string.Equals(verb, "test", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A name a <c>FullyQualifiedName=</c> term can carry verbatim. VSTest
    /// filter syntax gives <c>(</c>, <c>)</c>, <c>&amp;</c>, <c>|</c>, <c>=</c>,
    /// <c>!</c> and <c>~</c> their own meaning, so a theory case printed with
    /// its arguments is not targeted at all rather than targeted with an
    /// expression that might silently match nothing and read as green.
    /// </summary>
    internal static bool IsFilterable(string name)
        => FilterableName.IsMatch(name) && name.Contains('.', StringComparison.Ordinal);

    private static readonly Regex DotNetFailedPrefix = new(
        @"^\s*Failed\s+(?<name>.+?)\s+\[[^\]]+\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DotNetFailSuffix = new(
        @"^\s*(?:\[[^\]]+\]\s+)?(?<name>.+?)\s+\[FAIL\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnsiEscapeSequence = new(
        "\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExistingFilter = new(
        @"\s--filter(?:\s+|=)(?:""[^""]*""|'[^']*'|\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NoBuildOption = new(
        @"(?:^|\s)--no-build(?:\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CommandSeparator = new(
        @"[|&;]|\$\(|`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FilterableName = new(
        @"^[A-Za-z0-9_.+`]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
