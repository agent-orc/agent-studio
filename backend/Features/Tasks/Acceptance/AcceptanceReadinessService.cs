using AgentStudio.Runner;

namespace AgentStudio.Tasks;

/// <summary>One Human Review card and the answer its acceptance would give now.</summary>
public sealed record AcceptanceReadinessItem(
    string JobId,
    string TaskKey,
    string? Key,
    string ProjectName,
    string Title,
    string Readiness,
    string Headline,
    string Reason,
    string Stage,
    string Refusal,
    string? Aspect,
    string? ContentStatus,
    string? Concern,
    string? Missing,
    string? LatestResultRef,
    AcceptanceReadinessRound? Round);

/// <summary>The prefilled continue round for a refused card; nothing is sent until the owner confirms.</summary>
public sealed record AcceptanceReadinessRound(string Prompt, string? BaseRef);

public sealed record AcceptanceReadinessReport(
    DateTime CheckedAtUtc,
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<AcceptanceReadinessItem> Items);

/// <summary>
/// AGT-3564 - computes the acceptance readiness of Human Review cards with the
/// completion's own admission checks (<see cref="TaskTransitionService.PreviewCompletion"/>).
/// It reads; it never moves a card, records evidence, or sends a round.
/// </summary>
public sealed class AcceptanceReadinessService(
    TaskTransitionService transitions,
    ILogger<AcceptanceReadinessService> logger,
    AttemptAuthorityService? authority = null,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public AcceptanceReadinessReport Evaluate(IEnumerable<TaskInfo> cards, CancellationToken ct = default)
    {
        var items = new List<AcceptanceReadinessItem>();
        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();
            items.Add(Evaluate(card, ct));
        }
        var counts = AcceptanceReadinessClasses.All.ToDictionary(
            readiness => readiness,
            readiness => items.Count(item => item.Readiness == readiness),
            StringComparer.Ordinal);
        return new AcceptanceReadinessReport(_time.GetUtcNow().UtcDateTime, counts, items);
    }

    public AcceptanceReadinessItem Evaluate(TaskInfo card, CancellationToken ct = default)
    {
        CompletionAdmissionPreview admission;
        try
        {
            admission = transitions.PreviewCompletion(card, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "acceptance-readiness-failed task={TaskKey}", card.TaskKey);
            admission = CompletionAdmissionPreview.Refused(CompletionAdmissionStages.Lane,
                $"The completion checks could not run: {ex.Message}");
        }
        var verdict = AcceptanceReadinessPolicy.Classify(new AcceptanceReadinessFacts(admission, card.Mode, card.ParkedBlocker));
        var latestRef = LatestResultRef(card);
        var round = AcceptanceReadinessClasses.OffersRound(verdict.Readiness)
            ? new AcceptanceReadinessRound(AcceptanceReadinessPolicy.RoundPrompt(verdict, admission.Message, latestRef), latestRef)
            : null;
        return new AcceptanceReadinessItem(
            card.Id, card.TaskKey, card.Key, card.ProjectName, card.Title,
            verdict.Readiness, verdict.Headline, verdict.Reason, admission.Stage, admission.Message,
            verdict.Aspect, verdict.ContentStatus, verdict.Concern, verdict.Missing, latestRef, round);
    }

    /// <summary>The ref a continue round without a named base starts from (AGT-3483).</summary>
    private string? LatestResultRef(TaskInfo card)
    {
        var key = card.Key ?? card.TaskKey;
        var projection = authority is null || string.IsNullOrWhiteSpace(key) ? null : authority.GetTaskProjection(key);
        var refs = ContinueRoundBasePolicy.Collect(projection, AgentStudio.Pipeline.ReviewSubjectStore.Read(card.FolderPath));
        return ContinueRoundBasePolicy.Decide(null, refs, existingBase: null, startsFromClaim: true,
            _time.GetUtcNow().UtcDateTime).Base?.Ref;
    }
}
