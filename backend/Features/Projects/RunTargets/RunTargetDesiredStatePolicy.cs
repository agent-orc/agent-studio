using System.Text.RegularExpressions;

namespace AgentStudio.Projects;

/// <summary>
/// Pure decisions for the run-target protocol: which artifact an environment
/// should run, and which parts of a host observation Studio keeps.
/// </summary>
public static partial class RunTargetDesiredStatePolicy
{
    public const int MaxEventsPerEnvironment = 20;

    [GeneratedRegex(@"\A[A-Za-z0-9./:_-]+@sha256:[0-9a-f]{64}\z")]
    private static partial Regex Digest();

    /// <summary>
    /// An open deploy job wins: its artifact is what the host must converge to
    /// and report on. Without one, the environment keeps the last artifact that
    /// was reported healthy or rolled back. A failed first deploy leaves nothing
    /// to run.
    /// </summary>
    public static RunTargetEnvironmentSpec Project(string project, EnvironmentDeclaration declaration,
        DeployJob? openJob, IReadOnlyList<DeploymentHistoryItem> history)
    {
        var hosting = declaration.Hosting
            ?? throw new ArgumentException("Only edge-hosted environments have a run-target spec.", nameof(declaration));
        var artifact = openJob?.Artifact
            ?? history.LastOrDefault(item => item.Status is "healthy" or "rolled-back")?.Artifact;
        return new(project, declaration.Id, declaration.Kind, artifact, declaration.ContainerPort,
            hosting.HealthPath, hosting.Domains, hosting.Edge, hosting.Variables, declaration.SecretRefs,
            hosting.Resources, openJob?.Id, openJob?.LeaseToken, openJob?.IsRollback ?? false);
    }

    /// <summary>
    /// The host's report is untrusted text. Keep only the closed vocabulary,
    /// digests, and a bounded event list, so no host output reaches Studio.
    /// </summary>
    public static RunTargetObservation? Sanitize(RunTargetObservation observation, DateTime receivedAt)
    {
        if (!RunTargetVocabulary.States.Contains(observation.State)) return null;
        string? Artifact(string? value) => value is not null && Digest().IsMatch(value) ? value : null;
        var events = (observation.Events ?? [])
            .Where(item => RunTargetVocabulary.EventKinds.Contains(item.Kind))
            .TakeLast(MaxEventsPerEnvironment)
            .Select(item => new RunTargetEvent(item.At > receivedAt ? receivedAt : item.At, item.Kind, Artifact(item.Artifact)))
            .ToArray();
        return new(observation.Project, observation.EnvironmentId, observation.State,
            Artifact(observation.RunningArtifact), Artifact(observation.PreviousArtifact),
            Math.Clamp(observation.Restarts, 0, 1_000_000),
            observation.ObservedAt > receivedAt ? receivedAt : observation.ObservedAt, events);
    }
}
