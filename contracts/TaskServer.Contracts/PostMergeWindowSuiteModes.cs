namespace AgentStudio.TaskServer.Contracts;

/// <summary>Values of <see cref="PostMergeProjectState.WindowSuites"/>.</summary>
public static class PostMergeWindowSuiteModes
{
    /// <summary>
    /// The base suite plus the routine suites the window's changed paths select
    /// (AGT-3966). A path outside every area selects the base suite only; a
    /// project without a base suite falls back to every routine suite.
    /// </summary>
    public const string Affected = "affected";
    /// <summary>The base suite plus every routine suite.</summary>
    public const string Routine = "routine";
}
