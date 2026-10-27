using AgentStudio.Registry;

namespace AgentStudio.Pipeline;

/// <summary>Resolves the project switch and the active red-develop escalation fallback.</summary>
public sealed class PostMergeModeService(ProjectRegistry projects, PostMergeRunStore runs,
    TimeProvider clock)
{
    public string Resolve(string project, ProjectSettings? settings)
    {
        var configured = IntegrationTestModes.Resolve(settings);
        if (configured != IntegrationTestModes.PostMerge) return configured;
        var id = projects.FindByIdOrDisplayName(project)?.Id;
        if (id is null) return IntegrationTestModes.PreMerge;
        try
        {
            var state = runs.Read(id);
            return EscalationActive(state, clock.GetUtcNow())
                ? IntegrationTestModes.PreMerge : configured;
        }
        catch (IOException)
        {
            return IntegrationTestModes.PreMerge;
        }
    }

    internal static bool EscalationActive(
        AgentStudio.TaskServer.Contracts.PostMergeProjectState state, DateTimeOffset now)
    {
        if (state.AttributionEscalationActive) return true;
        if (state.DevelopState is not ("red" or "running")) return false;
        var openIncident = state.Runs.Reverse().TakeWhile(run => run.Status != "green")
            .Where(run => run.Status == "red" && run.FinishedAtUtc is not null)
            .Select(run => run.FinishedAtUtc!.Value).LastOrDefault();
        return openIncident != default && now - openIncident >= TimeSpan.FromHours(2);
    }
}
