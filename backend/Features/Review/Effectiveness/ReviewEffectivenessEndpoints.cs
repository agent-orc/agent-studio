using AgentStudio.Registry;
using AgentStudio.Security;

namespace AgentStudio.Review;

/// <summary>A deep review run outside the standard plan. Without an attempt and round it applies to the latest recorded delivery.</summary>
public sealed record RecordDeepReviewRequest(
    string Project,
    string JobId,
    string Model,
    string? ReasoningLevel,
    string Verdict,
    long InputTokens = 0,
    long OutputTokens = 0,
    long CacheReadTokens = 0,
    long CacheCreationTokens = 0,
    long DurationMs = 0,
    decimal? CostUsd = null,
    ReviewFindingCounts? Findings = null,
    int? PipelineAttempt = null,
    int? DeliveryRound = null,
    string? Reference = null,
    string? Id = null);

/// <summary>Defect evidence from an operator or production. Without an attempt and round it applies to the latest recorded delivery.</summary>
public sealed record RecordDefectEvidenceRequest(
    string Project,
    string JobId,
    string Source,
    string Summary,
    string? Severity = null,
    string? Reference = null,
    int? PipelineAttempt = null,
    int? DeliveryRound = null,
    string? Id = null);

public static class ReviewEffectivenessEndpoints
{
    public const int DefaultWindowDays = 30;
    public const int MaximumWindowDays = 365;

    public static void MapReviewEffectivenessEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/review-effectiveness");

        group.MapGet("", (HttpContext context, int? days, ReviewEffectivenessService service,
            ProjectRegistry registry, TimeProvider clock) =>
        {
            if (!TryWindow(days, clock, out var since, out var error)) return error!;
            return Results.Ok(service.Summary(since, AccessFilter(context, registry)));
        });

        group.MapGet("/depth-advice", (HttpContext context, string? level, string? model, string? reasoning,
            int? days, ReviewEffectivenessService service, ProjectRegistry registry, TimeProvider clock) =>
        {
            if (!ReviewLevels.IsKnown(level))
                return Results.BadRequest(new { error = $"level must be one of {string.Join(", ", ReviewLevels.All)}." });
            if (string.IsNullOrWhiteSpace(model))
                return Results.BadRequest(new { error = "model is required." });
            if (!TryWindow(days, clock, out var since, out var error)) return error!;
            return Results.Ok(service.AdviseDepth(level!, model.Trim(),
                string.IsNullOrWhiteSpace(reasoning) ? ReviewEffectivenessAggregation.Unknown : reasoning.Trim(),
                since, AccessFilter(context, registry)));
        });

        group.MapGet("/export", (HttpContext context, int? days, ReviewEffectivenessService service,
            ProjectRegistry registry, TimeProvider clock) =>
        {
            if (!TryWindow(days, clock, out var since, out var error)) return error!;
            return Results.Ok(service.Export(since, AccessFilter(context, registry)));
        });

        group.MapPost("/reviews", (HttpContext context, RecordDeepReviewRequest request,
            ReviewEffectivenessService service, ProjectRegistry registry, TimeProvider clock) =>
        {
            if (Forbidden(context, registry, request.Project)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var delivery = Resolve(service, request.Project, request.JobId, request.PipelineAttempt, request.DeliveryRound);
            var validation = ReviewEffectivenessRequests.DeepReview(request, delivery, clock.GetUtcNow().UtcDateTime);
            if (validation.Error is not null) return Results.BadRequest(new { error = validation.Error });
            var added = service.RecordReview(validation.Value!);
            return Results.Ok(new { id = validation.Value!.Id, delivery = delivery!.Key, added });
        });

        group.MapPost("/evidence", (HttpContext context, RecordDefectEvidenceRequest request,
            ReviewEffectivenessService service, ProjectRegistry registry, TimeProvider clock) =>
        {
            if (Forbidden(context, registry, request.Project)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var delivery = Resolve(service, request.Project, request.JobId, request.PipelineAttempt, request.DeliveryRound);
            var validation = ReviewEffectivenessRequests.Evidence(request, delivery, Actor(context),
                clock.GetUtcNow().UtcDateTime);
            if (validation.Error is not null) return Results.BadRequest(new { error = validation.Error });
            var added = service.RecordEvidence(validation.Value!);
            return Results.Ok(new { id = validation.Value!.Id, delivery = delivery!.Key, added });
        });
    }

    private static bool TryWindow(int? days, TimeProvider clock, out DateTime? since, out IResult? error)
    {
        since = null;
        error = null;
        var window = days ?? DefaultWindowDays;
        if (window is < 0 or > MaximumWindowDays)
        {
            error = Results.BadRequest(new { error = $"days must be between 0 and {MaximumWindowDays}; 0 reads everything." });
            return false;
        }
        if (window > 0) since = clock.GetUtcNow().UtcDateTime.AddDays(-window);
        return true;
    }

    private static ReviewDelivery? Resolve(ReviewEffectivenessService service, string? project, string? jobId,
        int? attempt, int? round)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(jobId)) return null;
        if (attempt is { } explicitAttempt)
            return new ReviewDelivery(project.Trim(), jobId.Trim(), explicitAttempt, round ?? 0);
        return service.LatestDelivery(project.Trim(), jobId.Trim());
    }

    private static Func<string, bool>? AccessFilter(HttpContext context, ProjectRegistry registry)
        => AgentStudio.Pipeline.GateEconomicsEndpoints.AccessFilter(context, registry);

    private static bool Forbidden(HttpContext context, ProjectRegistry registry, string? project)
        => !string.IsNullOrWhiteSpace(project)
           && AccessFilter(context, registry) is { } allows
           && !allows(project.Trim());

    private static string Actor(HttpContext context)
        => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
            ? human.User.Username
            : "api";
}

