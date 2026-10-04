using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>A merge gate's actionable diagnosis, derived from its report and AGT-2916 evidence.</summary>
public sealed record GateFailureAssessment(
    string Classification,
    string Fingerprint,
    IReadOnlyList<string> FailingItems,
    string Reason,
    string? MissingEvidence = null,
    bool TransportFailure = false,
    IReadOnlyList<string>? OtherCards = null);

public static partial class GateFailureAssessmentPolicy
{
    public const string Product = DeliveryFailureDiagnosis.Product;
    public const string Environment = DeliveryFailureDiagnosis.Environment;
    public const string IntegrationBranch = "integration-branch";
    public const string Undecidable = "undecidable";

    [GeneratedRegex(@"(?im)^\s*(?:\[stderr\]\s*)?(?:FAIL\s+|❯\s+(?:\|[^|]+\|\s+)?)(?<name>[^\s()]+\.spec\.(?:ts|js))\b", RegexOptions.CultureInvariant)]
    private static partial Regex FrontendFailure();

    public static GateFailureAssessment Classify(BuildTestGateResult gate)
    {
        var report = gate.Output + "\n" + gate.Reason;
        var items = ParseFailingItems(report);
        var reason = report.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Contains("[FAIL]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("❯ ", StringComparison.Ordinal) && line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Worker exited unexpectedly", StringComparison.OrdinalIgnoreCase)
                || line.Contains("javascript-transformer-worker", StringComparison.OrdinalIgnoreCase)
                || line.Contains("violated gate-run budget", StringComparison.OrdinalIgnoreCase)
                || line.Contains("gate-run budget exceeded", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ECONNRESET", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ETIMEDOUT", StringComparison.OrdinalIgnoreCase)
                || line.Contains("unable to access", StringComparison.OrdinalIgnoreCase))
            ?? gate.Reason;
        var transport = HasAny(report, "ECONNRESET", "ETIMEDOUT", "unable to access", "connection reset by peer");
        var host = HasAny(report, "Worker exited unexpectedly", "violated gate-run budget",
            "gate-run budget exceeded", "does not match .nvmrc", "out of memory", "heap limit",
            "javascript-transformer-worker")
            || gate.FailureKind == BuildTestGateFailureKind.OutOfMemory;
        var baselineRedLine = gate.Diagnosis?.Evidence.FirstOrDefault(line =>
            line.StartsWith("baseline=red; fingerprint=", StringComparison.Ordinal));
        var baselineRed = baselineRedLine is not null;
        var sameBaselineFailure = baselineRedLine is not null
            && !string.IsNullOrWhiteSpace(gate.FailureFingerprint)
            && string.Equals(baselineRedLine["baseline=red; fingerprint=".Length..],
                gate.FailureFingerprint, StringComparison.OrdinalIgnoreCase);
        var repeatedProduct = gate.FailureHistoryOtherCards.Count > 0 && !host && !transport
            && gate.Diagnosis?.Evidence.Any(line => line.Contains("baseline=green", StringComparison.Ordinal)) == true
            && gate.Diagnosis?.Evidence.Any(line => line.Contains("clean-repeat=red", StringComparison.Ordinal)) == true;
        var diagnosis = gate.Diagnosis?.Classification;
        var conflicting = items.Length > 0 && (host || transport);
        string classification;
        string? missing = null;
        if (sameBaselineFailure)
            classification = IntegrationBranch;
        else if (baselineRed)
        {
            classification = Undecidable;
            missing = "The integration baseline is red, but the same failing item was not established on that tree.";
        }
        else if (conflicting)
        {
            classification = Undecidable;
            missing = "The gate log contains both failed test items and host or transport markers; compare a clean run on the same tree.";
        }
        else if (transport || host)
            classification = Environment;
        else if (diagnosis == DeliveryFailureDiagnosis.Product || repeatedProduct)
            classification = Product;
        else if (diagnosis == DeliveryFailureDiagnosis.Environment)
            classification = Environment;
        else
        {
            classification = Undecidable;
            missing = string.IsNullOrWhiteSpace(gate.Output) && gate.Processes.Count == 0
                ? "The gate report is missing; baseline and clean-repeat diagnosis is unavailable."
                : gate.Diagnosis is null
                ? "The baseline and clean-repeat diagnosis is missing."
                : "The baseline or clean repeat did not establish a delivery-specific failure.";
        }

        var identity = items.Length > 0 ? string.Join("\n", items) : gate.FailureFingerprint;
        if (string.IsNullOrWhiteSpace(identity)) identity = reason;
        var fingerprint = gate.FailureFingerprint ?? (items.Length > 0 ? "gate:item:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToLowerInvariant()))).ToLowerInvariant()[..16]
            : BuildTestGateRunner.Fingerprint(gate.FailureKind, identity ?? "missing-report"));
        return new(classification, fingerprint, items, reason, missing, transport,
            gate.FailureHistoryOtherCards);
    }

    internal static string[] ParseFailingItems(string report)
        => GateFlakyRerunPolicy.ParseFailedTests(report)
            .Concat(FrontendFailure().Matches(report).Select(match => match.Groups["name"].Value.Trim()))
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool HasAny(string text, params string[] markers)
        => markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
}

public static class GateOutcomeRoutingPolicy
{
    public static bool OpensCause(GateFailureAssessment assessment)
        => assessment.Classification == GateFailureAssessmentPolicy.IntegrationBranch
            || assessment.Classification == GateFailureAssessmentPolicy.Product
                && assessment.OtherCards is { Count: > 0 };

    public static bool WaitsInAutoReview(string? outcome, string? recoveryDetail)
        => outcome is nameof(MergeIntoIntegrationOutcome.GateEnvironmentFailure)
            or nameof(MergeIntoIntegrationOutcome.GateIntegrationBranchFailure)
            || recoveryDetail?.StartsWith("waiting on ", StringComparison.OrdinalIgnoreCase) == true;
}
