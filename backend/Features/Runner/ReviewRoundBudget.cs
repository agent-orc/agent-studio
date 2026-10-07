using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.Review;

namespace AgentStudio.Runner;

public sealed record DeliveredReviewRound(
    string AttemptId,
    IReadOnlyList<string> BlockingAspects,
    IReadOnlyList<string> DegradedAspects,
    string? FollowUpTaskKey = null,
    string? SpentBy = null);

public sealed record ReviewRoundBudgetLedger(IReadOnlyList<DeliveredReviewRound> Rounds)
{
    public static ReviewRoundBudgetLedger Empty { get; } = new([]);
    public int Delivered => Rounds.Count;
    public string? SpentBy => Rounds.LastOrDefault(round => round.SpentBy is not null)?.SpentBy;
}

public sealed record ReviewRoundBudgetDecision(
    int RoundNumber,
    int MaximumRounds,
    IReadOnlyList<string> DegradedAspects,
    string? SpentBy)
{
    public bool Degrade => DegradedAspects.Count > 0;
}

/// <summary>Pure lifetime review budget. An operator epoch changes evidence, never this history.</summary>
public static class ReviewRoundBudgetPolicy
{
    public const int DefaultMaximumRounds = 4;
    public const int DefaultConsecutiveBlockRounds = 2;

    public static AgentStudio.TaskServer.Contracts.ReviewReportRequest ApplyRemoteReport(
        AgentStudio.TaskServer.Contracts.ReviewReportRequest request,
        ReviewRoundBudgetDecision decision)
    {
        if (!decision.Degrade
            || !string.Equals(request.Outcome, "ProductFailure", StringComparison.OrdinalIgnoreCase))
            return request;
        var verdicts = request.Verdicts.Select(verdict =>
            decision.DegradedAspects.Contains(verdict.Aspect, StringComparer.OrdinalIgnoreCase)
            && AgentStudio.TaskServer.Contracts.ReviewGradingPolicy.IsBlockingToken(verdict.Status)
                ? verdict with { Status = "concerns" }
                : verdict).ToArray();
        var remainingBlock = verdicts.Any(verdict =>
            AgentStudio.TaskServer.Contracts.ReviewGradingPolicy.IsBlockingToken(verdict.Status));
        return request with
        {
            Outcome = remainingBlock ? request.Outcome : "Pass",
            FailureClassification = remainingBlock ? request.FailureClassification : null,
            Summary = $"Review round {decision.RoundNumber} of {decision.MaximumRounds}: "
                      + $"{decision.SpentBy} spent the review budget; open findings moved to a linked follow-up. "
                      + request.Summary,
            Verdicts = verdicts,
        };
    }

    /// <summary>
    /// A spent budget moves the degraded findings to the linked follow-up card,
    /// so the delivery must not start another automatic finding or concern round
    /// for them. Shared by local aspect review and Remote Review.
    /// </summary>
    public static AgentStudio.TaskServer.Contracts.ReviewFollowUpDecision ApplyFollowUp(
        AgentStudio.TaskServer.Contracts.ReviewFollowUpDecision followUp,
        ReviewRoundBudgetDecision decision)
    {
        if (!decision.Degrade || !followUp.StartsCodingRound) return followUp;
        return followUp with
        {
            Action = AgentStudio.TaskServer.Contracts.ReviewFollowUpAction.Accept,
            Reason = "The lifetime review budget moved these findings to a linked follow-up.",
        };
    }

