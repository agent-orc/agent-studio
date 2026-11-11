using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Decides whether a clean repeat that exited zero showed that it ran the
/// JavaScript tests the candidate run failed (AGT-3650). Vitest and Jest
/// failures are named <c>file &gt; suite &gt; test</c>. A passed-test line for
/// each one, as Vitest's verbose reporter prints, is test-name proof. The
/// default reporters name failing tests only; a passed line for every test
/// file that held a failure is then test-file proof. Output without either
/// proves nothing, so the repeat stays review infrastructure.
/// </summary>
internal static partial class ReviewRepeatObservationPolicy
{
    /// <summary>
    /// Set on a clean repeat. A Vitest config that reads it selects the verbose
    /// reporter, so the repeat names every passing test.
    /// </summary>
    internal const string VitestReporterVariable = "AGENT_STUDIO_VITEST_REPORTER";

    internal sealed record Proof(string Level, IReadOnlyList<string> Tests);

    /// <summary>True when every failure name starts with a test file.</summary>
    internal static bool HasTestFileNames(IReadOnlyList<string> failures)
        => failures.Count > 0 && failures.All(name => TestFile(name) is not null);

    internal static Proof? Observe(string output, IReadOnlyList<string> failures)
    {
        if (!HasTestFileNames(failures)) return null;
        var passedTests = new HashSet<string>(StringComparer.Ordinal);
        var filesWithPassedTests = new HashSet<string>(StringComparer.Ordinal);
        var passedFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = AnsiEscapeSequence().Replace(raw, string.Empty);
            var match = PassedFile().Match(line);
            if (!match.Success) match = JestPassedFile().Match(line);
            if (match.Success)
            {
                if (TestFile(match.Groups["file"].Value) is { } passedFile) passedFiles.Add(passedFile);
                continue;
            }
            match = PassedTest().Match(line);
            if (!match.Success) continue;
            var parts = HierarchySeparator().Split(match.Groups["name"].Value.Trim());
            if (parts.Length < 2 || TestFile(parts[0]) is not { } file) continue;
            passedTests.Add(string.Join(" > ", [file, .. parts[1..]]));
            filesWithPassedTests.Add(file);
        }
        var failed = failures.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // A bare file failure (the file did not load) has no test name to
        // see; a passing test inside that file shows it ran.
        if (failed.All(name => passedTests.Contains(name)
                || (!name.Contains(" > ", StringComparison.Ordinal)
                    && filesWithPassedTests.Contains(TestFile(name)!))))
            return new(ReviewRepeatObservation.TestNames, failed);
        if (failed.All(name => passedFiles.Contains(TestFile(name)!)))
            return new(ReviewRepeatObservation.TestFiles, failed);
        return null;
    }

    /// <summary>The test file a failure name starts with, or null for another shape.</summary>
    internal static string? TestFile(string name)
    {
        var match = TestFileName().Match(name.Split(" > ", 2)[0].Trim());
        return match.Success ? match.Groups["file"].Value : null;
    }

    // Optional Vitest project label, a path without spaces, a test file
    // extension, then an optional :line:column or Vitest's "[ file ]" suffix.
    [GeneratedRegex(@"^(?<file>(?:\|[^|]+\|\s+)?\S+\.(?:spec|test)\.[cm]?[jt]sx?)(?::\d+:\d+)?(?:\s+\[\s*\S+\s*\])?$", RegexOptions.CultureInvariant)]
    private static partial Regex TestFileName();

    // Vitest default reporter: " ✓ |project| src/a.spec.ts (3 tests | 1 skipped) 12ms".
    [GeneratedRegex(@"^\s*✓\s+(?<file>\S.*?)\s+\(\d+\s+tests?(?:\s*\|[^)]*)?\)(?:\s+\d+(?:\.\d+)?\s*m?s)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PassedFile();

    // Jest default reporter: "PASS src/a.test.ts (5.2 s)".
    [GeneratedRegex(@"^\s*PASS\s+(?<file>\S.*?)(?:\s+\(\d+(?:\.\d+)?\s*m?s\))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex JestPassedFile();

    // Vitest verbose reporter: " ✓ |project| src/a.spec.ts > suite > test 4ms".
    [GeneratedRegex(@"^\s*✓\s+(?<name>.+?)(?:\s+\d+(?:\.\d+)?\s*m?s)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PassedTest();

    [GeneratedRegex(@"\s+(?:›|>)\s+", RegexOptions.CultureInvariant)]
    private static partial Regex HierarchySeparator();

    [GeneratedRegex("\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\))", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapeSequence();
}
