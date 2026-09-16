namespace AgentStudio.Pipeline;

/// <summary>
/// One command list where a central <see cref="BuildProfile"/> declares
/// commands that disagree with the repository's own
/// <c>.agent-studio/project.yml</c> for the same purpose.
/// </summary>
public sealed record BuildProfileContradiction(
    string Field,
    IReadOnlyList<string> ProfileCommands,
    IReadOnlyList<string> RepositoryCommands);

/// <summary>
/// AGT-2827: <see cref="VerifyCommandPlanner.Plan"/> already gives a valid
/// repository <c>.agent-studio/project.yml</c> outright precedence over a
/// central <see cref="BuildProfile"/> - correct behaviour, but silent. A
/// profile that still declares different build or test commands than the
/// repository definition is never run and never flagged, so an operator who
/// edits the central profile has no signal that the repository definition is
/// the one actually driving every gate and review. This pure policy flags
/// exactly that disagreement so project settings can surface a warning
/// instead of leaving the mismatch to be discovered from a review failure
/// (the QS-103 incident this closes: a corrected central profile never took
/// effect because the repository's own project.yml silently overrode it).
/// </summary>
public static class BuildProfileContradictionPolicy
{
    /// <summary>
    /// Empty when the repository definition is missing/invalid (nothing to
    /// contradict) or the profile declares no commands for a field (falling
    /// through to the repository definition is the documented behaviour, not
    /// a contradiction).
    /// </summary>
    public static IReadOnlyList<BuildProfileContradiction> Evaluate(
        AgentStudio.TaskServer.Contracts.ProjectExecutionDefinition? repositoryDefinition,
        BuildProfile? profile)
    {
        if (repositoryDefinition is null || profile is null)
            return [];

        var contradictions = new List<BuildProfileContradiction>();
        AddIfContradicted("build", profile.BuildCmds, repositoryDefinition.Commands.Build);
        AddIfContradicted("test", profile.TestCmds, repositoryDefinition.Commands.Test);
        return contradictions;

        void AddIfContradicted(
            string field,
            IReadOnlyList<string>? profileCommands,
            IReadOnlyList<string> repositoryCommands)
        {
            var declared = (profileCommands ?? [])
                .Where(command => !string.IsNullOrWhiteSpace(command))
                .Select(command => command.Trim())
                .ToList();
            if (declared.Count == 0)
                return;
            if (declared.SequenceEqual(repositoryCommands, StringComparer.Ordinal))
                return;

            contradictions.Add(new BuildProfileContradiction(field, declared, repositoryCommands));
        }
    }
}
