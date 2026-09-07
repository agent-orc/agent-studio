namespace AgentStudio.Review;

/// <summary>
/// Observed delivery facts the policy cannot derive from review rounds alone:
/// proven target-branch membership and the newest recorded gate failure.
/// </summary>
/// <param name="Integrated">True when the delivery is proven to be on its target branch.</param>
/// <param name="IntegratedDetail">Membership evidence for an integrated delivery.</param>
/// <param name="FailureReason">Newest <c>integration_failed</c> detail, when one exists.</param>
/// <param name="IntegrationBranch">Branch the delivery targeted, when resolved.</param>
public sealed record ReviewDeliveryObservation(
    bool Integrated,
    string? IntegratedDetail = null,
    string? FailureReason = null,
    string? IntegrationBranch = null);

/// <summary>Everything the policy needs, as pure data.</summary>
/// <param name="JobId">Card the projection belongs to.</param>
/// <param name="Rounds">Review rounds, oldest first.</param>
/// <param name="Delivery">Observed delivery facts, or null when nothing is known.</param>
/// <param name="Decision">Recorded human-decision requirement, or null.</param>
public sealed record ReviewProjectionInputs(
    string JobId,
    IReadOnlyList<ReviewRoundRecord> Rounds,
    ReviewDeliveryObservation? Delivery = null,
    ReviewDecisionRequirement? Decision = null);

/// <summary>
/// Pure derivation of the <see cref="ReviewProjection"/> head from review rounds.
/// No IO, no clock, no service dependencies, so the branching rules are covered
/// by a direct matrix test.
///
/// <para>
/// The rules that matter, in the order the operator reads them:
/// </para>
/// <list type="number">
///   <item>Rounds are counted from records only. Seven remote reports are seven
///   rounds; the count is never derived from one plane's file naming.</item>
///   <item>Build-tests come from the newest round that recorded a build-tests
///   row, and a blocking semantic verdict never changes them (AGT-2714).</item>
///   <item>A blocking aspect is reported with the reviewer's own reason. A block
///   without a reason says so rather than showing an empty phrase.</item>
///   <item>Delivery state prefers proven integration, then the newest round's
///   gate result, then the observed gate failure, then "not attempted".</item>
///   <item>The recommendation names the gap: a failing command, a blocking
///   aspect, or the gate to override.</item>
/// </list>
/// </summary>
public static class ReviewRoundProjectionPolicy
{
    public static ReviewProjection Build(ReviewProjectionInputs inputs)
    {
        var rounds = ReviewRoundRecordStore.Order(inputs.Rounds).ToList();
        var latest = rounds.Count == 0 ? null : rounds[^1];
        var buildTests = BuildTestsOf(rounds);
        var blocking = BlockingAspectsOf(latest);
        var delivery = DeliveryOf(rounds, inputs.Delivery);
        var (recommendation, reason) = Recommend(rounds, buildTests, blocking, delivery, inputs.Decision);

        return new ReviewProjection
        {
            JobId = inputs.JobId,
            RoundCount = rounds.Count,
            Plane = PlaneOf(rounds),
            Rounds = rounds,
            Latest = latest,
            LatestOutcome = string.IsNullOrWhiteSpace(latest?.Outcome) ? null : latest!.Outcome,
            LatestAt = latest?.ReceivedAt,
            Grade = rounds.LastOrDefault(round => !string.IsNullOrWhiteSpace(round.Grade))?.Grade,
            BlockingAspects = blocking,
            BuildTests = buildTests,
            Delivery = delivery,
            DecisionRequired = inputs.Decision,
            Recommendation = recommendation,
            RecommendationReason = reason,
        };
    }

    private static string PlaneOf(IReadOnlyList<ReviewRoundRecord> rounds)
    {
        if (rounds.Count == 0) return ReviewProjectionPlanes.None;
        var planes = rounds.Select(round => ReviewPlanes.Normalize(round.Plane)).Distinct().ToList();
        if (planes.Count > 1) return ReviewProjectionPlanes.Mixed;
        return planes[0] == ReviewPlanes.Remote
            ? ReviewProjectionPlanes.Remote
            : ReviewProjectionPlanes.Local;
    }

