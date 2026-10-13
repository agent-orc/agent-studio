using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>One settled ReviewAttempt in an infrastructure retry chain.</summary>
public sealed record ReviewInfrastructureAttemptFact(
    string AttemptId,
    string? Classification,
    string? Reason);

/// <summary>
/// A named diagnosis for an infrastructure cause that keeps repeating, ready to
/// be written onto the card.
/// </summary>
public sealed record ReviewInfrastructureRepeatDiagnosis(
    string Classification,
    int RepeatCount,
    IReadOnlyList<string> AttemptIds,
    string? IntegrationRef,
    string? BaselineSha,
    string? Step,
    string? Command,
    string Summary);

/// <summary>
/// Turns a chain of identically classified infrastructure failures into one
/// named diagnosis.
/// <para>
/// AGT-2220 burned four attempts (28.07., 00:28 / 01:46 / 02:57 / 04:11) on
/// <c>ReviewInfra</c> + <c>BaselineUnavailable</c>. Each attempt recorded the
/// classification and nothing else, so the retry budget drained silently and the
/// card never said which base or which command was involved. A classification
/// repeating is itself the signal: from the second identical cause on, the
/// concrete facts belong on the card, not only in the runner log.
/// </para>
/// </summary>
public static class ReviewInfrastructureRepeatPolicy
{
    /// <summary>
    /// Identical consecutive infrastructure causes needed before a diagnosis is
    /// written. Two, so the diagnosis lands while retries remain rather than
    /// after the budget is spent.
    /// </summary>
    public const int DiagnosisThreshold = 2;

    /// <summary>
    /// Returns the diagnosis for the trailing run of identical classifications
    /// in <paramref name="chainOldestFirst"/>, or <c>null</c> while the cause is
    /// still a one-off. <paramref name="plannedIntegrationRef"/> is the ref the
    /// plan handed the runner and is used when no attempt reported one.
    /// </summary>
    public static ReviewInfrastructureRepeatDiagnosis? Diagnose(
        IReadOnlyList<ReviewInfrastructureAttemptFact> chainOldestFirst,
        string? plannedIntegrationRef)
    {
        if (chainOldestFirst.Count == 0) return null;
        var latest = chainOldestFirst[^1];
        if (string.IsNullOrWhiteSpace(latest.Classification)) return null;

        var repeated = new List<ReviewInfrastructureAttemptFact>();
        for (var index = chainOldestFirst.Count - 1; index >= 0; index--)
        {
            if (!string.Equals(
                    chainOldestFirst[index].Classification,
                    latest.Classification,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            repeated.Insert(0, chainOldestFirst[index]);
        }
        if (repeated.Count < DiagnosisThreshold) return null;

        // Prefer the newest attempt that actually carried a fact: an older
        // attempt in the chain may predate the diagnosis convention.
        string? Newest(string key) => repeated
            .Select(attempt => ReviewInfrastructureDiagnosis.Parse(attempt.Reason))
            .Where(facts => facts.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            .Select(facts => facts[key])
            .LastOrDefault();

        var baselineSha = Newest(ReviewInfrastructureDiagnosis.BaseKey);
        var integrationRef = Newest(ReviewInfrastructureDiagnosis.RefKey) ?? plannedIntegrationRef;
        var step = Newest(ReviewInfrastructureDiagnosis.StepKey);
        var command = Newest(ReviewInfrastructureDiagnosis.CommandKey);
        var attemptIds = repeated.Select(attempt => attempt.AttemptId).ToArray();

        var facts = new List<string>();
        if (!string.IsNullOrWhiteSpace(integrationRef)) facts.Add($"base ref {integrationRef}");
        if (!string.IsNullOrWhiteSpace(baselineSha)) facts.Add($"base commit {baselineSha}");
        if (!string.IsNullOrWhiteSpace(step)) facts.Add($"step {step}");
        if (!string.IsNullOrWhiteSpace(command)) facts.Add($"command {command}");
        var detail = facts.Count == 0
            ? "no base or command was reported"
            : string.Join(", ", facts);

        return new ReviewInfrastructureRepeatDiagnosis(
            latest.Classification!,
            repeated.Count,
            attemptIds,
            string.IsNullOrWhiteSpace(integrationRef) ? null : integrationRef,
            baselineSha,
            step,
            command,
            $"Review infrastructure failed {repeated.Count}x with {latest.Classification}: {detail}.");
    }
}
