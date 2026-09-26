namespace AgentStudio.Tasks;

/// <summary>Typed Task Server control-plane API for canonical run and review attempt authority.</summary>
public static class AttemptAuthorityEndpoints
{
    public static void MapAttemptAuthorityEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/attempts");

        group.MapGet("/tasks/{taskKey}", (string taskKey, AttemptAuthorityService authority) =>
            Results.Ok(authority.GetTaskProjection(taskKey, includeArchived: true)));

        group.MapGet("/runs/{attemptId}", (string attemptId, AttemptAuthorityService authority) =>
            authority.GetRun(attemptId) is { } run ? Results.Ok(run) : Results.NotFound());

        group.MapGet("/reviews/{attemptId}", (string attemptId, AttemptAuthorityService authority) =>
            authority.GetReview(attemptId) is { } review ? Results.Ok(review) : Results.NotFound());

        group.MapPost("/reviews", (CreateReviewAttemptRequest request, AttemptAuthorityService authority) =>
            ToHttp(authority.CreateReviewAttempt(request)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Review);

        group.MapPost("/reviews/{attemptId}/claim", (
            string attemptId,
            ClaimReviewAttemptRequest request,
            ReviewAttemptTaskLifecycleService reviewAttemptLifecycle) =>
            ToHttp(reviewAttemptLifecycle.ClaimReview(
                attemptId,
                request.ExecutorId,
                request.HostId,
                request.RequestedTtlSeconds,
                request.IdempotencyKey)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);

        group.MapPost("/reviews/{attemptId}/settle", (string attemptId) =>
            Results.Conflict(new
            {
                code = "review-delivery-required",
                message = "Submit the fenced review report through the review plane. Authority settlement alone does not complete delivery.",
                attemptId,
            })).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);

        group.MapPost("/reviews/{attemptId}/renew", (
            string attemptId,
            RenewAttemptLeaseRequest request,
            AttemptAuthorityService authority) =>
        {
            if (!string.Equals(attemptId, request.Write.AttemptId, StringComparison.Ordinal))
                return Results.BadRequest(new AttemptWriteResult(
                    AttemptWriteStatus.Invalid, attemptId, "Route AttemptId must match the write reference."));
            return ToHttp(authority.RenewReview(request.Write, request.ExecutorId, request.RequestedTtlSeconds));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
    }

    private static IResult ToHttp(AttemptWriteResult result) => result.Status switch
    {
        AttemptWriteStatus.Accepted or AttemptWriteStatus.Duplicate => Results.Ok(result),
        AttemptWriteStatus.NotFound => Results.NotFound(result),
        AttemptWriteStatus.Invalid => Results.BadRequest(result),
        _ => Results.Conflict(result),
    };
}
