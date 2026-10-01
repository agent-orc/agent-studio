using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Pipeline;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// The stable identity of one failure cause, counted fleet-wide (AGT-W57 §5 E1).
/// <see cref="Value"/> hashes the three parts; the parts themselves are kept so
/// a fingerprint that turns out too coarse can be read back from the log.
/// </summary>
/// <param name="Value">16 hex characters over class, toolchain and text.</param>
/// <param name="FailureClass">Outcome and reported classification, e.g. <c>ReviewInfra/PreparationFailed</c>.</param>
/// <param name="Toolchain">The tool or model route that failed, e.g. <c>tool:npm</c> or <c>agent:codex:gpt-5.4-mini</c>.</param>
/// <param name="NormalizedText">The failure text without card keys, SHAs, times, durations, GUIDs, ports and temp paths.</param>
public sealed record CauseFingerprint(
    string Value,
    string FailureClass,
    string Toolchain,
    string NormalizedText);

/// <summary>
/// Pure fingerprint of a review infrastructure failure. Two attempts of
/// different cards that fail for the same reason on the same toolchain must
/// produce one value; the same text on a different tool or model must not.
/// </summary>
public static partial class CauseFingerprintPolicy
{
    public const string NoToolchain = "none";
    private const int MaxFallbackLines = 10;

    public static CauseFingerprint Compute(
        string? outcome,
        string? failureClassification,
        string? failureText,
        int? exitCode,
        string? toolchain)
    {
        var failureClass = FailureClass(outcome, failureClassification);
        var context = string.IsNullOrWhiteSpace(toolchain) ? NoToolchain : toolchain.Trim().ToLowerInvariant();
        var text = NormalizeText(failureClass, failureClassification, outcome, failureText ?? string.Empty, exitCode);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            failureClass.ToLowerInvariant() + "\n" + context + "\n" + text));
        return new CauseFingerprint(
            Convert.ToHexString(bytes).ToLowerInvariant()[..16],
            failureClass,
            context,
            text);
    }

    /// <summary>
    /// The toolchain context of the failed command: the agent route for a model
    /// call, the executable for a tool call. The step id is deliberately not
    /// part of it, so one withdrawn model failing in two aspects is one cause.
    /// </summary>
    public static string Toolchain(Contract.ReviewCommandDto? planned, string? fileName)
    {
        if (planned is not null && Contract.ReviewCommandKinds.IsAgent(planned.ExecutionKind))
            return $"agent:{Blank(planned.CliType) ?? "default"}:{Blank(planned.Model) ?? "default"}";
        var executable = Blank(planned?.FileName) ?? Blank(fileName);
        return executable is null ? NoToolchain : "tool:" + ExecutableName(executable);
    }

    public static string FailureClass(string? outcome, string? failureClassification)
        => $"{Blank(outcome) ?? "Unknown"}/{Blank(failureClassification) ?? "Unknown"}";

    private static string NormalizeText(
        string failureClass,
        string? failureClassification,
        string? outcome,
        string text,
        int? exitCode)
    {
        // The intervention policy already canonicalises the provider message of
        // a withdrawn model (it cuts the wrapper prefix each aspect adds). Use it
        // only when it agrees on the class; "command not found" in a
        // preparation log is not a withdrawn model.
        var classified = FailureInterventionPolicy.Classify(new FailureCommandEvidence(
            failureClassification ?? string.Empty, outcome, exitCode, StdoutTail: text));
        if (classified is not null
            && string.Equals(classified.FailureClass, failureClass, StringComparison.OrdinalIgnoreCase))
            return classified.Signature;

        var cleaned = TempPath().Replace(text, "<tmp>");
        var identity = Contract.FailureOutputNormalizer.Identity(cleaned, exitCode);
        if (!identity.StartsWith("exit:", StringComparison.Ordinal))
            return FailureInterventionPolicy.NormalizeSignature(identity);

        // No diagnostic line: keep the tail so "npm: not found" and an empty
        // crash with the same exit code stay two causes.
        var tail = cleaned.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => Contract.FailureOutputNormalizer.StripVolatile(line).Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .TakeLast(MaxFallbackLines);
        var joined = string.Join("\n", tail);
        return FailureInterventionPolicy.NormalizeSignature(joined.Length == 0 ? identity : joined + "\n" + identity);
    }

    private static string ExecutableName(string fileName)
    {
        var name = fileName.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        foreach (var extension in new[] { ".exe", ".cmd", ".bat", ".sh" })
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return name[..^extension.Length];
        }
        return name;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"(?:/tmp|/var/folders|[A-Za-z]:\\[^\s]*\\Temp)[/\\][^\s""']*", RegexOptions.IgnoreCase)]
    private static partial Regex TempPath();
}
