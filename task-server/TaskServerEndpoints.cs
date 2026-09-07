using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public static class TaskServerEndpoints
{
    public static void MapTaskServerEndpoints(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "live" }));
        app.MapGet("/readyz", (TaskServerStore store) => store.AuthorityReady
            ? Results.Ok(new { status = "ready", authority = "restored", mode = store.Mode.ToString() })
            : Results.Json(new ApiError("authority-not-ready", "Lease and fence authority has not been restored."), statusCode: 503));

        var api = app.MapGroup("/api/v1")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        api.MapGet("/protocol", (TaskServerStore store) => Results.Ok(store.Status().Protocol));
        api.MapPost("/protocol/compatibility", (ProtocolCompatibilityRequest request, TaskServerStore store) =>
        {
            var supported = TaskServerProtocol.Supports(request.ProtocolVersion)
                && request.ClientKind is "studio" or "runner" or "review-runner" or "management"
                    or TaskServerProtocol.EngineClientKind;
            var reason = supported
                ? null
                : !store.Status().Protocol.ClientKinds.Contains(request.ClientKind, StringComparer.Ordinal)
                    ? $"Client kind '{request.ClientKind}' is not supported. Supported client kinds: " +
                      $"{string.Join(", ", store.Status().Protocol.ClientKinds)}."
                    : $"Client protocol {request.ProtocolVersion} is outside the supported range " +
                      $"{TaskServerProtocol.MinimumSupported}-{TaskServerProtocol.MaximumSupported}.";
            var response = new ProtocolCompatibilityResponse(
                supported,
                store.Status().Protocol,
                reason);
            return supported ? Results.Ok(response) : Results.Json(response, statusCode: StatusCodes.Status426UpgradeRequired);
        });

        api.MapGet("/workspaces", async (TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListWorkspacesAsync(ct)));
        api.MapPost("/workspaces", async (HttpContext context, CreateWorkspaceRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.CreateWorkspaceAsync(request, Actor(context), ct), StatusCodes.Status201Created))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        api.MapGet("/projects", async (string? workspaceId, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListProjectsAsync(workspaceId, ct)));
        api.MapPost("/projects", async (HttpContext context, CreateProjectRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.CreateProjectAsync(request, Actor(context), ct), StatusCodes.Status201Created))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        var orchestratorContexts = api.MapGroup("/orchestrator-contexts");
        orchestratorContexts.MapGet("", async (
            bool? includeHidden,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(async () => new OrchestratorContextListResponse(
                await store.ListOrchestratorContextsAsync(includeHidden == true, ct))));
        orchestratorContexts.MapPut("/projects/{projectIdentity}", async (
            HttpContext context,
            string projectIdentity,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.EnsureOrchestratorContextAsync(
                projectIdentity, null, Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        orchestratorContexts.MapPut("/projects/{projectIdentity}/tasks/{taskIdentity}", async (
            HttpContext context,
            string projectIdentity,
            string taskIdentity,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.EnsureOrchestratorContextAsync(
                projectIdentity, taskIdentity, Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        orchestratorContexts.MapGet("/projects/{projectIdentity}/turns", async (
            HttpContext context,
            string projectIdentity,
            int? limit,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.ReadOrchestratorContextAsync(
                projectIdentity, null, limit ?? 500, Actor(context), ct)));
        orchestratorContexts.MapGet("/projects/{projectIdentity}/tasks/{taskIdentity}/turns", async (
            HttpContext context,
            string projectIdentity,
            string taskIdentity,
            int? limit,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.ReadOrchestratorContextAsync(
                projectIdentity, taskIdentity, limit ?? 500, Actor(context), ct)));
        orchestratorContexts.MapPost("/projects/{projectIdentity}/turns", async (
            HttpContext context,
            string projectIdentity,
            AppendOrchestratorContextTurnRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.AppendOrchestratorContextTurnAsync(
                projectIdentity, null, request, Actor(context), ct), StatusCodes.Status201Created))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Chat)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        orchestratorContexts.MapPost("/projects/{projectIdentity}/tasks/{taskIdentity}/turns", async (
            HttpContext context,
            string projectIdentity,
            string taskIdentity,
            AppendOrchestratorContextTurnRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.AppendOrchestratorContextTurnAsync(
                projectIdentity, taskIdentity, request, Actor(context), ct), StatusCodes.Status201Created))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Chat)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        orchestratorContexts.MapPost("/projects/{projectIdentity}/legacy-import", async (
            HttpContext context,
            string projectIdentity,
            ImportLegacyOrchestratorChatRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.ImportLegacyOrchestratorChatAsync(
                projectIdentity, request, Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        var hosts = api.MapGroup("/hosts");
        hosts.MapGet("/{hostId}/runtime-capacity", async (
            string hostId,
            RuntimeCapacitySettingsService capacity,
            CancellationToken ct)
            => await InvokeNullableAsync(() => capacity.GetAsync(hostId, ct)));
        hosts.MapPut("/{hostId}/runtime-capacity", async (
            HttpContext context,
            string hostId,
            UpdateRuntimeCapacitySettingsRequest request,
            RuntimeCapacitySettingsService capacity,
            CancellationToken ct)
            => await InvokeAsync(() => capacity.UpdateAsync(
                hostId,
                request,
                Actor(context),
                ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        hosts.MapGet("/{hostId}/project-policy", async (
            string hostId,
            HostProjectPolicyService projectPolicy,
            CancellationToken ct)
            => await InvokeNullableAsync(() => projectPolicy.GetAsync(hostId, ct)));
        hosts.MapPut("/{hostId}/project-policy", async (
            HttpContext context,
            string hostId,
            UpdateHostProjectPolicyRequest request,
            HostProjectPolicyService projectPolicy,
            CancellationToken ct)
            => await InvokeAsync(() => projectPolicy.UpdateAsync(
                hostId,
                request,
                Actor(context),
                ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        api.MapGet("/projects/{projectId}/tasks", async (string projectId, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListTasksAsync(projectId, ct)));
        api.MapGet("/projects/{projectId}/tasks/{taskIdentity}", async (string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await InvokeNullableAsync(() => store.GetTaskAsync(projectId, taskIdentity, ct)));
        api.MapGet("/projects/{projectId}/tasks/{taskIdentity}/attempts", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListAttemptsAsync(projectId, taskIdentity, ct)));
        api.MapGet("/projects/{projectId}/tasks/{taskIdentity}/history", async (
            string projectId, string taskIdentity, long? after, TaskServerStore store, CancellationToken ct)
            => await InvokeNullableAsync(() => store.GetTaskHistoryAsync(projectId, taskIdentity, after ?? 0, ct)));
        api.MapPost("/projects/{projectId}/tasks", async (
            HttpContext context, string projectId, CreateTaskRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.CreateTaskAsync(projectId, request, Actor(context), ct), StatusCodes.Status201Created))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        api.MapPut("/projects/{projectId}/tasks/{taskIdentity}", async (
            HttpContext context, string projectId, string taskIdentity, UpdateTaskRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeNullableAsync(() => store.UpdateTaskAsync(projectId, taskIdentity, request, Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        var runners = api.MapGroup("/runners")
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);
        runners.MapGet("", async (TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListHostProjectionsAsync(ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        runners.MapPut("/{runnerId}", async (
            HttpContext context, string runnerId, RegisterRunnerRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.RegisterRunnerAsync(runnerId, request, Actor(context), ct)));
        runners.MapPut("/{runnerId}/capabilities", async (
            HttpContext context,
            string runnerId,
            CapabilityAdvertisementRequest request,
            TaskServerStore store,
            CancellationToken ct) =>
        {
            if (!string.Equals(runnerId, request.RunnerId, StringComparison.Ordinal))
                return Results.BadRequest(new ApiError("runner-id-mismatch", "Route and capability runner ids differ."));
            return await InvokeAsync(() => store.AdvertiseCapabilitiesAsync(request, Actor(context), ct));
        });
        runners.MapPost("/{runnerId}/capability-failures", async (
            HttpContext context,
            string runnerId,
            CapabilityFailureRequest request,
            TaskServerStore store,
            CancellationToken ct) =>
        {
            if (!string.Equals(runnerId, request.RunnerId, StringComparison.Ordinal))
                return Results.BadRequest(new ApiError("runner-id-mismatch", "Route and capability runner ids differ."));
            return await InvokeAsync(() => store.ReportCapabilityFailureAsync(request, Actor(context), ct));
        });
        runners.MapPost("/{runnerId}/reports", async (
            HttpContext context,
            string runnerId,
            HostReportRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.AcceptHostReportAsync(
                runnerId,
                request,
                Actor(context),
                ct)));
        runners.MapPost("/{runnerId}/claims", async (
            HttpContext context, string runnerId, ClaimRequest request, TaskServerStore store, CancellationToken ct) =>
        {
            if (!string.Equals(runnerId, request.RunnerId, StringComparison.Ordinal))
                return Results.BadRequest(new ApiError("runner-id-mismatch", "Route and request runner ids differ."));
            return await InvokeAsync(() => store.ClaimAsync(request, Actor(context), ct));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim)
            .RequireTaskServerScope(TaskServerScopes.RunsClaim);
        runners.MapPut("/{runnerId}/outbox-status", async (
            HttpContext context,
            string runnerId,
            RunnerOutboxStatusRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.ReportRunnerOutboxAsync(
                runnerId, request, Actor(context), ct)));
        runners.MapPost("/{runnerId}/review-claims", async (
            HttpContext context, string runnerId, ReviewClaimRequest request, TaskServerStore store, CancellationToken ct) =>
        {
            if (!string.Equals(runnerId, request.ExecutorId, StringComparison.Ordinal))
                return Results.BadRequest(new ApiError("runner-id-mismatch", "Route and request review executor ids differ."));
            return await InvokeAsync(() => store.ClaimReviewAsync(request, Actor(context), ct));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim)
            .RequireTaskServerScope(TaskServerScopes.ReviewsClaim);

        var permits = api.MapGroup("/work-permits")
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);
        permits.MapPost("/{permitId}/accept", async (
            HttpContext context,
            string permitId,
            WorkPermitAcceptRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.AcceptWorkPermitAsync(
                permitId,
                request,
                Actor(context),
                ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        var runs = api.MapGroup("/runs")
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);
        runs.MapPost("/{runId}/reconcile", async (
            HttpContext context,
            string runId,
            RunReconcileRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.ReconcileRunAsync(
                runId,
                request,
                Actor(context),
                ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);
        runs.MapPost("/{runId}/post-steps/{stepExecutionId}/claim", async (
            HttpContext context,
            string runId,
            string stepExecutionId,
            PostStepClaimRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.ClaimPostStepAsync(
                runId,
                stepExecutionId,
                request,
                Actor(context),
                ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);
        runs.MapPost("/{runId}/post-steps/{stepExecutionId}/complete", async (
            HttpContext context,
            string runId,
            string stepExecutionId,
            PostStepCompleteRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.CompletePostStepAsync(
                runId,
                stepExecutionId,
                request,
                Actor(context),
                ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);
        runs.MapPost("/{runId}/lease/renew", async (
            HttpContext context, string runId, LeaseRenewRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.RenewLeaseAsync(runId, request, Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
        runs.MapPost("/{runId}/lease/release", async (
            HttpContext context, string runId, LeaseReleaseRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ReleaseLeaseAsync(runId, request, Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
        runs.MapPost("/{runId}/result-finalization", async (
            HttpContext context,
            string runId,
            ResultFinalizationRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.FinalizeResultAsync(
                runId,
                request,
                Actor(context),
                ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);
        runs.MapPost("/{runId}/completion", async (
            HttpContext context, string runId, CompleteRunRequest request, TaskServerStore store, CancellationToken ct) =>
        {
            if (ProtocolVersion(context) < 2)
                return Results.Json(
                    new ApiError(
                        "protocol-upgrade-required",
                        "Durable result handoff and idempotent completion require runner protocol 2."),
                    statusCode: StatusCodes.Status426UpgradeRequired);
            return await InvokeAsync(() => store.CompleteRunAsync(runId, request, Actor(context), ct));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);
        runs.MapPut("/{runId}/result-handoff", async (
            HttpContext context,
            string runId,
            ResultHandoffRequest request,
            TaskServerStore store,
            CancellationToken ct) =>
        {
            if (ProtocolVersion(context) < 2)
                return Results.Json(
                    new ApiError(
                        "protocol-upgrade-required",
                        "Immutable result handoff requires runner protocol 2."),
                    statusCode: StatusCodes.Status426UpgradeRequired);
            return await InvokeAsync(() => store.AcknowledgeResultHandoffAsync(
                runId, request, Actor(context), ct));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);
        runs.MapGet("/{runId}/result-handoff", async (
            string runId,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeNullableAsync(() => store.GetResultHandoffAsync(runId, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        runs.MapPost("/{runId}/events", async (
            HttpContext context, string runId, EventIngestRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.IngestEventAsync(runId, request, Actor(context), ct), StatusCodes.Status201Created))
            .RequireTaskServerScope(TaskServerScopes.EventsWrite);
        runs.MapGet("/{runId}/events", async (string runId, long? after, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListEventsAsync(runId, after ?? 0, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        runs.MapPost("/{runId}/artifacts", async (
            HttpContext context, string runId, ArtifactIngestRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.IngestArtifactAsync(runId, request, Actor(context), ct), StatusCodes.Status201Created));
        runs.MapGet("/{runId}/artifacts", async (string runId, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListArtifactsAsync(runId, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        runs.MapGet("/{runId}/artifacts/{artifactId}/content", async (
            string runId, string artifactId, TaskServerStore store, CancellationToken ct)
            => await InvokeNullableAsync(() => store.GetArtifactContentAsync(runId, artifactId, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        var reviews = api.MapGroup("/reviews")
            .RequireTaskServerScope(TaskServerScopes.ReviewsWrite);
        reviews.MapPost("/subjects", async (
            HttpContext context, CreateReviewSubjectRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.CreateReviewSubjectAsync(request, Actor(context), ct), StatusCodes.Status201Created))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Review)
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        reviews.MapGet("/subjects/{subjectId}", async (string subjectId, TaskServerStore store, CancellationToken ct)
            => await InvokeNullableAsync(() => store.GetReviewSubjectAsync(subjectId, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        reviews.MapGet("/attempts/{attemptId}", async (string attemptId, TaskServerStore store, CancellationToken ct)
            => await InvokeNullableAsync(() => store.GetReviewAttemptAsync(attemptId, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        reviews.MapPost("/attempts/{attemptId}/lease/renew", async (
            HttpContext context, string attemptId, ReviewLeaseRenewRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.RenewReviewLeaseAsync(attemptId, request, Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
        reviews.MapPost("/attempts/{attemptId}/report", async (
            HttpContext context, string attemptId, ReviewReportRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ReportReviewAsync(attemptId, request, Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Review);
        reviews.MapPost("/attempts/{attemptId}/cleanup", async (
            HttpContext context, string attemptId, ReviewCleanupRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.CleanupReviewAsync(attemptId, request, Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);

        var orchestration = api.MapGroup("/orchestration")
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        orchestration.MapGet("/projects/{projectId}/flow-definition", async (
            string projectId,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeNullableAsync(() => store.GetFlowDefinitionAsync(projectId, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        orchestration.MapPut("/projects/{projectId}/flow-definition", async (
            HttpContext context,
            string projectId,
            UpsertFlowDefinitionRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.UpsertFlowDefinitionAsync(
                projectId, request, Actor(context), ct)));
        orchestration.MapGet("/runs", async (
            string? projectId,
            string? status,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.ListOrchestrationRunsAsync(projectId, status, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        orchestration.MapGet("/runs/{runId}", async (
            string runId,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeNullableAsync(() => store.GetOrchestrationRunAsync(runId, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        orchestration.MapPost("/projects/{projectId}/runs", async (
            HttpContext context,
            string projectId,
            CreateOrchestrationRunRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.CreateOrchestrationRunAsync(
                projectId, request, Actor(context), ct), StatusCodes.Status201Created))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);
        orchestration.MapPost("/claims", async (
            HttpContext context,
            OrchestrationClaimRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.ClaimOrchestrationAsync(
                request, Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim)
            .RequireTaskServerScope(TaskServerScopes.OrchestrationClaim);
        orchestration.MapPost("/runs/{runId}/lease/renew", async (
            HttpContext context,
            string runId,
            OrchestrationLeaseRenewRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.RenewOrchestrationLeaseAsync(
                runId, request, Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue)
            .RequireTaskServerScope(TaskServerScopes.OrchestrationWrite);
        orchestration.MapPost("/runs/{runId}/lease/release", async (
            HttpContext context,
            string runId,
            ReleaseOrchestrationLeaseRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.ReleaseOrchestrationLeaseAsync(
                runId, request, Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue)
            .RequireTaskServerScope(TaskServerScopes.OrchestrationWrite);
        orchestration.MapPost("/runs/{runId}/stages/complete", async (
            HttpContext context,
            string runId,
            CompleteOrchestrationStageRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.CompleteOrchestrationStageAsync(
                runId, request, Actor(context), ct)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep)
            .RequireTaskServerScope(TaskServerScopes.OrchestrationWrite);

        var management = api.MapGroup("/management")
            .RequireTaskServerScope(TaskServerScopes.Management);
        management.MapGet("/principals", async (TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListPrincipalsAsync(ct)));
        management.MapPost("/principals", async (
            HttpContext context,
            CreatePrincipalRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(
                () => store.CreatePrincipalAsync(request, Actor(context), ct),
                StatusCodes.Status201Created));
        management.MapPost("/principals/{principalId}/rotate", async (
            HttpContext context,
            string principalId,
            RotatePrincipalRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.RotatePrincipalAsync(
                principalId, request, Actor(context), ct)));
        management.MapPost("/principals/{principalId}/revoke", async (
            HttpContext context,
            string principalId,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.RevokePrincipalAsync(
                principalId, Actor(context), ct)));
        management.MapGet("/status", (TaskServerStore store) => Results.Ok(store.Status()));
        management.MapGet("/outboxes", async (TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListRunnerOutboxesAsync(ct)));
        management.MapGet("/hosts", async (TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListHostProjectionsAsync(ct)));
        management.MapPut("/mode", async (
            HttpContext context, ChangeModeRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ChangeModeAsync(request, Actor(context), ct)));
        management.MapPost("/prepare-shutdown", async (
            HttpContext context, PrepareShutdownRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.PrepareShutdownAsync(request, Actor(context), ct)));
        management.MapPost("/backups", async (
            HttpContext context, BackupRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.CreateBackupAsync(request, Actor(context), ct), StatusCodes.Status201Created));
        management.MapPost("/restore", async (
            HttpContext context, RestoreRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.RestoreBackupAsync(request, Actor(context), ct)));
        management.MapPost("/attempts/{runId}/resolve-unknown", async (
            HttpContext context, string runId, ResolveUnknownAttemptRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ResolveUnknownAttemptAsync(runId, request, Actor(context), ct)));
        management.MapGet("/audit", async (long? after, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListAuditAsync(after ?? 0, ct)));
        management.MapGet("/invariants", async (TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.GetInvariantRegistryAsync(ct)));
        management.MapGet("/remote-hosts", async (TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListRunnerCapabilitySnapshotsAsync(ct)));
        management.MapPost("/remote-hosts/{hostId}/operator-drain", async (
            HttpContext context,
            string hostId,
            OperatorHostDrainRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.RequestOperatorHostDrainAsync(
                hostId, request, Actor(context), ct)));
        management.MapPost("/remote-hosts/{hostId}/automatic-drain/clear", async (
            HttpContext context,
            string hostId,
            ClearAutomaticHostDrainRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await InvokeAsync(() => store.ClearAutomaticHostDrainAsync(
                hostId, request, Actor(context), ct)));
        management.MapPost("/migrations/legacy/inventory", async (
            LegacyMigrationRequest request, LegacyMigrationService migration, CancellationToken ct)
            => await InvokeAsync(() => migration.InventoryAsync(request, ct)));
        management.MapPost("/migrations/legacy/import", async (
            HttpContext context, LegacyMigrationRequest request, LegacyMigrationService migration, CancellationToken ct)
            => await InvokeAsync(() => migration.ImportAsync(request, Actor(context), ct)));
        management.MapGet("/migrations/legacy/reports", async (
            TaskServerStore store, CancellationToken ct)
            => Results.Ok(await store.ListLegacyMigrationReportsAsync(ct)));
        management.MapGet("/migrations/legacy/reports/{migrationId}", async (
            string migrationId, TaskServerStore store, CancellationToken ct)
            => await store.GetLegacyMigrationReportAsync(migrationId, ct) is { } report
                ? Results.Ok(report)
                : Results.NotFound(new ApiError("legacy-migration-report-not-found", "Migration report was not found.")));

        var retention = management.MapGroup("/retention");
        retention.MapGet("/policy", async (RetentionManagementService retentionManagement, CancellationToken ct)
            => await InvokeAsync(() => retentionManagement.GetWorkspacePolicyAsync(ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        retention.MapPut("/policy", async (
            HttpContext context, UpdateRetentionPolicyRequest request, RetentionManagementService retentionManagement, CancellationToken ct)
            => await InvokeAsync(() => retentionManagement.UpdateWorkspacePolicyAsync(request, Actor(context), ct)));
        retention.MapGet("/policy/projects/{projectId}", async (
            string projectId, RetentionManagementService retentionManagement, CancellationToken ct)
            => await InvokeAsync(() => retentionManagement.GetProjectPolicyAsync(projectId, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        retention.MapPut("/policy/projects/{projectId}", async (
            HttpContext context, string projectId, UpdateRetentionPolicyRequest request, RetentionManagementService retentionManagement, CancellationToken ct)
            => await InvokeAsync(() => retentionManagement.UpdateProjectPolicyAsync(projectId, request, Actor(context), ct)));
        retention.MapDelete("/policy/projects/{projectId}", async (
            HttpContext context, string projectId, long expectedVersion, RetentionManagementService retentionManagement, CancellationToken ct)
            => await InvokeAsync(async () =>
            {
                await retentionManagement.DeleteProjectPolicyAsync(projectId, expectedVersion, Actor(context), ct);
                return new { deleted = true };
            }));
        retention.MapPost("/plan", async (
            HttpContext context, RunRetentionRequest request, RetentionManagementService retentionManagement, CancellationToken ct)
            => await InvokeAsync(() => retentionManagement.PlanAsync(request, Actor(context), ct)));
        retention.MapPost("/apply", async (
            HttpContext context, RunRetentionRequest request, RetentionManagementService retentionManagement, CancellationToken ct)
            => await InvokeAsync(() => retentionManagement.ApplyAsync(request, Actor(context), ct)));
        retention.MapGet("/runs", async (RetentionManagementService retentionManagement, CancellationToken ct)
            => await InvokeAsync(() => retentionManagement.ListRunsAsync(ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        retention.MapGet("/runs/{runId}", async (string runId, RetentionManagementService retentionManagement, CancellationToken ct)
            => await InvokeNullableAsync(() => retentionManagement.GetRunAsync(runId, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        retention.MapGet("/archive/{taskId}", async (string taskId, RetentionManagementService retentionManagement, CancellationToken ct)
            => await InvokeNullableAsync(() => retentionManagement.GetManifestAsync(taskId, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        retention.MapPost("/archive/{taskId}", async (
            HttpContext context, string taskId, RetentionArchiveTaskRequest request, RetentionManagementService retentionManagement, CancellationToken ct)
            => await InvokeAsync(() => retentionManagement.ArchiveNowAsync(taskId, request, Actor(context), ct)));
        retention.MapPost("/archive/{taskId}/restore", async (
            HttpContext context, string taskId, RetentionManagementService retentionManagement, CancellationToken ct)
            => await InvokeAsync(async () =>
            {
                await retentionManagement.RestoreAsync(taskId, Actor(context), ct);
                return new { restored = true, taskId };
            }));

        var fullBackups = management.MapGroup("/backups/full");
        fullBackups.MapPost("", async (HttpContext context, FullBackupManagementService fullBackup, CancellationToken ct)
            => await InvokeAsync(() => fullBackup.CreateAsync(Actor(context), ct), StatusCodes.Status201Created));
        fullBackups.MapGet("", async (FullBackupManagementService fullBackup, CancellationToken ct)
            => await InvokeAsync(async () => new ListFullBackupsResponse(await fullBackup.ListAsync(ct))))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        fullBackups.MapPost("/{backupId}/verify", async (string backupId, FullBackupManagementService fullBackup, CancellationToken ct)
            => await InvokeAsync(() => fullBackup.VerifyAsync(backupId, ct)));
        fullBackups.MapPost("/{backupId}/restore", async (
            HttpContext context, string backupId, FullBackupManagementService fullBackup, CancellationToken ct)
            => await InvokeAsync(() => fullBackup.RestoreAsync(backupId, Actor(context), ct)));
    }

    private static string Actor(HttpContext context)
        => context.Request.Headers["X-Actor-Id"].FirstOrDefault()
           ?? context.Request.Headers["X-Client-Id"].FirstOrDefault()
           ?? "local-compatibility";

    private static int ProtocolVersion(HttpContext context)
        => int.TryParse(
            context.Request.Headers[TaskServerProtocol.HeaderName].FirstOrDefault(),
            out var version)
            ? version
            : 0;

    private static async Task<IResult> InvokeAsync<T>(Func<Task<T>> action, int successStatus = StatusCodes.Status200OK)
    {
        try
        {
            var value = await action();
            return Results.Json(value, statusCode: successStatus);
        }
        catch (Exception exception)
        {
            return MapError(exception);
        }
    }

    private static async Task<IResult> InvokeNullableAsync<T>(Func<Task<T?>> action) where T : class
    {
        try
        {
            var value = await action();
            return value is null ? Results.NotFound(new ApiError("not-found", "Resource was not found.")) : Results.Ok(value);
        }
        catch (Exception exception)
        {
            return MapError(exception);
        }
    }

    private static IResult MapError(Exception exception) => exception switch
    {
        ArtifactArchivedException archived => Results.Json(
            new ApiError(
                "artifact-archived",
                archived.Message,
                new { taskId = archived.TaskId, taskKey = archived.TaskKey, manifestUrl = $"/api/v1/management/retention/archive/{archived.TaskId}" }),
            statusCode: StatusCodes.Status409Conflict),
        HostOrchestratorContractException contract => Results.Json(
            new ApiError(
                "host-orchestrator-contract-unsupported",
                contract.Message,
                new
                {
                    minimum = HostOrchestratorContract.MinimumSupported,
                    maximum = HostOrchestratorContract.MaximumSupported,
                }),
            statusCode: StatusCodes.Status426UpgradeRequired),
        TaskServerProtocolException protocol => Results.Json(
            new ApiError("protocol-unsupported", protocol.Message), statusCode: StatusCodes.Status426UpgradeRequired),
        TaskServerConflictException conflict => Results.Conflict(new ApiError(conflict.Code, conflict.Message)),
        KeyNotFoundException => Results.NotFound(new ApiError("not-found", exception.Message)),
        ArgumentException or FormatException => Results.BadRequest(new ApiError("invalid-request", exception.Message)),
        DirectoryNotFoundException => Results.BadRequest(new ApiError("legacy-root-not-found", exception.Message)),
        InvalidOperationException => Results.Json(new ApiError("not-ready", exception.Message), statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Json(new ApiError("internal-error", "The Task Server could not complete the request."), statusCode: StatusCodes.Status500InternalServerError),
    };
}
