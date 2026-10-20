namespace AgentStudio.Deploy;

/// <summary>BD-1 routes for the P1 data contract (releases, deployments, status, matrix).</summary>
public static class DeployEndpoints
{
    public static void MapDeployEndpoints(this WebApplication app)
    {
        var project = app.MapGroup("/api/projects/{projectName}/deploy");

        project.MapGet("/environments", (string projectName, DeployService deploy) =>
            deploy.Environments(projectName) is { } value ? Results.Ok(value) : UnknownProject(projectName));

        project.MapGet("/status", (string projectName, DeployService deploy) =>
            deploy.Status(projectName) is { } value ? Results.Ok(value) : UnknownProject(projectName));

        project.MapGet("/releases", (string projectName, DeployService deploy) =>
            deploy.Releases(projectName) is { } value ? Results.Ok(value) : UnknownProject(projectName));

        project.MapGet("/releases/{releaseId}", (string projectName, string releaseId, DeployService deploy) =>
            deploy.ReleaseDetail(projectName, releaseId) is { } value
                ? Results.Ok(value) : Results.NotFound(new { error = $"Unknown release '{releaseId}'." }));

        project.MapGet("/deployments", (string projectName, string? environmentId, DeployService deploy) =>
            deploy.Deployments(projectName, environmentId) is { } value ? Results.Ok(value) : UnknownProject(projectName));

        project.MapGet("/deployments/{deploymentId}", (string projectName, string deploymentId, DeployService deploy) =>
            deploy.DeploymentById(projectName, deploymentId) is { } value
                ? Results.Ok(value) : Results.NotFound(new { error = $"Unknown deployment '{deploymentId}'." }));

        project.MapGet("/deployments/{deploymentId}/log", (string projectName, string deploymentId, string? step,
            DeployService deploy) =>
            deploy.Log(projectName, deploymentId, step) is { } value
                ? Results.Ok(value) : Results.NotFound(new { error = $"Unknown deployment '{deploymentId}'." }));

        project.MapGet("/audit", (string projectName, DeployService deploy) =>
            deploy.Audit(projectName) is { } value ? Results.Ok(value) : UnknownProject(projectName));

        project.MapPost("/releases/{releaseId}/deployments", (HttpContext context, string projectName,
            string releaseId, DeploymentRequest request, DeployService deploy) =>
            Respond(deploy.RequestDeployment(projectName, releaseId, request, Actor(context, projectName)), accepted: true))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        project.MapPost("/deployments/{deploymentId}/approve", (HttpContext context, string projectName,
            string deploymentId, DeployService deploy) =>
            Respond(deploy.Approve(projectName, deploymentId, Actor(context, projectName))))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        project.MapPost("/deployments/{deploymentId}/reject", (HttpContext context, string projectName,
            string deploymentId, DeployService deploy) =>
            Respond(deploy.Reject(projectName, deploymentId, Actor(context, projectName))))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        project.MapPost("/deployments/{deploymentId}/cancel", (HttpContext context, string projectName,
            string deploymentId, DeployService deploy) =>
            Respond(deploy.Cancel(projectName, deploymentId, Actor(context, projectName))))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        project.MapPost("/environments/{environmentId}/redeploy", (HttpContext context, string projectName,
            string environmentId, EnvironmentActionRequest? request, DeployService deploy) =>
            Respond(deploy.Redeploy(projectName, environmentId, request?.Approve == true,
                Actor(context, projectName)), accepted: true))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        project.MapPost("/environments/{environmentId}/rollback", (HttpContext context, string projectName,
            string environmentId, EnvironmentActionRequest? request, DeployService deploy) =>
            Respond(deploy.Rollback(projectName, environmentId, request?.Approve == true,
                Actor(context, projectName)), accepted: true))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        app.MapGet("/api/deploy/matrix", (HttpContext context, DeployService deploy, ProjectRegistry projects) =>
        {
            var matrix = deploy.Matrix();
            return Results.Ok(context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                ? matrix with
                {
                    Projects = matrix.Projects
                        .Where(row => ProjectAccessAuthorization.Allows(human.User, row.ProjectId, projects)).ToArray(),
                }
                : matrix);
        });
    }

    public static DeployActor Actor(HttpContext context, string project)
        => DeployApprovalAuthority.Resolve(
            DeployApprovalAuthority.SignedInUser(context, context.RequestServices.GetRequiredService<AccessSecurityStore>()),
            project);

    private static IResult UnknownProject(string project)
        => Results.NotFound(new { error = $"Unknown project '{project}'" });

    private static IResult Respond<T>(DeployOutcome<T> outcome, bool accepted = false) => outcome.Kind switch
    {
        DeployOutcomeKind.Ok => accepted ? Results.Accepted(value: outcome.Value) : Results.Ok(outcome.Value),
        DeployOutcomeKind.NotFound => Results.NotFound(new { error = outcome.Error }),
        DeployOutcomeKind.Forbidden => Results.Json(new { error = outcome.Error }, statusCode: StatusCodes.Status403Forbidden),
        _ => Results.Conflict(new { error = outcome.Error }),
    };
}
