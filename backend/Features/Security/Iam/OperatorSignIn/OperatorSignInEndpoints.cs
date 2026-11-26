using System.Text.Json;

namespace AgentStudio.Security;

/// <summary>
/// Terminal sign-in routes (ES-3). <c>POST /api/auth/operator-sign-in</c> and
/// <c>.../poll</c> are anonymous: the terminal holds no credential yet, so the
/// start is throttled per address and the body is bounded before binding.
/// Looking up, confirming and denying a code sit under <c>/api/auth/iam</c>
/// and need an active Owner's browser session; confirming also consumes a
/// fresh step-up.
/// </summary>
public static class OperatorSignInEndpoints
{
    public const string StartPath = "/api/auth/operator-sign-in";
    public const string PollPath = "/api/auth/operator-sign-in/poll";
    /// <summary>The Studio page that confirms a terminal sign-in; the link adds <c>?code=</c>.</summary>
    public const string ConfirmationHash = "#/terminal-sign-in";
    private const int MaxBodyBytes = 4 * 1024;

    public static void MapOperatorSignInEndpoints(this WebApplication app)
    {
        app.MapPost(StartPath, async (HttpContext context, AccessSecurityStore store) =>
        {
            var (request, refusal) = await ReadBoundedAsync<StartOperatorSignInRequest>(context);
            if (refusal is not null) return refusal;
            return AccessSecurityEndpoints.Execute(() =>
            {
                var address = context.Connection.RemoteIpAddress;
                var started = store.StartOperatorSignIn(request!, address?.ToString(), AccessSecurityEndpoints.ClientThrottleKey(address));
                var page = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/{ConfirmationHash}";
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new StartOperatorSignInResponse(
                    started.SignIn.Id, OperatorSignInCodes.Display(started.UserCode), page, $"{page}?code={started.UserCode}",
                    started.PollHandle, AccessSecurityStore.OperatorSignInPollIntervalSeconds, started.SignIn.ExpiresAt,
                    started.SignIn.KeyThumbprint));
            });
        });

        app.MapPost(PollPath, async (HttpContext context, AccessSecurityStore store) =>
        {
            var (request, refusal) = await ReadBoundedAsync<PollOperatorSignInRequest>(context);
            if (refusal is not null) return refusal;
            return AccessSecurityEndpoints.Execute(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(store.PollOperatorSignIn(request!.PollHandle, context.Request, request.Certification));
            });
        });
    }

    public static void MapOperatorSignInOwnerEndpoints(this RouteGroupBuilder iam)
    {
        var signIns = iam.MapGroup("/operator-sign-ins");

        signIns.MapPost("/lookup", (OperatorSignInCodeRequest request, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                IamEndpoints.Require(context, store, IamPermissions.AccessManage);
                var owner = AccessSecurityEndpoints.RequireHuman(context, store);
                return Results.Ok(store.LookupOperatorSignIn(request.Code, owner, context.Connection.RemoteIpAddress?.ToString()));
            })).RequirePermission(IamPermissions.AccessManage);

        signIns.MapPost("/confirm", (ConfirmOperatorSignInRequest request, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                IamEndpoints.Require(context, store, IamPermissions.AccessManage);
                var owner = AccessSecurityEndpoints.RequireHuman(context, store);
                return Results.Ok(store.ConfirmOperatorSignIn(request, owner, context.Connection.RemoteIpAddress?.ToString()));
            })).RequirePermission(IamPermissions.AccessManage);

        signIns.MapPost("/deny", (OperatorSignInCodeRequest request, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                IamEndpoints.Require(context, store, IamPermissions.AccessManage);
                store.DenyOperatorSignIn(request.Code, AccessSecurityEndpoints.RequireHuman(context, store));
                return Results.NoContent();
            })).RequirePermission(IamPermissions.AccessManage);

        iam.MapDelete("/operator-agents/{id}/devices/{deviceId}", (string id, string deviceId, HttpContext context, AccessSecurityStore store) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                store.ForgetOperatorDevice(id, deviceId, IamEndpoints.Require(context, store, IamPermissions.AccessManage));
                return Results.NoContent();
            })).RequirePermission(IamPermissions.AccessManage);
    }

    /// <summary>Reads at most 4 KiB of JSON before binding, with or without Content-Length.</summary>
    private static async Task<(T? Request, IResult? Refusal)> ReadBoundedAsync<T>(HttpContext context) where T : class
    {
        if (!context.Request.HasJsonContentType()) return (null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        if (context.Request.ContentLength > MaxBodyBytes) return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        var body = new byte[MaxBodyBytes + 1];
        var length = 0;
        while (length < body.Length)
        {
            var read = await context.Request.Body.ReadAsync(body.AsMemory(length), context.RequestAborted);
            if (read == 0) break;
            length += read;
        }
        if (length > MaxBodyBytes) return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        try
        {
            var request = JsonSerializer.Deserialize<T>(body.AsSpan(0, length), JsonSerializerOptions.Web);
            return request is null
                ? (null, Results.Json(new { error = "invalid-request", message = "The request body is empty." }, statusCode: 400))
                : (request, null);
        }
        catch (JsonException)
        {
            return (null, Results.Json(new { error = "invalid-request", message = "The request body is not valid JSON." }, statusCode: 400));
        }
    }
}
