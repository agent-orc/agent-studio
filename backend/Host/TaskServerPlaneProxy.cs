using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Magazine;
using AgentStudio.Personas;
using AgentStudio.Registry;
using AgentStudio.Tasks;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Host;

/// <summary>
/// Transitional same-origin proxy for the versioned Task Server plane. When a
/// standalone base URL is configured, OrchestratorApi does not map any local
/// /api/v1 owner endpoints.
/// </summary>
public static class TaskServerPlaneProxy
{
    public const string ClientName = "standalone-task-server";

    // ForwardAsync always chooses explicitly. Requests made by backend services
    // with this named client have no option and retain their own edge identity.
    private static readonly HttpRequestOptionsKey<bool> UseEdgeCredential = new("TaskServerPlaneProxy.UseEdgeCredential");

    private static readonly HashSet<string> HopByHopHeaders = new(
        [
            "Connection",
            "Keep-Alive",
            "Proxy-Authenticate",
            "Proxy-Authorization",
            "TE",
            "Trailer",
            "Transfer-Encoding",
            "Upgrade",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsConfigured(IConfiguration configuration)
        => TryGetBaseUri(configuration, out _);

    public static void AddTaskServerPlaneProxy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (!TryGetBaseUri(configuration, out var baseUri))
        {
            services.AddHttpClient(ClientName);
            return;
        }

        services.AddHttpClient(ClientName, client =>
        {
            client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromSeconds(100);
            client.DefaultRequestHeaders.Add(
                TaskServerProtocol.HeaderName,
                TaskServerProtocol.Current.ToString());
            client.DefaultRequestHeaders.Add(
                TaskServerProtocol.ClientVersionHeaderName,
                typeof(TaskServerPlaneProxy).Assembly.GetName().Version?.ToString(3)
                ?? "unknown");
        }).AddHttpMessageHandler(() => new ReloadingProxyCredentialHandler(configuration));
    }

    public static bool MapTaskServerPlaneProxy(this WebApplication app)
    {
        if (!IsConfigured(app.Configuration)) return false;

        if (SecurityProfiles.IsPublicDemo(app.Configuration))
        {
            app.MapMethods(
                "/api/v1/{**path}",
                ["GET", "HEAD", "OPTIONS"],
                ForwardAsync)
                .WithMetadata(TaskServerPlaneProxyEndpoint.Instance)
                .RequirePermissions(IamPermissions.WorkspaceRead, IamPermissions.WorkspaceOperate);
            var unsafeProxy = app.MapMethods(
                "/api/v1/{**path}",
                ["POST", "PUT", "PATCH", "DELETE"],
                ForwardAsync)
                .WithMetadata(TaskServerPlaneProxyEndpoint.Instance)
                .RequirePermissions(IamPermissions.WorkspaceRead, IamPermissions.WorkspaceOperate);
            unsafeProxy.Add(endpoint =>
            {
                endpoint.Metadata.Add(new ExecutionRouteMetadata(ExecutionAdmissionPath.PostStep));
                endpoint.Metadata.Add(new PublicDemoExecutionExpectationMetadata(
                    ExecutionAdmissionPath.PostStep,
                    ExecutionAdmissionPolicy.ExecutionDisabledCode));
            });
            return true;
        }

        // Card authors address these writes by body project or task key.
        // The handler checks the resolved card project before using the edge credential.
        var cardWrite = new RequiredPermissionMetadata(IamPermissions.ProjectCardsWrite, ProjectBindingsSuffice: true);
        app.MapPost("/api/v1/studio/tasks", ForwardAsync)
            .WithMetadata(TaskServerPlaneProxyEndpoint.Instance, cardWrite);
        app.MapPost("/api/v1/projects/{projectId}/tasks", ForwardAsync)
            .WithMetadata(TaskServerPlaneProxyEndpoint.Instance, cardWrite);
        app.MapPut("/api/v1/projects/{projectId}/tasks/{taskId}/magazine-deliverable", ForwardAsync)
            .WithMetadata(TaskServerPlaneProxyEndpoint.Instance, cardWrite);
        app.MapPost("/api/v1/projects/{projectId}/tasks/{taskId}/magazine-deliverable/preview", ForwardAsync)
            .WithMetadata(TaskServerPlaneProxyEndpoint.Instance, cardWrite);

        app.MapMethods(
            "/api/v1/{**path}",
            ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"],
            ForwardAsync)
            .WithMetadata(TaskServerPlaneProxyEndpoint.Instance)
            .RequirePermissions(IamPermissions.WorkspaceRead, IamPermissions.WorkspaceOperate);
        return true;
    }

    internal static bool TryGetBaseUri(
        IConfiguration configuration,
        out Uri? baseUri)
    {
        var configured = configuration["TaskServer:BaseUrl"]?.Trim();
        if (string.IsNullOrEmpty(configured))
        {
            baseUri = null;
            return false;
        }
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "TaskServer:BaseUrl must be an absolute HTTP or HTTPS URL when set.");
        }
        baseUri = new Uri(parsed.ToString().TrimEnd('/') + "/", UriKind.Absolute);
        return true;
    }

    private static async Task ForwardAsync(HttpContext context)
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var actingRole = ActingRole(context, configuration);
        // A bearer is always forwarded as its own principal, even alongside a
        // browser cookie. Only a completed local authorization may use the edge.
        var useEdgeCredential = !context.Request.Headers.ContainsKey("Authorization")
            && (SecurityProfiles.IsNetworked(configuration)
                ? context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal
                    && context.Items[AccessSecurityMiddleware.RequestAccessItem] is RequestAccess
                : SecurityProfiles.IsLocal(configuration) && actingRole is not null);
        var client = context.RequestServices
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(ClientName);
        var path = context.Request.Path.Value!;
        var author = await AuthorizeCardWriteAsync(context, client, path, useEdgeCredential);
        if (author.Refusal is { } refusal)
        {
            await refusal.ExecuteAsync(context);
            return;
        }
        var target = (author.Route ?? path) + context.Request.QueryString;
        using var request = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            target);
        request.Options.Set(UseEdgeCredential, useEdgeCredential);

        if (author.Content is not null)
            request.Content = author.Content;
        else if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            request.Content = new StreamContent(context.Request.Body);

        foreach (var (name, values) in context.Request.Headers)
        {
            // Consumer metadata must belong to the same principal as the bearer.
            // The caller path keeps its own metadata; the edge supplies its own.
            if (useEdgeCredential
                && (string.Equals(name, "X-Principal-Consumer-Id", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "X-Principal-Consumer-Proof", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (context.Request.Path.StartsWithSegments("/api/v1/management/device-logins")
                && string.Equals(name, "X-Actor-Id", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!ForwardsRequestHeader(name))
            {
                continue;
            }
            if (!request.Headers.TryAddWithoutValidation(name, values.ToArray())
                && request.Content is not null)
            {
                request.Content.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
        }

        request.Headers.TryAddWithoutValidation("X-Studio-Public-Origin",
            context.Request.Scheme + "://" + context.Request.Host);

        if (useEdgeCredential && actingRole is not null)
            request.Headers.TryAddWithoutValidation(CredentialViewProtocol.ActingRoleHeader, actingRole);
        if (author.Actor is not null)
            request.Headers.TryAddWithoutValidation(MagazineDeliverableContract.EdgeAuthorHeader, author.Actor);
        if (context.Request.Path.StartsWithSegments("/api/v1/management/device-logins"))
        {
            var actor = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                ? human.User.Id : DefaultClientIdentity.Id;
            request.Headers.TryAddWithoutValidation("X-Actor-Id", actor);
        }

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var (name, values) in response.Headers)
                if (!HopByHopHeaders.Contains(name))
                    context.Response.Headers[name] = values.ToArray();
            foreach (var (name, values) in response.Content.Headers)
                if (!HopByHopHeaders.Contains(name)
                    && !string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    context.Response.Headers[name] = values.ToArray();
            await response.Content.CopyToAsync(
                context.Response.Body,
                context.RequestAborted);
        }
        catch (HttpRequestException exception)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(new
            {
                code = "task-server-unavailable",
                message = $"The standalone Task Server could not be reached: {exception.Message}",
            });
        }
    }

    private static readonly JsonSerializerOptions MagazineRequestJson = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
    };

    private static async Task<(IResult? Refusal, string? Actor, string? Route, HttpContent? Content)> AuthorizeCardWriteAsync(
        HttpContext context, HttpClient client, string path, bool useEdgeCredential)
    {
        // Direct Task Server bearers are checked by the Task Server. The edge
        // must perform the finer IAM check before it lends its own credential.
        if (!useEdgeCredential) return default;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var create = HttpMethods.IsPost(context.Request.Method)
            && (path.TrimEnd('/').Equals("/api/v1/studio/tasks", StringComparison.OrdinalIgnoreCase)
                || parts.Length == 5 && parts[2].Equals("projects", StringComparison.OrdinalIgnoreCase)
                    && parts[4].Equals("tasks", StringComparison.OrdinalIgnoreCase));
        var attach = parts.Length is 7 or 8 && parts[6].Equals("magazine-deliverable", StringComparison.OrdinalIgnoreCase)
            && (HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsPost(context.Request.Method));
        if (!create && !attach) return default;

        StudioCreateTaskRequest? createBody = null;
        AttachMagazineDeliverableRequest? attachBody = null;
        MagazineDeliverableRequest? magazine;
        string? project;
        string? route = null;
        context.Request.EnableBuffering();
        try
        {
            if (create)
            {
                createBody = await JsonSerializer.DeserializeAsync<StudioCreateTaskRequest>(
                    context.Request.Body, MagazineRequestJson, context.RequestAborted);
                magazine = createBody?.MagazineDeliverable;
                project = parts.Length == 5 ? parts[3] : createBody?.Project ?? createBody?.ProjectId;
            }
            else
            {
                attachBody = await JsonSerializer.DeserializeAsync<AttachMagazineDeliverableRequest>(
                    context.Request.Body, MagazineRequestJson, context.RequestAborted);
                magazine = attachBody?.MagazineDeliverable;
                project = parts[3];
            }
        }
        catch (JsonException)
        {
            return (Results.BadRequest(new { error = "The task request is invalid." }), null, null, null);
        }
        finally
        {
            context.Request.Body.Position = 0;
        }

        if (attach && magazine is null)
            return (Results.BadRequest(new { error = "A Magazine deliverable is required." }), null, null, null);
        var services = context.RequestServices;
        var projects = services.GetRequiredService<ProjectRegistry>();
        if (attach)
        {
            // Resolve the canonical card project from the data authority and
            // address the write to that project as well.
            try
            {
                var taskPath = "/" + string.Join('/', parts.Take(6));
                using var response = await client.GetAsync(taskPath, context.RequestAborted);
                if (!response.IsSuccessStatusCode) return (Results.StatusCode((int)response.StatusCode), null, null, null);
                var task = await response.Content.ReadFromJsonAsync<TaskDto>(context.RequestAborted);
                if (task is null) return (Results.NotFound(), null, null, null);
                project = task.ProjectId;
                parts[3] = Uri.EscapeDataString(project);
                parts[5] = Uri.EscapeDataString(task.TaskId);
                route = "/" + string.Join('/', parts);
            }
            catch (HttpRequestException)
            {
                return (Results.StatusCode(StatusCodes.Status502BadGateway), null, null, null);
            }
        }
        if (string.IsNullOrWhiteSpace(project))
            return (Results.BadRequest(new { error = "A project id is required." }), null, null, null);
        ProjectDto[]? directory = null;
        var rewriteBody = false;
        if (create && context.Items[AccessSecurityMiddleware.RequestAccessItem] is RequestAccess)
        {
            directory = await ReadTaskServerProjectsAsync(client, context.RequestAborted);
            if (directory is null) return (Results.StatusCode(StatusCodes.Status502BadGateway), null, null, null);
            var canonical = directory.FirstOrDefault(p => p.ProjectId.Equals(project, StringComparison.Ordinal))
                ?? directory.FirstOrDefault(p => p.Name.Equals(project, StringComparison.OrdinalIgnoreCase));
            if (canonical is null) return (Results.NotFound(), null, null, null);
            project = canonical.ProjectId;
            if (parts.Length == 5)
            {
                parts[3] = Uri.EscapeDataString(project);
                route = "/" + string.Join('/', parts);
            }
            else
            {
                createBody = createBody! with { Project = project };
                rewriteBody = true;
            }
        }
        if (!ProjectAccessAuthorization.AllowsTasks(context, [project], IamPermissions.ProjectCardsWrite, projects))
            return (Results.StatusCode(StatusCodes.Status403Forbidden), null, null, null);
        if (ProjectAccessAuthorization.IsProductRoleIn(context, project))
            return (Results.StatusCode(StatusCodes.Status403Forbidden), null, null, null);
        if (magazine is null)
            return (null, null, route, rewriteBody ? JsonContent.Create(createBody) : null);
        var catalogue = services.GetRequiredService<PersonaCatalogue>();
        if (MagazineDeliverableContract.Validate(magazine, id => catalogue.Find(id) is not null) is { } error)
            return (Results.BadRequest(new { error }), null, null, null);

        // The standalone directory owns its native IDs. Workspace-wide
        // Magazine rights cover installation and workspace audiences, and
        // the store checks that the requested workspace exists at write time.
        MagazineScope.TryParse(magazine.Scope, out var audience);
        if (audience.Kind == MagazineScopeKind.Project)
        {
            directory ??= await ReadTaskServerProjectsAsync(client, context.RequestAborted);
            if (directory is null) return (Results.StatusCode(StatusCodes.Status502BadGateway), null, null, null);
            var canonical = directory.FirstOrDefault(p => p.ProjectId.Equals(audience.Id, StringComparison.OrdinalIgnoreCase))
                ?? directory.FirstOrDefault(p => p.TaskKeyPrefix.Equals(audience.Id, StringComparison.OrdinalIgnoreCase));
            if (canonical is null) return (Results.NotFound(), null, null, null);
            audience = audience.WithId(canonical.ProjectId);
            rewriteBody = true;
        }
        var authorized = MagazineAccess.AuthorizeWrite(context, audience);
        if (authorized.Refusal is not null) return (authorized.Refusal, null, null, null);
        HttpContent? body = null;
        if (rewriteBody)
        {
            // Send the IDs we checked so an alias change cannot redirect the write.
            magazine = magazine with { Scope = audience.ToString() };
            body = createBody is not null
                ? JsonContent.Create(createBody with { MagazineDeliverable = magazine })
                : JsonContent.Create(attachBody! with { MagazineDeliverable = magazine });
        }
        return (null, ResearchMagazineDeliverable.Actor(context), route, body);
    }

    private static async Task<ProjectDto[]?> ReadTaskServerProjectsAsync(HttpClient client, CancellationToken ct)
    {
        try { return await client.GetFromJsonAsync<ProjectDto[]>("/api/v1/projects", ct) ?? []; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { return null; }
    }

    internal static bool ForwardsRequestHeader(string name)
        => !HopByHopHeaders.Contains(name)
           && !string.Equals(name, "X-Studio-Public-Origin", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(name, CredentialViewProtocol.ActingRoleHeader, StringComparison.OrdinalIgnoreCase)
           && !string.Equals(name, MagazineDeliverableContract.EdgeAuthorHeader, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The Studio role this edge authenticated, forwarded so the Task Server can scope
    /// credential metadata (I8). The networked profile derives it from the principal's role
    /// bindings (<see cref="RequestAccess.TaskServerRole"/>), never the legacy summary; the local
    /// profile mirrors the loopback local-default operator of the local management routes.
    /// </summary>
    internal static string? ActingRole(HttpContext context, IConfiguration configuration)
    {
        if (SecurityProfiles.IsNetworked(configuration))
            return context.Items[AccessSecurityMiddleware.RequestAccessItem] is RequestAccess access
                ? access.TaskServerRole()
                : null;
        if (!SecurityProfiles.IsLocal(configuration)) return null;
        var remote = context.Connection.RemoteIpAddress;
        var loopback = remote is null || System.Net.IPAddress.IsLoopback(remote);
        return loopback
               && string.Equals(context.Request.Headers["X-Client-Id"].FirstOrDefault(), DefaultClientIdentity.Id, StringComparison.Ordinal)
               && string.Equals(context.Items["ClientId"]?.ToString(), DefaultClientIdentity.Id, StringComparison.Ordinal)
            ? StudioRoles.Operator
            : null;
    }

    private static string? ReadServiceToken(IConfiguration configuration)
    {
        var direct = configuration["TaskServer:AuthToken"]?.Trim();
        var file = configuration["TaskServer:AuthTokenFile"]?.Trim();
        if (!string.IsNullOrWhiteSpace(direct) && !string.IsNullOrWhiteSpace(file))
            throw new InvalidOperationException(
                "Configure only one of TaskServer:AuthToken or TaskServer:AuthTokenFile.");
        if (!string.IsNullOrWhiteSpace(file))
        {
            var resolved = Path.GetFullPath(file);
            if (!File.Exists(resolved))
                throw new InvalidOperationException(
                    $"TaskServer:AuthTokenFile does not exist: {resolved}");
            direct = File.ReadAllText(resolved).Trim();
        }
        return string.IsNullOrWhiteSpace(direct) ? null : direct;
    }

    private sealed class ReloadingProxyCredentialHandler(IConfiguration configuration) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Passthrough must not read edge files or acquire ambient credentials,
            // consumer metadata, or rotation proof from the trusted client.
            if (request.Options.TryGetValue(UseEdgeCredential, out var useEdgeCredential) && !useEdgeCredential)
                return base.SendAsync(request, cancellationToken);

            var fileBacked = !string.IsNullOrWhiteSpace(configuration["TaskServer:AuthTokenFile"]);
            var token = ReadServiceToken(configuration);
            if (fileBacked && token is null)
                throw new InvalidOperationException("The Task Server service credential is empty.");
            if (token is not null && (fileBacked || request.Headers.Authorization is null))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var consumerId = configuration["TaskServer:ConsumerId"]?.Trim();
            if (!string.IsNullOrWhiteSpace(consumerId) && !request.Headers.Contains("X-Principal-Consumer-Id"))
                request.Headers.TryAddWithoutValidation("X-Principal-Consumer-Id", consumerId);
            if (fileBacked)
            {
                if (PrincipalConsumerProofFile.ReadForBearerFile(configuration["TaskServer:AuthTokenFile"]!) is { } proof)
                {
                    request.Headers.Remove("X-Principal-Consumer-Id");
                    request.Headers.Remove("X-Principal-Consumer-Proof");
                    request.Headers.TryAddWithoutValidation("X-Principal-Consumer-Id", proof.ConsumerId);
                    request.Headers.TryAddWithoutValidation("X-Principal-Consumer-Proof", proof.Value);
                }
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>
/// Endpoint metadata marking a route that forwards to the standalone Task
/// Server. The networked access middleware leaves bearer authentication to the
/// upstream only when routing selected an endpoint carrying this marker.
/// </summary>
public sealed class TaskServerPlaneProxyEndpoint
{
    public static readonly TaskServerPlaneProxyEndpoint Instance = new();

    private TaskServerPlaneProxyEndpoint() { }
}
