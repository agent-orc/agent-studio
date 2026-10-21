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
            QuotaRefusable(() => environments.RegisterGateArtifact(request.Project, request.Artifact, request.Commit)
                ? ReleaseCreated(deploy.CreateRelease(request.Project, request.Artifact, request.Commit, "release-gate"),
                    "Gate artifact or environment declarations are invalid.")
                : Results.BadRequest(new { error = "Gate artifact or environment declarations are invalid." })))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        app.MapGet("/api/projects/{projectName}/environments", (string projectName,
            ProjectEnvironmentService environments) => environments.Read(projectName) is { } value
                ? Results.Ok(value) : Results.NotFound());

        app.MapPost("/api/projects/{projectName}/environments/releases", (HttpContext context, string projectName,
            ReleaseArtifactRequest request, DeployService deploy) =>
            QuotaRefusable(() => ReleaseCreated(deploy.CreateRelease(projectName, request.Artifact, request.Commit,
                DeployEndpoints.Actor(context, projectName).Id),
                "Artifact, commit, or stage and production declarations are invalid.")))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        // Production approval follows the BD-1 standard lifecycle: Owner or
        // Maintainer only, on these AGT-3176 routes as on the deploy routes,
        // and the lifecycle decides each approval again.
        app.MapPost("/api/projects/{projectName}/environments/promotions/approve", (
            HttpContext context, string projectName, PromotionDecisionRequest request, DeployService deploy) =>
            QuotaRefusable(() => DecidePromotion(context, projectName, request, approve: true, deploy)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        app.MapPost("/api/projects/{projectName}/environments/promotions/reject", (
            HttpContext context, string projectName, PromotionDecisionRequest request, DeployService deploy) =>
            QuotaRefusable(() => DecidePromotion(context, projectName, request, approve: false, deploy)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        app.MapPost("/api/projects/{projectName}/environments/{environmentId}/rollback", (
            HttpContext context, string projectName, string environmentId, DeployService deploy) =>
        {
            var actor = DeployEndpoints.Actor(context, projectName);
            if (!actor.IsApprover && deploy.Environments(projectName)?.FirstOrDefault(item => item.Id == environmentId)
                    is { } environment && StandardLifecyclePolicy.RequiresApproval(environment))
                return ApprovalRequired();
            return QuotaRefusable(() => deploy.RollbackEnvironment(projectName, environmentId, actor) switch
            {
                { Kind: DeployOutcomeKind.Ok } => Results.Accepted(),
                { Kind: DeployOutcomeKind.NotFound } => Results.Conflict(new { error = "No previous artifact is available." }),
                { Kind: DeployOutcomeKind.Forbidden } refused => Results.Json(new { error = refused.Error },
                    statusCode: StatusCodes.Status403Forbidden),
                var refused => Results.Conflict(new { error = refused.Error }),
            });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        // The stop is queued for the environment's host; the slot is freed when that host confirms it.
        app.MapPost("/api/projects/{projectName}/environments/{environmentId}/stop", (
            HttpContext context, string projectName, string environmentId, ProjectEnvironmentService environments) =>
            environments.Stop(projectName, environmentId, DeployEndpoints.Actor(context, projectName).Id)
                ? Results.Accepted() : Results.Conflict(new { error = "The environment is not deployed or is already stopped." }))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

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

    /// <summary>
    /// 202 with the release; 409 when the stored releases refuse it (an older
    /// commit than the newest release, with <c>"reason":"superseded"</c>, or an
    /// unreadable declaration snapshot); 400 for every invalid input, as before BD-1.
    /// </summary>
    private static IResult ReleaseCreated(DeployOutcome<Release> outcome, string invalid) => outcome switch
    {
        { Kind: DeployOutcomeKind.Ok } => Results.Accepted(value: outcome.Value),
        { Kind: DeployOutcomeKind.Conflict, Reason: { } reason } => Results.Conflict(new { error = outcome.Error, reason }),
        { Kind: DeployOutcomeKind.Conflict } => Results.Conflict(new { error = outcome.Error }),
        _ => Results.BadRequest(new { error = invalid }),
    };

    private static IResult DecidePromotion(HttpContext context, string project, PromotionDecisionRequest request,
        bool approve, DeployService deploy)
    {
        var actor = DeployEndpoints.Actor(context, project);
        if (!actor.IsApprover) return ApprovalRequired();
        return deploy.DecidePromotion(project, request.Artifact, approve, actor) switch
        {
            { Kind: DeployOutcomeKind.Ok } => Results.Ok(),
            { Kind: DeployOutcomeKind.NotFound } => Results.Conflict(new { error = "Promotion is not pending." }),
            { Kind: DeployOutcomeKind.Forbidden } refused => Results.Json(new { error = refused.Error },
                statusCode: StatusCodes.Status403Forbidden),
            var refused => Results.Conflict(new { error = refused.Error }),
        };
    }

    private static IResult ApprovalRequired() => Results.Json(
        new { error = $"Production approval requires the {StandardLifecyclePolicy.ApprovalRoles} role." },
        statusCode: StatusCodes.Status403Forbidden);

    private static IResult QuotaRefusable(Func<IResult> action)
    {
        try { return action(); }
        catch (DeployQuotaRefusedException ex)
        {
            return Results.Conflict(new { error = ex.Message, reason = "quota" });
        }
    }
}
