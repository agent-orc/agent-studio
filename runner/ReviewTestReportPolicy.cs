using AgentStudio.TaskServer.Contracts;

using System.Text.RegularExpressions;

namespace AgentRunner;

/// <summary>Test output is evidence only when it names a completed test run.</summary>
internal static partial class ReviewTestReportPolicy
{
    internal const string MissingReportClassification = "TestReportUnavailable";

    internal static bool Missing(ReviewCommandDto command, ProcessResult result)
    {
        if (!command.CompareToBaseline || ReviewBaselineModes.IsExitStatus(command.BaselineMode)) return false;
        // A successful custom command can prove its exit status without using
        // one of the test reporters we parse. Keep incomplete known runners
        // visible as infrastructure failures rather than treating them as green.
        if (result.Success && !HasExplicitMissingReport(result)
            && !LooksLikeIncompleteRunner(result)) return false;
        var failures = RemoteReviewWorkspace.ParsedTestFailures(result);
        return (result.ExitCode != 0 && failures.Count == 0)
               || !ReviewBaselineTestResultPolicy.HasResult(failures, result.StdOut, result.StdErr);
    }

    private static bool LooksLikeIncompleteRunner(ProcessResult result)
    {
        var output = result.StdOut + "\n" + result.StdErr;
        return output.Contains("Application bundle generation complete", StringComparison.Ordinal)
               && !ReviewBaselineTestResultPolicy.HasResult([], result.StdOut, result.StdErr);
    }

    internal static string MissingPhase(ProcessResult result)
    {
        var output = result.StdOut + "\n" + result.StdErr;
        if (!output.Contains("Application bundle generation complete", StringComparison.Ordinal))
            return "test runner did not report a completed test";
        return output.Contains(" RUN  v", StringComparison.Ordinal)
            ? "Vitest started but did not report a completed test"
            : "Angular bundle completed but Vitest did not report a run";
    }

    internal static string MissingReportPath(ProcessResult result, string consoleArtifactPath)
    {
        var match = VitestReportPath().Match(result.StdOut + "\n" + result.StdErr);
        return match.Success ? match.Groups["path"].Value.TrimEnd(':') : consoleArtifactPath;
    }

    internal static bool HasExplicitMissingReport(ProcessResult result)
        => VitestReportPath().IsMatch(result.StdOut + "\n" + result.StdErr);

    [GeneratedRegex(@"Vitest completed without (?:a readable report at|test results in) (?<path>\S+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex VitestReportPath();

    internal static string? SetupChangeReason(IReadOnlyList<string> nameStatusLines)
    {
        foreach (var line in nameStatusLines)
        {
            var fields = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2) continue;
            var status = fields[0];
            // Git lists both paths for renames and copies. Either end can be
            // the test setup or a spec, even when the other end is not.
            foreach (var field in fields.Skip(1))
            {
                var path = field.Replace('\\', '/');
                if (path is "frontend/angular.json" or "frontend/vitest.config.ts" or "frontend/vitest.release.config.ts"
                    or "frontend/package.json" or "frontend/package-lock.json" or "global.json"
                    || path.EndsWith(".runsettings", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("frontend/scripts/", StringComparison.Ordinal)
                       && (path.Contains("test", StringComparison.OrdinalIgnoreCase)
                           || path.Contains("report", StringComparison.OrdinalIgnoreCase)))
                    return $"test configuration or runner changed: {path}";
                if (status[0] is 'A' or 'D' or 'R' or 'C'
                    && (path.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".test.ts", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase)))
                    return $"spec file list changed: {path}";
            }
        }
        return null;
    }
}
