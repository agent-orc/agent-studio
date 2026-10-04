using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>Card routing for a failed integration gate, using the AGT-2916 diagnosis.</summary>
public sealed record MergeGateFailure(
    string Classification,
    string Fingerprint,
    IReadOnlyList<string> FailingItems,
    string Reason,
    string MissingEvidence,
    bool TransportFailure = false,
    int OtherCardMatches = 0,
    IReadOnlyList<string>? OtherCardKeys = null,
    int PriorMatches = 0);

public enum MergeGateFailureRoute
{
    RetryGate,
    Redeliver,
    FixRound,
    WaitForCause,
    HumanReview,
}

public static class MergeGateFailurePolicy
{
    public const string Environment = "environment";
    public const string Product = "product";
    public const string IntegrationBranch = "integration-branch";
    public const string Undecidable = "undecidable";

    public static MergeGateFailureRoute Route(MergeGateFailure failure)
        => failure.Classification switch
        {
            Environment when failure.TransportFailure => MergeGateFailureRoute.RetryGate,
            Environment => MergeGateFailureRoute.Redeliver,
            IntegrationBranch => MergeGateFailureRoute.WaitForCause,
            Product when failure.OtherCardMatches > 0 || failure.PriorMatches >= 2
                => MergeGateFailureRoute.WaitForCause,
            Product => MergeGateFailureRoute.FixRound,
            _ => MergeGateFailureRoute.HumanReview,
        };

    public static bool WaitsInAutoReview(string? integrationOutcome, string? integrationDetail)
        => string.Equals(integrationOutcome,
                nameof(MergeIntoIntegrationOutcome.GateEnvironmentFailure), StringComparison.Ordinal)
           || integrationDetail?.StartsWith("waiting on ", StringComparison.Ordinal) == true;

    /// <summary>Reads the durable gate receipt shape written under post-steps.</summary>
    public static MergeGateFailure ClassifyLog(string? report)
    {
        if (string.IsNullOrWhiteSpace(report))
            return new(Undecidable, "missing", [], "No gate report was available.",
                "The gate report and command output are missing.");
        var lines = report.Split('\n');
        var header = lines.FirstOrDefault() ?? string.Empty;
        var reason = lines.FirstOrDefault(line => line.StartsWith("reason=", StringComparison.Ordinal))
            ?.Substring("reason=".Length).Trim() ?? string.Empty;
        var outputStart = report.IndexOf("--- last-300-lines ---", StringComparison.Ordinal);
        var output = outputStart >= 0
            ? report[(outputStart + "--- last-300-lines ---".Length)..]
            : report;
        var kindText = HeaderValue(header, "failureKind=");
        var kind = Enum.TryParse<BuildTestGateFailureKind>(kindText, true, out var parsed)
            ? parsed : BuildTestGateFailureKind.None;
        var diagnosisText = Between(reason, "diagnosis=", ' ');
        var recordedFingerprint = Regex.Match(reason, @"(?:^|; )fingerprint=(?<value>\S+)")
            .Groups["value"].Value.TrimEnd(';');
        var evidence = new List<string>();
        var baseline = Regex.Match(reason, @"baseline=(?:red|green); fingerprint=\S+");
        if (baseline.Success) evidence.Add(baseline.Value.TrimEnd(';'));
        var history = Regex.Match(reason, @"history-24h: other-cards=\d+; prior=\d+");
        if (history.Success) evidence.Add(history.Value);
        var cards = Regex.Match(reason, @"history-24h: cards=[^;]*");
        if (cards.Success) evidence.Add(cards.Value);
        var diagnosis = string.IsNullOrWhiteSpace(diagnosisText)
            || !reason.Contains("baseline=", StringComparison.Ordinal)
            || !reason.Contains("clean-repeat=", StringComparison.Ordinal)
            || diagnosisText == DeliveryFailureDiagnosis.Product
               && (!reason.Contains("baseline=green", StringComparison.Ordinal)
                   || !reason.Contains("clean-repeat=red", StringComparison.Ordinal)) ? null
            : new DeliveryFailureDiagnosisResult(diagnosisText, 1, evidence);
        return Classify(new BuildTestGateResult(
            BuildTestGateVerdict.Fail, null, 0, output, reason, false, false)
        {
            FailureKind = kind,
            Diagnosis = diagnosis,
            FailureFingerprint = recordedFingerprint is "" or "none"
                ? null : recordedFingerprint,
        });
    }

