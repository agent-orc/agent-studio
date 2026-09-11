using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The 102-route P2 "operations and insight" bundle from the Studio route
/// ownership dossier: bus, runtime, cycle time, token, deployment, security
/// review, analysis, drift, supervisor, and recovery projections, plus the
/// project-settings, admin, and CLI mutations grouped into the same D4b
/// bundle. Every route here is a durable read over
/// <see cref="TaskServerStore"/> (the <c>studio_operations</c> ledger, the
/// existing <c>events</c>/<c>audit</c> tables, or a small per-domain table)
/// or a dispatch through the existing fenced task/run lifecycle; see
/// docs/operations/setup/task-server.md, "Studio operations-and-insight
/// bundle (P2)".
/// </summary>
public static class StudioOperationsAndInsightEndpoints
{
    public static void MapStudioOperationsAndInsightEndpoints(this WebApplication app)
    {
        var studio = app.MapGroup("/api/v1/studio").RequireTaskServerScope(TaskServerScopes.TasksRead);
        MapAdminEndpoints(studio);
        MapAnalysisEndpoints(studio);
        MapBusEndpoints(studio);
        MapCliEndpoints(studio);
        MapComponentRoutingEndpoints(studio);
        MapCrashRecoveryEndpoints(studio);
        MapDriftEndpoints(studio);
        MapPipelineEndpoints(studio);
        MapRuntimeEndpoints(studio);
        MapSupervisorEndpoints(studio);
        MapTextHelperEndpoints(studio);
        MapTokenPricingEndpoints(studio);
        MapWatchPathEndpoints(studio);
        MapProjectEndpoints(studio);
        MapProjectSettingsEndpoints(studio);
    }

    private static void MapAdminEndpoints(RouteGroupBuilder studio)
    {
        var admin = studio.MapGroup("/admin").RequireTaskServerScope(TaskServerScopes.Management);
        admin.MapPut("/config/orchestrator", async (
            HttpContext context, AdminOrchestratorConfigRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.UpdateAdminOrchestratorConfigAsync(request, TaskServerEndpoints.Actor(context), ct);
                return request;
            }));

