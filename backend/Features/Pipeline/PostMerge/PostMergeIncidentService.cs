using System.Security.Cryptography;
using System.Text;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>Turns a terminal T3 receipt into one durable repair card per fingerprint.</summary>
public sealed class PostMergeIncidentService
{
    private readonly object _gate = new();
    private readonly PostMergeRunStore _store;
    private readonly TaskMutationService _mutations;
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly HumanReviewEscalation _humanReviewEscalation;
    private readonly TimeProvider _clock;
    private readonly ILogger<PostMergeIncidentService> _logger;

    public PostMergeIncidentService(PostMergeRunStore store, TaskMutationService mutations,
        TaskScannerService scanner, TaskStateMachine states, HumanReviewEscalation humanReviewEscalation,
        TimeProvider clock,
        ILogger<PostMergeIncidentService> logger)
    {
        _store = store;
        _mutations = mutations;
        _scanner = scanner;
        _states = states;
        _humanReviewEscalation = humanReviewEscalation;
        _clock = clock;
        _logger = logger;
    }

    public void Apply(ProjectRecord project, PostMergeRunRecord run)
    {
        if (run.Status is not ("red" or "green")) return;
        lock (_gate)
        {
            var state = _store.Read(project.Id);
            if (run.Status == "green")
            {
                CloseOnGreen(project, state, run);
                return;
            }
            var failure = run.Failure;
            if (failure?.AllFailuresQuarantined == true) return;
            var fingerprint = failure?.Fingerprint ?? FallbackFingerprint(run);
            var existing = (state.Incidents ?? []).FirstOrDefault(item => item.Fingerprint == fingerprint
                && item.ResolvedAtUtc is null);
            var firstRed = existing?.FirstRedAtUtc
                ?? (state.Incidents ?? []).Where(item => item.ResolvedAtUtc is null)
                    .Select(item => (DateTimeOffset?)item.FirstRedAtUtc).Min()
                ?? run.FinishedAtUtc ?? _clock.GetUtcNow();
            var facts = new RedDevelopRepairFacts(
                failure?.AttributedMerge is null ? 0 : 1,
                failure?.RevertAppliesCleanly == true,
                failure?.LaterMergeDependsOnSuspect == true,
                _clock.GetUtcNow() - firstRed);
            var decision = MonolithTransitionAdapter.RedDevelopRepair(facts);
            if (existing is not null)
            {
                if (decision.Action == RedDevelopRepairAction.Escalate && !existing.Escalated)
                    Escalate(project, existing);
                return;
            }

            var action = decision.Action == RedDevelopRepairAction.Escalate
                ? MonolithTransitionAdapter.RedDevelopRepair(facts with { RedAge = TimeSpan.Zero }).Action
                : decision.Action;
            var title = $"Repair red develop: {fingerprint}";
            var priorCard = _scanner.ScanAllJobsWithArchive().FirstOrDefault(card => card.ProjectName == project.DisplayName
                && card.Title == title && card.CreationSource == TimelineActors.Orchestrator);
            var cardId = priorCard?.Id ?? _mutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
            {
                Project = project.Id,
                Title = title,
                PromptMarkdown = Prompt(project, run, state.LastGreenSha, failure, action, fingerprint),
                TargetState = TaskStates.Preparation,
                TaskType = TaskTypes.Bug,
                Order = 0,
                CreationSource = TimelineActors.Orchestrator,
                CreatedBy = "Orchestrator",
            }) ?? throw new InvalidOperationException("The red-develop repair card could not be created.");
            if (priorCard is not null)
            {
                if (!_mutations.AppendContinuationNote(priorCard.Id,
                        Prompt(project, run, state.LastGreenSha, failure, action, fingerprint), project.StorageLocation))
                    throw new InvalidOperationException("The recurring repair card could not receive new evidence.");
                var moved = _states.MoveJob(priorCard.Id, TaskStates.Preparation, project.StorageLocation,
                    expectedSourceState: priorCard.State, reason: "Recurring red-develop fingerprint.");
                if (moved.Status != MoveJobStatus.Success)
                    throw new InvalidOperationException("The recurring repair card could not reopen: " + moved.Message);
            }
            var incident = new PostMergeIncident(fingerprint, cardId, action.ToString(), firstRed,
                null, failure?.AttributedMerge, failure?.FailingTests ?? [], failure?.FailingPaths ?? [],
                decision.Action == RedDevelopRepairAction.Escalate, OriginalTaskId: failure?.AttributedTaskId);
            _store.UpdateIncidents(project.Id, items => [.. items, incident]);
            if (incident.Escalated) Escalate(project, incident);
        }
    }

    /// <summary>The scheduled runner polls state even while no new merge arrives.</summary>
    public void EscalateOverdue(ProjectRecord project)
    {
        lock (_gate)
        {
            var state = _store.Read(project.Id);
            if (state.DevelopState == "red"
                && state.Runs.LastOrDefault(run => run.Status is "red" or "green") is
                    { Status: "red" } red)
            {
                // A receipt can be durable before card creation succeeds. The
                // next scheduled check completes the idempotent card step.
                Apply(project, red);
                state = _store.Read(project.Id);
            }
            ReopenResolved(project, state);
            if (state.DevelopState != "red") return;
            foreach (var incident in state.Incidents ?? [])
            {
                if (incident.ResolvedAtUtc is not null || incident.Escalated) continue;
                var decision = MonolithTransitionAdapter.RedDevelopRepair(new(0, false, false,
                    _clock.GetUtcNow() - incident.FirstRedAtUtc));
                if (decision.Action == RedDevelopRepairAction.Escalate)
                    Escalate(project, incident);
            }
        }
    }

    private void Escalate(ProjectRecord project, PostMergeIncident incident)
    {
        var card = _scanner.FindJob(incident.RepairCardId, project.StorageLocation);
        if (card is null) throw new InvalidOperationException("The red-develop repair card is missing.");
        var note = "Develop has remained red for two hours. Human Operator: review the repair "
            + "and consider switching this project to the pre-merge batch gate until T3 is proven green.";
        if (!_mutations.AppendContinuationNote(card.Id, note, project.StorageLocation))
            throw new InvalidOperationException("The red-develop escalation note could not be attached.");
        var moved = _humanReviewEscalation.EscalateAsync(card.Id, project.StorageLocation,
            project.DisplayName, HumanReviewEscalationCategories.FailureIntervention, note,
            expectedSourceState: card.State).GetAwaiter().GetResult();
        if (moved.Status != MoveJobStatus.Success)
            throw new InvalidOperationException("The red-develop repair card could not be escalated: " + moved.Message);
        _store.UpdateIncidents(project.Id, items => items.Select(item => item.Fingerprint == incident.Fingerprint
            && item.ResolvedAtUtc is null ? item with { Escalated = true } : item).ToArray());
    }

    private void CloseOnGreen(ProjectRecord project, PostMergeProjectState state, PostMergeRunRecord run)
    {
        var open = (state.Incidents ?? []).Where(item => item.ResolvedAtUtc is null).ToArray();
        if (open.Length == 0) return;
        var at = run.FinishedAtUtc ?? _clock.GetUtcNow();
        var reopened = open.Where(item => item.Action == nameof(RedDevelopRepairAction.Revert))
            .Where(item => ReopenOriginal(project, item)).Select(item => item.Fingerprint)
            .ToHashSet(StringComparer.Ordinal);
        _store.UpdateIncidents(project.Id, items => items.Select(item => item.ResolvedAtUtc is null
            ? item with { ResolvedAtUtc = at, OriginalReopened = reopened.Contains(item.Fingerprint) }
            : item).ToArray());
    }

    private void ReopenResolved(ProjectRecord project, PostMergeProjectState state)
    {
        var reopened = (state.Incidents ?? []).Where(item => item.ResolvedAtUtc is not null
            && item.Action == nameof(RedDevelopRepairAction.Revert) && !item.OriginalReopened)
            .Where(item => ReopenOriginal(project, item)).Select(item => item.Fingerprint)
            .ToHashSet(StringComparer.Ordinal);
        if (reopened.Count > 0)
            _store.UpdateIncidents(project.Id, items => items.Select(item => reopened.Contains(item.Fingerprint)
                ? item with { OriginalReopened = true } : item).ToArray());
    }

    private bool ReopenOriginal(ProjectRecord project, PostMergeIncident incident)
    {
        if (incident.AttributedMerge is null) return false;
        var repair = _scanner.FindJob(incident.RepairCardId, project.StorageLocation);
        if (repair?.State is not (TaskStates.HumanReview or TaskStates.Completed)) return false;
        var origin = (incident.OriginalTaskId is null ? null
                : _scanner.FindJob(incident.OriginalTaskId, project.StorageLocation))
            ?? _scanner.ScanAllJobsWithArchive().FirstOrDefault(card => card.ProjectName == project.DisplayName
            && card.IntegrationRecords.Any(record => string.Equals(record.IntegrationSha,
                incident.AttributedMerge, StringComparison.OrdinalIgnoreCase)));
        if (origin is null)
        {
            _logger.LogWarning("Red-develop revert {Fingerprint} has no matching original card", incident.Fingerprint);
            return false;
        }
        var note = $"Post-merge failure {incident.Fingerprint} attributed to {incident.AttributedMerge}. "
            + $"Repair card {incident.RepairCardId} reverted the merge. Failing tests: "
            + string.Join(", ", incident.FailingTests);
        if (!_mutations.AppendContinuationNote(origin.Id, note, origin.WatchPath))
            throw new InvalidOperationException("The original card could not receive the post-merge failure.");
        var moved = _states.MoveJob(origin.Id, TaskStates.Preparation, origin.WatchPath,
            expectedSourceState: origin.State, reason: note);
        if (moved.Status != MoveJobStatus.Success)
            throw new InvalidOperationException("The reverted card could not return to Preparation: " + moved.Message);
        return true;
    }

    private static string Prompt(ProjectRecord project, PostMergeRunRecord run, string? lastGreen,
        PostMergeFailureEvidence? failure, RedDevelopRepairAction action, string fingerprint)
    {
        var instruction = action == RedDevelopRepairAction.Revert
            ? "Create a revert commit for the attributed merge. Deliver it through the normal review and integration pipeline; its publication lease protects develop."
            : "Fix the failing tests in a coding attempt and deliver through the normal review and integration pipeline.";
        return $"# Repair red develop\n\nProject: {project.DisplayName}\nFingerprint: {fingerprint}\n"
            + $"Red tip: {run.TipSha}\nLast green: {lastGreen ?? "unknown"}\n"
            + $"Attributed merge: {failure?.AttributedMerge ?? "unresolved"}\n"
            + $"Original card: {failure?.AttributedTaskId ?? "unresolved"}\n"
            + $"Suspects: {string.Join(", ", failure?.SuspectMerges ?? [])}\n"
            + $"Failing tests: {string.Join(", ", failure?.FailingTests ?? [])}\n"
            + $"Failing paths: {string.Join(", ", failure?.FailingPaths ?? [])}\n\n"
            + instruction + "\n\n## Log excerpt\n```text\n"
            + (failure?.LogExcerpt ?? string.Join("\n", run.Verdicts.Where(item => item.ExitCode != 0)
                .Select(item => item.OutputTail))) + "\n```\n";
    }

    private static string FallbackFingerprint(PostMergeRunRecord run)
    {
        var failed = run.Verdicts.FirstOrDefault(item => item.ExitCode != 0);
        var text = failed?.Command + "\n" + failed?.OutputTail;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? run.TipSha)))[..16];
    }
}
