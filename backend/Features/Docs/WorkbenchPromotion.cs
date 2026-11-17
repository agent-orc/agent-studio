namespace AgentStudio.Docs;

/// <summary>
/// Whether a Dossier can create its implementation cards (AGT-3875). Only a
/// concept card's reviewed Dossier with validated proposals can; otherwise
/// <paramref name="Reason"/> names the missing piece (a
/// <see cref="ConceptPromotionRefusals"/> code) and <paramref name="Message"/>
/// says it in words, so the Dossier view never offers a control that cannot work.
/// </summary>
public sealed record WorkbenchPromotion(
    bool Available,
    string? Reason,
    string? Message,
    string? SourceTaskKey,
    string? SourceJobId,
    string? SourceTitle,
    string? SourceLane,
    int ProposalCount)
{
    public static WorkbenchPromotion Refused(string reason, string message, TaskInfo? card = null) =>
        new(false, reason, message, card?.Key ?? card?.Id, card?.Id, card?.Title, card?.State, 0);
}

/// <summary>
/// Resolves <see cref="WorkbenchPromotion"/> from the descriptor's own
/// <c>sourceTaskKeys</c> (not <c>relatedTaskKeys</c>: a related card is not the
/// Dossier's author) and the concept card's promotion plan.
/// </summary>
public static class WorkbenchPromotionPolicy
{
    public static WorkbenchPromotion Resolve(
        string projectName,
        WorkbenchListItem workbench,
        IReadOnlyList<TaskInfo> tasks,
        Func<TaskInfo, ConceptPromotionPlanRead> readPlan)
    {
        if (workbench.ImplementationTasks.Count == 0 && workbench.ImplementationTaskKeys.Length == 0)
            return WorkbenchPromotion.Refused(ConceptPromotionRefusals.NoProposals,
                "This Dossier proposes no implementation cards.");

        var keys = workbench.DescriptorSourceTaskKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var card = keys
            .Select(key => tasks.FirstOrDefault(task =>
                string.Equals(task.ProjectName, projectName, StringComparison.OrdinalIgnoreCase)
                && task.State != TaskStates.Archive
                && TaskModes.IsConcept(task.Mode)
                && (string.Equals(task.Key, key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(task.Id, key, StringComparison.OrdinalIgnoreCase))))
            .FirstOrDefault(task => task != null);
        if (card == null)
            return WorkbenchPromotion.Refused(ConceptPromotionRefusals.NoConceptCard, keys.Length == 0
                ? "This Dossier has no source concept card. Implementation cards come from a concept card's reviewed Dossier."
                : $"None of this Dossier's source cards ({string.Join(", ", keys)}) is a live concept card in {projectName}.");

        var read = readPlan(card);
        if (read.Plan is not { } plan)
            return WorkbenchPromotion.Refused(
                read.RefusalCode ?? ConceptPromotionRefusals.NoReview,
                read.Refusal ?? "The concept Dossier could not be read.", card);

        var directory = DossierDirectory(workbench.EntryPath);
        if (read.RepoRelativeDirectory is { } published
            && directory != null
            && !string.Equals(published.Trim('/'), directory, StringComparison.OrdinalIgnoreCase))
            return WorkbenchPromotion.Refused(ConceptPromotionRefusals.OtherDossier,
                $"Concept card {card.Key ?? card.Id} delivered {published.Trim('/')}, not this Dossier.", card);

        if (plan.Items.Count == 0)
            return WorkbenchPromotion.Refused(ConceptPromotionRefusals.NoProposals,
                $"The reviewed Dossier of {card.Key ?? card.Id} proposes no validated implementation cards.", card);

        return new WorkbenchPromotion(true, null, null, card.Key ?? card.Id, card.Id, card.Title,
            card.State, plan.Items.Count);
    }

    private static string? DossierDirectory(string entryPath)
    {
        var normalized = entryPath.Replace('\\', '/').Trim('/');
        var slash = normalized.LastIndexOf('/');
        return slash <= 0 ? null : normalized[..slash];
    }
}

public sealed class WorkbenchPromotionService(TaskScannerService scanner)
{
    public WorkbenchPromotion Resolve(string projectName, WorkbenchListItem workbench) =>
        WorkbenchPromotionPolicy.Resolve(projectName, workbench, scanner.ScanAllJobs(),
            card => scanner.ReadPromoteConceptPlan(card.Id, card.WatchPath));
}
