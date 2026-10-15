using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.Review;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

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

    public static void RecordAcceptedEngineReview(string folder, StagePayload payload)
    {
        if (payload.ReviewAttemptId is null
            || payload.ReviewOutcome is not (ReviewOutcomeKinds.Pass
                or ReviewOutcomeKinds.ProductFailure or ReviewOutcomeKinds.IntegrationBranchDefect))
            return;
        var blocked = (payload.Verdicts ?? [])
            .Where(verdict => AgentStudio.TaskServer.Contracts.ReviewGradingPolicy.IsBlockingToken(verdict.Status))
            .Select(verdict => verdict.Aspect).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var seed = payload.DeliveredRounds ?? ReviewRoundBudgetLedger.Empty;
        var decision = ReviewRoundBudgetPolicy.Decide(seed, payload.ReviewAttemptId, blocked,
            payload.MaximumDeliveredRounds, ReviewRoundBudgetPolicy.DefaultConsecutiveBlockRounds,
            payload.ReissuesUsed, payload.MaximumReissues);
        Record(folder, seed, new DeliveredReviewRound(payload.ReviewAttemptId, blocked,
            decision.DegradedAspects, SpentBy: decision.SpentBy));
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