    public static MergeGateFailure Classify(BuildTestGateResult gate)
    {
        var output = gate.Output ?? string.Empty;
        var reason = gate.Reason ?? string.Empty;
        var evidence = reason + "\n" + output;
        var items = GateFlakyRerunPolicy.ParseFailedTests(output).Concat(
                Regex.Matches(output, @"(?mi)^\s*(?:FAIL\s+|❯\s+)(?<item>\S+\.spec\.(?:ts|tsx|js))")
                    .Select(match => match.Groups["item"].Value))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var fingerprint = gate.FailureFingerprint;
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            var identity = items.Length > 0
                ? string.Join("\n", items)
                : FailureOutputNormalizer.Identity(evidence, gate.ExitCode);
            fingerprint = "gate:" + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(identity.ToLowerInvariant()))).ToLowerInvariant()[..16];
        }

        var transport = ContainsAny(evidence, "ECONNRESET", "ETIMEDOUT", "unable to access",
            "connection reset", "connection timed out");
        var host = ContainsAny(evidence, "Worker exited unexpectedly", "violated gate-run budget",
            "out of memory", "allocation failed", "heap out of memory");
        var diagnosis = gate.Diagnosis;
        var baselineRed = diagnosis?.Evidence.Any(line =>
            line.StartsWith("baseline=red", StringComparison.Ordinal)) == true;
        var sameBaselineItem = baselineRed && diagnosis?.Evidence.Any(line =>
            line.StartsWith("baseline=red", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(fingerprint)
            && line.EndsWith("fingerprint=" + fingerprint, StringComparison.Ordinal)) == true;
        var otherCards = ReadOtherCardMatches(diagnosis);
        var otherCardKeys = ReadOtherCardKeys(diagnosis);
        var priorMatches = ReadPriorMatches(diagnosis);

        // Conflicting signals are evidence to investigate, not permission to
        // charge a delivery or to repeatedly retry a broken host.
        if ((transport || host) && (items.Length > 0 || diagnosis?.ChargesCard == true))
            return new(Undecidable, fingerprint, items, reason,
                "Host/transport markers conflict with failed test evidence.");
        if (transport)
            return new(Environment, fingerprint, items, reason, string.Empty,
                TransportFailure: true);
        if (host)
            return new(Environment, fingerprint, items, reason, string.Empty);
        if (sameBaselineItem)
            return new(IntegrationBranch, fingerprint, items, reason, string.Empty,
                OtherCardMatches: otherCards, OtherCardKeys: otherCardKeys,
                PriorMatches: priorMatches);
        if (baselineRed)
            return new(Undecidable, fingerprint, items, reason,
                "The integration baseline is red, but the same failing item was not established.");
        if (diagnosis?.ChargesCard == true)
            return new(Product, fingerprint, items, reason, string.Empty,
                OtherCardMatches: otherCards, OtherCardKeys: otherCardKeys,
                PriorMatches: priorMatches);
        // AGT-2916 calls a cross-card match environment to avoid charging an
        // individual delivery. The cause breaker owns that shared red item.
        if (otherCards > 0 && items.Length > 0)
            return new(Product, fingerprint, items, reason, string.Empty,
                OtherCardMatches: otherCards, OtherCardKeys: otherCardKeys,
                PriorMatches: priorMatches);
        if (diagnosis is null && gate.FailureKind is (BuildTestGateFailureKind.Environment
            or BuildTestGateFailureKind.OutOfMemory or BuildTestGateFailureKind.Timeout
            or BuildTestGateFailureKind.ProcessLaunch or BuildTestGateFailureKind.Lock
            or BuildTestGateFailureKind.MissingSource))
            return new(Environment, fingerprint, items, reason, string.Empty);
        return new(Undecidable, fingerprint, items, reason,
            diagnosis is null
                ? "Baseline, clean repeat, and fingerprint history are unavailable."
                : "The baseline, clean repeat, and fingerprint history did not establish ownership.");
    }

    private static int ReadOtherCardMatches(DeliveryFailureDiagnosisResult? diagnosis)
    {
        var line = diagnosis?.Evidence.FirstOrDefault(item =>
            item.StartsWith("history-24h: other-cards=", StringComparison.Ordinal));
        if (line is null) return 0;
        var value = line["history-24h: other-cards=".Length..].Split(';')[0];
        return int.TryParse(value, out var count) ? count : 0;
    }

    private static IReadOnlyList<string> ReadOtherCardKeys(DeliveryFailureDiagnosisResult? diagnosis)
    {
        var line = diagnosis?.Evidence.FirstOrDefault(item =>
            item.StartsWith("history-24h: cards=", StringComparison.Ordinal));
        return line is null ? [] : line["history-24h: cards=".Length..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int ReadPriorMatches(DeliveryFailureDiagnosisResult? diagnosis)
    {
        var line = diagnosis?.Evidence.FirstOrDefault(item =>
            item.StartsWith("history-24h: other-cards=", StringComparison.Ordinal));
        if (line is null) return 0;
        var marker = "; prior=";
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        return start >= 0 && int.TryParse(line[(start + marker.Length)..], out var count)
            ? count : 0;
    }

    private static bool ContainsAny(string value, params string[] markers)
        => markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string? HeaderValue(string header, string key)
    {
        var start = header.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return null;
        start += key.Length;
        var end = header.IndexOf(' ', start);
        return end < 0 ? header[start..] : header[start..end];
    }

    private static string? Between(string value, string marker, char terminator)
    {
        var start = value.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = value.IndexOf(terminator, start);
        return end < 0 ? value[start..] : value[start..end];
    }
}
