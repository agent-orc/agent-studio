using AgentStudio.Pipeline;

namespace AgentStudio.Review;

public sealed record ReviewEffectivenessSummary(
    DateTime GeneratedAtUtc,
    DateTime? SinceUtc,
    IReadOnlyList<ReviewEffectivenessRow> Rows,
    ReviewEffectivenessCoverage Coverage);

/// <summary>
/// Records review observations and later defect evidence, attributes misses,
/// and answers the aggregate, depth-advice and export reads (AGT-3269).
/// <list type="bullet">
///   <item>Aspect and grade reviews and failed build/test gates arrive from
///   <see cref="PipelineExecutionLog"/> through <see cref="IPipelineStepObserver"/>.</item>
///   <item>An operator send-back from Human Review arrives from
///   <c>OperatorReviewRequeueService</c>.</item>
///   <item>Deep reviews, operator defects, production incidents and rollbacks
///   arrive through the API.</item>
/// </list>
/// After every write the delivery's misses are re-derived by
/// <see cref="ReviewMissAttributionPolicy"/> and the new pairs are stored.
/// </summary>
public sealed class ReviewEffectivenessService : IPipelineStepObserver
{
    private readonly ReviewEffectivenessStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger<ReviewEffectivenessService> _logger;

    public ReviewEffectivenessService(
        ReviewEffectivenessStore store,
        TimeProvider clock,
        ILogger<ReviewEffectivenessService> logger)
    {
        _store = store;
        _clock = clock;
        _logger = logger;
    }

    public void StepRecorded(string jobFolderPath, PipelineExecutionRecord record, PipelineStepExecution step)
    {
        if (!_store.Enabled) return;
        if (step.Status is not (PipelineStepStatus.Passed or PipelineStepStatus.Failed)) return;
        if (string.IsNullOrWhiteSpace(record.Project) || string.IsNullOrWhiteSpace(record.JobId)) return;
        var delivery = new ReviewDelivery(record.Project, record.JobId, record.Attempt, DeliveryRound(record));
        if (ReviewStepObservation.FromAspect(delivery, step) is { } aspect)
            RecordReview(aspect);
        else if (ReviewStepObservation.FromGrade(delivery, step, jobFolderPath) is { } grade)
            RecordReview(grade);
        else if (ReviewStepObservation.GateEvidence(delivery, step, _clock.GetUtcNow().UtcDateTime) is { } gate)
            RecordEvidence(gate);
    }

    /// <summary>Stores a review; a blocking review is also stored as evidence against the other levels.</summary>
    public bool RecordReview(ReviewObservation observation)
    {
        if (!_store.AddObservation(observation)) return false;
        if (ReviewMissAttributionPolicy.EvidenceFrom(observation) is { } evidence)
            _store.AddEvidence(evidence);
        Reconcile(observation.Delivery);
        return true;
    }

    public bool RecordEvidence(ReviewDefectEvidence evidence)
    {
        var added = _store.AddEvidence(evidence);
        Reconcile(evidence.Delivery);
        return added;
    }

    /// <summary>
    /// An operator sent a delivery that every review let through back from
    /// Human Review. The latest recorded delivery of the task is the one the
    /// operator saw.
    /// </summary>
    public void RecordOperatorRequeue(string project, string jobId, string? reason, string actor)
    {
        if (!_store.Enabled) return;
        var delivery = _store.LatestDelivery(project, jobId);
        if (delivery is null) return;
        var now = _clock.GetUtcNow().UtcDateTime;
        RecordEvidence(new ReviewDefectEvidence(
            $"operator-requeue:{delivery.Key}:{now:O}",
            delivery,
            ReviewDefectSources.Operator,
            string.IsNullOrWhiteSpace(reason) ? "Operator sent the delivery back from Human Review." : reason.Trim(),
            now,
            actor));
        _logger.LogInformation(
            "review-effectiveness: operator requeue recorded as defect evidence delivery={Delivery}", delivery.Key);
    }

    public ReviewDelivery? LatestDelivery(string project, string jobId) => _store.LatestDelivery(project, jobId);

    public ReviewEffectivenessSummary Summary(DateTime? sinceUtc, Func<string, bool>? projectFilter = null)
    {
        var observations = _store.ReadObservations(sinceUtc, projectFilter);
        var misses = _store.ReadMisses(observations.Select(item => item.Id).ToHashSet(StringComparer.Ordinal));
        var evidence = _store.ReadEvidence(sinceUtc, projectFilter);
        return new ReviewEffectivenessSummary(
            _clock.GetUtcNow().UtcDateTime,
            sinceUtc,
            ReviewEffectivenessAggregation.Aggregate(observations, misses),
            ReviewEffectivenessAggregation.Coverage(observations, evidence.Count, misses));
    }

    public ReviewDepthAdvice AdviseDepth(
        string level, string model, string reasoningLevel, DateTime? sinceUtc,
        Func<string, bool>? projectFilter = null, ReviewDepthThresholds? thresholds = null)
        => ReviewDepthAdvisor.Advise(level, model, reasoningLevel, Summary(sinceUtc, projectFilter).Rows, thresholds);

    public ReviewEffectivenessExportDocument Export(DateTime? sinceUtc, Func<string, bool>? projectFilter = null)
        => ReviewEffectivenessExport.Build(Summary(sinceUtc, projectFilter));

    private void Reconcile(ReviewDelivery delivery)
    {
        var added = _store.AddMisses(ReviewMissAttributionPolicy.Attribute(
            _store.ReadObservations(delivery), _store.ReadEvidence(delivery), _clock.GetUtcNow().UtcDateTime));
        if (added > 0)
            _logger.LogInformation(
                "review-effectiveness: attributed {Count} miss(es) delivery={Delivery}", added, delivery.Key);
    }

    /// <summary>Completed coding runs in this attempt; a fix round makes a new delivery.</summary>
    internal static int DeliveryRound(PipelineExecutionRecord record)
        => record.Steps.FirstOrDefault(item => string.Equals(item.StepId, PipelineCatalogue.CoreAgentRunStepId,
            StringComparison.OrdinalIgnoreCase))?.Runs ?? 0;
}
