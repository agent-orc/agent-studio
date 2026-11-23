namespace AgentStudio.Pipeline;

/// <summary>
/// Resolves the project's integration test placement. RT-1 (AGT-3988, RT-D7 B):
/// the answer is the configured placement. A red develop, however long, and an
/// escalated repair card never switch a fast or balanced project to pre-merge;
/// the owner is asked per incident instead (<see cref="PostMergeIncidentService"/>),
/// and switching to strict stays an owner setting.
/// <c>PostMergeProjectState.AttributionEscalatedTaskId</c> stays readable for
/// compatibility and is ignored here.
/// </summary>
public sealed class PostMergeModeService
{
    public string Resolve(string project, ProjectSettings? settings)
        => IntegrationTestModes.Resolve(settings);

    /// <summary>The project's T3 state (last window tip, runs), or null when it cannot be read.</summary>
    public AgentStudio.TaskServer.Contracts.PostMergeProjectState? ReadState(string project)
    {
        var record = projects.FindByIdOrDisplayName(project);
        if (record is null) return null;
        try { return runs.Read(record.Id); }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// The effective placement through <paramref name="service"/> (AGT-3965).
    /// A host built without the service has no post-merge store, so it
    /// integrates pre-merge.
    /// </summary>
    public static string Effective(PostMergeModeService? service, string project, ProjectSettings? settings)
        => service?.Resolve(project, settings) ?? IntegrationTestModes.PreMerge;
}
