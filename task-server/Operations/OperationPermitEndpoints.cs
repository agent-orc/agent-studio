using AgentStudio.Operations.Contracts;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public static class OperationPermitEndpoints
{
    public static void MapOperationPermitEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/operations/permits", (IssueOperationPermitRequest request, HttpContext context, TaskServerStore store, CancellationToken ct) =>
        {
            var principal = context.TaskServerPrincipal();
            if (principal is null) return Task.FromResult<IResult>(Results.Unauthorized());
            return TaskServerEndpoints.InvokeAsync(() => store.IssueOperationPermitAsync(request, principal.PrincipalId, ct));
        }).RequireTaskServerScope(TaskServerScopes.OperationsIssue)
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);
        app.MapPost("/api/v1/operations/permits/introspect", (PermitIntrospectionRequest request, HttpContext context, TaskServerStore store, CancellationToken ct) =>
        {
            var principal = context.TaskServerPrincipal();
            if (principal is null || principal.Kind != TaskServerPrincipalKinds.Operations)
                return Task.FromResult<IResult>(Results.Unauthorized());
            return TaskServerEndpoints.InvokeAsync(() => store.IntrospectOperationPermitAsync(request, ct));
        }).RequireTaskServerScope(TaskServerScopes.OperationsInspect)
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);
    }
}
