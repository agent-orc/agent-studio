using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>Bounded, scrubbed command evidence for a deterministic review finding.</summary>
public static partial class ReviewCommandFailureEvidence
{
    public const string Marker = "Candidate command evidence:";
    private const int EncodedStreamLimit = 16 * 1024;
    private const int OutputLimit = 2 * 1024;

    public static string Format(
        ReviewCommandEvidenceDto candidate,
        ReviewCommandEvidenceDto? cleanRepeat,
        IReadOnlyList<ReviewArtifactEvidenceDto> artifacts)
    {
        var command = candidate.Arguments.Count == 0
            ? candidate.FileName
            : $"{candidate.FileName} {string.Join(' ', candidate.Arguments)}";
        var output = string.Join('\n', new[]
        {
            ReadStream(candidate.StdoutSha256, artifacts),
            ReadStream(candidate.StderrSha256, artifacts),
        }.Where(value => value.Length > 0));
        output = Scrub(output);
        var lines = output.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var tail = string.Join('\n', lines.TakeLast(20));
        if (tail.Length > OutputLimit) tail = tail[^OutputLimit..];
        tail = Scrub(tail);
        if (tail.Length == 0) tail = "(no output captured)";
        return $"{Marker} step={candidate.StepId}; command={Scrub(command)}; "
               + $"baseline={candidate.BaselineSha ?? "unavailable"} exit={candidate.BaselineExitCode?.ToString() ?? "unavailable"}; "
               + $"candidate={candidate.ExpectedResultSha} exit={candidate.ExitCode?.ToString() ?? "unavailable"}; "
               + $"clean-repeat exit={cleanRepeat?.ExitCode?.ToString() ?? "unavailable"}\n"
               + $"Candidate output tail:\n{tail}";
    }

    private static string ReadStream(string digest, IReadOnlyList<ReviewArtifactEvidenceDto> artifacts)
    {
        var encoded = artifacts.FirstOrDefault(artifact =>
            string.Equals(artifact.Sha256, digest, StringComparison.OrdinalIgnoreCase)
            && artifact.ContentBase64 is not null)?.ContentBase64;
        if (encoded is null) return string.Empty;
        var start = Math.Max(0, encoded.Length - EncodedStreamLimit);
        start += (4 - start % 4) % 4;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded[start..]));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static string Scrub(string value)
    {
        var scrubbed = ProductSecretRegex().Replace(value, "[REDACTED_CREDENTIAL]");
        scrubbed = HeaderSecretRegex().Replace(scrubbed, "$1[REDACTED]");
        scrubbed = NamedSecretRegex().Replace(scrubbed, "$1[REDACTED]");
        return BearerSecretRegex().Replace(scrubbed, "Bearer [REDACTED]");
    }

    [GeneratedRegex(@"\b(?:rnr|enr|ssn|otl)\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b")]
    private static partial Regex ProductSecretRegex();

    [GeneratedRegex(@"(?im)(\b(?:authorization|proxy-authorization|cookie|set-cookie)\s*:\s*)[^\r\n]+")]
    private static partial Regex HeaderSecretRegex();

    [GeneratedRegex("""(?ix)(["']?(?:password|secret|token|api_key|access_token|authorization|cookie)["']?\s*[:=]\s*)(?:"[^"]*"|'[^']*'|[^\s,;}&]+)""")]
    private static partial Regex NamedSecretRegex();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/-]+")]
    private static partial Regex BearerSecretRegex();
}
