using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Review;

/// <summary>
/// A review command that is already red on the integration branch without the
/// reviewed change (a build-tests verdict classified
/// <c>IntegrationBranchDefect</c>). AGT-3812: the Evidence tab, the Result
/// header, the Acceptance grounds and the Human Review park all state this one
/// sentence, so the owner learns from the UI that the integration branch is red
/// instead of seeing a passing or unproven verdict.
/// </summary>
public sealed record IntegrationBranchRedFinding(
    string Step,
    string Command,
    string Branch,
    string? BaselineSha,
    IReadOnlyList<string> FailingTests,
    string? OutputRef)
{
    public const string Classification = "IntegrationBranchDefect";

    /// <summary>
    /// <c>npm test (verify-2) is red on develop at 08eb16b7 without this change; failing tests: test.</c>
    /// </summary>
    public string Sentence
    {
        get
        {
            var at = string.IsNullOrWhiteSpace(BaselineSha) ? "" : $" at {Short(BaselineSha)}";
            var tests = FailingTests.Count == 0 ? "" : $"; failing tests: {string.Join(", ", FailingTests)}";
            return $"{Command} ({Step}) is red on {Branch}{at} without this change{tests}.";
        }
    }

    public static bool IsIntegrationBranchDefect(string? classification) =>
        string.Equals(classification?.Trim(), Classification, StringComparison.OrdinalIgnoreCase);

    /// <summary>The integration branch as an owner reads it: <c>refs/heads/develop</c> becomes <c>develop</c>.</summary>
    public static string BranchName(string? integrationRef)
    {
        var value = integrationRef?.Trim().Trim('`') ?? "";
        if (value.StartsWith("refs/heads/", StringComparison.Ordinal)) value = value["refs/heads/".Length..];
        return value.Length == 0 || value.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? "the integration branch"
            : value;
    }

    /// <summary>
    /// Builds the finding from one verdict's raw cells. The step comes from the
    /// verdict's <c>command:</c> citation unless the caller already knows it;
    /// the failing tests from the runner's <c>failed-tests=</c> token, falling
    /// back to its <c>pre-existing failures:</c> list.
    /// </summary>
    public static IntegrationBranchRedFinding FromVerdict(
        string? step,
        string summary,
        string? evidenceChecked,
        Func<string, string?> commandForStep,
        string? integrationRef,
        Func<string, string?>? outputForStep = null)
    {
        var resolvedStep = CitedValue(evidenceChecked, "command:") ?? step ?? "build-tests";
        var baseline = CitedValue(evidenceChecked, "baseline:") ?? Token(summary, "baseline=");
        var tests = List(Token(summary, "failed-tests="));
        if (tests.Count == 0) tests = List(Token(summary, "pre-existing failures:"));
        return new IntegrationBranchRedFinding(
            resolvedStep,
            DisplayCommand(commandForStep(resolvedStep)) ?? resolvedStep,
            BranchName(integrationRef),
            baseline,
            tests,
            outputForStep?.Invoke(resolvedStep));
    }

    /// <summary>Every red-integration-branch build-tests verdict of a review report.</summary>
    public static IReadOnlyList<IntegrationBranchRedFinding> FromReport(Contract.ReviewReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Verdicts
            .Where(verdict => IsIntegrationBranchDefect(verdict.Classification))
            .Select(verdict => FromVerdict(
                null,
                verdict.Summary,
                verdict.EvidenceChecked,
                stepId => request.Commands
                    .Where(command => string.Equals(command.StepId, stepId, StringComparison.OrdinalIgnoreCase))
                    .Select(command => string.Join(' ', new[] { command.FileName }.Concat(command.Arguments)))
                    .FirstOrDefault(),
                request.Workspace.IntegrationRef))
            .ToList();
    }

    /// <summary>
    /// The Human Review park reason for a review that found the integration
    /// branch red, or null when it did not. The reason names the branch and the
    /// decision, so the Needs you row and the park state it instead of the
    /// generic "finished with a finding" text.
    /// </summary>
    public static string? ParkReason(IReadOnlyList<IntegrationBranchRedFinding> findings)
    {
        if (findings.Count == 0) return null;
        var branch = findings[0].Branch;
        return $"Integration branch {branch} is red: "
               + string.Join(" ", findings.Select(finding => finding.Sentence))
               + $" Decide whether to accept this delivery while {branch} is red or to hold it until {branch} is fixed.";
    }

    /// <summary>
    /// Drops a <c>sh -lc</c> / <c>bash -c</c> wrapper so the sentence names the
    /// command the project defined (<c>npm test</c>), not the shell around it.
    /// </summary>
    internal static string? DisplayCommand(string? command)
    {
        var value = command?.Trim().Trim('`').Trim();
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (var shell in new[] { "sh", "bash", "/bin/sh", "/bin/bash" })
        {
            foreach (var flag in new[] { "-lc", "-c" })
            {
                var prefix = $"{shell} {flag} ";
                if (value.StartsWith(prefix, StringComparison.Ordinal))
                    return value[prefix.Length..].Trim().Trim('"', '\'');
            }
        }
        return value;
    }

    private static string? CitedValue(string? evidenceChecked, string key)
    {
        if (string.IsNullOrWhiteSpace(evidenceChecked)) return null;
        foreach (var part in evidenceChecked.Split(';'))
        {
            var value = part.Trim();
            if (value.StartsWith(key, StringComparison.OrdinalIgnoreCase) && value.Length > key.Length)
                return value[key.Length..].Trim();
        }
        return null;
    }

    private static string? Token(string summary, string key)
    {
        var start = summary.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += key.Length;
        var end = summary.IndexOf(';', start);
        var value = (end < 0 ? summary[start..] : summary[start..end]).Trim();
        // "baseline=<sha> (baseline executed in this attempt)" keeps the SHA only.
        var space = value.IndexOf(' ');
        return key.EndsWith('=') && key != "failed-tests=" && space > 0 ? value[..space] : value.TrimEnd('.');
    }

    private static IReadOnlyList<string> List(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;
}
