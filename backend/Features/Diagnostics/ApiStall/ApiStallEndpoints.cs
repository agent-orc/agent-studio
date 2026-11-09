namespace AgentStudio.Diagnostics;

/// <summary>
/// Read route of the stall detector. Mapped in every security profile, unlike
/// the local-only <see cref="DiagnosticsEndpoints"/>, because Operations on a
/// networked host is where an operator has to see the alarm.
/// </summary>
public static class ApiStallEndpoints
{
    public const string Route = "/api/diagnostics/api-stalls";

    public static void MapApiStallEndpoints(this WebApplication app)
        => app.MapGet(Route, (ApiStallDetector detector) => Results.Ok(detector.Snapshot()));
}
