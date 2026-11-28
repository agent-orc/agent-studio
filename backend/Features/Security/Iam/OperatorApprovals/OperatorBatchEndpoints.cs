namespace AgentStudio.Security;

public static class OperatorBatchEndpoints
{
    public const string SessionPath = "/api/auth/operator-session/batches";
    public static bool IsSessionRoute(string method, string path)
        => HttpMethods.IsPost(method) && path.Equals(SessionPath, StringComparison.OrdinalIgnoreCase)
           || HttpMethods.IsGet(method) && path.StartsWith(SessionPath + "/", StringComparison.OrdinalIgnoreCase)
               && !path[(SessionPath.Length + 1)..].Contains('/');

    public static void MapOperatorBatchSessionEndpoints(this WebApplication app)
    {
        app.MapPost(SessionPath, async (HttpContext context, AccessSecurityStore store, OperatorBatchService batches,
            OperatorApprovalNotifier notifier) =>
        {
            var (request, refusal) = await OperatorSignInEndpoints.ReadBoundedAsync<SubmitOperatorBatchRequest>(context, 64 * 1024);
            if (refusal is not null) return refusal;
            return AccessSecurityEndpoints.Execute(() =>
            {
                var principal = Authenticate(context, store);
                var batch = batches.Submit(principal, request!);
                notifier.EnqueueBatch(batch);
                return Results.Ok(batch);
            });
        }).RequirePermission(IamPermissions.ProjectCardsWrite);
        app.MapGet(SessionPath + "/{id}", (string id, HttpContext context, AccessSecurityStore store) => AccessSecurityEndpoints.Execute(() =>
        {
            var principal = Authenticate(context, store);
            return Results.Ok(store.ListOperatorBatches().FirstOrDefault(batch => batch.Id == id
                && batch.SessionId == principal.TokenId && batch.PrincipalId == principal.AccountId)
                ?? throw new SecurityOperationException(404, "batch-not-found", "Batch not found."));
        })).RequirePermission(IamPermissions.ProjectRead);
    }

    private static ServiceAccountPrincipal Authenticate(HttpContext context, AccessSecurityStore store)
    {
        context.Response.Headers.CacheControl = "no-store";
        var header = context.Request.Headers.Authorization.ToString();
        var bearer = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : null;
        var principal = store.AuthenticateOperatorSession(bearer, recordUse: false)
            ?? throw new SecurityOperationException(401, "operator-session-required", "A live operator session is required.");
        if (principal.Operator is null)
            throw new SecurityOperationException(403, "operator-session-required", "An operator session is required.");
        if (principal.BoundKeyThumbprint is { } thumb && context.Items["OperatorSession.ProofThumbprint"] as string != thumb
            && store.ValidateOperatorProof(context.Request, bearer!, thumb) is { } refusal)
            throw new SecurityOperationException(401, refusal, "A valid device proof is required.");
        store.RecordOperatorSessionUse(bearer!, context.Connection.RemoteIpAddress?.ToString());
        return principal;
    }

    public static void MapOperatorBatchOwnerEndpoints(this RouteGroupBuilder iam)
    {
        iam.MapGet("/operator-batches", (HttpContext context, AccessSecurityStore store) => AccessSecurityEndpoints.Execute(() =>
        {
            IamEndpoints.Require(context, store, IamPermissions.AccessRead);
            return Results.Ok(store.ListOperatorBatches());
        })).RequirePermission(IamPermissions.AccessRead);
        iam.MapPost("/operator-batches/{id}/decide", async (string id, DecideOperatorBatchRequest request, HttpContext context,
            AccessSecurityStore store, OperatorBatchService batches, CancellationToken ct) =>
        {
            try
            {
                var owner = AccessSecurityEndpoints.RequireHuman(context, store);
                return Results.Ok(await batches.DecideAsync(id, request, owner, context, ct));
            }
            catch (SecurityOperationException exception)
            {
                return AccessSecurityEndpoints.Execute(() => throw exception);
            }
        }).RequirePermission(IamPermissions.AccessManage);
    }
}
