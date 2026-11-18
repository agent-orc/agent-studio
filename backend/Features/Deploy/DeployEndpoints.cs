using AgentStudio.Projects;

namespace AgentStudio.Deploy;

/// <summary>BD-1 routes for the P1 data contract (releases, deployments, status, matrix).</summary>
public static class DeployEndpoints
{
    public static void MapDeployEndpoints(this WebApplication app)
    {
        var project = app.MapGroup("/api/projects/{projectName}/deploy");

        project.MapGet("/environments", (string projectName, DeployService deploy) =>
            deploy.Environments(projectName) is { } value ? Results.Ok(value) : UnknownProject(projectName));

        project.MapGet("/tenants", (string projectName, DeployService deploy) =>
            deploy.Tenants(projectName) is { } value ? Results.Ok(value) : UnknownProject(projectName));

        project.MapGet("/status", (string projectName, string? tenant, DeployService deploy) =>
            tenant is not null && deploy.Tenants(projectName)?.All(item => item.Name != tenant) == true
                ? Results.NotFound(new { error = $"Unknown tenant '{tenant}'." })
                : deploy.Status(projectName, tenant) is { } value ? Results.Ok(value) : UnknownProject(projectName));

        project.MapGet("/delivery-lanes", (string projectName, DeployService deploy) =>
            deploy.DeliveryLanes(projectName) is { } value ? Results.Ok(value) : UnknownProject(projectName));

        project.MapGet("/lanes", (string projectName, int? releases, DeployService deploy) =>
            deploy.DeliveryColumns(projectName, Math.Clamp(releases ?? DeliveryColumnProjection.DefaultReleaseDepth,
                1, DeliveryColumnProjection.MaxReleaseDepth)) is { } value ? Results.Ok(value) : UnknownProject(projectName));

        project.MapGet("/releases", (string projectName, DeployService deploy) =>
            deploy.Releases(projectName) is { } value ? Results.Ok(value) : UnknownProject(projectName));

        project.MapGet("/releases/page", (string projectName, int? offset, int? limit, string? artifact,
            DateTime? notAfterUtc, DeployService deploy) =>
            deploy.ReleasePage(projectName, offset ?? 0, limit ?? 25, artifact, notAfterUtc) is { } value
                ? Results.Ok(value) : UnknownProject(projectName));

        project.MapGet("/releases/{releaseId}/page", (string projectName, string releaseId, int? limit, DeployService deploy) =>
            deploy.ReleasePageContaining(projectName, releaseId, limit ?? 25) is { } value
                ? Results.Ok(value) : Results.NotFound(new { error = $"Unknown release '{releaseId}'." }));

        project.MapGet("/releases/{releaseId}", (string projectName, string releaseId, DeployService deploy) =>
            deploy.ReleaseDetail(projectName, releaseId) is { } value
                ? Results.Ok(value) : Results.NotFound(new { error = $"Unknown release '{releaseId}'." }));

        project.MapGet("/releases/{releaseId}/notes", (string projectName, string releaseId, DeployService deploy) =>
            deploy.Notes(projectName, releaseId) is { } value
                ? Results.Ok(value) : Results.NotFound(new { error = $"Unknown release '{releaseId}'." }));

        project.MapPut("/releases/{releaseId}/notes/{cardKey}", (HttpContext context, string projectName,
            string releaseId, string cardKey, ReleaseNoteEditRequest request, DeployService deploy) =>
            Respond(deploy.EditNote(projectName, releaseId, cardKey, request, Actor(context, projectName))));

        // F14: unbound or missing secrets per environment, before a run is queued.
        project.MapGet("/secret-readiness", async (string projectName, string? releaseId, DeploySecretReadiness readiness,
            CancellationToken ct) => await readiness.ReadAsync(projectName, releaseId, ct) is { } value
                ? Results.Ok(value)
                : releaseId is null ? UnknownProject(projectName)
                : Results.NotFound(new { error = $"Unknown release '{releaseId}'." }));

        project.MapGet("/cards", (string projectName, DeployService deploy) =>
            deploy.Cards(projectName) is { } value ? Results.Ok(value) : UnknownProject(projectName));

        project.MapGet("/deployments", (string projectName, string? environmentId, string? tenant, DeployService deploy) =>
            deploy.Deployments(projectName, environmentId, tenant) is { } value ? Results.Ok(value) : UnknownProject(projectName));

        project.MapGet("/deployments/page", (string projectName, string? environmentId, string? tenant, string? state,
            DateTime? sinceUtc, int? offset, int? limit, DeployService deploy) =>
            deploy.DeploymentPage(projectName, environmentId, state, sinceUtc, offset ?? 0, limit ?? 25, tenant) is { } value
                ? Results.Ok(value) : UnknownProject(projectName));

        project.MapGet("/deployments/{deploymentId}", (string projectName, string deploymentId, DeployService deploy) =>
            deploy.DeploymentById(projectName, deploymentId) is { } value
                ? Results.Ok(value) : Results.NotFound(new { error = $"Unknown deployment '{deploymentId}'." }));

        project.MapGet("/deployments/{deploymentId}/log", (string projectName, string deploymentId, string? step,
            DeployService deploy) =>
            deploy.Log(projectName, deploymentId, step) is { } value
                ? Results.Ok(value) : Results.NotFound(new { error = $"Unknown deployment '{deploymentId}'." }));

        // F2: the gate keys a refusal by the registered display name. A file read, no git.
        project.MapGet("/release-gate", (string projectName, DeployStore store,
            AgentStudio.Registry.ProjectRegistry registry) =>
            Results.Ok(new ReleaseGateStatus(store.ReadReleaseRefusal(
                (registry.FindByIdOrDisplayName(projectName) ?? registry.FindByShortCode(projectName))?.DisplayName
                ?? projectName))));

        project.MapGet("/audit", (string projectName, DeployService deploy) =>
            deploy.Audit(projectName) is { } value ? Results.Ok(value) : UnknownProject(projectName));

        project.MapGet("/runbooks", (string projectName, RunbookService runbooks) =>
            runbooks.Definitions(projectName) is { } value ? Results.Ok(value) : UnknownProject(projectName));

        project.MapGet("/runbooks/runs", (string projectName, string? environmentId, RunbookService runbooks) =>
            runbooks.Runs(projectName, environmentId) is { } value ? Results.Ok(value) : UnknownProject(projectName));

        project.MapGet("/runbooks/runs/{runId}", (string projectName, string runId, RunbookService runbooks) =>
            runbooks.Run(projectName, runId) is { } value ? Results.Ok(value) : Results.NotFound());

        project.MapGet("/runbooks/runs/{runId}/log", (string projectName, string runId, string? step,
            RunbookService runbooks) => runbooks.Log(projectName, runId, step) is { } value
                ? Results.Ok(value) : Results.NotFound());

        project.MapPost("/environments/{environmentId}/runbooks/{name}/runs", (HttpContext context,
            string projectName, string environmentId, string name, RunbookService runbooks) =>
            Respond(runbooks.Request(projectName, environmentId, name, Actor(context, projectName)), accepted: true))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        project.MapPost("/runbooks/runs/{runId}/approve", (HttpContext context, string projectName,
            string runId, RunbookService runbooks) =>
            RespondApproval(context, runbooks.Approve(projectName, runId, Actor(context, projectName))))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        project.MapPost("/runbooks/runs/{runId}/reject", (HttpContext context, string projectName,
            string runId, RunbookService runbooks) =>
            Respond(runbooks.Reject(projectName, runId, Actor(context, projectName))))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
        project.MapGet("/operations", (string projectName, AgentStudio.Projects.ProjectOperationsService operations) =>
        {
            try
            {
                return operations.Read(projectName) is { } value ? Results.Ok(value) : UnknownProject(projectName);
            }
            catch (AgentStudio.Projects.OperationsTelemetryReadException exception)
            {
                return Results.Json(new { error = exception.Message }, statusCode: 503);
            }
        });

        project.MapGet("/operations/logs", (string projectName, string environmentId, DateTimeOffset? since,
            DateTimeOffset? until, string? level, string? releaseId, string? text, int? limit, string? cursor,
            AgentStudio.Projects.ProjectOperationsService operations) =>
        {
            try
            {
                return operations.SearchLogs(projectName, environmentId, since, until, level, releaseId, text, limit, cursor)
                    is { } value ? Results.Ok(value) : Results.NotFound(new { error = "Unknown project or environment." });
            }
            catch (AgentStudio.Projects.OperationsTelemetryReadException exception)
            {
                return Results.Json(new { error = exception.Message }, statusCode: 503);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        // AGT-3891: what creates the next release, what blocks it, and the
        // one action that creates the release of the gated integration tip.
        project.MapGet("/integration-release", async (string projectName, IntegrationReleaseService releases,
            CancellationToken ct) =>
            await releases.StatusAsync(projectName, ct) is { } value ? Results.Ok(value) : UnknownProject(projectName));

        // The route catalog requires environment.deploy on Stage, as for a release request.
        project.MapPost("/integration-release", async (HttpContext context, string projectName,
            IntegrationReleaseService releases, CancellationToken ct) =>
            await releases.CreateAsync(projectName, Actor(context, projectName).Id, automatic: false, ct) is { } value
                ? Results.Ok(value) : UnknownProject(projectName))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        project.MapPost("/releases/{releaseId}/deployments", async (HttpContext context, string projectName,
            string releaseId, DeploymentRequest request, DeployService deploy,
            ProjectEnvironmentService environments, DeploySecretReadiness readiness, CancellationToken ct) =>
        {
            if (context.Items[AccessSecurityMiddleware.RequestAccessItem] is RequestAccess access)
            {
                var environment = IamEnvironments.ForDeclaration(environments.Declarations(projectName), request.EnvironmentId);
                if (!access.Allows(IamPermissions.EnvironmentDeploy,
                        PermissionTarget.ForProject(projectName, environment)))
                    return Results.Json(new { error = "environment-scope-denied" },
                        statusCode: StatusCodes.Status403Forbidden);
            }
            var actor = Actor(context, projectName);
            return await WithSecretPreflight(readiness, projectName,
                request.Approve && actor.IsApprover
                    ? deploy.SecretDemandFor(projectName, releaseId, request.EnvironmentId, request.Tenant) : null,
                request.ConfirmMissingSecrets,
                missing => QuotaRefusable(() => Respond(deploy.RequestDeployment(projectName, releaseId, request,
                    actor, missing), accepted: true)), ct);
        })
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        project.MapPost("/deployments/{deploymentId}/approve", async (HttpContext context, string projectName,
            string deploymentId, DeploymentApprovalRequest? request, DeployService deploy,
            DeploySecretReadiness readiness, CancellationToken ct) =>
        {
            var actor = Actor(context, projectName);
            return await WithSecretPreflight(readiness, projectName,
                actor.IsApprover ? deploy.SecretDemandForRun(projectName, deploymentId) : null,
                request?.ConfirmMissingSecrets == true,
                missing => QuotaRefusable(() => RespondApproval(context, deploy.Approve(projectName, deploymentId, actor,
                    request?.GuidedFailure, missing))), ct);
        })
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        project.MapPost("/deployments/{deploymentId}/reject", (HttpContext context, string projectName,
            string deploymentId, DeployService deploy) =>
            Respond(deploy.Reject(projectName, deploymentId, Actor(context, projectName))))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        project.MapPost("/deployments/{deploymentId}/cancel", (HttpContext context, string projectName,
            string deploymentId, DeployService deploy) =>
            Respond(deploy.Cancel(projectName, deploymentId, Actor(context, projectName))))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        project.MapPost("/deployments/{deploymentId}/intervention", (HttpContext context, string projectName,
            string deploymentId, InterventionDecisionRequest request, DeployService deploy) =>
            Respond(deploy.DecideIntervention(projectName, deploymentId, request, Actor(context, projectName))))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        project.MapPost("/environments/{environmentId}/redeploy", (HttpContext context, string projectName,
            string environmentId, EnvironmentActionRequest? request, DeployService deploy,
            DeploySecretReadiness readiness, CancellationToken ct) =>
            EnvironmentAction(context, projectName, environmentId, request, deploy, readiness, rollback: false, ct))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        project.MapPost("/environments/{environmentId}/rollback", (HttpContext context, string projectName,
            string environmentId, EnvironmentActionRequest? request, DeployService deploy,
            DeploySecretReadiness readiness, CancellationToken ct) =>
            EnvironmentAction(context, projectName, environmentId, request, deploy, readiness, rollback: true, ct))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        app.MapGet("/api/deploy/matrix", (HttpContext context, string? tenant, DeployService deploy, ProjectRegistry projects) =>
        {
            var matrix = deploy.Matrix();
            if (tenant is not null)
                matrix = matrix with { Projects = matrix.Projects.Select(row => row with
                    { TenantStatuses = row.TenantStatuses.Where(status => status.Tenant == tenant).ToArray() })
                    .Where(row => row.TenantStatuses.Count > 0).ToArray() };
            return Results.Ok(context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                ? matrix with
                {
                    Projects = matrix.Projects
                        .Where(row => ProjectAccessAuthorization.Allows(human.User, row.ProjectId, projects)).ToArray(),
                }
                : matrix);
        });

        app.MapGet("/api/deploy/cards", (HttpContext context, DeployService deploy, ProjectRegistry projects) =>
        {
            var cards = deploy.CardMatrix();
            return Results.Ok(context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                ? cards.Where(card => ProjectAccessAuthorization.Allows(human.User, card.ProjectId, projects)).ToArray()
                : cards);
        });
    }

    public static DeployActor Actor(HttpContext context, string project)
    {
        if (context.Items[AccessSecurityMiddleware.ServiceAccountPrincipalItem] is ServiceAccountPrincipal account)
            return new DeployActor(account.ActorId, false);
        var user = DeployApprovalAuthority.SignedInUser(context,
            context.RequestServices.GetRequiredService<AccessSecurityStore>());
        if (context.Items[AccessSecurityMiddleware.RequestAccessItem] is RequestAccess access)
            return new DeployActor(user?.Id ?? DeployApprovalAuthority.LocalOperator,
                user is { Disabled: false, MustChangePassword: false }
                && access.Allows(IamPermissions.EnvironmentProductionDeployApprove,
                    PermissionTarget.ForProject(project, IamEnvironments.Production)));
        return DeployApprovalAuthority.Resolve(user, project);
    }

    private static async Task<IResult> EnvironmentAction(HttpContext context, string project, string environmentId,
        EnvironmentActionRequest? request, DeployService deploy, DeploySecretReadiness readiness, bool rollback,
        CancellationToken ct)
    {
        var actor = Actor(context, project);
        var approve = request?.Approve == true;
        return await WithSecretPreflight(readiness, project,
            approve && actor.IsApprover
                ? deploy.SecretDemandForEnvironment(project, environmentId, rollback, request?.Tenant) : null,
            request?.ConfirmMissingSecrets == true,
            missing => QuotaRefusable(() => Respond(rollback
                ? deploy.Rollback(project, environmentId, approve, actor, request?.Tenant, request?.GuidedFailure, missing,
                    request?.AcceptIncidentTarget == true)
                : deploy.Redeploy(project, environmentId, approve, actor, request?.Tenant, request?.GuidedFailure, missing),
                accepted: true)), ct);
    }

    /// <summary>
    /// F14: a Production approval that would queue a run without its secret
    /// bindings is refused with the names (409, reason
    /// <see cref="DeploySecretReadiness.MissingReason"/>) unless the approver
    /// confirmed them; a confirmed approval passes the names on for the run log.
    /// </summary>
    internal static async Task<IResult> WithSecretPreflight(DeploySecretReadiness readiness, string project,
        EnvironmentSecretDemand? demand, bool confirmed, Func<IReadOnlyList<string>, IResult> proceed,
        CancellationToken ct)
    {
        var missing = await readiness.ApprovalGapAsync(project, demand, ct);
        var names = missing.Select(item => item.Name).ToArray();
        if (names.Length > 0 && !confirmed)
            return Results.Conflict(new
            {
                error = $"{demand!.Environment.Name} is missing {names.Length} secret binding(s): {string.Join(", ", names)}. "
                    + "Bind them first, or confirm the approval to deploy without them.",
                reason = DeploySecretReadiness.MissingReason,
                missing,
            });
        return proceed(names);
    }

    private static IResult UnknownProject(string project)
        => Results.NotFound(new { error = $"Unknown project '{project}'" });

    private static IResult Respond<T>(DeployOutcome<T> outcome, bool accepted = false) => outcome.Kind switch
    {
        DeployOutcomeKind.Ok => accepted ? Results.Accepted(value: outcome.Value) : Results.Ok(outcome.Value),
        DeployOutcomeKind.NotFound => Results.NotFound(new { error = outcome.Error }),
        DeployOutcomeKind.Invalid => Results.BadRequest(new { error = outcome.Error }),
        DeployOutcomeKind.Forbidden => Results.Json(new { error = outcome.Error }, statusCode: StatusCodes.Status403Forbidden),
        _ => Results.Conflict(new { error = outcome.Error }),
    };

    private static IResult RespondApproval<T>(HttpContext context, DeployOutcome<T> outcome)
    {
        if (outcome.Kind == DeployOutcomeKind.Forbidden &&
            DeployApprovalAuthority.SignedInUser(context,
                context.RequestServices.GetRequiredService<AccessSecurityStore>()) is null)
            return Results.Json(new { error = "Sign in again to approve." },
                statusCode: StatusCodes.Status401Unauthorized);
        return Respond(outcome);
    }

    /// <summary>Maps a quota refusal from a deploy action to 409 with reason <c>quota</c>.</summary>
    internal static IResult QuotaRefusable(Func<IResult> action)
    {
        try { return action(); }
        catch (DeployQuotaRefusedException ex)
        {
            return Results.Conflict(new { error = ex.Message, reason = "quota" });
        }
    }
}
