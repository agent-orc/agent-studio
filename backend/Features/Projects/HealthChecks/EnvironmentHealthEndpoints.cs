namespace AgentStudio.Projects;

public static class EnvironmentHealthEndpoints
{
    public static void MapEnvironmentHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/environment/health", (EnvironmentHealthService health) =>
            Results.Ok(health.Overview()));

        app.MapGet("/api/projects/{projectName}/environments/health", (string projectName,
            EnvironmentHealthService health) => health.Read(projectName) is { } value
                ? Results.Ok(value) : Results.NotFound(new { error = $"Unknown project '{projectName}'" }));
    }
}
