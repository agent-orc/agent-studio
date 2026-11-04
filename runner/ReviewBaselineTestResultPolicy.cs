using System.Text.RegularExpressions;

namespace AgentRunner;

/// <summary>Requires evidence that a baseline test command ran at least one test.</summary>
internal static partial class ReviewBaselineTestResultPolicy
{
    internal static bool HasResult(
        IReadOnlyList<string> failures,
        string stdout,
        string stderr)
    {
        if (failures.Any(failure => !failure.StartsWith("npm script ", StringComparison.Ordinal)))
            return true;

        var output = stdout + "\n" + stderr;
        return DotNetTotal().IsMatch(output)
               || DotNetPassed().IsMatch(output)
               || NodeTotal().IsMatch(output)
               || JestOrVitestPassed().IsMatch(output);
    }

    [GeneratedRegex(@"\bTotal(?: tests)?:\s*[1-9]\d*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DotNetTotal();

    [GeneratedRegex(@"\bPassed:\s*[1-9]\d*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DotNetPassed();

    [GeneratedRegex(@"(?m)^\s*#\s*tests\s+[1-9]\d*\b", RegexOptions.CultureInvariant)]
    private static partial Regex NodeTotal();

    [GeneratedRegex(@"\bTests:?\s+[1-9]\d*\s+passed\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JestOrVitestPassed();
}
