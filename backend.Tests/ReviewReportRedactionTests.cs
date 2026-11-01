using System.Security.Cryptography;
using System.Text;
using AgentStudio.Runner;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

public sealed class ReviewReportRedactionTests
{
    private static readonly DateTime At = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Masks_report_text_and_repoints_command_evidence_at_the_masked_artifact()
    {
        var token = "gh" + "s_" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var stdout = Artifact("stdout.txt", $"cloning https://x-access-token:{token}@github.com/org/repo\nok\n");
        var stderr = Artifact("stderr.txt", "plain warning\n");
        var binary = new Contract.ReviewArtifactEvidenceDto(
            "shot.png", "image/png", Sha([0xff, 0xfe, 0x00]), 3, Convert.ToBase64String([0xff, 0xfe, 0x00]));
        var report = Report(
            $"Push failed with {token}.",
            [Command(stdout.Sha256, stderr.Sha256, ["--token", token])],
            [stdout, stderr, binary],
            [new Contract.ReviewVerdictDto("tests", "block", "product", $"saw {token}", $"log {token}", $"none {token}",
                new Contract.DeliveryFailureDiagnosisResult("product", 1, [$"line {token}"]))]);

        var redacted = ReviewReportRedaction.Redact(report);

        Assert.DoesNotContain(token, System.Text.Json.JsonSerializer.Serialize(redacted), StringComparison.Ordinal);
        var maskedStdout = redacted.Artifacts[0];
        var bytes = Convert.FromBase64String(maskedStdout.ContentBase64!);
        Assert.Contains("https://[REDACTED]@github.com/org/repo", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(Sha(bytes), maskedStdout.Sha256);
        Assert.Equal(bytes.LongLength, maskedStdout.SizeBytes);
        Assert.Equal(maskedStdout.Sha256, redacted.Commands[0].StdoutSha256);
        Assert.Equal(stderr, redacted.Artifacts[1]);
        Assert.Equal(stderr.Sha256, redacted.Commands[0].StderrSha256);
        Assert.Equal(binary, redacted.Artifacts[2]);
    }

    [Fact]
    public void A_report_without_credentials_is_unchanged_in_content()
    {
        var stdout = Artifact("stdout.txt", "all tests passed\n");
        var report = Report("Clean.", [Command(stdout.Sha256, stdout.Sha256, ["test"])], [stdout],
            [new Contract.ReviewVerdictDto("tests", "pass", "none", "Clean.")]);

        var redacted = ReviewReportRedaction.Redact(report);

        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(report),
            System.Text.Json.JsonSerializer.Serialize(redacted));
    }

    private static Contract.ReviewReportRequest Report(
        string summary,
        IReadOnlyList<Contract.ReviewCommandEvidenceDto> commands,
        IReadOnlyList<Contract.ReviewArtifactEvidenceDto> artifacts,
        IReadOnlyList<Contract.ReviewVerdictDto> verdicts)
        => new("exec", "inst", "lease", 1, "idem", "ProductFailure", "tests", summary,
            null!, null!, commands, artifacts, verdicts);

    private static Contract.ReviewCommandEvidenceDto Command(string stdoutSha, string stderrSha, IReadOnlyList<string> arguments)
        => new("verify-1", "tests", "dotnet", arguments, new string('a', 40), new string('a', 40), new string('a', 40),
            At, At, 1, null, stdoutSha, stderrSha);

    private static Contract.ReviewArtifactEvidenceDto Artifact(string name, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new Contract.ReviewArtifactEvidenceDto(name, "text/plain", Sha(bytes), bytes.LongLength, Convert.ToBase64String(bytes));
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
