using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Http.Features;

namespace AgentStudio.TaskServer;

internal static class AuthorCheckEndpoints
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static void MapAuthorCheckEndpoints(this RouteGroupBuilder api)
    {
        var checks = api.MapGroup("/author-checks");
        checks.MapPost("/profile", async (HttpContext context, AuthorCheckParent parent, TaskServerStore store,
            CancellationToken ct) => Runner(context, parent.RunnerId) is { } principal
                ? await TaskServerEndpoints.InvokeAsync(() => store.GetAuthorCheckProfileAsync(parent, principal, ct))
                : Denied()).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep)
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);

        checks.MapPost("/submit", async (HttpContext context, TaskServerStore store, CancellationToken ct) =>
        {
            var header = context.Request.Headers["X-Author-Check"].ToString();
            AuthorCheckSubmission? submission;
            try
            {
                submission = header.Length is > 0 and <= 4096
                    ? JsonSerializer.Deserialize<AuthorCheckSubmission>(Convert.FromBase64String(header), WebJson) : null;
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                submission = null;
            }
            if (submission?.Parent is null || submission.Snapshot is null
                || context.Request.ContentLength != submission.Snapshot.Bytes)
                return Results.BadRequest(new ApiError("author-check-submission", "The submission or content length is invalid."));
            if (Runner(context, submission.Parent.RunnerId) is not { } principal) return Denied();
            if (context.Request.ContentLength > AuthorCheckProtocol.BundleLimit)
                return ArtifactRequestLimitMiddleware.Problem(AuthorCheckProtocol.BundleLimit, context.Request.ContentLength);
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
                bodyLimit.MaxRequestBodySize = AuthorCheckProtocol.BundleLimit;
            return await TaskServerEndpoints.InvokeAsync(() => store.SubmitAuthorCheckAsync(
                submission, principal, context.Request.Body, ct));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep)
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);

        checks.MapPost("/{id}/receipt", async (HttpContext context, string id, AuthorCheckParent parent,
            TaskServerStore store, CancellationToken ct) => Runner(context, parent.RunnerId) is { } principal
                ? await TaskServerEndpoints.InvokeAsync(() => store.ReadAuthorCheckAsync(id, parent, principal, ct))
                : Denied()).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep)
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);

        checks.MapPost("/attempts/{attemptId}/source", async (HttpContext context, string attemptId,
            AuthorCheckSourceGrant request, TaskServerStore store, CancellationToken ct) =>
        {
            if (request.Authority is null || Runner(context, request.Authority.ExecutorId) is null) return Denied();
            try
            {
                var source = await store.ReadAuthorCheckSourceAsync(attemptId, request, ct);
                return Results.Bytes(source, "application/octet-stream");
            }
            catch (Exception exception) { return TaskServerEndpoints.MapError(exception); }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep)
            .RequireTaskServerScope(TaskServerScopes.ReviewsWrite);

        checks.MapPost("/attempts/{attemptId}/settle", async (HttpContext context, string attemptId,
            AuthorCheckSettlement report, TaskServerStore store, CancellationToken ct)
            => report.Authority is not null && Runner(context, report.Authority.ExecutorId) is not null
                ? await TaskServerEndpoints.InvokeAsync(() => store.SettleAuthorCheckAsync(attemptId, report, ct))
                : Denied()).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep)
            .RequireTaskServerScope(TaskServerScopes.ReviewsWrite);

        checks.MapPost("/attempts/{attemptId}/contained", async (HttpContext context, string attemptId,
            PostWorkAuthority authority, TaskServerStore store, CancellationToken ct)
            => Runner(context, authority.ExecutorId) is not null
                ? await TaskServerEndpoints.InvokeAsync(() => store.ReleaseContainedAuthorCheckAsync(attemptId, authority, ct))
                : Denied()).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep)
            .RequireTaskServerScope(TaskServerScopes.ReviewsWrite);

        checks.MapPost("/protected-work", async (HttpContext context, AuthorCheckProtectedWork facts,
            TaskServerStore store, CancellationToken ct)
            => context.TaskServerPrincipal() is { Kind: not TaskServerPrincipalKinds.Runner } principal
                ? await TaskServerEndpoints.InvokeAsync(() => store.ObserveAuthorCheckProtectedWorkAsync(facts, principal.PrincipalId, ct))
                : Denied()).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep)
            .RequireTaskServerScope(TaskServerScopes.OrchestrationWrite);
    }

    private static string? Runner(HttpContext context, string runnerId)
        => context.TaskServerPrincipal() is { Kind: TaskServerPrincipalKinds.Runner } principal
            && principal.RunnerId == runnerId ? principal.PrincipalId : null;

    private static IResult Denied()
        => Results.Json(new ApiError("author-check-identity", "A bound pilot principal is required."), statusCode: 403);
}

internal sealed class AuthorCheckSweep(TaskServerStore store, ILogger<AuthorCheckSweep> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await store.SweepAuthorChecksAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "Author-check cleanup will retry on the next sweep");
            }
        }
    }
}
