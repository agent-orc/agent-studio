namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// The answer a server gives a runner credential on a route it does not map,
/// for example a standalone Task Server feature route called against the
/// monolith. A host that reads this code knows the feature is absent on this
/// server, not refused, and pauses the loop that polls it.
/// </summary>
public static class RouteAbsenceContract
{
    /// <summary>The <c>error</c> field of the 404 body.</summary>
    public const string ErrorCode = "route-not-mapped";
}
