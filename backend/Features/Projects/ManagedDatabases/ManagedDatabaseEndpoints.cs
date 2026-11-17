using AgentStudio.Deploy;
using AgentStudio.Secrets;
using AgentStudio.Security;

namespace AgentStudio.Projects;

/// <summary>
/// AGT-3877 routes of the Bereitstellung area. Reads show each managed
/// database with its credential reference, never a value. Deleting data is a
/// separate owner action: the caller repeats the database name as confirmation,
/// and only a database no deploy declares any more can go.
/// </summary>
public static class ManagedDatabaseEndpoints
{
    public static void MapManagedDatabaseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectName}/environments/{environmentId}/databases");
        group.MapGet("", (string projectName, string environmentId, ProjectEnvironmentService environments) =>
            environments.ManagedDatabases(projectName, environmentId) is { } databases
                ? Results.Ok(new { databases })
                : Results.NotFound(new { error = "The environment is not declared." }));

        group.MapPost("/{name}/deletion", (HttpContext context, string projectName, string environmentId, string name,
            ManagedDatabaseDeletionRequest? request, ProjectEnvironmentService environments, AccessSecurityStore security) =>
        {
            var user = DeployApprovalAuthority.SignedInUser(context, security);
            if (user is not { Disabled: false, MustChangePassword: false, Role: StudioRoles.Owner })
                return Results.Json(new { error = "Deleting database data requires a signed-in Owner." },
                    statusCode: StatusCodes.Status403Forbidden);
            if (!string.Equals(request?.Confirm, name, StringComparison.Ordinal))
                return Results.BadRequest(new { error = $"Type the database name '{name}' to confirm the deletion." });
            return environments.RequestDatabaseDeletion(projectName, environmentId, name, user.Id) switch
            {
                ManagedDatabaseDeletionOutcome.Requested => Results.Accepted(),
                ManagedDatabaseDeletionOutcome.NotFound => Results.NotFound(new { error = "The database is not known for this environment." }),
                ManagedDatabaseDeletionOutcome.StillDeclared => Results.Conflict(new { error =
                    "The database is still declared by the running or an open deploy. Remove it from project.yml and deploy first." }),
                ManagedDatabaseDeletionOutcome.AlreadyRequested => Results.Conflict(new { error = "The deletion is already requested." }),
                _ => Results.Conflict(new { error = "The database data is already deleted." }),
            };
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);
    }

    /// <summary>
    /// Runs credential upkeep for a host call. A secret store that cannot be
    /// written does not fail the poll: the host keeps what runs, and a database
    /// without a credential reports the failure on its deploy.
    /// </summary>
    internal static async Task QuietlyAsync(HttpContext context, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is SecretValidationException or SecretKeyUnavailableException or IOException
            or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ManagedDatabaseEndpoints))
                .LogWarning(ex, "Managed database credentials could not be prepared; the run host keeps its databases as they are");
        }
    }
}
