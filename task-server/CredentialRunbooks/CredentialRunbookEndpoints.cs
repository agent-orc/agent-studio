using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Operator action API for typed credential runbooks. The existing management scope is
/// the actor's authority; browser consent and repository administration stay explicit
/// receipts and are never performed by these routes.
/// </summary>
public static class CredentialRunbookEndpoints
{
    public static void MapCredentialRunbookEndpoints(this WebApplication app)
    {
        var management = app.MapGroup("/api/v1/management")
            .RequireTaskServerScope(TaskServerScopes.Management);
        management.MapGet("/credential-runbooks", (TaskServerStore store)
            => Results.Ok(store.GetCredentialRunbookCatalogue()));
        management.MapPost("/credential-runbooks/select", (CredentialIncidentFacts facts, TaskServerStore store)
            => TaskServerEndpoints.InvokeAsync(() => Task.FromResult(store.SelectCredentialRunbook(facts))));
        management.MapPost("/credential-runbook-operations", (
            HttpContext context, BeginCredentialRunbookRequest request, TaskServerStore store, CancellationToken ct)
            => TaskServerEndpoints.InvokeAsync(() => store.BeginCredentialRunbookAsync(
                request, TaskServerEndpoints.Actor(context), ct)));
        management.MapGet("/credential-runbook-operations/{operationId}", (
            string operationId, TaskServerStore store, CancellationToken ct)
            => TaskServerEndpoints.InvokeNullableAsync(() => store.GetCredentialRunbookAsync(operationId, ct)));
        management.MapPost("/credential-runbook-operations/{operationId}/run", (
            HttpContext context, string operationId, ContinueCredentialRunbookRequest request,
            TaskServerStore store, CancellationToken ct)
            => TaskServerEndpoints.InvokeAsync(() => store.ContinueCredentialRunbookAsync(
                operationId, request, TaskServerEndpoints.Actor(context), ct)));
        management.MapPost("/credential-runbook-operations/{operationId}/cancel", (
            HttpContext context, string operationId, TaskServerStore store, CancellationToken ct)
            => TaskServerEndpoints.InvokeAsync(() => store.CancelCredentialRunbookAsync(
                operationId, TaskServerEndpoints.Actor(context), ct)));
        management.MapPost("/credential-runbook-operations/{operationId}/steps", (
            HttpContext context, string operationId, RecordCredentialRunbookStepRequest request,
            TaskServerStore store, CancellationToken ct)
            => TaskServerEndpoints.InvokeAsync(() => store.RecordCredentialRunbookStepAsync(
                operationId, request, TaskServerEndpoints.Actor(context), ct)));
    }
}
