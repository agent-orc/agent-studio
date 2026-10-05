using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public sealed class TaskServerAuthenticationMiddleware(
    RequestDelegate next,
    TaskServerBootstrapOptions bootstrap,
    TaskServerStore store,
    ILogger<TaskServerAuthenticationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!bootstrap.RequiresAuthentication
            || !IsProtectedPath(context.Request.Path)
            || IsOpenPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        var presented = ReadBearer(context.Request);
        var principal = presented is null
            ? null
            : await store.AuthenticatePrincipalAsync(presented, context.RequestAborted);
        if (principal is null)
        {
            await DenyAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "authentication-required",
                "A valid Task Server principal credential is required.");
            return;
        }

        var requiredScope = RequiredScope(context);
        if (requiredScope is null || !requiredScope.Allows(principal.Scopes))
        {
            await DenyAsync(
                context,
                StatusCodes.Status403Forbidden,
                "insufficient-scope",
                requiredScope is null
                    ? "The route has no authorization scope declaration."
                    : $"The authenticated principal requires scope '{requiredScope.Scope}' or one of its declared alternatives.");
            return;
        }

        if (principal.Kind == TaskServerPrincipalKinds.Runner
            && principal.RunnerId is not null
            && context.Request.RouteValues.TryGetValue("runnerId", out var routeRunner)
            && !string.Equals(
                principal.RunnerId,
                Convert.ToString(routeRunner),
                StringComparison.Ordinal))
        {
            await DenyAsync(
                context,
                StatusCodes.Status403Forbidden,
                "runner-identity-mismatch",
                "A Runner principal may mutate only its bound Runner identity.");
            return;
        }

        context.Items[typeof(TaskServerPrincipal)] = principal;
        await next(context);
        if (context.Response.StatusCode is >= 200 and < 300
            && !context.Request.Path.StartsWithSegments("/api/v1/principal-rotations")
            && requiredScope is not null)
        {
            try
            {
                await store.RecordPrincipalScopeProofAsync(principal, requiredScope.Scope,
                    context.Request.Headers["X-Principal-Consumer-Id"].FirstOrDefault(),
                    context.Request.Headers["X-Principal-Consumer-Proof"].FirstOrDefault(),
                    context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The consumer will make another scoped request before acknowledgement.
            }
            catch (Exception exception)
            {
                logger.LogWarning("Principal rotation proof recording failed: {ErrorType}",
                    exception.GetType().Name);
            }
        }
    }

    private static bool IsProtectedPath(PathString path)
        => path.StartsWithSegments("/api/v1")
           || path.StartsWithSegments("/hubs");

    private static bool IsOpenPath(PathString path)
        => path.Equals("/api/v1/protocol", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/v1/protocol/compatibility", StringComparison.OrdinalIgnoreCase);

    private static string? ReadBearer(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.FirstOrDefault();
        const string prefix = "Bearer ";
        var header = authorization?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? authorization[prefix.Length..].Trim()
            : null;
        if (!string.IsNullOrWhiteSpace(header))
            return header;
        return request.Path.StartsWithSegments("/hubs")
            ? request.Query["access_token"].FirstOrDefault()?.Trim()
            : null;
    }

    private static TaskServerScopeMetadata? RequiredScope(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/hubs"))
            return new TaskServerScopeMetadata(TaskServerScopes.EventsSubscribe);
        return context.GetEndpoint()?
            .Metadata
            .GetOrderedMetadata<TaskServerScopeMetadata>()
            .LastOrDefault();
    }

    private static async Task DenyAsync(
        HttpContext context,
        int status,
        string code,
        string message)
    {
        context.Response.StatusCode = status;
        if (status == StatusCodes.Status401Unauthorized)
            context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsJsonAsync(new ApiError(code, message));
    }
}
