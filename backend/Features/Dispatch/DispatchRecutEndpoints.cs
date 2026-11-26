using AgentStudio.Registry;
using AgentStudio.Security;

namespace AgentStudio.Dispatch;

public static class DispatchRecutEndpoints
{
    public static void MapDispatchRecutEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{project}/dispatch/recuts", (string project, HttpContext context,
            ProjectRegistry projects, DispatchStore store) =>
        {
            var registered = projects.FindByIdOrDisplayName(project);
            if (registered is null) return Results.NotFound();
            if (!ProjectAccessAuthorization.AllowsTasks(context, [registered.DisplayName], IamPermissions.ProjectRead, projects))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(store.ReadRecuts(registered.DisplayName));
        }).RequirePermission(IamPermissions.ProjectRead);

        app.MapPost("/api/projects/{project}/dispatch/recuts/waves/{wave}", async (string project, string wave,
            DispatchRecutRequest request, HttpContext context, ProjectRegistry projects, DispatchRecutService service) =>
        {
            var registered = projects.FindByIdOrDisplayName(project);
            if (registered is null) return Results.NotFound();
            if (!ProjectAccessAuthorization.AllowsTasks(context, [registered.DisplayName], IamPermissions.ProjectCardsWrite, projects))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            using var client = Client(context);
            try { return Results.Ok(await service.PlanAsync(registered.DisplayName, registered.WorkspaceId, wave,
                request, new DispatchRecutTaskApi(client, registered.Id,
                    DispatchRecutService.ReceiptId(registered.DisplayName, wave, request.Apply)), context.RequestAborted)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (HttpRequestException) { return Results.Conflict(new { error = "The task API refused a recut operation. The receipt retains recovery and undo information." }); }
        }).RequireStudioRole(StudioRoles.Owner).RequirePermission(IamPermissions.ProjectCardsWrite);

        app.MapPost("/api/projects/{project}/dispatch/recuts/{id}/undo", async (string project, string id,
            HttpContext context, ProjectRegistry projects, DispatchRecutService service) =>
        {
            var registered = projects.FindByIdOrDisplayName(project);
            if (registered is null) return Results.NotFound();
            if (!ProjectAccessAuthorization.AllowsTasks(context, [registered.DisplayName], IamPermissions.ProjectCardsWrite, projects))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            using var client = Client(context);
            try { return Results.Ok(await service.UndoAsync(registered.DisplayName, id,
                new DispatchRecutTaskApi(client, registered.Id, id), context.RequestAborted)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (HttpRequestException) { return Results.Conflict(new { error = "The task API refused an undo operation. Retry the saved undo receipt." }); }
        }).RequireStudioRole(StudioRoles.Owner).RequirePermission(IamPermissions.ProjectCardsWrite);
    }

    internal static HttpClient Client(HttpContext context)
    {
        // Pin the connection to this listener. Retain the original host for TLS
        // certificate validation and the browser origin checks; never resolve it.
        var address = context.Connection.LocalIpAddress ?? System.Net.IPAddress.Loopback;
        var tls = context.Features.Get<Microsoft.AspNetCore.Http.Features.ITlsConnectionFeature>() is not null;
        var uri = new UriBuilder(tls ? "https" : "http", context.Request.Host.Host, context.Connection.LocalPort).Uri;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            ConnectCallback = async (_, ct) =>
            {
                var socket = new System.Net.Sockets.Socket(address.AddressFamily, System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new System.Net.IPEndPoint(address, context.Connection.LocalPort), ct);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        };
        var client = new HttpClient(handler)
            { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Host = context.Request.Host.Value;
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", context.Request.Scheme + "://" + context.Request.Host.Value);
        if (!tls && context.Request.IsHttps) client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        foreach (var name in new[] { "Authorization", "Cookie", "X-Client-Id", "X-CSRF-Token" })
            if (context.Request.Headers.TryGetValue(name, out var value)) client.DefaultRequestHeaders.TryAddWithoutValidation(name, value.ToArray());
        return client;
    }
}