    /// <summary>
    /// Build and test proof from the newest round that recorded any build-tests
    /// row. An older round's green build never covers a newer round that ran
    /// nothing, and a semantic block never turns a passing build red.
    /// </summary>
    private static ReviewBuildTestsView BuildTestsOf(IReadOnlyList<ReviewRoundRecord> rounds)
    {
        var newest = rounds.LastOrDefault(round => round.BuildTests.Count > 0);
        if (newest is null)
        {
            return new ReviewBuildTestsView
            {
                Result = ReviewBuildTestsResults.NotProven,
                Reason = rounds.Count == 0
                    ? "No review round has recorded a build or test command."
                    : "No review round recorded a build-tests verdict.",
            };
        }

        var verdicts = newest.BuildTests
            .Where(row => ReviewVerdicts.Normalize(row.Status) != ReviewVerdicts.Missing)
            .ToList();
        var steps = newest.BuildTests
            .Select(row => row.StepId)
            .Where(step => !string.IsNullOrWhiteSpace(step))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (verdicts.Count == 0)
        {
            return new ReviewBuildTestsView
            {
                Result = ReviewBuildTestsResults.NotProven,
                Steps = steps,
                Reason = steps.Count > 0
                    ? Sentence($"Build-tests verdict is missing for {NaturalList(steps)}")
                    : "The review report recorded no build-tests command.",
            };
        }

        var failures = verdicts
            .Where(row => ReviewVerdicts.Normalize(row.Status) != ReviewVerdicts.Pass)
            .ToList();
        if (failures.Count > 0)
        {
            return new ReviewBuildTestsView
            {
                Result = ReviewBuildTestsResults.Failed,
                Steps = steps,
                Reason = Sentence(string.Join("; ", failures.Select(Describe))),
            };
        }

        return new ReviewBuildTestsView
        {
            Result = ReviewBuildTestsResults.Passed,
            Steps = steps,
            Reason = steps.Count > 0
                ? Sentence($"{NaturalList(steps)} passed")
                : "All build-tests verdicts passed.",
        };
    }

    /// <summary>Name the failing command, then quote why it failed.</summary>
    private static string Describe(ReviewRoundCommandRow row)
    {
        var step = string.IsNullOrWhiteSpace(row.StepId) ? "build-tests" : row.StepId;
        var command = string.IsNullOrWhiteSpace(row.Command) ? "" : $" (`{row.Command}`)";
        var exit = row.ExitCode is { } code and not 0 ? $" exit {code}" : "";
        var why = Trim(row.Summary);
        return why.Length > 0
            ? $"{step}{command} failed{exit}: {why}"
            : $"{step}{command} failed{exit}";
    }

    private static List<ReviewRoundAspect> BlockingAspectsOf(ReviewRoundRecord? latest) =>
        latest is null
            ? []
            : latest.Aspects
                .Where(aspect => ReviewVerdicts.IsBlocking(aspect.Verdict))
                .Select(aspect => aspect with
                {
                    Verdict = ReviewVerdicts.Block,
                    Summary = Trim(aspect.Summary),
                })
                .ToList();

