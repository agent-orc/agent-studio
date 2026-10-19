using AgentStudio.Deploy;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Projects;

public static class ProjectOperatorDashboardEndpoints
{
    public static void MapProjectOperatorDashboardEndpoints(this WebApplication app)
    {
        app.MapGet("/api/environment/flow", (EnvironmentFlowService flow) =>
            Results.Ok(flow.Build()));

        // BD-1: a green integration build becomes an immutable release and
        // starts the automatic Staging phase of the standard lifecycle.
        app.MapPost("/api/deploy/gate-releases", (GateArtifactRequest request,
            ProjectEnvironmentService environments, DeployService deploy) =>
            environments.RegisterGateArtifact(request.Project, request.Artifact, request.Commit)
            && deploy.CreateRelease(request.Project, request.Artifact, request.Commit, "release-gate") is
                { Kind: DeployOutcomeKind.Ok } created
                ? Results.Accepted(value: created.Value)
                : Results.BadRequest(new { error = "Gate artifact or environment declarations are invalid." }))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        app.MapGet("/api/projects/{projectName}/environments", (string projectName,
            ProjectEnvironmentService environments) => environments.Read(projectName) is { } value
                ? Results.Ok(value) : Results.NotFound());

        app.MapPost("/api/projects/{projectName}/environments/releases", (HttpContext context, string projectName,
            ReleaseArtifactRequest request, DeployService deploy, IConfiguration configuration) =>
            deploy.CreateRelease(projectName, request.Artifact, request.Commit,
                DeployEndpoints.Actor(context, projectName, configuration).Id) is { Kind: DeployOutcomeKind.Ok } created
                ? Results.Accepted(value: created.Value)
                : Results.BadRequest(new { error = "Artifact, commit, or stage and production declarations are invalid." }))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        // Production approval follows the BD-1 standard lifecycle: Owner or
        // Maintainer only, on these AGT-3176 routes as on the deploy routes.
        app.MapPost("/api/projects/{projectName}/environments/promotions/approve", (
            HttpContext context, string projectName, PromotionDecisionRequest request, ProjectEnvironmentService environments,
            DeployService deploy, IConfiguration configuration) =>
            DecidePromotion(context, projectName, request, approve: true, environments, deploy, configuration))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        app.MapPost("/api/projects/{projectName}/environments/promotions/reject", (
            HttpContext context, string projectName, PromotionDecisionRequest request, ProjectEnvironmentService environments,
            DeployService deploy, IConfiguration configuration) =>
            DecidePromotion(context, projectName, request, approve: false, environments, deploy, configuration))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        app.MapPost("/api/projects/{projectName}/environments/{environmentId}/rollback", (
            HttpContext context, string projectName, string environmentId, ProjectEnvironmentService environments,
            DeployService deploy, IConfiguration configuration) =>
        {
            var actor = DeployEndpoints.Actor(context, projectName, configuration);
            if (!actor.IsApprover && deploy.Environments(projectName)?.FirstOrDefault(item => item.Id == environmentId)
                    is { } environment && StandardLifecyclePolicy.RequiresApproval(environment))
                return ApprovalRequired();
            if (!environments.Rollback(projectName, environmentId, actor.Id))
                return Results.Conflict(new { error = "No previous artifact is available." });
            deploy.SyncAfterHostCall(projectName);
            return Results.Accepted();
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        app.MapPost("/api/deploy/jobs/claim", (HttpContext context, DeployClaimRequest request,
            ProjectEnvironmentService environments, DeployService deploy, IConfiguration configuration) =>
        {
            if (SecurityProfiles.IsNetworked(configuration) &&
                (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is not RunnerPrincipal principal
                 || principal.RunnerId != request.HostId))
                return Results.Forbid();
            var job = environments.Claim(request.HostId);
            if (job is not null) deploy.SyncAfterHostCall(job.Project);
            return Results.Ok(job);
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);

        app.MapPost("/api/deploy/jobs/{project}/{jobId}/report", (string project, string jobId,
            DeployReportRequest request, ProjectEnvironmentService environments, DeployService deploy) =>
        {
            if (!environments.Report(project, jobId, request)) return Results.Conflict();
            deploy.SyncAfterHostCall(project);
            return Results.Ok();
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);

        app.MapGet("/api/projects/{projectName}/throughput", (
            string projectName,
            ProjectThroughputService throughput) =>
        {
            var summary = throughput.Build(projectName);
            return summary is null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(summary);
        });

        app.MapGet("/api/projects/{projectName}/deployment/summary", (
            string projectName,
            ProjectDeploymentSummaryService deployments) =>
        {
            var summary = deployments.Build(projectName);
            return summary is null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(summary);
        });

        app.MapPost("/api/projects/{projectName}/deployment/compile", (
            string projectName,
            CompileDeploymentPromptRequest request,
            ProjectDeploymentSummaryService deployments,
            ProjectDeploymentCompiler compiler) =>
        {
            if (deployments.Build(projectName) is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            return Results.Ok(compiler.Compile(request.Prompt));
        });

        app.MapGet("/api/projects/{projectName}/visual-evidence", (
            string projectName,
            bool? refresh,
            ProjectVisualEvidenceService evidence) =>
        {
            var queue = evidence.Build(projectName, refresh == true);
            return queue is null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(queue);
        });

        app.MapPost("/api/projects/{projectName}/visual-evidence/{itemId}/acknowledge", (
            string projectName,
            string itemId,
            ProjectVisualEvidenceService evidence) =>
        {
            var item = evidence.Acknowledge(projectName, itemId);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });
    }

    private static IResult DecidePromotion(HttpContext context, string project, PromotionDecisionRequest request,
        bool approve, ProjectEnvironmentService environments, DeployService deploy, IConfiguration configuration)
    {
        var actor = DeployEndpoints.Actor(context, project, configuration);
        if (!actor.IsApprover) return ApprovalRequired();
        if (!environments.Decide(project, request.Artifact, approve, actor.Id))
            return Results.Conflict(new { error = "Promotion is not pending." });
        deploy.SyncAfterHostCall(project);
        return Results.Ok();
    }

    private static IResult ApprovalRequired() => Results.Json(
        new { error = $"Production approval requires the {StandardLifecyclePolicy.ApprovalRoles} role." },
        statusCode: StatusCodes.Status403Forbidden);
}
