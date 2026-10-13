using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

internal sealed record RepeatedReviewBlockDiagnosis(
    string Fingerprint,
    string Finding,
    int ConsecutiveRounds,
    int MaximumRounds)
{
    internal bool MustEscalate => ConsecutiveRounds >= MaximumRounds;
}

/// <summary>
/// Gives the task-wide Remote Review budget a semantic identity. The Task
/// Server compares structured aspect, classification, and summary fields from
/// immutable review payloads. The same block cannot create an endless sequence
/// of fresh orchestration runs with a reset-looking generic reason.
/// </summary>
internal static partial class RepeatedReviewBlockPolicy
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static RepeatedReviewBlockDiagnosis? Diagnose(
        string currentPayloadJson,
        IEnumerable<string> priorDecisionPayloadsNewestFirst,
        int maximumRounds)
    {
        var current = Describe(currentPayloadJson);
        if (current is null || current.Count == 0) return null;

        var prior = priorDecisionPayloadsNewestFirst
            .Select(Describe)
            .ToList();
        var candidates = new List<(BlockIdentity Block, int Rounds)>();
        foreach (var block in current)
        {
            var priorRounds = 0;
            foreach (var priorRound in prior)
            {
                if (priorRound is null
                    || !priorRound.Any(candidate => string.Equals(
                        candidate.Fingerprint,
                        block.Fingerprint,
                        StringComparison.Ordinal)))
                    break;
                priorRounds++;
            }
            candidates.Add((block, priorRounds + 1));
        }

        var selected = candidates
            .OrderByDescending(candidate => candidate.Rounds)
            .ThenBy(candidate => candidate.Block.Finding, StringComparer.OrdinalIgnoreCase)
            .First();
        return new RepeatedReviewBlockDiagnosis(
            selected.Block.Fingerprint,
            selected.Block.Finding,
            selected.Rounds,
            Math.Max(1, maximumRounds));
    }

    private static IReadOnlyList<BlockIdentity>? Describe(string payloadJson)
    {
        ReviewOrchestrationPayloadDto? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ReviewOrchestrationPayloadDto>(payloadJson, Json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (payload?.Verdicts is null) return null;

        var blocked = payload.Verdicts
            .Where(verdict => string.Equals(verdict.Status, "block", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(verdict.Status, "fail", StringComparison.OrdinalIgnoreCase))
            .OrderBy(verdict => verdict.Aspect, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (blocked.Count == 0) return null;

        return blocked.Select(verdict =>
        {
            var classification = string.IsNullOrWhiteSpace(verdict.Classification)
                ? "unclassified"
                : verdict.Classification.Trim();
            var finding = $"{verdict.Aspect} [{classification}]: {verdict.Summary?.Trim()}";
            var normalized = Whitespace().Replace(finding.Trim().ToLowerInvariant(), " ");
            var fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
            return new BlockIdentity(fingerprint, finding);
        }).ToList();
    }

    private sealed record BlockIdentity(string Fingerprint, string Finding);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
