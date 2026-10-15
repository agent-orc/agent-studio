using System.Text.Json;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// v1 gate claim routes on the monolith authority (AGT-3055). They match the
/// Task Server routes, so the existing runner gate daemon claims batch suites
/// from either authority. Subjects are minted in-process by the batch pilot.
/// </summary>
public static class MonolithGateEndpoints
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    public static void MapMonolithGateEndpoints(this WebApplication app)
    {
        var gates = app.MapGroup("/api/v1/gates");
        gates.MapGet("/subjects/{subjectId}", (string subjectId, MonolithGateStore store) =>
            store.GetStatus(subjectId) is { } status ? Wire(status) : Results.NotFound());
        gates.MapPost("/claims", (HttpContext context, GateClaimRequest request, MonolithGateStore store) =>
            RunnerMatches(context, request.ExecutorId)
                ? Execute(() => store.Claim(request))
                : Denied())
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);
        gates.MapPost("/attempts/{attemptId}/renew", (HttpContext context, string attemptId,
            GateRenewRequest request, MonolithGateStore store) =>
            RunnerMatches(context, request.Authority.ExecutorId)
                ? Execute(() => store.Renew(attemptId, request))
                : Denied())
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
        gates.MapPost("/attempts/{attemptId}/phase", (HttpContext context, string attemptId,
            GatePhaseRequest request, MonolithGateStore store) =>
            RunnerMatches(context, request.Authority.ExecutorId)
                ? Execute(() => store.Advance(attemptId, request))
                : Denied())
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
        gates.MapPost("/attempts/{attemptId}/report", (HttpContext context, string attemptId,
            SubmitGateReportRequest request, MonolithGateStore store) =>
            RunnerMatches(context, request.Authority.ExecutorId)
                ? Execute(() => store.Report(attemptId, request))
                : Denied())
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);
        gates.MapPost("/attempts/{attemptId}/containment", (HttpContext context, string attemptId,
            GateContainmentRequest request, MonolithGateStore store) =>
            RunnerMatches(context, request.ExecutorId)
                ? Execute(() => store.ConfirmContainment(attemptId, request))
                : Denied())
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);
    }

    private static bool RunnerMatches(HttpContext context, string executorId)
        => context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is not RunnerPrincipal principal
           || string.Equals(principal.RunnerId, executorId, StringComparison.Ordinal);

    private static IResult Denied()
        => Results.Json(new ApiError("runner-identity-mismatch",
            "Gate executor identity differs from its principal."), WireJson,
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult Wire(object value) => Results.Json(value, WireJson);

    private static IResult Execute(Func<object> action)
    {
        try { return Wire(action()); }
        catch (KeyNotFoundException exception) { return Results.NotFound(new ApiError("not-found", exception.Message)); }
        catch (ArgumentException exception) { return Results.BadRequest(new ApiError("invalid-request", exception.Message)); }
        catch (GateConflictException exception)
        {
            return Results.Json(new ApiError(exception.Code, exception.Message), WireJson,
                statusCode: StatusCodes.Status409Conflict);
        }
    }
}
