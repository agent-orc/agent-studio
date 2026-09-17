using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentRunner;

/// <summary>What a failed review command's output attributes the failure to.</summary>
internal enum ReviewFailureAttribution
{
    /// <summary>
    /// The command's own results own the verdict: baseline comparison and
    /// test-failure parsing decide between a product failure and a pass.
    /// </summary>
    ProductOwned,

    /// <summary>
    /// Process-level torn-down-<c>/tmp</c> evidence (AGT-2750). The command
    /// never produced a trustworthy test result, so the attempt is settled as
    /// <c>ReviewInfra</c>/<c>TmpMountTornDown</c>.
    /// </summary>
    TmpMountTornDown,
}

/// <summary>
/// Decides whether a failed review command is an infrastructure incident or a
/// product failure, from the command's own output alone.
///
/// AGT-2750 introduced the torn-down-<c>/tmp</c> signature table: a
/// <c>PrivateTmp=true</c> unit restart deletes the detached worker's
/// <c>/tmp</c> mount while it keeps running under <c>KillMode=process</c>, and
/// MSBuild's node pipe and NuGet's global mutex mkdtemp then fail against the
/// deleted mount without producing parseable test output.
///
/// AGT-2857: that table was matched against the whole command output, so a
/// test whose display name, inline data, or assertion message quoted one of the
/// signatures turned a red suite into an infrastructure verdict - a passing
/// theory case named
/// <c>...(reply: "System.IO.IOException: mkdtemp(\"/tmp/.dotnet.AbC1"...)</c>
/// hid one genuinely failed test, kept the card from learning about it, and
/// suppressed the AGT-2841 replacement attempt. Signature matching therefore
/// reads process-level evidence only (never test result lines, display names,
/// inline data, or the indented detail printed under a test), and a run that
/// reported a test summary with failed tests is always product-owned.
/// </summary>
internal static class ReviewFailureAttributionPolicy
{
    /// <summary>The classification other components key on. Never rename.</summary>
    internal const string TmpMountTornDownClassification = "TmpMountTornDown";

    internal static ReviewFailureAttribution Attribute(ProcessResult process)
        => Attribute(process.Success, process.StdOut, process.StdErr);

    internal static ReviewFailureAttribution Attribute(
        bool commandSucceeded,
        string standardOutput,
        string standardError)
    {
        if (commandSucceeded) return ReviewFailureAttribution.ProductOwned;
        var evidence = Read(standardOutput, standardError);

        // A runner that got far enough to report "Failed: N" for N failed tests
        // did not lose its /tmp mount mid-build: those tests are the failure,
        // whatever infra-looking text the suite itself printed. Infrastructure
        // attribution stays available only when no such summary exists - the
        // runner aborted, or never started.
        if (evidence.ReportsFailedTests) return ReviewFailureAttribution.ProductOwned;

        return TornDownTmpSignature(evidence.ProcessLevelOutput)
            ? ReviewFailureAttribution.TmpMountTornDown
            : ReviewFailureAttribution.ProductOwned;
    }

    private static bool TornDownTmpSignature(string processLevelOutput)
        => processLevelOutput.Contains("MSB1025", StringComparison.Ordinal)
           || processLevelOutput.Contains("SocketException (99)", StringComparison.Ordinal)
           || (processLevelOutput.Contains("mkdtemp(\"/tmp/.dotnet.", StringComparison.Ordinal)
               && processLevelOutput.Contains("ENOENT", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Splits one command's output into the evidence the policy may read: the
    /// lines the tool itself emitted, plus whether a test summary counted at
    /// least one failed test that also appears as a failed test result line.
    /// </summary>
    private static CommandOutputEvidence Read(string standardOutput, string standardError)
    {
        var processLevel = new StringBuilder();
        var failedTests = 0;
        var summarizedFailures = 0;
        var insideTestResult = false;
        foreach (var rawLine in $"{standardOutput}\n{standardError}"
                     .Split(['\r', '\n'], StringSplitOptions.None))
        {
            var line = AnsiEscapeSequence.Replace(rawLine, string.Empty);
            if (IsTestResultLine(line, out var failed))
            {
                insideTestResult = true;
                if (failed) failedTests++;
                continue;
            }

            // Counted before the indentation filter below: runners that print
            // their summary as an indented block ("Total tests: 3" followed by
            // "    Failed: 1") still have to be heard.
            var summary = FailedTestSummary.Match(line);
            if (summary.Success
                && int.TryParse(
                    summary.Groups["count"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var count)
                && count > summarizedFailures)
                summarizedFailures = count;

            // Error message, stack trace, standard output, and wrapped inline
            // data of the test result above are indented under it and are just
            // as much test-owned text as its display name.
            if (insideTestResult && IsIndentedDetail(line)) continue;
            insideTestResult = false;
            processLevel.Append(line).Append('\n');
        }

        return new CommandOutputEvidence(
            processLevel.ToString(),
            summarizedFailures > 0 && failedTests > 0);
    }

    private static bool IsIndentedDetail(string line)
        => line.Length > 0 && char.IsWhiteSpace(line[0]);

    /// <summary>
    /// Recognizes one printed test result. The timed form (<c>Passed Foo.Bar
    /// [&lt; 1 ms]</c>) and the marker form (<c>Foo.Bar [FAIL]</c>) carry
    /// arbitrary display names and inline data; the bare form must look like a
    /// test identifier so that prose such as "Failed to restore packages" stays
    /// process-level evidence.
    /// </summary>
    private static bool IsTestResultLine(string line, out bool failed)
    {
        failed = false;
        var marker = TestResultMarker.Match(line);
        if (marker.Success)
        {
            failed = string.Equals(marker.Groups["marker"].Value, "FAIL", StringComparison.Ordinal);
            return true;
        }

        var timed = TimedTestResult.Match(line);
        if (timed.Success)
        {
            failed = string.Equals(timed.Groups["outcome"].Value, "Failed", StringComparison.Ordinal);
            return true;
        }

        var bare = BareTestResult.Match(line);
        if (!bare.Success) return false;
        var name = bare.Groups["name"].Value;
        if (!name.Contains('.', StringComparison.Ordinal)
            || name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal))
            return false;
        failed = string.Equals(bare.Groups["outcome"].Value, "Failed", StringComparison.Ordinal);
        return true;
    }

    private sealed record CommandOutputEvidence(string ProcessLevelOutput, bool ReportsFailedTests);

    private static readonly Regex TimedTestResult = new(
        @"^\s*(?<outcome>Passed|Failed|Skipped)\s+(?<name>\S.*?)\s+\[[^\]]*\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BareTestResult = new(
        @"^\s*(?<outcome>Passed|Failed|Skipped)\s+(?<name>\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TestResultMarker = new(
        @"^\s*(?:\[[^\]]+\]\s+)?\S.*\s\[(?<marker>FAIL|PASS|SKIP)\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FailedTestSummary = new(
        @"\bFailed:\s*(?<count>\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnsiEscapeSequence = new(
        "\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
