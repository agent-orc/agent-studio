using System.Security.Cryptography;
using System.Text;
using AgentStudio.Security;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Passes a Remote Review report through <see cref="CredentialRedactor"/>
/// before Studio stores or projects it. Free-text fields are masked in place.
/// UTF-8 artifacts are masked too; a changed artifact gets a new digest and
/// size, and the command evidence that cites the old digest is pointed at the
/// new one, so the persisted files still match their declared digests.
/// Binary and undecodable artifacts pass through unchanged.
/// </summary>
internal static class ReviewReportRedaction
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static Contract.ReviewReportRequest Redact(Contract.ReviewReportRequest request)
    {
        var digests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var artifacts = request.Artifacts.Select(artifact => RedactArtifact(artifact, digests)).ToArray();
        return request with
        {
            FailureClassification = Text(request.FailureClassification),
            Summary = Text(request.Summary),
            Artifacts = artifacts,
            Commands = request.Commands.Select(command => RedactCommand(command, digests)).ToArray(),
            Verdicts = request.Verdicts.Select(RedactVerdict).ToArray(),
        };
    }

    private static Contract.ReviewArtifactEvidenceDto RedactArtifact(
        Contract.ReviewArtifactEvidenceDto artifact,
        Dictionary<string, string> digests)
    {
        if (artifact.ContentBase64 is null) return artifact;
        string text;
        try
        {
            text = StrictUtf8.GetString(Convert.FromBase64String(artifact.ContentBase64));
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return artifact;
        }
        var redacted = CredentialRedactor.Redact(text);
        if (string.Equals(redacted, text, StringComparison.Ordinal)) return artifact;
        var bytes = StrictUtf8.GetBytes(redacted);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        digests[artifact.Sha256] = digest;
        return artifact with
        {
            Sha256 = digest,
            SizeBytes = bytes.LongLength,
            ContentBase64 = Convert.ToBase64String(bytes),
        };
    }

    private static Contract.ReviewCommandEvidenceDto RedactCommand(
        Contract.ReviewCommandEvidenceDto command,
        IReadOnlyDictionary<string, string> digests)
        => command with
        {
            Arguments = command.Arguments.Select(CredentialRedactor.Redact).ToArray(),
            StdoutSha256 = digests.GetValueOrDefault(command.StdoutSha256, command.StdoutSha256),
            StderrSha256 = digests.GetValueOrDefault(command.StderrSha256, command.StderrSha256),
            NewFailures = TextList(command.NewFailures),
            PreExistingFailures = TextList(command.PreExistingFailures),
            FlakyQuarantinedFailures = TextList(command.FlakyQuarantinedFailures),
            RepeatObservedTests = TextList(command.RepeatObservedTests),
            InitialFailures = TextList(command.InitialFailures),
            Diagnosis = RedactDiagnosis(command.Diagnosis),
        };

    private static Contract.ReviewVerdictDto RedactVerdict(Contract.ReviewVerdictDto verdict)
        => verdict with
        {
            Summary = CredentialRedactor.Redact(verdict.Summary),
            EvidenceChecked = Text(verdict.EvidenceChecked),
            Missing = Text(verdict.Missing),
            Diagnosis = RedactDiagnosis(verdict.Diagnosis),
        };

    private static Contract.DeliveryFailureDiagnosisResult? RedactDiagnosis(
        Contract.DeliveryFailureDiagnosisResult? diagnosis)
        => diagnosis is null ? null : diagnosis with { Evidence = diagnosis.Evidence.Select(CredentialRedactor.Redact).ToArray() };

    private static IReadOnlyList<string>? TextList(IReadOnlyList<string>? values)
        => values?.Select(CredentialRedactor.Redact).ToArray();

    private static string? Text(string? value) => value is null ? null : CredentialRedactor.Redact(value);
}
