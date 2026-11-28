using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Evidence required to charge a backend test failure to a delivery.</summary>
internal static partial class ReviewTestFailureAttribution
{
    internal const string HostCrashEvidence = "test-host crash: ";

    internal static bool IsTestCommand(ReviewCommandDto command)
    {
        var invocation = (command.FileName + " " + string.Join(' ', command.Arguments))
            .Replace('\\', '/');
        return invocation.Contains("dotnet test", StringComparison.OrdinalIgnoreCase)
               || invocation.Contains("node --test", StringComparison.OrdinalIgnoreCase)
               || invocation.Contains("npm test", StringComparison.OrdinalIgnoreCase)
               || invocation.Contains("ng test", StringComparison.OrdinalIgnoreCase)
               || invocation.Contains("run test", StringComparison.OrdinalIgnoreCase)
               || invocation.Contains("jest", StringComparison.OrdinalIgnoreCase)
               || invocation.Contains("vitest", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsBackendTestCommand(ReviewCommandDto command)
        => (command.FileName + " " + string.Join(' ', command.Arguments))
            .Replace('\\', '/')
            .Contains("backend.Tests/", StringComparison.OrdinalIgnoreCase);

    internal static bool BackendChanged(IReadOnlyList<string> changedPaths)
        => changedPaths.Any(path => path.Replace('\\', '/')
            .StartsWith("backend/", StringComparison.Ordinal));

    internal static DeliveryFailureDiagnosisResult? Diagnose(
        IReadOnlyList<string> candidate,
        IReadOnlyList<string>? cleanRepeat,
        IReadOnlyList<string> baseline,
        int baselineExitCode,
        bool backendTest,
        bool? backendChanged,
        IReadOnlyList<string>? runOutputs = null)
    {
        // AGT-3755: an aborted test host fails whatever was running when it
        // died; those names say nothing about the delivery.
        if (runOutputs?.FirstOrDefault(TestHostAborted) is { } crashed)
            return Environment(HostCrashEvidence
                + "the test host process aborted; "
                + (CrashCause(crashed) is { } cause ? $"cause: {cause}. " : "no unobserved exception in the output. ")
                + "The tests running at the abort are not new failures of this delivery.");
        if (backendTest && backendChanged is null)
            return new DeliveryFailureDiagnosisResult(
                DeliveryFailureDiagnosis.FirstOccurrence, 0.2,
                ["The backend diff could not be read; a backend test failure cannot be attributed without it."]);
        if (backendTest && backendChanged == false)
            return Environment("The delivery changes no file under backend/; a backend test failure cannot be attributed to this delivery by test output alone.");

        if (cleanRepeat is not null && AssertionsNeedingControl(candidate, cleanRepeat, baseline) is { Count: > 0 } assertions)
            return new(DeliveryFailureDiagnosis.FirstOccurrence, 0.2,
                [$"Incomplete exact-test evidence for {string.Join(", ", assertions)}: matching candidate repeats and passing same-filter base controls are unavailable."]);

        if (cleanRepeat is { Count: > 0 } && !Same(candidate, cleanRepeat))
            return Environment("Candidate and clean repeat failed different tests; the failing set varies on the review host.");

        if (backendTest && baselineExitCode != 0 && !Same(candidate, baseline))
            return Environment("The integration baseline also failed, and its failing set differs from the candidate; the review host failure set varies.");

        return null;
    }

    internal static IReadOnlyList<string> AssertionsNeedingControl(
        IReadOnlyList<string> candidate, IReadOnlyList<string> repeat, IReadOnlyList<string> baseline)
        => Same(candidate, repeat) && (baseline.Count == 0 || Same(candidate, baseline))
            ? []
            : candidate.Intersect(repeat, StringComparer.Ordinal).Except(baseline, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();

    internal static string? AssertionFingerprint(
        string attemptRoot, string stepId, string test, ProcessResult process)
    {
        // Prefer the detailed VSTest block to xUnit's earlier, potentially
        // interleaved report. Never combine the streams into one assertion.
        var streams = new[] { process.StdOut, process.StdErr }.Select(stream => stream.Split('\n')).ToArray();
        foreach (var vstest in new[] { true, false })
        {
            var matches = streams.SelectMany(lines => lines.Select((line, index) => (lines, line, index)))
                .Where(item => IsAssertionHeading(ConsoleHeading(item.line), test, vstest)).ToArray();
            if (matches.Length == 0) continue;
            if (matches.Length != 1) return null;
            var (raw, _, start) = matches[0];
            var leading = LeadingAnsi().Match(raw[start]).Value;
            var trailing = TrailingAnsi().Match(raw[start]).Value;
            var xunit = XunitPrefix().IsMatch(AnsiEscape().Replace(raw[start], ""));
            var diagnostic = new List<string>();
            var stack = false;
            var source = false;
            var message = false;
            for (var index = start + 1; index < raw.Length; index++)
            {
                var heading = ConsoleHeading(raw[index]);
                if (TestResultHeading().IsMatch(heading) || TestRunSummary().IsMatch(heading))
                {
                    // Without a stack boundary, this could be text asserted by
                    // the test. A partial message cannot prove a reproduction.
                    if (!stack) return null;
                    if (heading.StartsWith("Passed ", StringComparison.Ordinal)
                        || heading.StartsWith("Skipped ", StringComparison.Ordinal))
                        continue;
                    if (TestRunSummary().IsMatch(heading)
                        && raw.Skip(index + 1).Any(item => ConsoleHeading(item) == "Stack Trace:"))
                        return null;
                    break;
                }
                if (index == raw.Length - 1 && raw[index].Length == 0) break;
                var line = raw[index];
                if (leading.Length > 0 || trailing.Length > 0)
                {
                    if (!line.StartsWith(leading, StringComparison.Ordinal)
                        || !line.EndsWith(trailing, StringComparison.Ordinal)
                        || line.Length < leading.Length + trailing.Length)
                        return null;
                    line = line.Substring(leading.Length, line.Length - leading.Length - trailing.Length);
                }
                if (xunit)
                {
                    var prefix = XunitPrefix().Match(line);
                    if (!prefix.Success) return null;
                    line = line[prefix.Length..];
                }
                // Remove one framing label, never whitespace or message lines
                // that happen to look like console metadata.
                if (vstest && index == start + 1)
                {
                    if (line.Trim() != "Error Message:") return null;
                    continue;
                }
                if (line.Trim() == "Stack Trace:")
                {
                    if (stack || !message) return null;
                    stack = true;
                }
                else if (stack && StackSource().Match(line) is { Success: true } location)
                {
                    source = true;
                    var path = location.Groups["path"];
                    line = line[..path.Index] + ReviewFailureFingerprint.NormalizePaths(attemptRoot, path.Value)
                        + line[(path.Index + path.Length)..];
                }
                if (!stack && !string.IsNullOrWhiteSpace(line)) message = true;
                if (stack && string.IsNullOrWhiteSpace(line)) continue;
                if (stack && XunitPrefix().Match(line) is { Success: true } footer
                    && line[footer.Length..].TrimStart().StartsWith("Finished: ", StringComparison.Ordinal))
                    continue;
                diagnostic.Add(line);
            }
            // Identical messages from different assertions in one method need
            // their source locations. Missing locations are incomplete proof.
            if (!source || diagnostic.All(string.IsNullOrWhiteSpace)) return null;
            var identity = string.Join('\n', diagnostic);
            return ReviewFailureFingerprint.Prefix + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(stepId + "\n" + test + "\n" + identity))).ToLowerInvariant();
        }
        return null;
    }

    private static string ConsoleHeading(string line)
        => XunitPrefix().Replace(AnsiEscape().Replace(line, ""), "").Trim();

    private static bool IsAssertionHeading(string heading, string test, bool vstest)
        => vstest
            ? heading == "Failed " + test || (heading.StartsWith("Failed " + test + " [", StringComparison.Ordinal)
                && TestResultHeading().IsMatch(heading))
            : heading == test + " [FAIL]";

    [GeneratedRegex(@"^(?:\x1b\[[0-?]*[ -/]*[@-~])+")]
    private static partial Regex LeadingAnsi();

    [GeneratedRegex(@"(?:\x1b\[[0-?]*[ -/]*[@-~])+$")]
    private static partial Regex TrailingAnsi();

    [GeneratedRegex(@"^(?:(?:Failed|Passed|Skipped) .+ \[[0-9.,<> hms]+\]|[\w.+`]+(?:\(.*\))? \[FAIL\])$")]
    private static partial Regex TestResultHeading();

    [GeneratedRegex(@"^(?:Test Run (?:Failed|Successful|Aborted)\.?|(?:Failed|Passed)!\s+-\s+Failed:.*|Total tests: [0-9]+)$")]
    private static partial Regex TestRunSummary();

    [GeneratedRegex(@"^(?:[ \t]+at .+ in (?<path>.+):line [0-9]+|[ \t]+(?<path>(?:/|[A-Za-z]:[/\\]).+)\([0-9]+(?:,[0-9]+)?\): at .+)\r?$")]
    private static partial Regex StackSource();

    internal static DeliveryFailureDiagnosisResult DiagnoseAssertions(IReadOnlyList<ReviewAssertionEvidenceDto> proofs)
    {
        foreach (var proof in proofs.Where(proof => proof.IncompleteReason is null))
        {
            var diagnosis = DeliveryFailureDiagnosis.Classify(new(
                proof.Fingerprint, true, null, false, proof.RepeatFingerprint));
            if (diagnosis.ChargesCard)
                return diagnosis with { Evidence = diagnosis.Evidence.Prepend(
                    $"exact-test assertion: {proof.Test}; filter={proof.Filter}").ToArray() };
        }
        return new(DeliveryFailureDiagnosis.FirstOccurrence, 0.2, proofs.Select(proof =>
            $"Incomplete exact-test evidence for {proof.Test}: {proof.IncompleteReason ?? "assertion fingerprint did not match"}; "
            + $"fingerprint={proof.Fingerprint ?? "unavailable"}; repeat={proof.RepeatFingerprint ?? "unavailable"}").ToArray());
    }

    internal static bool TestHostAborted(string output)
        => AbortedHost().IsMatch(output);

    /// <summary>The first exception line a crash handler wrote, for example <c>[UnobservedTaskException] System.X: message</c>.</summary>
    internal static string? CrashCause(string output)
        => CrashLine().Match(output) is { Success: true } match ? match.Groups["cause"].Value.Trim() : null;

    internal static bool IsHostCrash(DeliveryFailureDiagnosisResult? diagnosis)
        => diagnosis?.Evidence.Any(item => item.StartsWith(
            ReviewUnattributedTestFailurePolicy.EvidencePrefix + HostCrashEvidence, StringComparison.Ordinal)) == true;

    [GeneratedRegex(@"Test Run Aborted|active test run was aborted|test host (?:process )?(?:crashed|terminated)|host process exited unexpectedly", RegexOptions.IgnoreCase)]
    private static partial Regex AbortedHost();

    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"^[ \t]*\[xUnit\.net [^\]]+\][ \t]?")]
    private static partial Regex XunitPrefix();

    [GeneratedRegex(@"\[(?:UnobservedTaskException|UnhandledException)\](?:\s+terminating=\w+)?\s+(?<cause>[\w.]+(?:Exception|Error)\b[^\r\n]*)")]
    private static partial Regex CrashLine();

    private static bool Same(IReadOnlyList<string> left, IReadOnlyList<string> right)
        => left.ToHashSet(StringComparer.Ordinal).SetEquals(right);

    private static DeliveryFailureDiagnosisResult Environment(string reason)
        => new(DeliveryFailureDiagnosis.Environment, 0.9,
            [ReviewUnattributedTestFailurePolicy.EvidencePrefix + reason]);
}
