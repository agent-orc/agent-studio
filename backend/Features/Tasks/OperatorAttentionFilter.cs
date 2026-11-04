using AgentStudio.Operator;
using AgentStudio.Security;

namespace AgentStudio.Tasks;

/// <summary>Card write precondition for API-token and operator-session actors.</summary>
public sealed class OperatorAttentionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation,
        EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var request = context.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
            || HttpMethods.IsOptions(request.Method)
            || request.Path.Value?.EndsWith("/attention", StringComparison.OrdinalIgnoreCase) == true
            || !context.Request.RouteValues.TryGetValue("jobId", out var rawId)
            || rawId is not string jobId)
            return await next(invocation);

        var scanner = context.RequestServices.GetRequiredService<TaskScannerService>();
        var watchPath = request.Query["watchPath"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(watchPath)
            && request.Query["project"].FirstOrDefault() is { Length: > 0 } project)
            watchPath = context.RequestServices.GetRequiredService<AgentStudio.Registry.ProjectRegistry>()
                .FindByIdOrDisplayName(project)?.StorageLocation;
        var card = scanner.FindJob(jobId, watchPath);
        if (card is null) return await next(invocation);
        var store = context.RequestServices.GetRequiredService<OperatorCoordinationStore>();
        var service = context.Items[AccessSecurityMiddleware.ServiceAccountPrincipalItem] as ServiceAccountPrincipal;
        var operatorWrite = service is { Origin: ServiceAccountOrigins.ApiToken } || service?.Operator is not null;
        if (operatorWrite)
        {
            if (store.Current(card.TaskKey)?.Holder != service!.ActorId)
                return Results.Conflict(new { error = "attention-lease-required" });
            if (request.Headers.IfMatch.Count == 0)
                return Results.Json(new { error = "card-revision-required" },
                    statusCode: StatusCodes.Status428PreconditionRequired);
            if (!string.Equals(request.Headers.IfMatch.ToString(),
                    OperatorCoordinationStore.Revision(card.FolderPath), StringComparison.Ordinal))
                return Results.Json(new { error = "stale-card-revision" },
                    statusCode: StatusCodes.Status412PreconditionFailed);
        }
        var result = await next(invocation);
        if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
            && human.User.Role == StudioRoles.Owner
            && (result as IStatusCodeHttpResult)?.StatusCode is null or < 400)
            await store.OwnerActionAsync(card.TaskKey, card.ProjectName,
                OperatorEndpoints.Actor(context), context.RequestAborted);
        return result;
    }
}
