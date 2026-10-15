namespace AgentStudio.Runner;

/// <summary>
/// AGT-3060: applies the engine's <c>intake-routing</c> receipt. A card that
/// left 2-ready after its intake verdict was submitted is settled without an
/// effect; otherwise it goes to Human Review for a person to confirm and
/// complete, through the same funnel as the in-process decision.
/// </summary>
public sealed class IntakeRoutingEffect(
    TaskScannerService scanner,
    HumanReviewEscalation escalation,
    ILogger<IntakeRoutingEffect> logger) : IAiOperatorEffect
{
    public string Decider => AiOperatorDeciders.IntakeRouting;

    public static string SubjectKey(string taskId, IntakeOperatorFacts facts)
        => AiOperatorDecisionGateway.FactsSubjectKey("intake", taskId, facts);

    public Task ApplyAsync(AiOperatorApplication application, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (application.Decision.Effect != AiOperatorEffects.Escalate) return Task.CompletedTask;
        var facts = application.Payload.IntakeRouting
            ?? throw new InvalidDataException("The intake-routing occurrence has no facts.");
        var entry = scanner.GetWatchPaths().FirstOrDefault(item =>
            string.Equals(item.Name, application.ProjectId, StringComparison.Ordinal));
        var card = entry is null ? null : scanner.FindJob(application.TaskId, entry.Path);
        if (entry is null || card is null || card.State != application.ExpectedSourceState) return Task.CompletedTask;
        IntakeHostedService.RouteAlreadyDone(escalation, card.Id, entry.Path, entry.Name,
            facts.Reason ?? application.Decision.Detail, logger);
        return Task.CompletedTask;
    }
}
