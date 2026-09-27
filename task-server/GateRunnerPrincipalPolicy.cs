using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>Runner tokens may mutate only their own fenced gate attempts.</summary>
public static class GateRunnerPrincipalPolicy
{
    public static bool Matches(TaskServerPrincipal? principal, string executorId)
        => principal is not { Kind: TaskServerPrincipalKinds.Runner }
            || principal.RunnerId is { } runnerId
                && string.Equals(runnerId, executorId, StringComparison.Ordinal);

    public static IResult Denied()
        => Results.Json(new ApiError("runner-identity-mismatch",
            "Gate executor identity differs from its principal."),
            statusCode: StatusCodes.Status403Forbidden);
}
