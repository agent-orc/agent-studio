using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Routes of the derived source data store (AGT-3318). Reads need
/// <c>tasks:read</c>; publishing, the producer lease and demand completion need
/// <c>runs:write</c>, which the runner-host producer holds; recording a demand
/// or a review material read needs one of the write scopes. A missing object
/// answers 404 <c>source-object-missing</c>; the reader then records a demand
/// and shows warming. Nothing here starts Git.
/// </summary>
internal static class SourceFactsEndpoints
{
    internal static void MapSourceFactsEndpoints(this WebApplication app)
        => MapSourceFactsEndpoints((IEndpointRouteBuilder)app);

    internal static void MapSourceFactsEndpoints(this IEndpointRouteBuilder app)
    {
        var facts = app.MapGroup("/api/v1/source-facts/{repositoryId}")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        facts.MapPost("/producer/lease", async (
            string repositoryId, SourceProducerLeaseRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ClaimSourceProducerLeaseAsync(repositoryId, request, ct)))
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);

        facts.MapPut("/diffs", async (
            string repositoryId, SourceDiffObjectPublishRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.PublishSourceDiffObjectAsync(repositoryId, request, ct)))
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);

        facts.MapGet("/diffs", async (
            string repositoryId, string @base, string head, string? options, int? renderer,
            TaskServerStore store, CancellationToken ct)
            => await ReadAsync(() => store.GetSourceDiffObjectAsync(
                repositoryId, @base, head, options ?? SourceDiffObjectOptions.DefaultReview,
                renderer ?? SourceDiffObjectOptions.RendererVersion, ct), "diff object"));

        facts.MapPut("/path-histories", async (
            string repositoryId, SourcePathHistoryPublishRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.PublishSourcePathHistoryAsync(repositoryId, request, ct)))
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);

        facts.MapGet("/path-histories", async (
            string repositoryId, string head, string path, TaskServerStore store, CancellationToken ct)
            => await ReadAsync(() => store.GetSourcePathHistoryAsync(repositoryId, head, path, ct), "path history"));

        facts.MapPut("/blames", async (
            string repositoryId, SourceBlamePublishRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.PublishSourceBlameAsync(repositoryId, request, ct)))
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);

        facts.MapGet("/blames", async (
            string repositoryId, string head, string path, TaskServerStore store, CancellationToken ct)
            => await ReadAsync(() => store.GetSourceBlameAsync(repositoryId, head, path, ct), "blame"));

        facts.MapPut("/reachability", async (
            string repositoryId, SourceReachabilityPublishRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.PublishSourceReachabilityAsync(repositoryId, request, ct)))
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);

        facts.MapGet("/reachability", async (
            string repositoryId, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetSourceReachabilityAsync(
                repositoryId,
                context.Request.Query["sha"].Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray(),
                context.Request.Query["ref"].Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray(),
                ct)));

        // A demand is a reader's cache-miss signal: the Studio (tasks:write),
        // a review executor (reviews:write) or a runner (runs:write) records
        // it. The store deduplicates by content and bounds the pending queue.
        facts.MapPost("/demands", async (
            string repositoryId, SourceFactDemandRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.RecordSourceFactDemandAsync(repositoryId, request, ct), StatusCodes.Status202Accepted))
            .RequireAnyTaskServerScope(TaskServerScopes.RunsWrite, TaskServerScopes.ReviewsWrite, TaskServerScopes.TasksWrite);

        facts.MapGet("/demands", async (
            string repositoryId, int? limit, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListSourceFactDemandsAsync(repositoryId, limit ?? 20, ct)))
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);

        facts.MapPost("/demands/{demandId:long}/complete", async (
            string repositoryId, long demandId, SourceFactDemandCompleteRequest request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.CompleteSourceFactDemandAsync(repositoryId, demandId, request, ct)))
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);

        facts.MapPost("/review-material/reads", async (
            string repositoryId, SourceReviewMaterialReadReport report, TaskServerStore store, CancellationToken ct) =>
        {
            try
            {
                await store.RecordSourceReviewMaterialReadAsync(repositoryId, report, ct);
                return Results.NoContent();
            }
            catch (Exception exception)
            {
                return TaskServerEndpoints.MapError(exception);
            }
        }).RequireAnyTaskServerScope(TaskServerScopes.ReviewsWrite, TaskServerScopes.RunsWrite, TaskServerScopes.TasksWrite);

        app.MapGet("/api/v1/source-facts/review-material/timings", async (
            string? repositoryId, DateTime? sinceUtc, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.GetSourceReviewMaterialTimingAsync(repositoryId, sinceUtc, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
    }

    private static async Task<IResult> ReadAsync<T>(Func<Task<T?>> read, string what) where T : class
    {
        try
        {
            var value = await read();
            return value is null
                ? Results.NotFound(new ApiError("source-object-missing", $"No {what} is stored under this key."))
                : Results.Ok(value);
        }
        catch (Exception exception)
        {
            return TaskServerEndpoints.MapError(exception);
        }
    }
}
