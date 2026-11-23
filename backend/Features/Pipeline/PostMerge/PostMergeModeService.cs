using AgentStudio.Registry;

namespace AgentStudio.Pipeline;

/// <summary>Resolves the project switch and the active red-develop escalation fallback.</summary>
public sealed class PostMergeModeService(ProjectRegistry projects, PostMergeRunStore runs,
    TaskScannerService scanner, TimeProvider clock)
{
    public string Resolve(string project, ProjectSettings? settings)
    {
        var configured = IntegrationTestModes.Resolve(settings);
        if (configured != IntegrationTestModes.PostMerge) return configured;
        var record = projects.FindByIdOrDisplayName(project);
        if (record is null) return IntegrationTestModes.PreMerge;
        try
        {
            var state = runs.Read(record.Id);
            var repairEscalated = state.AttributionEscalatedTaskId is { } taskId
                && scanner.FindJob(taskId, record.StorageLocation)?.State == TaskStates.Escalated;
            return EscalationActive(state, clock.GetUtcNow(), repairEscalated)
                ? IntegrationTestModes.PreMerge : configured;
        }
        catch (IOException)
        {
            return IntegrationTestModes.PreMerge;
        }
    }

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
    /// A host built without the service has no post-merge store and no
    /// red-develop fallback, so it integrates pre-merge, which is also what
    /// <see cref="Resolve"/> answers for a project it cannot find.
    /// </summary>
    public static string Effective(PostMergeModeService? service, string project, ProjectSettings? settings)
        => service?.Resolve(project, settings) ?? IntegrationTestModes.PreMerge;

    internal static bool EscalationActive(
        AgentStudio.TaskServer.Contracts.PostMergeProjectState state, DateTimeOffset now,
        bool repairEscalated = false)
    {
        if (repairEscalated) return true;
        if (state.DevelopState is not ("red" or "running")) return false;
        var openIncident = state.Runs.Reverse().TakeWhile(run => run.Status != "green")
            .Where(run => run.Status == "red" && run.FinishedAtUtc is not null)
            .Select(run => run.FinishedAtUtc!.Value).LastOrDefault();
        return openIncident != default && now - openIncident >= TimeSpan.FromHours(2);
    }
}
