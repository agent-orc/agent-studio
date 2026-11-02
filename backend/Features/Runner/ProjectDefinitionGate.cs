using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// AOW-15: checks the delivery's own <c>.agent-studio/project.yml</c> right
/// after the completion gate, before the build gate and the review aspects.
/// An invalid definition stops every gate before its first command; checked
/// here, the agent gets the issues in its own round instead of at the merge
/// gate. A delivery without the file is not judged: the gates fall back to
/// compatibility preparation for it.
/// </summary>
public static class ProjectDefinitionGate
{
    /// <param name="definitionYaml">The file at the delivered commit, or null when the delivery has none.</param>
    /// <param name="fileExistsAtDelivery">Whether a repository-relative path exists at the delivered commit.</param>
    /// <param name="priorReissues">Shared auto-review reissue count of the card.</param>
    /// <param name="maxReissues">Shared auto-review reissue budget.</param>
    public static CompletionGate.Decision Evaluate(
        string? definitionYaml,
        Func<string, bool> fileExistsAtDelivery,
        int priorReissues,
        int maxReissues)
    {
        ArgumentNullException.ThrowIfNull(fileExistsAtDelivery);
        if (definitionYaml is null)
            return Pass("The delivery has no repository execution definition.");

        var issues = ProjectDefinitionReader.Parse(definitionYaml, fileExistsAtDelivery).Issues;
        if (issues.Count == 0)
            return Pass("The delivery's repository execution definition is valid.");

        return new CompletionGate.Decision
        {
            Action = priorReissues >= maxReissues
                ? CompletionGate.CompletionGateAction.Escalate
                : CompletionGate.CompletionGateAction.Reissue,
            Findings = issues.Select(ProjectDefinitionIssueText.Describe).ToList(),
            Reason = ProjectDefinitionIssueText.InvalidReason(issues),
            Source = CompletionGate.CompletionGateSource.ProjectDefinition,
        };
    }

    private static CompletionGate.Decision Pass(string reason)
        => new() { Reason = reason, Source = CompletionGate.CompletionGateSource.ProjectDefinition };
}
