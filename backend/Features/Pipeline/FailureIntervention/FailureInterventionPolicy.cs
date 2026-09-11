using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

public static class FailureDomains
{
    public const string Product = "product";
    public const string Infrastructure = "infrastructure";
}

public sealed record FailureCommandEvidence(
    string FailureCode,
    string? Outcome = null,
    int? ExitCode = null,
    long? DurationMs = null,
    string? StdoutTail = null,
    string? StderrTail = null,
    string? StepId = null,
    IReadOnlyList<string>? EvidencePointers = null,
    DateTime? OccurredAt = null);

public sealed record FailureClassificationResult(
    string Domain,
    string FailureClass,
    string Fingerprint,
    string Signature,
    bool Deterministic,
    string Reason);

/// <summary>Pure first-pass policy. Unknown failures return null so only those reach an LLM fallback.</summary>
public static partial class FailureInterventionPolicy
{
    [GeneratedRegex(@"\b(AGT|ASS|PROJ|ATP|RB)-\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex TaskKeyPattern();
    [GeneratedRegex(@"\b[0-9a-f]{7,64}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ShaPattern();
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpacePattern();

    public static FailureClassificationResult? Classify(FailureCommandEvidence evidence)
    {
        var code = (evidence.FailureCode ?? string.Empty).Trim();
        var text = EvidenceText(evidence);
        var lower = text.ToLowerInvariant();

        string? domain = null;
        string failureClass = code;
        string reason = string.Empty;

        if (EqualsAny(code, "ToolUnavailable") || lower.Contains("model is not supported")
            || lower.Contains("tool unavailable") || lower.Contains("command not found"))
        {
            domain = FailureDomains.Infrastructure;
            failureClass = "ReviewInfra/ToolUnavailable";
            reason = "The declared review tool or provider model is unavailable.";
        }
        else if (EqualsAny(code, "MissingSource"))
        {
            domain = FailureDomains.Infrastructure;
            failureClass = "gate/MissingSource";
            reason = "The gate could not materialize its configured source.";
        }
        else if (EqualsAny(code, "build-gate-failed", "BuildGateFailed"))
        {
            domain = lower.Contains("timeout") || lower.Contains("not configured") || lower.Contains("no such file")
                ? FailureDomains.Infrastructure
                : FailureDomains.Product;
            failureClass = "gate/build-gate-failed";
            reason = domain == FailureDomains.Product
                ? "The delivery failed its deterministic build or test gate."
                : "The build gate failed before it could evaluate the delivery.";
        }
        else if (lower.Contains("origin is not configured") || lower.Contains("fetch timeout")
                 || lower.Contains("fetch timed out") || EqualsAny(code, "IntegrationError", "FetchTimeout"))
        {
            domain = FailureDomains.Infrastructure;
            failureClass = "integration/configuration";
            reason = "Repository integration failed because its remote or network boundary was unavailable.";
        }
        else if (EqualsAny(code, "crash-as-completion", "CrashAsCompletion")
                 || lower.Contains("crash-as-completion"))
        {
            domain = FailureDomains.Infrastructure;
            failureClass = "run/crash-as-completion";
            reason = "A crashed execution was observed at a completion boundary.";
        }
        else if (EqualsAny(code, "InfraCrash", "WatchdogTimeout", "CliLaunchFailed",
                     "EnvironmentBlocker", "EnvironmentalTransient"))
        {
            domain = FailureDomains.Infrastructure;
            failureClass = "run/" + code.ToLowerInvariant();
            reason = "The run ended at an infrastructure or toolchain boundary.";
        }
        else if (EqualsAny(code, "PermissionBlocked", "AuthRefreshFailed", "ModelInvalid"))
        {
            domain = FailureDomains.Infrastructure;
            failureClass = "run/configuration";
            reason = "The run could not continue with its configured provider or permissions.";
        }
        else if (EqualsAny(code, "ContextOverflow"))
        {
            domain = FailureDomains.Product;
            failureClass = "run/context-overflow";
            reason = "The delivery exceeded the context available to its configured execution route.";
        }
        else if (string.Equals(evidence.Outcome, "ReviewInfra", StringComparison.OrdinalIgnoreCase))
        {
            domain = FailureDomains.Infrastructure;
            failureClass = "ReviewInfra/" + (string.IsNullOrWhiteSpace(code) ? "Unknown" : code);
            reason = "The review plane classified the outcome as infrastructure failure.";
        }

        if (domain is null) return null;
        var signature = NormalizeSignature(CanonicalEvidence(failureClass, text));
        return new FailureClassificationResult(
            domain,
            failureClass,
            Fingerprint(failureClass, signature),
            signature,
            true,
            reason);
    }

    public static FailureClassificationResult FromFallback(
        FailureCommandEvidence evidence, string domain, string reason)
    {
        var normalizedDomain = string.Equals(domain, FailureDomains.Product, StringComparison.OrdinalIgnoreCase)
            ? FailureDomains.Product
            : FailureDomains.Infrastructure;
        var signature = NormalizeSignature(EvidenceText(evidence));
        var failureClass = string.IsNullOrWhiteSpace(evidence.FailureCode) ? "unknown" : evidence.FailureCode.Trim();
        return new FailureClassificationResult(normalizedDomain, failureClass,
            Fingerprint(failureClass, signature), signature, false, reason);
    }

    public static string NormalizeSignature(string text)
    {
        var normalized = TaskKeyPattern().Replace(text ?? string.Empty, "<task>");
        normalized = ShaPattern().Replace(normalized, "<sha>");
        normalized = WhiteSpacePattern().Replace(normalized.Trim(), " ").ToLowerInvariant();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    private static string EvidenceText(FailureCommandEvidence evidence) => string.Join("\n", new[]
    {
        evidence.StdoutTail,
        evidence.StderrTail,
        evidence.Outcome,
        evidence.FailureCode,
    }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string CanonicalEvidence(string failureClass, string text)
    {
        var needles = failureClass switch
        {
            "ReviewInfra/ToolUnavailable" => new[] { "model is not supported", "tool unavailable", "command not found" },
            "integration/configuration" => new[] { "origin is not configured", "fetch timed out", "fetch timeout" },
            "gate/MissingSource" => new[] { "missing source", "source checkout missing" },
            "run/crash-as-completion" => new[] { "crash-as-completion", "process exited" },
            _ => [],
        };
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var needle = needles.FirstOrDefault(candidate => line.Contains(candidate, StringComparison.OrdinalIgnoreCase));
            if (needle is null) continue;
            var start = line.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (failureClass == "ReviewInfra/ToolUnavailable")
            {
                var sentence = line.IndexOf("The '", StringComparison.OrdinalIgnoreCase);
                if (sentence >= 0) start = sentence;
            }
            return line[start..];
        }
        return text;
    }

    private static string Fingerprint(string failureClass, string signature)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(failureClass.ToLowerInvariant() + "\n" + signature));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private static bool EqualsAny(string value, params string[] candidates)
        => candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));
}