/// <summary>Boundary validation for the two write requests.</summary>
public static class ReviewEffectivenessRequests
{
    public sealed record Validated<T>(T? Value, string? Error);

    public static Validated<ReviewObservation> DeepReview(
        RecordDeepReviewRequest request, ReviewDelivery? delivery, DateTime nowUtc)
    {
        if (Missing(request.Project, request.JobId) is { } missing) return new(null, missing);
        if (delivery is null) return new(null, NoDelivery);
        if (string.IsNullOrWhiteSpace(request.Model)) return new(null, "model is required.");
        if (ReviewOutcomes.From(request.Verdict) is not { } outcome)
            return new(null, "verdict must be pass, concerns, block, or a grade A-D.");
        var findings = request.Findings ?? ReviewFindingCounts.None;
        if (findings.HasNegative || request.InputTokens < 0 || request.OutputTokens < 0
            || request.CacheReadTokens < 0 || request.CacheCreationTokens < 0 || request.DurationMs < 0
            || request.CostUsd < 0)
            return new(null, "Token, duration, cost and finding counts must not be negative.");
        var id = string.IsNullOrWhiteSpace(request.Id)
            ? $"api:{delivery.Key}:{ReviewLevels.Deep}:{nowUtc:O}"
            : $"api:{request.Id.Trim()}";
        return new(new ReviewObservation(
            id, delivery, ReviewLevels.Deep, "deep-review", request.Model.Trim(),
            string.IsNullOrWhiteSpace(request.ReasoningLevel) ? null : request.ReasoningLevel.Trim(),
            request.InputTokens, request.OutputTokens, request.CacheReadTokens, request.CacheCreationTokens,
            request.DurationMs, request.CostUsd, request.Verdict.Trim(), outcome, findings, nowUtc,
            ReviewObservationOrigins.Api, request.Reference), null);
    }

    public static Validated<ReviewDefectEvidence> Evidence(
        RecordDefectEvidenceRequest request, ReviewDelivery? delivery, string actor, DateTime nowUtc)
    {
        if (Missing(request.Project, request.JobId) is { } missing) return new(null, missing);
        if (!ReviewDefectSources.External.Contains(request.Source, StringComparer.Ordinal))
            return new(null, $"source must be one of {string.Join(", ", ReviewDefectSources.External)}.");
        if (string.IsNullOrWhiteSpace(request.Summary)) return new(null, "summary is required.");
        if (delivery is null) return new(null, NoDelivery);
        var id = string.IsNullOrWhiteSpace(request.Id)
            ? $"{request.Source}:{delivery.Key}:{nowUtc:O}"
            : $"{request.Source}:{request.Id.Trim()}";
        return new(new ReviewDefectEvidence(id, delivery, request.Source, request.Summary.Trim(), nowUtc, actor,
            Severity: string.IsNullOrWhiteSpace(request.Severity) ? null : ReviewFindingSeverity.Of(request.Severity),
            Reference: request.Reference), null);
    }

    private const string NoDelivery =
        "No reviewed delivery is recorded for this task; pass pipelineAttempt and deliveryRound.";

    private static string? Missing(string? project, string? jobId)
        => string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(jobId) ? "project and jobId are required." : null;
}
