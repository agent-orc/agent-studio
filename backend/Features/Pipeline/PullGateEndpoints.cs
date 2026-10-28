using AgentStudio.Security;

namespace AgentStudio.Pipeline;

public sealed record PullGateRegistration(string Name);
public sealed record PullGateHeartbeat(string? Progress);

public static class PullGateEndpoints
{
    public static void MapPullGateEndpoints(this WebApplication app)
    {
        var gates = app.MapGroup("/api/pull-gates");
        gates.MapPost("/register", (HttpContext context, PullGateRegistration request,
            PullGateStore store, AccessSecurityStore security) =>
        {
            var principal = GatePrincipal(context, security);
            return principal is null ? Results.Unauthorized()
                : Safe(() => Results.Ok(store.Register(principal.RunnerId, request.Name)));
        });
        gates.MapPost("/claim", (HttpContext context, PullGateStore store,
            AccessSecurityStore security) =>
        {
            var principal = GatePrincipal(context, security);
            return principal is null ? Results.Unauthorized()
                : Results.Ok(store.Claim(principal.RunnerId));
        });
        gates.MapPost("/runs/{runId}/heartbeat", (HttpContext context, string runId,
            PullGateHeartbeat request, PullGateStore store, AccessSecurityStore security) =>
            WithLease(context, security, store, runId,
                (host, lease) => Results.Ok(new { expiresAt =
                    store.Heartbeat(host, runId, lease, request.Progress) })));
        gates.MapPost("/runs/{runId}/progress", (HttpContext context, string runId,
            PullGateHeartbeat request, PullGateStore store, AccessSecurityStore security) =>
            WithLease(context, security, store, runId, (host, lease) =>
            {
                store.ReportProgress(host, runId, lease, request.Progress ?? "");
                return Results.NoContent();
            }));
        gates.MapGet("/runs/{runId}/{name}", (HttpContext context, string runId,
            string name, PullGateStore store, AccessSecurityStore security) =>
        {
            if (name is not ("request.json" or "source.bundle")) return Results.NotFound();
            return WithLease(context, security, store, runId, (host, lease) =>
            {
                var path = store.FileForLease(host, runId, lease, name);
                return Results.File(path, "application/octet-stream");
            });
        });
        gates.MapPost("/runs/{runId}/source-bundle", async (HttpContext context, string runId,
            PullGateStore store, AccessSecurityStore security, CancellationToken ct) =>
        {
            var principal = GatePrincipal(context, security);
            if (principal is null) return Results.Unauthorized();
            var lease = context.Request.Headers["X-Gate-Lease"].ToString();
            try
            {
                var (bundle, digest) = await store.BundleForLeaseAsync(principal.RunnerId,
                    runId, lease, context.Request.Query["base"].FirstOrDefault(), ct);
                context.Response.Headers["X-Source-Bundle-Sha256"] = digest;
                return Results.File(bundle, "application/octet-stream");
            }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        gates.MapPut("/runs/{runId}/artifacts/{name}", (HttpContext context,
            string runId, string name, PullGateStore store, AccessSecurityStore security) =>
            WithLease(context, security, store, runId, (host, lease) =>
            {
                if (context.Request.ContentLength is > 512L * 1024 * 1024)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                store.UploadArtifact(host, runId, lease, name, context.Request.Body);
                return Results.NoContent();
            }));
        gates.MapPost("/runs/{runId}/result", (HttpContext context, string runId,
            RemoteGateResponse response, PullGateStore store, AccessSecurityStore security) =>
            WithLease(context, security, store, runId, (host, lease) =>
            {
                store.Complete(host, runId, lease, response);
                return Results.NoContent();
            }));
        // Completed release files are served by Studio's store. Deployment
        // clients authenticate with runner.artifacts through the API middleware.
        app.MapGet("/api/gate-artifacts/{runId}", (string runId, PullGateStore store)
            => Results.Ok(store.Artifacts(runId)));
        app.MapGet("/api/gate-artifacts/{runId}/{name}", (string runId, string name,
            PullGateStore store) => store.Artifact(runId, name) is { } path
                ? Results.File(path, "application/octet-stream", name)
                : Results.NotFound());
    }

    private static RunnerPrincipal? GatePrincipal(HttpContext context, AccessSecurityStore security)
    {
        var principal = context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] as RunnerPrincipal;
        if (principal is null && context.Request.Headers.Authorization.ToString()
            is { } authorization && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            principal = security.AuthenticateRunner(authorization[7..].Trim());
        return principal?.Scopes.Contains(RunnerScopes.Gate) == true ? principal : null;
    }

    private static IResult WithLease(HttpContext context, AccessSecurityStore security,
        PullGateStore store, string runId, Func<string, string, IResult> action)
    {
        var principal = GatePrincipal(context, security);
        if (principal is null) return Results.Unauthorized();
        var lease = context.Request.Headers["X-Gate-Lease"].ToString();
        return Safe(() => action(principal.RunnerId, lease));
    }

    private static IResult Safe(Func<IResult> action)
    {
        try { return action(); }
        catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
    }
}
