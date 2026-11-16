using AgentStudio.Shared;
using Microsoft.AspNetCore.Http.HttpResults;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// AGT-3840: hands operator re-probe requests to the runner on its next claim
/// answer. Requests stay queued until an answer of the expected shape carries
/// them, so an error or an unauthorized poll does not drop a request.
/// </summary>
public static class CapabilityReprobeDelivery
{
    public static RouteHandlerBuilder WithReviewClaimReprobes(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            if (result is not Ok<Contract.ReviewClaimResponse> { Value: { } response }
                || context.HttpContext.GetRouteValue("runnerId") is not string runnerId)
                return result;
            var registry = context.HttpContext.RequestServices.GetRequiredService<V1ReviewExecutorRegistry>();
            return registry.TakeReprobeRequests(runnerId) is { } keys
                ? TypedResults.Ok(response with { ReprobeCapabilities = Merge(response.ReprobeCapabilities, keys) })
                : result;
        });

    public static RouteHandlerBuilder WithCodingClaimReprobes(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            if (result is not Ok<RunnerClaimResponse> { Value: { } response }
                || context.Arguments.OfType<RunnerClaimRequest>().FirstOrDefault() is not { } request
                || string.IsNullOrWhiteSpace(request.RunnerId))
                return result;
            var registry = context.HttpContext.RequestServices.GetRequiredService<V1ReviewExecutorRegistry>();
            return registry.TakeReprobeRequests(request.RunnerId.Trim()) is { } keys
                ? TypedResults.Ok(response with { ReprobeCapabilities = Merge(response.ReprobeCapabilities, keys) })
                : result;
        });

    internal static IReadOnlyList<string> Merge(IReadOnlyList<string>? existing, IReadOnlyList<string> requested)
        => (existing ?? []).Concat(requested).Distinct(StringComparer.Ordinal).ToArray();
}
