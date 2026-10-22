using AgentStudio.Tasks;

namespace AgentStudio.Review;

public static class ReviewObservationEndpoints
{
    public static void MapReviewObservationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/token-usage/review-observations", (
            string projectName, TaskScannerService scanner, ReviewObservationStore observations) =>
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(item =>
                string.Equals(item.Name, projectName, StringComparison.OrdinalIgnoreCase));
            return entry is null ? Results.NotFound() : Results.Ok(observations.Project(projectName, entry.Path));
        });

        app.MapGet("/api/projects/{projectName}/token-usage/review-observations/export", (
            string projectName, TaskScannerService scanner, ReviewObservationStore observations) =>
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(item =>
                string.Equals(item.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Results.NotFound();
            var report = observations.Project(projectName, entry.Path);
            var exportable = report.Reviews.Select(review => (Review: review,
                Run: TokenEconomyReviewExport.From(review, projectName))).ToArray();
            return Results.Ok(new
            {
                format = "quality-studio-review-run-v1-drop",
                status = "studio-observation",
                observedAt = report.GeneratedAt,
                method = report.Method,
                project = report.Project,
                studioObservations = report.Reviews,
                runs = exportable.Where(item => item.Run is not null).Select(item => item.Run),
                excludedObservations = exportable.Where(item => item.Run is null)
                    .Select(item => new { item.Review.Id, reason = "Token Economy requires a known CLI, model, reasoning level, and measured positive reviewed file count." }),
                laterEvidence = report.LaterEvidence,
                aggregates = report.ByModelAndReasoning,
            });
        });

        app.MapGet("/api/projects/{projectName}/token-usage/review-observations/export/{reviewId}", (
            string projectName, string reviewId, TaskScannerService scanner,
            ReviewObservationStore observations) =>
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(item =>
                string.Equals(item.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Results.NotFound();
            var review = observations.Project(projectName, entry.Path).Reviews
                .FirstOrDefault(item => item.Id == reviewId);
            if (review is null) return Results.NotFound();
            var run = TokenEconomyReviewExport.From(review, projectName);
            return run is null
                ? Results.UnprocessableEntity(new { error = "Token Economy requires a known CLI, model, reasoning level, and measured positive reviewed file count." })
                : Results.Ok(run);
        });
    }

    public static void MapTaskReviewObservationEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{jobId}/review-observations", (
            string jobId, string project, ReviewObservation observation,
            TaskScannerService scanner, ReviewObservationStore observations,
            ITokenPricing pricing, TimeProvider clock) =>
        {
            var task = FindTask(scanner, project, jobId);
            if (task is null) return Results.NotFound();
            if (observation.JobId != jobId || string.IsNullOrWhiteSpace(observation.Id)
                || observation.Id.Length > 200
                || observation.Level is not ("aspect" or "grade" or "deep")
                || ReviewObservationStore.ValidSha(observation.ResultSha) is null
                || observation.ReviewedAt == default
                || observation.ReviewedAt > clock.GetUtcNow().UtcDateTime
                || observation.InputTokens < 0 || observation.OutputTokens < 0
                || observation.CacheReadTokens < 0 || observation.CacheCreationTokens < 0
                || observation.DurationMs < 0 || observation.CostUsd < 0
                || observation.Findings is null
                || observation.Findings.Blocker < 0 || observation.Findings.Major < 0
                || observation.Findings.Minor < 0
                || observation.ConfirmedFindings < 0
                || observation.ConfirmedFindings > observation.Findings.Total
                || observation.DismissedFindings < 0
                || observation.ConfirmedFindings + observation.DismissedFindings > observation.Findings.Total
                || observation.FilesReviewed < 0
                || string.IsNullOrWhiteSpace(observation.EvidenceRef)
                || observation.EvidenceRef.Length > 500)
                return Results.BadRequest(new { error = "A valid review, exact result SHA, nonnegative measurements, and evidence reference are required." });
            var estimate = pricing.Estimate(observation.Model, observation.InputTokens,
                observation.OutputTokens, observation.CacheReadTokens,
                observation.CacheCreationTokens, observation.ReviewedAt);
            var recorded = observation.CostUsd is null && estimate.ModelKnown
                ? observation with { CostUsd = estimate.Total }
                : observation;
            return observations.RecordReview(task.FolderPath, task.WatchPath, recorded)
                ? Results.Created($"/api/tasks/{jobId}/review-observations/{observation.Id}", recorded)
                : Results.Conflict(new { error = "Review observation id already exists." });
        });

        group.MapPost("/{jobId}/review-observations/{reviewId}/later-evidence", (
            string jobId, string reviewId, string project, LaterEvidenceRequest body,
            TaskScannerService scanner, ReviewObservationStore observations, TimeProvider clock) =>
        {
            var task = FindTask(scanner, project, jobId);
            if (task is null) return Results.NotFound();
            if (body.Source is not ("later-review" or "gate" or "operator" or "production")
                || string.IsNullOrWhiteSpace(body.EvidenceRef) || body.EvidenceRef.Length > 500
                || string.IsNullOrWhiteSpace(body.Summary) || body.Summary.Length > 2000
                || body.Severity is not ("blocker" or "major" or "minor"))
                return Results.BadRequest(new { error = "A cited defect source, severity, and summary are required." });
            var report = observations.Project(project, task.WatchPath);
            var review = report.Reviews.FirstOrDefault(item => item.JobId == jobId && item.Id == reviewId);
            if (review is null) return Results.NotFound(new { error = "Review observation not found." });
            var evidence = new ReviewLaterEvidence(
                Guid.NewGuid().ToString("N"), reviewId, body.ResultSha,
                clock.GetUtcNow().UtcDateTime, body.Source, body.EvidenceRef,
                body.Severity, body.Summary);
            if (!ReviewObservationPolicy.IsMiss(review, evidence))
                return Results.BadRequest(new { error = "The earlier review must have approved this exact SHA before the defect was found." });
            observations.RecordEvidence(task.FolderPath, task.WatchPath, evidence);
            return Results.Created($"/api/tasks/{jobId}/review-observations/{reviewId}/later-evidence", evidence);
        });

        group.MapPost("/{jobId}/review-observations/{reviewId}/finding-outcomes", (
            string jobId, string reviewId, string project, FindingOutcomesRequest body,
            TaskScannerService scanner, ReviewObservationStore observations, TimeProvider clock) =>
        {
            var task = FindTask(scanner, project, jobId);
            if (task is null) return Results.NotFound();
            var review = observations.Project(project, task.WatchPath).Reviews
                .FirstOrDefault(item => item.JobId == jobId && item.Id == reviewId);
            if (review is null) return Results.NotFound(new { error = "Review observation not found." });
            if (body.Confirmed < 0 || body.Dismissed < 0
                || (long)body.Confirmed + body.Dismissed > review.Findings.Total
                || string.IsNullOrWhiteSpace(body.EvidenceRef)
                || body.EvidenceRef.Length > 500)
                return Results.BadRequest(new { error = "Confirmed and dismissed counts need a citation and cannot exceed reported findings." });
            var disposition = new ReviewFindingDisposition(reviewId, body.Confirmed,
                body.Dismissed, clock.GetUtcNow().UtcDateTime, body.EvidenceRef);
            observations.RecordDisposition(task.FolderPath, task.WatchPath, disposition);
            return Results.Ok(disposition);
        });
    }

    private static TaskInfo? FindTask(TaskScannerService scanner, string project, string jobId)
    {
        var entry = scanner.GetWatchPaths().FirstOrDefault(item =>
            string.Equals(item.Name, project, StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : scanner.FindJob(jobId, entry.Path);
    }
}

public sealed record LaterEvidenceRequest(string ResultSha, string Source,
    string EvidenceRef, string Severity, string Summary);
public sealed record FindingOutcomesRequest(int Confirmed, int Dismissed, string EvidenceRef);
