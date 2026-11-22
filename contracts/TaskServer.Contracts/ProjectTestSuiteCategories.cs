namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// The <c>testSuites[].category</c> values that scheduling reads. Other
/// categories (<c>release</c>, <c>machine-bound</c>) are inventory only.
/// </summary>
public static class ProjectTestSuiteCategories
{
    /// <summary>
    /// A small fixed set that answers "does the product still start and do its
    /// main paths work" (AGT-3966). Every merge-window run executes it; it must
    /// finish within about ten minutes on the run host.
    /// </summary>
    public const string Base = "base";

    /// <summary>A suite that a merge window selects when its area changed, and that the nightly run always executes.</summary>
    public const string Routine = "routine";
}
