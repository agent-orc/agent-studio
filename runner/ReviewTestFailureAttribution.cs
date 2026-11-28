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
        var lines = (process.StdOut + "\n" + process.StdErr).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(line => XunitPrefix().Replace(AnsiEscape().Replace(line, ""), "")).ToArray();
        var start = Array.FindIndex(lines, line =>
        {
            var heading = line.Trim();
            return heading == test + " [FAIL]" || heading == "Failed " + test
                || (heading.StartsWith("Failed " + test + " [", StringComparison.Ordinal)
                    && heading.EndsWith("]", StringComparison.Ordinal));
        });
        if (start < 0) return null;
        var diagnostic = lines.Skip(start + 1).TakeWhile(line =>
        {
            var metadata = line.Trim();
            return !metadata.StartsWith("Failed ", StringComparison.Ordinal)
                && !metadata.StartsWith("Passed ", StringComparison.Ordinal)
                && !metadata.StartsWith("Failed!", StringComparison.Ordinal)
                && !metadata.StartsWith("Passed!", StringComparison.Ordinal)
                && !metadata.EndsWith("[FAIL]", StringComparison.Ordinal)
                && metadata != "Stack Trace:" && !metadata.StartsWith("at ", StringComparison.Ordinal)
                && !metadata.StartsWith("Test Run ", StringComparison.Ordinal)
                && !metadata.StartsWith("Total tests:", StringComparison.Ordinal);
        }).SkipWhile(line => string.IsNullOrWhiteSpace(line) || line.Trim() == "Error Message:");
        var message = string.Join('\n', diagnostic).Trim('\n');
        if (string.IsNullOrWhiteSpace(message)) return null;
        // Whitespace, times, GUIDs and ports can be asserted values. Only remove
        // known console framing and checkout paths, not data from the assertion.
        var normalized = ReviewFailureFingerprint.NormalizePaths(attemptRoot, message);
        return ReviewFailureFingerprint.Prefix + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(stepId + "\n" + test + "\n" + normalized))).ToLowerInvariant();
    }

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
