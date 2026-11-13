using AgentStudio.Security;
using AgentStudio.Quota;
using AgentStudio.Magazine;
using AgentStudio.Tasks;
using AgentStudio.Tokens;

namespace AgentStudio.Personas;

public sealed record PersonaRatingRequest(string Rating);

/// <summary>
/// Workspace persona catalogue and activation (persona team PT-1). The list
/// shows every catalogue entry and custom persona with its activation state.
/// Writes need a human Owner session; the store asks for a fresh step-up on
/// activation and on adopting into a live activation, and for nothing more on
/// pause, resume, deactivate and fork. Refusals answer
/// <c>{ error, message }</c> with the store's status, for example
/// <c>403 step-up-required</c>.
/// </summary>
public static class PersonaEndpoints
{
    public static void MapPersonaEndpoints(this WebApplication app)
    {
        var personas = app.MapGroup("/api/workspaces/{id}/personas");
        PersonaManagerEndpoints.Map(personas);

        personas.MapGet("/items", (string id, PersonaItemStore items) => Results.Ok(items.List(id)))
            .RequirePermission(IamPermissions.WorkspaceRead);
        personas.MapGet("/items/{itemId}", (string id, string itemId, PersonaItemStore items) =>
            items.List(id).FirstOrDefault(item => item.Id == itemId) is { } item
                ? Results.Ok(item) : Results.NotFound()).RequirePermission(IamPermissions.WorkspaceRead);
        personas.MapPost("/items/{itemId}/decision", (string id, string itemId, PersonaItemDecision request,
            HttpContext context, AccessSecurityStore security, PersonaActivationStore activations,
            PersonaItemStore items, PersonaProposalLifecycle lifecycle) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                var owner = AccessSecurityEndpoints.RequireHuman(context, security);
                security.RequirePersonaOwner(owner);
                var source = items.List(id).FirstOrDefault(i => i.Id == itemId);
                if (source is not null && activations.List(id).Personas.FirstOrDefault(p => p.Id == source.PersonaId)
                    ?.Activation?.State == PersonaStates.Retired)
                    return Results.Conflict(new { error = "persona-retired", message = "A retired persona is read-only." });
                try
                {
                    var item = lifecycle.Decide(id, itemId, request, DateTime.UtcNow);
                    security.AppendPersonaAudit(owner.User.Id, "persona.item.decide", id, itemId, request);
                    return Results.Ok(item);
                }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return Results.BadRequest(new { error = "invalid-persona-item-decision", message = ex.Message }); }
                catch (PersonaDecisionConflictException ex) { return Results.Conflict(new { error = ex.Code, message = ex.Message }); }
            })).RequirePermission(IamPermissions.WorkspaceAdminister);
        personas.MapGet("/reports", (string id, PersonaReportStore reports) => Results.Ok(reports.List(id)))
            .RequirePermission(IamPermissions.WorkspaceRead);
        personas.MapGet("/reports/ratings", (string id, PersonaReportRatingStore ratings) =>
            Results.Ok(ratings.List(id))).RequirePermission(IamPermissions.WorkspaceRead);
        personas.MapPost("/reports/{reportId}/rating", (string id, string reportId,
            PersonaRatingRequest request, HttpContext context, AccessSecurityStore security,
            PersonaReportStore reports, PersonaReportRatingStore ratings, PersonaActivationStore activations) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                var owner = AccessSecurityEndpoints.RequireHuman(context, security);
                security.RequirePersonaOwner(owner);
                var report = reports.List(id).FirstOrDefault(row => row.Id == reportId);
                if (report is null) return Results.NotFound();
                if (activations.List(id).Personas.FirstOrDefault(p => p.Id == report.PersonaId)
                    ?.Activation?.State == PersonaStates.Retired)
                    return Results.Conflict(new { error = "persona-retired", message = "A retired persona is read-only." });
                if (request.Rating is not ("useful" or "not-useful"))
                    return Results.BadRequest(new { error = "invalid-report-rating" });
                var rating = ratings.Rate(id, report, request.Rating, DateTime.UtcNow);
                security.AppendPersonaAudit(owner.User.Id, "persona.report.rate", id, reportId, rating);
                return Results.Ok(rating);
            })).RequirePermission(IamPermissions.WorkspaceAdminister);
        personas.MapGet("/{personaId}/sheet", (string id, string personaId,
            PersonaActivationStore activations, PersonaItemStore items,
            PersonaSchedulingService scheduling, PersonaReportStore reports, TaskScannerService scanner,
            PersonaReportRatingStore ratings, ITokenPricing pricing) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                var team = activations.List(id).Personas.Select(p => p.Activation).OfType<PersonaActivationV1>().ToArray();
                var activation = team.FirstOrDefault(p => p.PersonaId == personaId);
                if (activation is null) return Results.NotFound();
                var allItems = items.List(id);
                var receipts = scheduling.Receipts(id);
                var cards = scanner.GetResidentIdentityFacts();
                var now = DateTime.UtcNow;
                return Results.Ok(PersonaStatsProjection.Build(activation, allItems, receipts, reports.List(id),
                    now, cards, pricing) with { Value = PersonaValueProjection.Build(id, activation,
                        team, allItems, receipts, ratings.List(id), cards, now, pricing: pricing) });
            })).RequirePermission(IamPermissions.WorkspaceRead);

        personas.MapGet("/issues", (string id, WeeklyIssueService issues) =>
            Results.Ok(issues.List(id))).RequirePermission(IamPermissions.WorkspaceRead);
        personas.MapGet("/issues/{issueId}", (string id, string issueId, WeeklyIssueService issues) =>
            AccessSecurityEndpoints.Execute(() => issues.Get(id, issueId) is { } issue
                ? Results.Ok(issue) : Results.NotFound())).RequirePermission(IamPermissions.WorkspaceRead);
        personas.MapGet("/issues/{issueId}/export", (string id, string issueId, WeeklyIssueService issues) =>
            AccessSecurityEndpoints.Execute(() => issues.Get(id, issueId) is { } issue
                ? Results.Text(WeeklyIssueHtml.Render(issue), "text/html; charset=utf-8")
                : Results.NotFound())).RequirePermission(IamPermissions.WorkspaceRead);
        personas.MapPost("/issues/compose", async (string id, bool? daily, HttpContext context,
            AccessSecurityStore security, WeeklyIssueService issues, CancellationToken ct) =>
        {
            try
            {
                var owner = AccessSecurityEndpoints.RequireHuman(context, security);
                security.RequirePersonaOwner(owner);
            }
            catch (SecurityOperationException ex)
            { return Results.Json(new { error = ex.Code, message = ex.Message }, statusCode: ex.Status); }
            var issue = await issues.ComposeDueAsync(id, DateTime.UtcNow, daily == true, ct);
            return issue is null ? Results.Conflict(new { error = "issue-not-due" }) : Results.Ok(issue);
        }).RequirePermission(IamPermissions.WorkspaceAdminister);

        app.MapPost("/api/runner/personas/claim", async (HttpContext context,
            PersonaLeaseClaimRequest request, PersonaOccurrenceService occurrences, CancellationToken ct) =>
        {
            if (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is RunnerPrincipal principal
                && principal.RunnerId != request.RunnerId) return Results.Unauthorized();
            return Results.Ok(await occurrences.ClaimAsync(request.RunnerId, DateTime.UtcNow, ct));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);
        app.MapPost("/api/runner/personas/complete", async (HttpContext context,
            PersonaLeaseCompletion completion, PersonaOccurrenceService occurrences, CancellationToken ct) =>
        {
            if (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is RunnerPrincipal principal
                && principal.RunnerId != completion.RunnerId) return Results.Unauthorized();
            var (accepted, error) = await occurrences.CompleteAsync(completion, DateTime.UtcNow, ct);
            await occurrences.PublishCompletionLifecycleAsync(completion, error, ct);
            return accepted ? Results.Ok(new { accepted = true })
                : Results.Json(new { accepted = false, error }, statusCode:
                    error == "stale-persona-lease" ? StatusCodes.Status409Conflict :
                        StatusCodes.Status422UnprocessableEntity);
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        personas.MapGet("", (string id, PersonaActivationStore store) =>
            AccessSecurityEndpoints.Execute(() => Results.Ok(store.List(id))));

        personas.MapGet("/budget", (string id, PersonaActivationStore store,
            PersonaSchedulingService scheduling, WorkspaceQuotaService quota) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                var activations = store.List(id).Personas.Select(p => p.Activation)
                    .Where(a => a is not null).Cast<PersonaActivationV1>().ToArray();
                var snapshot = quota.Snapshot(id);
                return Results.Ok(scheduling.Budget(id, DateTime.UtcNow,
                    snapshot.Limits?.ModelTokensPerWeek, activations));
            })).RequirePermission(IamPermissions.WorkspaceRead);

        personas.MapPut("/budget", (string id, PersonaBudgetSettings request, HttpContext context,
            AccessSecurityStore security, PersonaActivationStore store,
            PersonaSchedulingService scheduling, WorkspaceQuotaService quota) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                var owner = AccessSecurityEndpoints.RequireHuman(context, security);
                security.RequirePersonaOwner(owner);
                var activations = store.List(id).Personas.Select(p => p.Activation)
                    .Where(a => a is { State: PersonaStates.Active or PersonaStates.Shadow })
                    .Cast<PersonaActivationV1>().ToArray();
                var snapshot = quota.Snapshot(id);
                var defaultPool = snapshot.Limits?.ModelTokensPerWeek is { } limit ? limit / 10 : 1_200_000;
                try
                {
                    if (request.PoolTokensPerWeek is null && activations.Sum(a => (long)a.Allowance.TokensPerWeek) > defaultPool)
                        throw new ArgumentException("Active persona allowances exceed the default pool.");
                    scheduling.SetBudget(id, request.PoolTokensPerWeek, request.MaxOpenPerWorkspace,
                        activations.Sum(a => (long)a.Allowance.TokensPerWeek));
                }
                catch (ArgumentException ex)
                {
                    throw new SecurityOperationException(400, "invalid-persona-budget", ex.Message);
                }
                security.AppendPersonaAudit(owner.User.Id, "persona.budget.set", id, "pool", request);
                return Results.Ok(scheduling.Budget(id, DateTime.UtcNow,
                    snapshot.Limits?.ModelTokensPerWeek, activations));
            })).RequirePermission(IamPermissions.WorkspaceAdminister);

        personas.MapPost("/{personaId}/activate", (string id, string personaId, PersonaActivateRequest? request,
            HttpContext context, AccessSecurityStore security, PersonaActivationStore store) =>
            AccessSecurityEndpoints.Execute(() => Results.Ok(store.Activate(id, personaId, request ?? new PersonaActivateRequest(),
                AccessSecurityEndpoints.RequireHuman(context, security)))))
            .RequirePermission(IamPermissions.WorkspaceAdminister);
        personas.MapPut("/{personaId}/attributes", (string id, string personaId,
            PersonaAttributeChangeRequest request, HttpContext context, AccessSecurityStore security,
            PersonaActivationStore store) => AccessSecurityEndpoints.Execute(() => Results.Ok(
                store.ChangeAttributes(id, personaId, request, AccessSecurityEndpoints.RequireHuman(context, security)))))
            .RequirePermission(IamPermissions.WorkspaceAdminister);

        personas.MapPost("/{personaId}/run-now", (string id, string personaId, HttpContext context,
            AccessSecurityStore security, PersonaSweepCoordinator sweeps) =>
            AccessSecurityEndpoints.Execute(() =>
            {
                var owner = AccessSecurityEndpoints.RequireHuman(context, security);
                security.RequirePersonaOwner(owner);
                return sweeps.RequestRun(id, personaId, DateTime.UtcNow)
                    ? Results.Accepted() : Results.Conflict(new { error = "persona-not-running" });
            })).RequirePermission(IamPermissions.WorkspaceAdminister);

        MapChange(personas, "pause", (store, id, personaId, request, owner) => store.Pause(id, personaId, request, owner));
        MapChange(personas, "resume", (store, id, personaId, request, owner) => store.Resume(id, personaId, request, owner));
        MapChange(personas, "deactivate", (store, id, personaId, request, owner) => store.Deactivate(id, personaId, request, owner));
        MapChange(personas, "retire", (store, id, personaId, request, owner) => store.Retire(id, personaId, request, owner));
        MapChange(personas, "adopt", (store, id, personaId, request, owner) => store.Adopt(id, personaId, request, owner));

        personas.MapPost("/{personaId}/fork", (string id, string personaId, PersonaForkRequest request,
            HttpContext context, AccessSecurityStore security, PersonaActivationStore store) =>
            AccessSecurityEndpoints.Execute(() => Results.Ok(store.Fork(id, personaId, request,
                AccessSecurityEndpoints.RequireHuman(context, security)))))
            .RequirePermission(IamPermissions.WorkspaceAdminister);

        personas.MapPost("/{personaId}/operations/{operationId}", async (string id, string personaId, string operationId,
            PersonaManagerRequest? request, HttpContext context, PersonaManagerService managers) =>
        {
            if (!OperatorOperationIds.PersonaRoute.Contains(operationId, StringComparer.Ordinal))
                return Results.NotFound(new { error = "persona-operation-not-found" });
            if (OperatorOperationIds.PersonaManagerRoute.Contains(operationId, StringComparer.Ordinal))
            {
                if (context.Items[AccessSecurityMiddleware.ServiceAccountPrincipalItem]
                    is not ServiceAccountPrincipal principal)
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                var receipt = await managers.ApplyAsync(id, personaId, operationId,
                    request ?? new PersonaManagerRequest(), principal, context.RequestAborted);
                return receipt.Outcome == "applied" ? Results.Ok(receipt)
                    : Results.Json(receipt, statusCode: StatusCodes.Status409Conflict);
            }
            return Results.Json(new { error = "persona-operation-not-implemented",
                message = "The persona operation is catalogued and authorized; its handler belongs to a later slice." },
                statusCode: StatusCodes.Status501NotImplemented);
        }).RequirePermission(IamPermissions.WorkspaceOperate);

        personas.MapPost("/stop-all", (string id, HttpContext context, AccessSecurityStore security,
            PersonaActivationStore store) => AccessSecurityEndpoints.Execute(() =>
        {
            var owner = AccessSecurityEndpoints.RequireHuman(context, security);
            return Results.Ok(new { principalIds = store.StopAll(id, owner) });
        })).RequirePermission(IamPermissions.WorkspaceAdminister);

        personas.MapPost("/renew-grants", (string id, HttpContext context, AccessSecurityStore security,
            PersonaActivationStore store) => AccessSecurityEndpoints.Execute(() =>
        {
            var owner = AccessSecurityEndpoints.RequireHuman(context, security);
            var workspace = store.List(id);
            var active = workspace.Personas.Where(entry => entry.Activation is { State: not (PersonaStates.Inactive or PersonaStates.Retired) })
                .Select(entry => entry.Id).ToList();
            var grants = security.RenewPersonaTeamGrants(workspace.WorkspaceId, active, owner);
            return Results.Ok(new { grants = grants.Select(security.ToStandingGrantResponse).ToList() });
        })).RequirePermission(IamPermissions.WorkspaceAdminister);
    }

    private static void MapChange(RouteGroupBuilder personas, string action,
        Func<PersonaActivationStore, string, string, PersonaChangeRequest, HumanPrincipal, object> change)
        => personas.MapPost("/{personaId}/" + action, (string id, string personaId, PersonaChangeRequest? request,
                HttpContext context, AccessSecurityStore security, PersonaActivationStore store) =>
                AccessSecurityEndpoints.Execute(() => Results.Ok(change(store, id, personaId, request ?? new PersonaChangeRequest(),
                    AccessSecurityEndpoints.RequireHuman(context, security)))))
            .RequirePermission(IamPermissions.WorkspaceAdminister);
}

public sealed record PersonaBudgetSettings(long? PoolTokensPerWeek, int MaxOpenPerWorkspace = 2);
