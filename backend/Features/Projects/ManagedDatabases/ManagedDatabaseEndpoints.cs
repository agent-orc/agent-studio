using AgentStudio.Deploy;
using AgentStudio.Secrets;
using AgentStudio.Security;

namespace AgentStudio.Projects;

/// <summary>
/// AGT-3877 routes of the Bereitstellung area. Reads show each managed
/// database with its credential reference, never a value. Deleting data is a
/// separate owner action: the caller repeats the database name as confirmation,
/// and only a database no deploy declares any more can go. AGT-3878 adds
/// operations: a signed-in user starts a backup, a verification restore into a
/// scratch database, or a restore (typing the target's name); a production
/// restore waits until an Owner approves it.
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

        group.MapPost("/{name}/operations", (HttpContext context, string projectName, string environmentId, string name,
            ManagedDatabaseOperationRequest? request, ProjectEnvironmentService environments, AccessSecurityStore security) =>
        {
            var user = DeployApprovalAuthority.SignedInUser(context, security);
            if (user is not { Disabled: false, MustChangePassword: false })
                return Results.Json(new { error = "Database operations require a signed-in user." },
                    statusCode: StatusCodes.Status403Forbidden);
            var decision = environments.RequestDatabaseOperation(projectName, environmentId, name,
                request ?? new(null, null, null, null), user.Id);
            return decision.Operation is { } operation ? Results.Accepted(value: new { operation = View(operation) }) : Failure(decision);
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        foreach (var (verb, approve) in new[] { ("approve", true), ("reject", false) })
        {
            group.MapPost("/{name}/operations/{operationId}/" + verb, (HttpContext context, string projectName,
                string environmentId, string name, string operationId, ProjectEnvironmentService environments,
                AccessSecurityStore security) =>
            {
                var user = DeployApprovalAuthority.SignedInUser(context, security);
                if (user is not { Disabled: false, MustChangePassword: false, Role: StudioRoles.Owner })
                    return Results.Json(new { error = "Approving or rejecting a production restore requires a signed-in Owner." },
                        statusCode: StatusCodes.Status403Forbidden);
                var decision = environments.DecideDatabaseOperation(projectName, environmentId, name, operationId, approve, user.Id);
                return decision.Operation is { } operation ? Results.Ok(new { operation = View(operation) }) : Failure(decision);
            }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
        }
    }

    private static ManagedDatabaseOperationView View(ManagedDatabaseOperationRecord operation)
        => ManagedDatabaseOperationPolicy.Views([operation], operation.EnvironmentId, operation.SourceDatabase)[0];

    private static IResult Failure(ManagedDatabaseOperationDecision decision) => decision.Outcome switch
    {
        ManagedDatabaseOperationOutcome.NotFound => Results.NotFound(new { error = decision.Error }),
        ManagedDatabaseOperationOutcome.Invalid => Results.BadRequest(new { error = decision.Error }),
        _ => Results.Conflict(new { error = decision.Error }),
    };

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