    public static ReviewRoundBudgetDecision Decide(
        ReviewRoundBudgetLedger ledger,
        string attemptId,
        IEnumerable<string> blockedAspects,
        int maximumRounds,
        int consecutiveBlockRounds,
        int? priorAutomaticReissues = null,
        int? maximumAutomaticReissues = null)
    {
        var prior = ledger.Rounds.Where(round => round.AttemptId != attemptId).ToArray();
        var blocked = blockedAspects.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var roundNumber = prior.Length + 1;
        maximumRounds = Math.Clamp(maximumRounds, 1, 20);
        consecutiveBlockRounds = Math.Clamp(consecutiveBlockRounds, 1, 20);
        // The deterministic build/test gate owns its failure. Never turn that
        // failure, or a mixed review containing it, into an acceptance.
        if (blocked.Contains("build-tests", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("tests-and-evidence", StringComparer.OrdinalIgnoreCase))
            return new(roundNumber, maximumRounds, [],
                blocked.Contains("build-tests", StringComparer.OrdinalIgnoreCase)
                    ? "build-tests" : "tests-and-evidence");

        string? recurrent = null;
        foreach (var aspect in blocked)
        {
            var streak = 0;
            foreach (var previous in prior.Reverse())
            {
                if (!previous.BlockingAspects.Contains(aspect, StringComparer.OrdinalIgnoreCase)) break;
                streak++;
            }
            if (streak >= consecutiveBlockRounds)
            {
                recurrent = aspect;
                break;
            }
        }
        var reissuesSpent = priorAutomaticReissues is not null
                            && maximumAutomaticReissues is not null
                            && priorAutomaticReissues > 0
                            && priorAutomaticReissues >= maximumAutomaticReissues;
        var lifetimeSpent = roundNumber >= maximumRounds || reissuesSpent;
        var spentBy = recurrent ?? (lifetimeSpent ? blocked.FirstOrDefault() : null);
        return new(roundNumber, maximumRounds,
            lifetimeSpent ? blocked : recurrent is null ? [] : [recurrent], spentBy);
    }
}

/// <summary>Durable, attempt-id deduped counter carried with the card across lane moves.</summary>
public static class ReviewRoundBudgetStore
{
    public const string FileName = "review-round-budget.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static ReviewRoundBudgetLedger Read(string folder, IReadOnlyList<ReviewAttempt>? legacyAttempts = null)
    {
        var path = Path.Combine(folder, FileName);
        if (File.Exists(path))
            return JsonSerializer.Deserialize<ReviewRoundBudgetLedger>(File.ReadAllText(path), Json)
                   ?? throw new InvalidDataException($"Invalid review round budget at {path}.");
        // Existing cards already have delivered rounds. Seed from their review
        // artifacts so deploying this policy does not grant a fresh budget.
        return new ReviewRoundBudgetLedger((legacyAttempts ?? [])
            .Where(attempt => attempt.Plane == ReviewPlane.Local
                              || string.Equals(attempt.Outcome, "Pass", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(attempt.Outcome, "ProductFailure", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(attempt.Outcome, "IntegrationBranchDefect", StringComparison.OrdinalIgnoreCase)
                              || (string.IsNullOrWhiteSpace(attempt.Outcome) && attempt.Aspects.Count > 0))
            .Reverse()
            .Select(attempt => new DeliveredReviewRound(
                attempt.AttemptId,
                attempt.Aspects.Where(aspect =>
                        AgentStudio.TaskServer.Contracts.ReviewGradingPolicy.IsBlockingToken(aspect.Status))
                    .Select(aspect => aspect.Aspect).ToArray(),
                []))
            .ToArray());
    }

    public static ReviewRoundBudgetLedger Record(
        string folder,
        ReviewRoundBudgetLedger seed,
        DeliveredReviewRound round)
    {
        lock (Gates.GetOrAdd(Path.GetFullPath(folder), _ => new object()))
        {
            var current = File.Exists(Path.Combine(folder, FileName)) ? Read(folder) : seed;
            if (current.Rounds.Any(item => item.AttemptId == round.AttemptId)) return current;
            var updated = current with { Rounds = current.Rounds.Append(round).ToArray() };
            Write(folder, updated);
            return updated;
        }
    }

    public static void MarkFollowUp(string folder, string attemptId, string taskKey)
    {
        lock (Gates.GetOrAdd(Path.GetFullPath(folder), _ => new object()))
        {
            var current = Read(folder);
            var updated = current with
            {
                Rounds = current.Rounds.Select(round => round.AttemptId == attemptId
                    ? round with { FollowUpTaskKey = taskKey }
                    : round).ToArray(),
            };
            Write(folder, updated);
        }
    }

    private static void Write(string folder, ReviewRoundBudgetLedger ledger)
    {
        // The sidecar lives inside the card folder. A lane move renames that
        // folder, so a caller holding a stale path must fail instead of
        // recreating an empty card folder in the lane the card already left.
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Card folder {folder} no longer exists.");
        var path = Path.Combine(folder, FileName);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(ledger, Json));
        File.Move(temp, path, overwrite: true);
    }
}
