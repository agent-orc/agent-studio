using AgentStudio.Registry;
using AgentStudio.Security;

namespace AgentStudio.Projects;

public static class EnvironmentHealthEndpoints
{
    public static void MapEnvironmentHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/environment/health", (HttpContext context, EnvironmentHealthService health, ProjectRegistry registry) =>
        {
            var overview = health.Overview();
            // A signed-in account sees only the environments of projects its
            // bindings grant this read on (the request's projected principal).
            return context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal principal
                ? Results.Ok(overview with
                {
                    Environments = overview.Environments
                        .Where(environment => ProjectAccessAuthorization.Allows(principal.User, environment.Project, registry))
                        .ToArray(),
                })
                : Results.Ok(overview);
        });

        app.MapGet("/api/projects/{projectName}/environments/health", (string projectName,
            EnvironmentHealthService health) => health.Read(projectName) is { } value
                ? Results.Ok(value) : Results.NotFound(new { error = $"Unknown project '{projectName}'" }));

        app.MapGet("/api/projects/{projectName}/incident-webhook", (string projectName,
            IncidentWebhookService webhooks, ProjectRegistry registry) =>
            registry.FindByIdOrDisplayName(projectName) is not { } project ? Results.NotFound()
                : Results.Ok(webhooks.Get(project.DisplayName)))
            .RequirePermission(IamPermissions.ProjectSettingsWrite);

        app.MapPut("/api/projects/{projectName}/incident-webhook", (string projectName,
            IncidentWebhookRequest request, IncidentWebhookService webhooks, ProjectRegistry registry) =>
        {
            if (registry.FindByIdOrDisplayName(projectName) is not { } project) return Results.NotFound();
            try { return Results.Ok(webhooks.Set(project.DisplayName, request.Url, request.StudioBaseUrl)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequirePermission(IamPermissions.ProjectSettingsWrite);

        app.MapPost("/api/projects/{projectName}/incident-webhook/test", async Task<IResult> (string projectName,
            IncidentWebhookService webhooks, ProjectRegistry registry, TimeProvider clock, CancellationToken ct) =>
        {
            if (registry.FindByIdOrDisplayName(projectName) is not { } project) return Results.NotFound();
            if (!webhooks.Get(project.DisplayName).Configured) return Results.BadRequest(new { error = "Configure a webhook first." });
            var sent = await webhooks.DeliverAsync(project.DisplayName, "test", "production", "Production",
                "test-message", clock.GetUtcNow(), null, ct);
            return sent ? Results.Ok(new { delivered = true })
                : Results.Problem("The test message could not be delivered.", statusCode: 502);
        }).RequirePermission(IamPermissions.ProjectSettingsWrite);
    }
}