        var prompts = admin.MapGroup("/prompts");
        prompts.MapPut("/{name}", async (
            string name, HttpContext context, UpdatePromptRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.UpsertPromptAsync(name, request, TaskServerEndpoints.Actor(context), ct)));
        prompts.MapDelete("/{name}", async (string name, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.DeletePromptAsync(name, ct);
                return new { deleted = true, name };
            }));
        prompts.MapPost("/{name}/preview", async (string name, PromptPreviewRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.PreviewPromptAsync(name, request, ct)));
        prompts.MapPost("/{name}/rebaseline", async (string name, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.RebaselinePromptAsync(name, TaskServerEndpoints.Actor(context), ct)));
        prompts.MapPost("/{name}/review", async (string name, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ReviewPromptAsync(name, TaskServerEndpoints.Actor(context), ct)));
        prompts.MapPost("/review-all", async (HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ReviewAllPromptsAsync(TaskServerEndpoints.Actor(context), ct)));
    }

    private static void MapAnalysisEndpoints(RouteGroupBuilder studio)
    {
        var analysis = studio.MapGroup("/analysis/{project}");
        analysis.MapGet("/reports", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListAnalysisReportsAsync(project, ct)));
        analysis.MapPost("/reports", async (
            string project, HttpContext context, DispatchStudioOperationRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.CreateAnalysisReportAsync(project, request, TaskServerEndpoints.Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        analysis.MapGet("/reports/{reportId}", async (string project, string reportId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetAnalysisReportAsync(project, reportId, ct)));
        analysis.MapGet("/schedule", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetAnalysisScheduleAsync(project, ct)));
        analysis.MapPut("/schedule", async (
            string project, UpdateAnalysisScheduleRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.UpdateAnalysisScheduleAsync(project, request, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    private static void MapBusEndpoints(RouteGroupBuilder studio)
    {
        var bus = studio.MapGroup("/bus/{project}");
        bus.MapGet("/messages", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListBusMessagesAsync(project, 200, ct)));
        bus.MapGet("/messages/{id}", async (string project, long id, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetBusMessageAsync(project, id, ct)));
        bus.MapGet("/recent", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListRecentBusMessagesAsync(project, ct)));
        bus.MapGet("/summary", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetBusSummaryAsync(project, ct)));
        bus.MapGet("/token-aggregate", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetBusTokenAggregateAsync(project, ct)));
    }

    private static void MapCliEndpoints(RouteGroupBuilder studio)
    {
        var cli = studio.MapGroup("/cli").RequireTaskServerScope(TaskServerScopes.Management);
        cli.MapPut("/model-routing/economy-mode", async (HttpContext context, CliEconomyModeRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.UpdateCliEconomyModeAsync(request, TaskServerEndpoints.Actor(context), ct);
                return request;
            }));
        cli.MapPut("/quota/caps", async (HttpContext context, CliQuotaCapsRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.UpdateCliQuotaCapsAsync(request, TaskServerEndpoints.Actor(context), ct);
                return request;
            }));
        cli.MapPut("/quota/model-routes", async (HttpContext context, CliQuotaModelRoutesRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.UpdateCliQuotaModelRoutesAsync(request, TaskServerEndpoints.Actor(context), ct);
                return request;
            }));
        cli.MapPut("/quota/wait-policy", async (HttpContext context, CliQuotaWaitPolicyRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.UpdateCliQuotaWaitPolicyAsync(request, TaskServerEndpoints.Actor(context), ct);
                return request;
            }));
    }

    private static void MapComponentRoutingEndpoints(RouteGroupBuilder studio)
        => studio.MapPost("/component-routing/resolve", async (ComponentRoutingResolveRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ResolveComponentRoutingAsync(request, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

    private static void MapCrashRecoveryEndpoints(RouteGroupBuilder studio)
    {
        var crashRecovery = studio.MapGroup("/crash-recovery");
        crashRecovery.MapGet("/pending", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListCrashRecoveryPendingAsync(ct)));
        crashRecovery.MapPost("/pending/{id}/commit", async (string id, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.CommitCrashRecoveryAsync(id, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        crashRecovery.MapPost("/pending/{id}/dismiss", async (string id, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.DismissCrashRecoveryAsync(id, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    private static void MapDriftEndpoints(RouteGroupBuilder studio)
    {
        var globalDrift = studio.MapGroup("/drift/actions");
        globalDrift.MapPost("/code-pattern-drift", async (
            CodePatternDriftRequest request, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.EvaluateCodePatternDriftAsync(request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        globalDrift.MapGet("/code-pattern-drift/rules", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetCodePatternDriftRulesAsync(ct)));

        var drift = studio.MapGroup("/drift/{project}");
        drift.MapPost("/actions/{action}", async (
            string project, string action, HttpContext context, DispatchStudioOperationRequest request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.DispatchDriftActionAsync(project, action, request, TaskServerEndpoints.Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        drift.MapGet("/actions/software-architecture-drift/prompt", (string project) =>
            Results.Ok(new { prompt = $"Produce a software-architecture-drift report for '{project}'." }));
        drift.MapGet("/architecture", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetArchitectureAsync(project, ct)));
        drift.MapPost("/architecture/{modelId}/elements/{elementId}/status", async (
            string project, string modelId, string elementId, HttpContext context,
            UpdateArchitectureElementStatusRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.UpdateArchitectureElementStatusAsync(
                project, modelId, elementId, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        drift.MapGet("/reports", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListDriftReportsAsync(project, ct)));
        drift.MapGet("/reports/{reportId}", async (string project, string reportId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetDriftReportAsync(project, reportId, ct)));
    }

    private static void MapPipelineEndpoints(RouteGroupBuilder studio)
        => studio.MapGet("/pipeline/accepted-integration-alert", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetPipelineAcceptedIntegrationAlertAsync(ct)));

    private static void MapRuntimeEndpoints(RouteGroupBuilder studio)
        => studio.MapGet("/runtime/{project}/events", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListRuntimeEventsAsync(project, ct)));

    private static void MapSupervisorEndpoints(RouteGroupBuilder studio)
    {
        var supervisor = studio.MapGroup("/supervisor/{project}");
        var intervene = supervisor.MapGroup("/intervene").RequireTaskServerScope(TaskServerScopes.TasksWrite);
        intervene.MapPost("/cancel-run", async (
            string project, HttpContext context, SupervisorInterveneRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.CancelRunAsync(project, request, TaskServerEndpoints.Actor(context), ct)));
        intervene.MapPost("/force-fail", async (
            string project, HttpContext context, SupervisorInterveneRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ForceFailRunAsync(project, request, TaskServerEndpoints.Actor(context), ct)));
        intervene.MapPost("/pause-pickup", async (string project, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.PausePickupAsync(project, TaskServerEndpoints.Actor(context), ct)));
        intervene.MapPost("/resume", async (string project, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ResumePickupAsync(project, TaskServerEndpoints.Actor(context), ct)));

        supervisor.MapGet("/meta-cycle", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetSupervisorMetaCycleAsync(project, ct)));
        supervisor.MapGet("/observation", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetSupervisorObservationAsync(project, ct)));
        supervisor.MapGet("/recent-events", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetSupervisorRecentEventsAsync(project, ct)));
    }

    private static void MapTextHelperEndpoints(RouteGroupBuilder studio)
    {
        studio.MapPost("/prompt/enhance", async (HttpContext context, PromptEnhanceRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.EnhancePromptAsync(request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        studio.MapPost("/title/generate", async (HttpContext context, TitleGenerateRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GenerateTitleAsync(request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    private static void MapTokenPricingEndpoints(RouteGroupBuilder studio)
        => studio.MapPost("/token-pricing/calculate", async (TokenPricingCalculateRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.CalculateTokenPricingAsync(request, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

    private static void MapWatchPathEndpoints(RouteGroupBuilder studio)
    {
        var watchPaths = studio.MapGroup("/watch-paths").RequireTaskServerScope(TaskServerScopes.TasksWrite);
        watchPaths.MapPost("", async (HttpContext context, CreateWatchPathRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.AddWatchPathAsync(request, TaskServerEndpoints.Actor(context), ct), StatusCodes.Status201Created));
        watchPaths.MapDelete("/{name}", async (string name, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.DeleteWatchPathAsync(name, ct);
                return new { deleted = true, name };
            }));
    }

    private static void MapProjectEndpoints(RouteGroupBuilder studio)
    {
        var projects = studio.MapGroup("/projects/{projectId}").RequireTaskServerScope(TaskServerScopes.TasksWrite);
        projects.MapPut("", async (string projectId, HttpContext context, UpdateProjectRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.UpdateProjectAsync(projectId, request, TaskServerEndpoints.Actor(context), ct)));
        projects.MapDelete("", async (string projectId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.DeleteProjectAsync(projectId, ct);
                return new { deleted = true, projectId };
            }));
        projects.MapPut("/ownership-mappings/{mappingId}", async (
            string projectId, string mappingId, UpdateOwnershipMappingRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.UpsertOwnershipMappingAsync(projectId, mappingId, request, ct)));
        projects.MapPost("/urls", async (
            string projectId, HttpContext context, CreateProjectUrlRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.AddProjectUrlAsync(projectId, request, TaskServerEndpoints.Actor(context), ct), StatusCodes.Status201Created));
        projects.MapPut("/urls/{urlId}", async (
            string projectId, string urlId, UpdateProjectUrlRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.UpdateProjectUrlAsync(projectId, urlId, request, ct)));
        projects.MapDelete("/urls/{urlId}", async (string projectId, string urlId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.DeleteProjectUrlAsync(projectId, urlId, ct);
                return new { deleted = true, urlId };
            }));
    }

    private static void MapProjectSettingsEndpoints(RouteGroupBuilder studio)
    {
        var scoped = studio.MapGroup("/projects/{project}");

        MapBooleanSetting(scoped, "/auto-commit", (store, project, value, actor, ct) => store.UpdateAutoCommitAsync(project, value, actor, ct));
        MapStringSetting(scoped, "/auto-push-strategy", (store, project, value, actor, ct) => store.UpdateAutoPushStrategyAsync(project, value, actor, ct));
        MapStringSetting(scoped, "/cli-context-mode", (store, project, value, actor, ct) => store.UpdateCliContextModeAsync(project, value, actor, ct));
        MapStringSetting(scoped, "/cli-mode", (store, project, value, actor, ct) => store.UpdateCliModeAsync(project, value, actor, ct));
        MapBooleanSetting(scoped, "/crash-recovery", (store, project, value, actor, ct) => store.UpdateCrashRecoveryEnabledAsync(project, value, actor, ct));
        MapStringSetting(scoped, "/lane-sort-strategy", (store, project, value, actor, ct) => store.UpdateLaneSortStrategyAsync(project, value, actor, ct));
        MapStringSetting(scoped, "/orchestrator-model", (store, project, value, actor, ct) => store.UpdateOrchestratorModelAsync(project, value, actor, ct));
        MapStringSetting(scoped, "/quota-wait-policy", (store, project, value, actor, ct) => store.UpdateQuotaWaitPolicyAsync(project, value, actor, ct));

        scoped.MapPut("/max-parallelism", async (
            string project, HttpContext context, IntegerSettingRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.UpdateMaxParallelismAsync(project, request.Value, TaskServerEndpoints.Actor(context), ct);
                return request;
            })).RequireTaskServerScope(TaskServerScopes.TasksWrite);

        scoped.MapGet("/cycle-time", async (string project, string? window, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetProjectCycleTimeAsync(project, window ?? "30d", ct)));
        scoped.MapGet("/cycle-time/tasks/{taskKey}", async (string project, string taskKey, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetTaskCycleTimeAsync(project, taskKey, ct)));
        scoped.MapGet("/throughput", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetProjectThroughputAsync(project, ct)));
        scoped.MapGet("/snapshot", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetStudioProjectSnapshotAsync(project, ct)));
        scoped.MapGet("/regression-radar", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListRegressionRadarAsync(project, ct)));
        scoped.MapGet("/test-runs", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListTestRunsAsync(project, ct)));

        scoped.MapGet("/visual-evidence", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListVisualEvidenceAsync(project, ct)));
        scoped.MapPost("/visual-evidence/{itemId}/acknowledge", async (
            string project, long itemId, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.AcknowledgeVisualEvidenceAsync(project, itemId, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        var tokenUsage = scoped.MapGroup("/token-usage");
        tokenUsage.MapGet("/expensive", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetExpensiveTokenUsageAsync(project, ct)));
        tokenUsage.MapGet("/heatmap", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetTokenUsageHeatmapAsync(project, ct)));
        tokenUsage.MapGet("/job/{taskId}", async (string project, string taskId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetTokenUsageForJobAsync(project, taskId, ct)));
        tokenUsage.MapGet("/pipeline-cost", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetTokenUsagePipelineCostAsync(project, ct)));
        tokenUsage.MapGet("/summary", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetTokenUsageSummaryAsync(project, ct)));

        var deployment = scoped.MapGroup("/deployment");
        deployment.MapPost("/compile", async (
            string project, HttpContext context, DeploymentCompileRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.CompileDeploymentAsync(project, request, TaskServerEndpoints.Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        deployment.MapGet("/summary", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetDeploymentSummaryAsync(project, ct)));

        var design = scoped.MapGroup("/design");
        design.MapPost("/actions/{action}", async (
            string project, string action, HttpContext context, DesignActionRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.DispatchDesignActionAsync(project, action, request, TaskServerEndpoints.Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        design.MapGet("/council", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListDesignCouncilAsync(project, ct)));
        design.MapGet("/council/{fileName}", async (string project, string fileName, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetDesignCouncilItemAsync(project, fileName, ct)));
        design.MapPost("/council/{fileName}/accept", async (
            string project, string fileName, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.AcceptDesignCouncilItemAsync(project, fileName, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        design.MapGet("/overview", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetDesignOverviewAsync(project, ct)));
        design.MapGet("/references", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetDesignReferencesAsync(project, ct)));

        var proposals = scoped.MapGroup("/proposals");
        proposals.MapDelete("", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.DeleteAllProposalsAsync(project, ct);
                return new { deleted = true };
            })).RequireTaskServerScope(TaskServerScopes.TasksWrite);
        proposals.MapPost("/generate", async (
            string project, HttpContext context, GenerateProposalsRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.GenerateProposalsAsync(project, request, TaskServerEndpoints.Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        proposals.MapPost("/refine-feedback", async (
            string project, HttpContext context, RefineProposalsFeedbackRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.RefineProposalsFeedbackAsync(project, request, TaskServerEndpoints.Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        proposals.MapDelete("/{proposalId}", async (string project, string proposalId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.DeleteProposalAsync(project, proposalId, ct);
                return new { deleted = true, proposalId };
            })).RequireTaskServerScope(TaskServerScopes.TasksWrite);
        proposals.MapPost("/{proposalId}/decision", async (
            string project, string proposalId, HttpContext context, ProposalDecisionRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.DecideProposalAsync(project, proposalId, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        var publish = scoped.MapGroup("/publish");
        publish.MapPut("/automation", async (
            string project, HttpContext context, PublishAutomationRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.UpdatePublishAutomationAsync(project, request, TaskServerEndpoints.Actor(context), ct);
                return request;
            })).RequireTaskServerScope(TaskServerScopes.TasksWrite);
        publish.MapPost("/package", async (
            string project, HttpContext context, PublishPackageRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.PublishPackageAsync(project, request, TaskServerEndpoints.Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        publish.MapPost("/website", async (
            string project, HttpContext context, PublishWebsiteRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.PublishWebsiteAsync(project, request, TaskServerEndpoints.Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        scoped.MapPost("/queue-health/repair", async (string project, HttpContext context, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.RepairQueueHealthAsync(project, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        var security = scoped.MapGroup("/security");
        security.MapPost("/audit", async (
            string project, HttpContext context, SecurityAuditRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.DispatchSecurityAuditAsync(project, request, TaskServerEndpoints.Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        security.MapGet("/baseline", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetSecurityBaselineAsync(project, ct)));
        security.MapGet("/reviews", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListSecurityReviewsAsync(project, ct)));
        security.MapGet("/reviews/{fileName}", async (string project, string fileName, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetSecurityReviewAsync(project, fileName, ct)));

        scoped.MapPost("/skill-readiness/fix-task", async (
            string project, HttpContext context, SkillReadinessFixTaskRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.DispatchSkillReadinessFixAsync(project, request, TaskServerEndpoints.Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        var wikiGrading = scoped.MapGroup("/wiki/grading");
        wikiGrading.MapPost("/abort", async (
            string project, HttpContext context, WikiGradingAbortRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.AbortWikiGradingAsync(project, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        wikiGrading.MapPost("/run", async (
            string project, HttpContext context, WikiGradingRunRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.RunWikiGradingAsync(project, request, TaskServerEndpoints.Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    private static void MapBooleanSetting(
        RouteGroupBuilder scoped, string path, Func<TaskServerStore, string, bool, string, CancellationToken, Task> update)
        => scoped.MapPut(path, async (
            string project, HttpContext context, BooleanSettingRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await update(store, project, request.Value, TaskServerEndpoints.Actor(context), ct);
                return request;
            })).RequireTaskServerScope(TaskServerScopes.TasksWrite);

    private static void MapStringSetting(
        RouteGroupBuilder scoped, string path, Func<TaskServerStore, string, string, string, CancellationToken, Task> update)
        => scoped.MapPut(path, async (
            string project, HttpContext context, StringSettingRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await update(store, project, request.Value, TaskServerEndpoints.Actor(context), ct);
                return request;
            })).RequireTaskServerScope(TaskServerScopes.TasksWrite);
}
