using AgentStudio.Runner;

namespace AgentStudio.Orchestration.Decisions;

public static class SweepTriggerPolicy
{
    public static OperatorSweepTrigger? FixRound(string lane, string? outcome, string? attemptId,
        string? reviewedSha, string? deliverySha, bool handled)
    {
        if (lane != "5-human-review" || outcome != "ProductFailure" || attemptId is null) return null;
        if (!string.IsNullOrWhiteSpace(deliverySha) && !string.IsNullOrWhiteSpace(reviewedSha)
            && !string.Equals(deliverySha, reviewedSha, StringComparison.OrdinalIgnoreCase)) return null;
        return new("review:" + attemptId, handled);
    }

    public static OperatorSweepTrigger? Gate(string lane, bool isGateFailure, string? sha, string? step,
        string? domain, bool handled)
        => lane is "5-human-review" or "5e-escalated" && isGateFailure
            ? new($"gate:{sha ?? "unknown"}:{step}", handled, domain) : null;

    public static OperatorSweepTrigger? Salvage(string lane, string? sha, bool handled)
        => lane == "5e-escalated" && sha is not null ? new("salvage:" + sha, handled) : null;
}

public static class ReviewedDeliveryBasePolicy
{
    public static ContinuationBaseFact? Decide(string reviewAttemptId, string? resultRef, string? resultSha)
    {
        if (string.IsNullOrWhiteSpace(reviewAttemptId) || resultSha?.Length != 40
            || !resultSha.All(Uri.IsHexDigit)) return null;
        var reference = (resultRef ?? string.Empty).Trim();
        if (reference.StartsWith("origin/", StringComparison.Ordinal)) reference = reference[7..];
        if (reference.Length == 0 || (reference.StartsWith("refs/", StringComparison.Ordinal)
            && !reference.StartsWith("refs/heads/", StringComparison.Ordinal))) return null;
        if (!reference.StartsWith("refs/heads/", StringComparison.Ordinal)) reference = "refs/heads/" + reference;
        return new(reference, resultSha.Trim().ToLowerInvariant(), reviewAttemptId.Trim());
    }
}