    private static ReviewDeliveryView DeliveryOf(
        IReadOnlyList<ReviewRoundRecord> rounds,
        ReviewDeliveryObservation? observed)
    {
        var branch = observed?.IntegrationBranch
                     ?? rounds.LastOrDefault(round => round.DeliveryGate?.IntegrationBranch is not null)
                         ?.DeliveryGate!.IntegrationBranch;

        if (observed?.Integrated == true)
        {
            return new ReviewDeliveryView
            {
                State = ReviewDeliveryStates.Integrated,
                Reason = Trim(observed.IntegratedDetail ?? ""),
                IntegrationBranch = branch,
            };
        }

        var gate = rounds.LastOrDefault(round => round.DeliveryGate is not null)?.DeliveryGate;
        if (gate is not null && gate.Result != ReviewDeliveryStates.NotAttempted)
        {
            return new ReviewDeliveryView
            {
                State = gate.Result,
                Reason = Trim(gate.Reason),
                IntegrationBranch = branch,
            };
        }

        if (!string.IsNullOrWhiteSpace(observed?.FailureReason))
        {
            return new ReviewDeliveryView
            {
                State = ReviewDeliveryStates.GateFailed,
                Reason = Trim(observed!.FailureReason!),
                IntegrationBranch = branch,
            };
        }

        return new ReviewDeliveryView
        {
            State = ReviewDeliveryStates.NotAttempted,
            Reason = rounds.Count == 0
                ? "No review round has produced a delivery yet."
                : "The delivery has not reached the integration gate.",
            IntegrationBranch = branch,
        };
    }

    /// <summary>
    /// The next step, named after the thing that is actually open. Order matters:
    /// a failing command is more concrete than a semantic block, and both are
    /// more actionable than a gate an operator could simply override.
    /// </summary>
    private static (string Kind, string Reason) Recommend(
        IReadOnlyList<ReviewRoundRecord> rounds,
        ReviewBuildTestsView buildTests,
        IReadOnlyList<ReviewRoundAspect> blocking,
        ReviewDeliveryView delivery,
        ReviewDecisionRequirement? decision)
    {
        if (rounds.Count == 0)
            return (ReviewRecommendations.None, "No review round has been recorded for this card.");

        if (buildTests.Result == ReviewBuildTestsResults.Failed)
            return (ReviewRecommendations.Reissue, $"Reissue with the failing build: {buildTests.Reason}");

        if (blocking.Count > 0)
        {
            var named = string.Join("; ", blocking.Select(aspect => aspect.Summary.Length > 0
                ? $"{aspect.Name}: {aspect.Summary}"
                : $"{aspect.Name} blocked without a recorded reason"));
            return (ReviewRecommendations.Reissue, Sentence($"Reissue with the named gap - {named}"));
        }

        if (delivery.State == ReviewDeliveryStates.GateFailed)
        {
            return (ReviewRecommendations.AcceptWithOverride, delivery.Reason.Length > 0
                ? Sentence($"Review found nothing blocking; accept with an override of the delivery gate - {Trim(delivery.Reason)}")
                : "Review found nothing blocking; accept with an override of the delivery gate.");
        }

        if (IsInconclusive(rounds[^1].Outcome))
        {
            return (ReviewRecommendations.Wait,
                Sentence($"The newest round ended {Trim(rounds[^1].Outcome)}; wait for the retry before deciding"));
        }

        if (decision is not null)
            return (ReviewRecommendations.AcceptWithOverride, Sentence(decision.Reason.Length > 0
                ? $"Review found nothing blocking; accept - {Trim(decision.Reason)}"
                : "Review found nothing blocking; accept"));

        return (ReviewRecommendations.None, "Review recorded no open finding.");
    }

    /// <summary>A round that neither passed nor produced a product verdict.</summary>
    private static bool IsInconclusive(string outcome)
    {
        var head = outcome.Split('/')[0].Trim();
        return head.Equals("ReviewInfra", StringComparison.OrdinalIgnoreCase)
               || head.Equals("Inconclusive", StringComparison.OrdinalIgnoreCase)
               || head.Equals("Cancellation", StringComparison.OrdinalIgnoreCase);
    }

    private static string NaturalList(IReadOnlyList<string> values) => values.Count switch
    {
        0 => "",
        1 => values[0],
        2 => $"{values[0]} and {values[1]}",
        _ => string.Join(", ", values.Take(values.Count - 1)) + $", and {values[^1]}",
    };

    private static string Sentence(string value) => Trim(value) + ".";

    private static string Trim(string value) => value.Trim().TrimEnd('.', ';', ':');
}
