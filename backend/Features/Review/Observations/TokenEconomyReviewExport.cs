using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AgentStudio.Review;

/// <summary>Token Economy's version-1 Quality Studio review-run drop shape.</summary>
public sealed record TokenEconomyReviewRun(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion, string SourceRunId, DateTime CompletedAtUtc,
    string Model, string ThinkingLevel, string Cli, string ReviewAspect,
    TokenEconomyReviewScope Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TokenEconomyReviewOutcomes? Outcomes,
    string EvidenceStatus, bool IsFixture);

public sealed record TokenEconomyReviewScope(int FilesReviewed, int FindingsReported);
public sealed record TokenEconomyReviewOutcomes(int ConfirmedFindings, int DismissedFindings);

public static class TokenEconomyReviewExport
{
    public static TokenEconomyReviewRun? From(ReviewObservation review, string project = "")
    {
        var cli = review.Cli?.Trim().ToLowerInvariant() switch
        {
            "codex" or "codex.exe" => "codex",
            "claude" or "claude.exe" or "claude-code" => "claude-code",
            _ => null,
        };
        if (cli is null || review.FilesReviewed is not > 0
            || string.IsNullOrWhiteSpace(review.Model)
            || string.IsNullOrWhiteSpace(review.ReasoningLevel)) return null;
        var id = "studio-" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(project + ":" + review.Id)))[..32];
        var outcomes = review.ConfirmedFindings is { } confirmed
                       && review.DismissedFindings is { } dismissed
            ? new TokenEconomyReviewOutcomes(confirmed, dismissed)
            : null;
        return new TokenEconomyReviewRun(
            "quality-studio-review-run.schema.json", 1, id,
            review.ReviewedAt.ToUniversalTime(), review.Model!, review.ReasoningLevel!, cli,
            review.Aspect ?? review.Level,
            new TokenEconomyReviewScope(review.FilesReviewed.Value, review.Findings.Total),
            outcomes, "observational", false);
    }
}
