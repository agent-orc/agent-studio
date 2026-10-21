using AgentStudio.Pipeline;

namespace AgentStudio.Review;

/// <summary>
/// Maps a terminal pipeline step row to a review observation or to gate
/// evidence. Pure apart from reading the grade report named by the row.
/// </summary>
public static class ReviewStepObservation
{
    /// <summary>
    /// A fresh aspect verdict. Carried-over verdicts are not new reviews.
    /// A semantic concern or block is a Passed row; a Failed row is an
    /// execution failure (environmental crash, CLI error, or the runner's
    /// fail-closed block after a failed command) and has no review outcome.
    /// </summary>
    public static ReviewObservation? FromAspect(ReviewDelivery delivery, PipelineStepExecution step)
    {
        if (step.Kind != StepKind.Aspect || step.Status != PipelineStepStatus.Passed) return null;
        if (step.CarriedOverFrom is not null
            || string.Equals(step.ExecutionLocation, "carried-over", StringComparison.OrdinalIgnoreCase))
            return null;
        if (ReviewOutcomes.From(step.Verdict) is not { } outcome) return null;
        var subject = step.StepId.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase)
            ? step.StepId["aspect-".Length..]
            : step.StepId;
        return Observation(delivery, step, ReviewLevels.Aspect, subject, outcome, ReviewFindingCounts.ForAspect(outcome));
    }

    /// <summary>A code-review grade. Findings are read from the grade report and classified by their severity tag.</summary>
    public static ReviewObservation? FromGrade(ReviewDelivery delivery, PipelineStepExecution step, string jobFolderPath)
    {
        if (!string.Equals(step.StepId, PipelineCatalogue.CodeReviewGradeStepId, StringComparison.OrdinalIgnoreCase))
            return null;
        if (ReviewOutcomes.From(step.Verdict) is not { } outcome) return null;
        return Observation(delivery, step, ReviewLevels.Grade, "code-review-grade", outcome,
            ReviewFindingCounts.Classify(CodeReviewFindingParsing.Parse(ReadReport(jobFolderPath, step.EvidenceRef))));
    }

    /// <summary>
    /// A build/test gate that failed on the delivery. Cancellations and
    /// skipped runs are not product evidence; a cached failure of the same
    /// tree is.
    /// </summary>
    public static ReviewDefectEvidence? GateEvidence(ReviewDelivery delivery, PipelineStepExecution step, DateTime nowUtc)
    {
        if (!string.Equals(step.StepId, PipelineCatalogue.BuildTestGateStepId, StringComparison.OrdinalIgnoreCase))
            return null;
        if (step.Status != PipelineStepStatus.Failed) return null;
        var productFailure = step.Verdict is "fail" or "cached-fail"
            || (step.Verdict is null && string.Equals(step.ExecutionLocation, "remote", StringComparison.OrdinalIgnoreCase));
        if (!productFailure) return null;
        var observedAt = step.CompletedAt ?? nowUtc;
        return new ReviewDefectEvidence(
            $"gate:{delivery.Key}:{step.StartedAt?.ToString("O") ?? observedAt.ToString("O")}",
            delivery,
            ReviewDefectSources.Gate,
            string.IsNullOrWhiteSpace(step.Reason) ? "The build/test gate failed." : step.Reason!,
            observedAt,
            ReviewObservationOrigins.Pipeline,
            Severity: ReviewFindingSeverities.Blocker,
            Reference: step.GateOriginEvidencePath ?? step.EvidenceRef);
    }

    private static ReviewObservation Observation(
        ReviewDelivery delivery, PipelineStepExecution step, string level, string subject, string outcome,
        ReviewFindingCounts findings)
    {
        var observedAt = step.CompletedAt ?? step.StartedAt ?? DateTime.MinValue;
        var run = step.StartedAt?.ToString("O") ?? $"run{step.Runs}";
        return new ReviewObservation(
            $"pipeline:{delivery.Project}/{delivery.JobId}/a{delivery.PipelineAttempt}/{step.StepId}/{run}",
            delivery,
            level,
            subject,
            string.IsNullOrWhiteSpace(step.Model) ? null : step.Model.Trim(),
            string.IsNullOrWhiteSpace(step.ThinkingLevel) ? null : step.ThinkingLevel.Trim(),
            step.InputTokens,
            step.OutputTokens,
            step.CacheReadTokens,
            step.CacheCreationTokens,
            step.DurationMs,
            step.EstimatedCostUsd,
            step.Verdict!.Trim(),
            outcome,
            findings,
            observedAt,
            ReviewObservationOrigins.Pipeline,
            step.EvidenceRef);
    }

    private static string ReadReport(string jobFolderPath, string? evidenceRef)
    {
        if (string.IsNullOrWhiteSpace(evidenceRef) || string.IsNullOrWhiteSpace(jobFolderPath)) return string.Empty;
        var path = Path.Combine(jobFolderPath, Path.GetFileName(evidenceRef));
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (IOException ex)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(ex, $"review-effectiveness: read grade report {path}");
            return string.Empty;
        }
        catch (UnauthorizedAccessException ex)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(ex, $"review-effectiveness: read grade report {path}");
            return string.Empty;
        }
    }
}
