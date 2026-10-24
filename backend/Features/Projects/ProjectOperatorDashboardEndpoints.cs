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
        app.MapPost("/api/deploy/gate-releases", (HttpContext context, GateArtifactRequest request,
            ProjectEnvironmentService environments, DeployService deploy, IConfiguration configuration,
            AccessSecurityStore security) =>
        {
            var builder = RunnerCredential(context, configuration, security);
            if (builder is null || !builder.Scopes.Contains(RunnerScopes.Release))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return DeployEndpoints.QuotaRefusable(() => environments.RegisterGateArtifact(
                request.Project, request.Artifact, request.Commit, builder.RunnerId)
                ? ReleaseCreated(deploy.CreateRelease(request.Project, request.Artifact, request.Commit, "release-gate",
                        request.VersionTag, request.Branch, request.Channel),
                    "Gate artifact or environment declarations are invalid.")
                : Results.BadRequest(new { error = "Gate artifact or environment declarations are invalid." }));
        })
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        app.MapGet("/api/projects/{projectName}/environments", (string projectName,
            ProjectEnvironmentService environments) => environments.Read(projectName) is { } value
                ? Results.Ok(value) : Results.NotFound());

        app.MapGet("/api/projects/{projectName}/previews/{cardKey}/screenshot", (
            HttpContext context, string projectName, string cardKey,
            ProjectEnvironmentService environments, ProjectRegistry projects) =>
        {
            var project = projects.FindByIdOrDisplayName(projectName) ?? projects.FindByShortCode(projectName);
            if (project is null) return Results.NotFound();
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                && !ProjectAccessAuthorization.Allows(human.User, project.Id, projects))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return environments.PreviewScreenshotPath(projectName, cardKey) is { } path
                ? Results.File(path, "image/png") : Results.NotFound();
        });

        app.MapPost("/api/projects/{projectName}/environments/releases", (HttpContext context, string projectName,
            ReleaseArtifactRequest request, DeployService deploy) =>
            DeployEndpoints.QuotaRefusable(() => ReleaseCreated(deploy.CreateRelease(projectName, request.Artifact, request.Commit,
                DeployEndpoints.Actor(context, projectName).Id, request.VersionTag, request.Branch,
                request.Channel, DeployEndpoints.Actor(context, projectName).IsApprover),
                "Artifact, commit, or stage and production declarations are invalid.")))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        // Production approval follows the BD-1 standard lifecycle: Owner or
        // Maintainer only, on these AGT-3176 routes as on the deploy routes,
        // and the lifecycle decides each approval again.
        app.MapPost("/api/projects/{projectName}/environments/promotions/approve", (
            HttpContext context, string projectName, PromotionDecisionRequest request, DeployService deploy) =>
            DeployEndpoints.QuotaRefusable(() => DecidePromotion(context, projectName, request, approve: true, deploy)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        app.MapPost("/api/projects/{projectName}/environments/promotions/reject", (
            HttpContext context, string projectName, PromotionDecisionRequest request, DeployService deploy) =>
            DeployEndpoints.QuotaRefusable(() => DecidePromotion(context, projectName, request, approve: false, deploy)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        app.MapPost("/api/projects/{projectName}/environments/{environmentId}/rollback", (
            HttpContext context, string projectName, string environmentId, DeployService deploy) =>
        {
            var actor = DeployEndpoints.Actor(context, projectName);
            if (!actor.IsApprover && deploy.Environments(projectName)?.FirstOrDefault(item => item.Id == environmentId)
                    is { } environment && StandardLifecyclePolicy.RequiresApproval(environment))
                return ApprovalRequired();
            return DeployEndpoints.QuotaRefusable(() => deploy.RollbackEnvironment(projectName, environmentId, actor) switch
            {
                { Kind: DeployOutcomeKind.Ok } => Results.Accepted(),
                { Kind: DeployOutcomeKind.NotFound } => Results.Conflict(new { error = "No previous artifact is available." }),
                { Kind: DeployOutcomeKind.Forbidden } refused => Results.Json(new { error = refused.Error },
                    statusCode: StatusCodes.Status403Forbidden),
                var refused => Results.Conflict(new { error = refused.Error }),
            });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        app.MapPost("/api/projects/{projectName}/environments/{environmentId}/stop", (
            HttpContext context, string projectName, string environmentId, ProjectEnvironmentService environments) =>
            // The host stops the environment; it stays running and counted until the host acknowledges.
            environments.Stop(projectName, environmentId, DeployEndpoints.Actor(context, projectName).Id)
                ? Results.Accepted() : Results.Conflict(new { error = "The environment is not deployed, is already stopped or has a stop pending." }))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        // IP-1: a production or stage environment that is no longer placed on
        // its run target waits here for the operator's decision.
        app.MapPost("/api/projects/{projectName}/environments/{environmentId}/run-target-removal/confirm", (
            HttpContext context, string projectName, string environmentId, RunTargetRemovalConfirmRequest? request,
            ProjectEnvironmentService environments) =>
            environments.ConfirmRunTargetRemoval(projectName, environmentId, request?.HostId,
                context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                    ? human.User.Id : "local-operator")
                ? Results.Accepted() : Results.Conflict(new { error = "No removal is pending for this environment." }))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        app.MapPut("/api/projects/{projectName}/run-target-domain-suffixes", (string projectName,
            RunTargetDomainSuffixesRequest request, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            if (!scanner.GetWatchPaths().Any(item => string.Equals(item.Name, projectName, StringComparison.OrdinalIgnoreCase)))
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            if (RunTargetDesiredStatePolicy.NormalizeDomainSuffixes(request.Suffixes) is not { } suffixes)
                return Results.BadRequest(new { error = "Every suffix must be a domain such as example.com." });
            settings.SetRunTargetDomainSuffixes(projectName, suffixes);
            return Results.Ok(new { suffixes });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        // IP-1: a production or stage environment that is no longer placed on
        // its run target waits here for the operator's decision.
        app.MapPost("/api/projects/{projectName}/environments/{environmentId}/run-target-removal/confirm", (
            HttpContext context, string projectName, string environmentId, RunTargetRemovalConfirmRequest? request,
            ProjectEnvironmentService environments) =>
            environments.ConfirmRunTargetRemoval(projectName, environmentId, request?.HostId,
                context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                    ? human.User.Id : "local-operator")
                ? Results.Accepted() : Results.Conflict(new { error = "No removal is pending for this environment." }))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        app.MapPut("/api/projects/{projectName}/run-target-domain-suffixes", (string projectName,
            RunTargetDomainSuffixesRequest request, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            if (!scanner.GetWatchPaths().Any(item => string.Equals(item.Name, projectName, StringComparison.OrdinalIgnoreCase)))
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            if (RunTargetDesiredStatePolicy.NormalizeDomainSuffixes(request.Suffixes) is not { } suffixes)
                return Results.BadRequest(new { error = "Every suffix must be a domain such as example.com." });
            settings.SetRunTargetDomainSuffixes(projectName, suffixes);
            return Results.Ok(new { suffixes });
        });

        app.MapPost("/api/deploy/jobs/claim", (HttpContext context, DeployClaimRequest request,
            ProjectEnvironmentService environments, DeployService deploy, IConfiguration configuration) =>
        {
            if (SecurityProfiles.IsNetworked(configuration) &&
                (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is not RunnerPrincipal principal
                 || principal.RunnerId != request.HostId))
                return Results.Forbid();
            var job = environments.Claim(request.HostId, requireVerifiedRelease: true);
            if (job is not null) deploy.SyncAfterHostCall(job.Project);
            return Results.Ok(job);
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);

        app.MapPost("/api/deploy/jobs/{project}/{jobId}/report", (string project, string jobId,
            DeployReportRequest request, ProjectEnvironmentService environments, DeployService deploy) =>
        {
            if (!environments.Report(project, jobId, request, requireVerifiedRelease: true)) return Results.Conflict();
            deploy.SyncAfterHostCall(project);
            return Results.Ok();
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);

        app.MapGet("/api/deploy/jobs/{project}/{jobId}/artifact", (HttpContext context, string project, string jobId,
            ProjectEnvironmentService environments, DeployStore releases, ReleaseArtifactRegistry registry,
            IConfiguration configuration, AccessSecurityStore security) =>
        {
            var principal = RunnerCredential(context, configuration, security);
            if (principal is null || !principal.Scopes.Contains(RunnerScopes.Deploy))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var lease = context.Request.Headers["X-Deploy-Lease"].ToString();
            var claim = environments.FindClaimedJobVerification(project, jobId, lease, requireVerifiedRelease: true);
            if (claim.Retryable) return RegistryUnavailable();
            var job = claim.Job;
            if (job is null || SecurityProfiles.IsNetworked(configuration) && principal.RunnerId != job.HostId)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                var record = releases.ReadReleaseRecords(project).SingleOrDefault(item =>
                    item.Release.Id == job.ReleaseId
                    && item.Release.ArtifactDigests.Contains(job.Artifact, StringComparer.Ordinal)
                    && item.Environments.Any(environment => environment.Id == job.EnvironmentId
                        && environment.Template == job.Template));
                if (record is null) return Results.Conflict(new { error = "The deploy job has no release." });
                var provenance = registry.VerifiedForDeployment(project, record.Release.CommitSha, job.Artifact,
                    job.Template);
                if (provenance is null) return Results.Conflict(new { error = "Artifact provenance is unverified." });
                if (context.Request.Query.ContainsKey("content"))
                {
                    var path = registry.VerifiedBlob(provenance);
                    return path is null ? Results.Conflict(new { error = "Artifact content is unavailable." })
                        : Results.File(path, "application/octet-stream");
                }
                return Results.Ok(provenance);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return RegistryUnavailable();
            }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);

        // IP-1 run-target role: the host polls; Studio never dials in.
        app.MapGet("/api/deploy/run-targets/{hostId}/desired-state", (HttpContext context, string hostId,
            ProjectEnvironmentService environments, IConfiguration configuration) =>
            RunTargetCallerDenied(context, hostId, configuration)
                ? RunTargetForbidden() : Results.Ok(environments.DesiredState(hostId)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);

        app.MapPost("/api/deploy/run-targets/{hostId}/observed-state", (HttpContext context, string hostId,
            RunTargetObservedReport report, ProjectEnvironmentService environments, IConfiguration configuration) =>
            RunTargetCallerDenied(context, hostId, configuration)
                ? RunTargetForbidden() : Results.Ok(new { accepted = environments.RecordObserved(hostId, report) }))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);

        app.MapPost("/api/deploy/jobs/{project}/{jobId}/log", (string project, string jobId,
            DeployStepLogRequest request, DeployService deploy) =>
            deploy.AppendHostStepLog(project, jobId, request) ? Results.Ok() : Results.Conflict())
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);

        app.MapPost("/api/deploy/jobs/{project}/{jobId}/renew", (string project, string jobId,
            DeployLeaseRequest request, ProjectEnvironmentService environments) =>
            environments.RenewClaim(project, jobId, request.LeaseToken) ? Results.Ok() : Results.Conflict())
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

    // Results.Forbid() needs an authentication scheme; Runner credentials are
    // checked by AccessSecurityMiddleware, so answer 403 directly.
    private static IResult RunTargetForbidden() => Results.Json(
        new { error = "A run target may access only its enrolled host identity." }, statusCode: StatusCodes.Status403Forbidden);

    private static bool RunTargetCallerDenied(HttpContext context, string hostId, IConfiguration configuration)
        => SecurityProfiles.IsNetworked(configuration)
            && (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is not RunnerPrincipal principal
                || principal.RunnerId != hostId);
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
        _ => Results.BadRequest(new { error = outcome.Error ?? invalid }),
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

    private static IResult RegistryUnavailable() => Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    private static RunnerPrincipal? RunnerCredential(HttpContext context, IConfiguration configuration,
        AccessSecurityStore security)
    {
        if (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is RunnerPrincipal principal)
            return principal;
        if (SecurityProfiles.IsNetworked(configuration)) return null;
        var authorization = context.Request.Headers.Authorization;
        if (authorization.Count != 1 || authorization[0] is not { } bearer
            || !bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        return security.AuthenticateRunner(bearer["Bearer ".Length..].Trim());
    }
}
