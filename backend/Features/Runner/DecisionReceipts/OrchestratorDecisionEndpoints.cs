using System.Text.Json;
using AgentStudio.Bus;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Registry;
using AgentStudio.Projects;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;

namespace AgentStudio.Runner;

/// <summary>
/// AGT-W73 OD-S1 routes. The engine reads frozen grounds input, creates the
/// shadow model job and stores the receipt under the orchestration scope; a
/// runner host claims and completes the job with its own provider credential;
/// Studio reads receipts. Nothing here decides a transition.
/// </summary>
public static class OrchestratorDecisionEndpoints
{
    private static readonly JsonSerializerOptions WireJson = DecisionGroundsPolicy.Json;

    public static void MapOrchestratorDecisionEndpoints(this WebApplication app, bool mapsEngineRoutes)
    {
        if (mapsEngineRoutes)
        {
            var engine = app.MapGroup("/api/v1/orchestration");
            engine.MapGet("/runs/{runId}/grounds-input", (HttpContext context, string runId,
                AccessSecurityStore security, OrchestratorDecisionStore store) =>
                MonolithOrchestrationEndpoints.Authorize(context, security) is null ? Results.Unauthorized()
                    : store.GetInputs(runId) is { } input ? Wire(input) : Results.NotFound());
            engine.MapGet("/decision-grounds/pending", (HttpContext context, int? limit,
                AccessSecurityStore security, OrchestratorDecisionStore store) =>
                MonolithOrchestrationEndpoints.Authorize(context, security) is null ? Results.Unauthorized()
                    : Wire(store.PendingReceipts(limit ?? 10)));
            engine.MapPost("/host-decisions", (HttpContext context, HostDecisionOccurrenceRequestV1 request,
                AccessSecurityStore security, OrchestratorDecisionStore store,
                DecisionRuleVersionStore rules, WorkspaceRegistry workspaces,
                WorkspaceSettingsService settings) => MonolithOrchestrationEndpoints.Execute(() =>
            {
                if (MonolithOrchestrationEndpoints.Authorize(context, security) is null)
                    return Results.Unauthorized();
                if (workspaces.Find(request.WorkspaceId) is null) return Results.NotFound();
                if (string.IsNullOrWhiteSpace(request.OccurrenceId)
                    || string.IsNullOrWhiteSpace(request.HostId)
                    || request.HostId.Length > 128 || request.OccurrenceId.Length > 160
                    || string.IsNullOrWhiteSpace(request.TriggerKind)
                    || string.IsNullOrWhiteSpace(request.Profile)
                    || request.ObservedAt.Kind != DateTimeKind.Utc
                    || request.Facts is null || request.Alternatives is null || request.Omissions is null
                    || request.Facts.Count > DecisionGroundsPolicy.MaxFacts
                    || request.Facts.Any(fact => fact is null || fact.Source is null
                        || string.IsNullOrWhiteSpace(fact.Source.Ref)
                        || fact.Source.Ref.Length > 256
                        || fact.Source.Ref.Contains("raw-session", StringComparison.OrdinalIgnoreCase)
                        || fact.Source.Ref.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                        || fact.Source.Ref.StartsWith('/'))
                    || request.Alternatives.Count > DecisionGroundsPolicy.MaxAlternatives
                    || request.Alternatives.Any(candidate => candidate is null
                        || !DecisionActionKinds.All.Contains(candidate.Action))
                    || request.Profile == "capacity-check" && request.TriggerKind != "capacity-check"
                    || request.Cadence is not (null or "scheduled" or "event"))
                    return Results.BadRequest(new ApiError("invalid-host-decision", "Host occurrence has invalid scope, trigger, or candidates."));
                var workspace = settings.Get(request.WorkspaceId);
                var activeRules = rules.EffectiveRules(request.WorkspaceId);
                var policyVersion = rules.ActiveVersionId(request.WorkspaceId) ?? "platform.v1";
                var model = activeRules
                    .Where(rule => rule.Trigger == request.Profile).Select(rule => rule.Model)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                    ?? workspace.OrchestratorModel ?? "gpt-5.6-sol";
                if (!DecisionRulePolicy.Validate([new DecisionRuleV1("model-route", request.Profile,
                        "host.health", null, [], "A1", model, "ask")], false).Accepted)
                    return Results.BadRequest(new ApiError("model-below-floor", "Host decision model is below the trusted correctness floor."));
                var at = request.ObservedAt;
                var ledger = new CardRunLedgerV1(0, 0, 0, DecisionTokenUsageV1.Zero, 0,
                    DecisionTokenUsageV1.Zero, 0, 0, 0, [], DecisionPriceStatuses.NoUsage, "host", []);
                var grounds = new DecisionGroundsInputV1(request.OccurrenceId,
                    new(request.TriggerKind, request.Profile, at, request.Cadence),
                    new("host", null, null, null, null, null, request.HostId, request.WorkspaceId),
                    null, null, policyVersion,
                    new("codex", model, workspace.OrchestratorThinkingLevel ?? "high",
                        DecisionModelCustody.RunnerHostJob),
                    request.Facts.Concat(DecisionRulePolicy.Signals(activeRules, request.Profile,
                        request.Facts, policyVersion, at)).ToArray(), request.Alternatives, [], ledger,
                    new("host.v1", "Host occurrence has no card rounds.", null, 0, 0, 0), request.Omissions);
                return Wire(store.FreezeInputs(new(grounds, DecisionActionKinds.Observe,
                    request.HostId, "Host occurrence", DecisionReceiptPolicy.ActiveMode)));
            }));
            engine.MapPost("/decision-jobs", (HttpContext context, OrchestratorDecisionJobRequest request,
                AccessSecurityStore security, OrchestratorDecisionStore store) =>
                MonolithOrchestrationEndpoints.Execute(() =>
                    MonolithOrchestrationEndpoints.Authorize(context, security) is null ? Results.Unauthorized()
                        : Wire(store.CreateJob(request))));
            engine.MapGet("/decision-jobs/{jobId}", (HttpContext context, string jobId,
                AccessSecurityStore security, OrchestratorDecisionStore store) =>
                MonolithOrchestrationEndpoints.Authorize(context, security) is null ? Results.Unauthorized()
                    : store.GetJob(jobId) is { } job ? Wire(job) : Results.NotFound());
            engine.MapPut("/runs/{runId}/decision-receipt", async (HttpContext context, string runId,
                OrchestratorDecisionReceiptV1 receipt, AccessSecurityStore security,
                OrchestratorDecisionStore store, AgentMessageBusBridge bus, CancellationToken ct) =>
            {
                OrchestratorDecisionReceiptV1? createdReceipt = null;
                IReadOnlyList<DecisionLateCallV1> billedCalls = [];
                var result = MonolithOrchestrationEndpoints.Execute(() =>
                {
                    if (MonolithOrchestrationEndpoints.Authorize(context, security) is null)
                        return Results.Unauthorized();
                    if (receipt.OccurrenceId != runId)
                        return Results.BadRequest(new ApiError("invalid-request", "Receipt occurrence does not match the route."));
                    var projection = store.PutReceiptWithProjection(receipt);
                    if (projection.Created)
                    {
                        createdReceipt = projection.Receipt;
                        billedCalls = projection.OtherBilledCalls;
                    }
                    return Wire(projection.Receipt);
                });
                if (createdReceipt is not null)
                    await bus.EmitDecisionReceiptAsync(createdReceipt, ct, billedCalls);
                return result;
            });
        }

        app.MapPost("/api/runner/orchestrator-decisions/claim", (HttpContext context,
            OrchestratorDecisionJobClaimRequest request, OrchestratorDecisionStore store) =>
            RunnerMatches(context, request.RunnerId) ? Wire(store.Claim(request)) : Results.Unauthorized())
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);
        app.MapPost("/api/runner/orchestrator-decisions/complete", async (HttpContext context,
            OrchestratorDecisionJobCompletion completion, OrchestratorDecisionStore store,
            AgentMessageBusBridge bus, CancellationToken ct) =>
        {
            if (!RunnerMatches(context, completion.RunnerId)) return Results.Unauthorized();
            var job = store.GetJob(completion.JobId);
            var projection = store.CompleteWithProjection(completion);
            if (!projection.Accepted)
                return Results.Conflict(new { accepted = false, error = "stale decision-job claim" });
            var receipt = job is null ? null : store.ReceiptForOccurrence(job.OccurrenceId);
            if (receipt is not null)
                foreach (var call in projection.NewLateCalls)
                    await bus.EmitDecisionLateCallAsync(receipt, call, ct);
            return Results.Ok(new { accepted = true });
        })
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        var studio = app.MapGroup("/api/orchestration/decision-receipts");
        studio.MapGet("", (HttpContext context, string? taskId, string? projectId, string? hostId,
            string? workspaceId, int? limit,
            OrchestratorDecisionStore store, ProjectRegistry projects) =>
            Wire(store.ListReceipts(string.IsNullOrWhiteSpace(taskId) ? null : taskId, limit ?? 20,
                string.IsNullOrWhiteSpace(projectId) ? null : projectId,
                project => AllowsProject(context, project, projects), hostId, workspaceId)));
        studio.MapGet("/spend", (HttpContext context, string? taskId, string? projectId, string? hostId,
            string? workspaceId,
            OrchestratorDecisionStore store, ProjectRegistry projects) =>
            Wire(store.SpendSummary(string.IsNullOrWhiteSpace(taskId) ? null : taskId,
                string.IsNullOrWhiteSpace(projectId) ? null : projectId,
                project => AllowsProject(context, project, projects), hostId, workspaceId)));
        studio.MapGet("/{receiptId}", (HttpContext context, string receiptId,
            OrchestratorDecisionStore store, ProjectRegistry projects) =>
            AuthorizedReceipt(context, receiptId, store, projects) is { } receipt
                ? Wire(receipt) : Results.NotFound());
        studio.MapGet("/{receiptId}/operator-intent", (HttpContext context, string receiptId,
            OrchestratorDecisionStore store, ProjectRegistry projects) =>
            AuthorizedReceipt(context, receiptId, store, projects) is null ? Results.NotFound()
                : store.GetOperatorIntent(receiptId) is { } intent ? Wire(intent) : Results.NotFound());
        studio.MapPost("/{receiptId}/operator-intent", async (HttpContext context, string receiptId,
            DecisionOperatorIntentRequest request, OrchestratorDecisionStore store,
            ProjectRegistry projects, TaskScannerService scanner, TaskRunnerService runner,
            RunLeaseService leases, RemoteRunStopRequestStore stops,
            TaskTransitionService transitions, ProjectSettingsService settings,
            V1ReviewExecutorRegistry runners, IConfiguration configuration, CancellationToken ct) =>
            await ApplyOperatorIntentAsync(context, receiptId, request, store, projects, scanner,
                runner, leases, stops, transitions, settings, runners, configuration, ct))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
        studio.MapGet("/{receiptId}/outcome", (HttpContext context, string receiptId,
            OrchestratorDecisionStore store, MonolithOrchestrationStore orchestration,
            ProjectRegistry projects) =>
        {
            if (AuthorizedReceipt(context, receiptId, store, projects) is not { } receipt)
                return Results.NotFound();
            var occurrence = orchestration.GetOccurrence(receipt.OccurrenceId);
            var later = receipt.Scope.TaskId is { } taskId
                ? store.ReceiptsForTask(taskId).Where(item => item.Scope.ProjectId == receipt.Scope.ProjectId
                        && item.EndedAt > receipt.EndedAt)
                    .Select(store.Summary).ToArray()
                : [];
            return Wire(new
            {
                receipt.ReceiptId,
                receipt.OccurrenceId,
                occurrenceStatus = occurrence?.Run.Status,
                appliedAction = occurrence?.MonolithReceipt?.Action,
                appliedReason = occurrence?.MonolithReceipt?.Reason,
                engineAction = occurrence?.EngineReceipt?.Action,
                operatorIntent = store.GetOperatorIntent(receipt.ReceiptId),
                spend = store.Summary(receipt),
                lateCalls = store.LateCalls(receipt.OccurrenceId),
                laterDecisions = later,
            });
        });
    }

    private static IResult Wire(object value) => Results.Json(value, WireJson);

    private static async Task<IResult> ApplyOperatorIntentAsync(HttpContext context, string receiptId,
        DecisionOperatorIntentRequest request, OrchestratorDecisionStore store, ProjectRegistry projects,
        TaskScannerService scanner, TaskRunnerService runner, RunLeaseService leases,
        RemoteRunStopRequestStore stops, TaskTransitionService transitions,
        ProjectSettingsService settings, V1ReviewExecutorRegistry runners,
        IConfiguration configuration, CancellationToken ct)
    {
        var receipt = AuthorizedReceipt(context, receiptId, store, projects);
        if (receipt is null) return Results.NotFound();
        // AGT-3147: a pending remote decision Stop can be lost on lease release
        // or takeover. Keep the action unavailable until every attempt exit
        // applies the card hold before another claim can begin.
        if (request.Action == "stop")
            return Results.Conflict(new ApiError("stop-not-available",
                "Stop is temporarily unavailable for decision receipts."));
        var activeRunner = receipt.Scope.TaskId is { } taskKey
            ? leases.Peek(taskKey).Lease?.RunnerId : null;
        var location = receipt.Scope.ProjectId is { } projectId
            ? ProjectExecutionPolicy.ResolveExecutionLocation(settings.Get(projectId))
            : ExecutionLocations.Local;
        var classCapability = ExecutionLocations.RequiredClassCapability(location);
        var classTimeouts = activeRunner is null && classCapability is not null
            ? runners.GetCodingRunTimeoutSecondsForClass(classCapability) : [];
        var runnerTimeoutSeconds = activeRunner is not null
            ? runners.GetCodingRunTimeoutSeconds(activeRunner)
            : classCapability is not null
                ? classTimeouts.Count == 0 ? null
                    : request.Action == "raise" ? classTimeouts.Max() : classTimeouts.Min()
                : runners.GetCodingRunTimeoutSeconds(location);
        if (request.Action is "raise" or "cut" && runnerTimeoutSeconds is null
            && (activeRunner is not null || location != ExecutionLocations.Local)
            && (receipt.Scope.TaskId is not { } cardId
                || store.AppliedRunLimitSeconds(cardId, receipt.Scope.ProjectId) is null))
            return Results.Conflict(new ApiError("runner-timeout-unavailable",
                "The coding runner must report its run timeout before this limit can be applied."));
        var defaultRunLimitSeconds = runnerTimeoutSeconds
            ?? configuration.GetValue("Runner:RunTimeoutSeconds", 3600);
        DecisionOperatorIntentV1 intent;
        try
        {
            var actor = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                ? human.User.Username : context.User.Identity?.Name ?? "Human Operator";
            intent = store.PutOperatorIntent(receiptId, request, actor, defaultRunLimitSeconds);
            if (intent.AppliedAt is not null) return Wire(intent);
            if (intent.Action is "raise" or "cut")
                return Wire(store.ApplyRunLimit(receiptId, defaultRunLimitSeconds));
        }
        catch (ArgumentException ex) { return Results.BadRequest(new ApiError("invalid-request", ex.Message)); }
        catch (InvalidOperationException ex) { return Results.Conflict(new ApiError("invalid-state", ex.Message)); }

        var task = scanner.ScanAllJobs().FirstOrDefault(item => item.Id == intent.TaskId
            && item.ProjectName == receipt.Scope.ProjectId);
        if (task is null) return Results.Conflict(new ApiError("card-missing", "The card is no longer available."));
        var authorityKey = TaskIdentity.AuthorityKey(task);
        await LeaseEndpoints.ClaimGate.WaitAsync(ct);
        try
        {
            var lease = leases.Peek(authorityKey).Lease;
            if (lease is { AttemptId: not null })
            {
                try
                {
                    var stop = stops.Record(authorityKey, RemoteRunStopReasons.DecisionStop, lease.AttemptId,
                        intent.Actor, lease.FencingToken,
                        $"decision-stop:{receiptId}:fence:{lease.FencingToken}", lease.RunnerId);
                    stop = stops.ReconcileWithLease(stop, leases.Peek(authorityKey));
                    if (stop.TerminalReason == "superseded")
                        return Results.Conflict(new ApiError("stop-superseded",
                            "The run changed before the stop request reached its owner. Retry to stop the current run."));
                    if (stop.TerminalAtUtc is null)
                        return Wire(store.SetOperatorOutcome(receiptId, "recorded, not applied:stop-requested"));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Conflict(new ApiError("stop-conflict", ex.Message));
                }
            }
            runner.StopJob(task.Id, task.WatchPath);
            var move = await transitions.MoveAsync(task.Id, TaskStates.Escalated, task.WatchPath, ct,
                cause: TimelineActors.Human(intent.Actor), reason: intent.Reason,
                suppressProductExecution: true, transitionCause: LaneChangeCauses.RunnerRequeue,
                transitionDetail: "decision-stop");
            if (move.Status != MoveJobStatus.Success)
                return Results.Conflict(new ApiError("stop-hold-refused", move.Message ?? move.Status.ToString()));
            return Wire(store.SetOperatorOutcome(receiptId, "applied:stopped-and-held"));
        }
        finally
        {
            LeaseEndpoints.ClaimGate.Release();
        }
    }

    private static OrchestratorDecisionReceiptV1? AuthorizedReceipt(HttpContext context, string receiptId,
        OrchestratorDecisionStore store, ProjectRegistry projects)
    {
        var receipt = store.GetReceipt(receiptId);
        return receipt is not null && AllowsProject(context, receipt.Scope.ProjectId, projects) ? receipt : null;
    }

    private static bool AllowsProject(HttpContext context, string? projectId, ProjectRegistry projects)
        => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human
            || ProjectAccessAuthorization.Allows(human.User, projectId, projects);

    private static bool RunnerMatches(HttpContext context, string runnerId)
        => context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is not RunnerPrincipal principal
            || string.Equals(principal.RunnerId, runnerId, StringComparison.Ordinal);
}
