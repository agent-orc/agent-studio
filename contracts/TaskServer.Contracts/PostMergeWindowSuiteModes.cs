namespace AgentStudio.TaskServer.Contracts;

/// <summary>Values of <see cref="PostMergeProjectState.WindowSuites"/>.</summary>
public static class PostMergeWindowSuiteModes
{
    /// <summary>The routine suites the window's changed paths select; an unknown path selects every routine suite.</summary>
    public const string Affected = "affected";
    /// <summary>Every routine suite.</summary>
    public const string Routine = "routine";
}
