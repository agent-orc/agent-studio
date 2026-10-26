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
    }
}
