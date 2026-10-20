using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Projects;

public static class ProjectOperatorDashboardEndpoints
{
    public static void MapProjectOperatorDashboardEndpoints(this WebApplication app)
    {
        app.MapGet("/api/environment/flow", (EnvironmentFlowService flow) =>
            Results.Ok(flow.Build()));

        app.MapPost("/api/deploy/gate-releases", (GateArtifactRequest request,
            ProjectEnvironmentService environments) => QuotaRefusable(() =>
            environments.RegisterGateArtifact(request.Project, request.Artifact, request.Commit)
            && environments.QueueRelease(request.Project, request.Artifact, request.Commit)
                ? Results.Accepted() : Results.BadRequest(new { error = "Gate artifact or environment declarations are invalid." })))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        app.MapGet("/api/projects/{projectName}/environments", (string projectName,
            ProjectEnvironmentService environments) => environments.Read(projectName) is { } value
                ? Results.Ok(value) : Results.NotFound());

        app.MapPost("/api/projects/{projectName}/environments/releases", (string projectName,
            ReleaseArtifactRequest request, ProjectEnvironmentService environments) => QuotaRefusable(() =>
            environments.QueueRelease(projectName, request.Artifact, request.Commit)
                ? Results.Accepted() : Results.BadRequest(new { error = "Artifact, commit, or stage and production declarations are invalid." })))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        app.MapPost("/api/projects/{projectName}/environments/promotions/approve", (
            HttpContext context, string projectName, PromotionDecisionRequest request, ProjectEnvironmentService environments) =>
            QuotaRefusable(() => environments.Decide(projectName, request.Artifact, true,
                context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                    ? human.User.Id : "local-operator")
                ? Results.Ok() : Results.Conflict(new { error = "Promotion is not pending." })))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        app.MapPost("/api/projects/{projectName}/environments/promotions/reject", (
            HttpContext context, string projectName, PromotionDecisionRequest request, ProjectEnvironmentService environments) =>
            environments.Decide(projectName, request.Artifact, false,
                context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                    ? human.User.Id : "local-operator")
                ? Results.Ok() : Results.Conflict(new { error = "Promotion is not pending." }))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        app.MapPost("/api/projects/{projectName}/environments/{environmentId}/rollback", (
            HttpContext context, string projectName, string environmentId, ProjectEnvironmentService environments) =>
            QuotaRefusable(() => environments.Rollback(projectName, environmentId,
                context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                    ? human.User.Id : "local-operator")
                ? Results.Accepted() : Results.Conflict(new { error = "No previous artifact is available." })))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        app.MapPost("/api/projects/{projectName}/environments/{environmentId}/stop", (
            HttpContext context, string projectName, string environmentId, ProjectEnvironmentService environments) =>
            environments.Stop(projectName, environmentId,
                context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                    ? human.User.Id : "local-operator")
                ? Results.Ok() : Results.Conflict(new { error = "The environment is not deployed or is already stopped." }))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        app.MapPost("/api/deploy/jobs/claim", (HttpContext context, DeployClaimRequest request,
            ProjectEnvironmentService environments, IConfiguration configuration) =>
            SecurityProfiles.IsNetworked(configuration) &&
            (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is not RunnerPrincipal principal
             || principal.RunnerId != request.HostId)
                ? Results.Forbid() : Results.Ok(environments.Claim(request.HostId)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);

        app.MapPost("/api/deploy/jobs/{project}/{jobId}/report", (string project, string jobId,
            DeployReportRequest request, ProjectEnvironmentService environments) =>
            environments.Report(project, jobId, request) ? Results.Ok() : Results.Conflict())
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);

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

    private static IResult QuotaRefusable(Func<IResult> action)
    {
        try { return action(); }
        catch (DeployQuotaRefusedException ex) { return Results.Conflict(new { error = ex.Message, reason = "quota" }); }
    }
}
