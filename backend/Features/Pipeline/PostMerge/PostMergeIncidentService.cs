using System.Security.Cryptography;
using System.Text;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>
/// Applies pure per-test triage decisions to the quarantine store and repair cards.
/// Every incident keeps its own clock and owner questions. Legacy receipts retain
/// the compatibility repair path; no timer moves a reviewed or settled card.
/// </summary>
public sealed partial class PostMergeIncidentService
{
    private readonly object _gate = new();
    private readonly PostMergeRunStore _store;
    private readonly TaskMutationService _mutations;
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly TimeProvider _clock;
    private readonly ILogger<PostMergeIncidentService> _logger;

    public PostMergeIncidentService(PostMergeRunStore store, TaskMutationService mutations,
        TaskScannerService scanner, TaskStateMachine states, TimeProvider clock,
        ILogger<PostMergeIncidentService> logger, TestQuarantineStore? quarantines = null,
        AgentStudio.Git.GitService? git = null, ModelRoutingPolicyRegistry? routing = null,
        IModelRoutingModeProvider? routingMode = null,
        Func<ProjectRecord, string, string, bool>? containsRepair = null,
        AgentStudio.Personas.LineGuardianStore? guardian = null, AgentStudio.Bus.AgentMessageBusBridge? bus = null)
    {
        _store = store;
        _mutations = mutations;
        _scanner = scanner;
        _states = states;
        _clock = clock;
        _logger = logger;
        _quarantines = quarantines ?? new TestQuarantineStore(Path.Combine(store.Root, "..", "test-quarantines"));
        _git = git;
        _routing = routing ?? new ModelRoutingPolicyRegistry();
        _routingMode = routingMode;
        _containsRepair = containsRepair;
        _guardian = guardian ?? new AgentStudio.Personas.LineGuardianStore(Path.Combine(store.Root, "..", "line-guardian"));
        _guardianBus = bus;
    }

    private void ApplyLegacy(ProjectRecord project, PostMergeRunRecord run)
    {
        if (run.Status is not ("red" or "green")) return;
        lock (_gate)
        {
            if (run.TestVerdicts is not null || run.Status == "green"
                && (_store.Read(project.Id).Incidents ?? []).Any(item => item.TestId is not null && item.ResolvedAtUtc is null))
            {
                ApplyTests(project, run);
                return;
            }
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
            // The clock belongs to this incident, not to the project's red develop.
            var firstRed = existing?.FirstRedAtUtc ?? run.FinishedAtUtc ?? _clock.GetUtcNow();
            var facts = new RedDevelopRepairFacts(
                failure?.AttributedMerge is null ? 0 : 1,
                failure?.RevertAppliesCleanly == true,
                failure?.LaterMergeDependsOnSuspect == true,
                _clock.GetUtcNow() - firstRed,
                existing is null ? null : _scanner.FindJob(existing.RepairCardId, project.StorageLocation)?.State);
            var decision = MonolithTransitionAdapter.RedDevelopRepair(facts);
            if (existing is not null)
            {
                if (decision.Action == RedDevelopRepairAction.AskOwner)
                    AskOwner(project, existing, decision, run);
                return;
            }

            var action = decision.Repair;
            var title = $"Repair red develop: {fingerprint}";
            var prompt = Prompt(project, run, state.LastGreenSha, failure, action, fingerprint);
            var priorCards = _scanner.ScanAllJobsWithArchive().Where(card => card.ProjectName == project.DisplayName
                && card.Title == title && card.CreationSource is TimelineActors.Orchestrator
                    or HumanReviewEscalationCategories.PostMergeRepairCreationSource).ToArray();
            var priorCard = priorCards.FirstOrDefault(card => MonolithTransitionAdapter.RecurringRepairCard(card.State)
                != RecurringRepairCardAction.CreateNew) ?? priorCards.FirstOrDefault();
            var recurring = MonolithTransitionAdapter.RecurringRepairCard(priorCard?.State);
            string cardId;
            if (priorCard is null || recurring == RecurringRepairCardAction.CreateNew)
            {
                cardId = _mutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
                {
                    Project = project.Id,
                    Title = title,
                    PromptMarkdown = prompt,
                    TargetState = TaskStates.Preparation,
                    TaskType = TaskTypes.Bug,
                    Order = 0,
                    CreationSource = HumanReviewEscalationCategories.PostMergeRepairCreationSource,
                    CreatedBy = "Orchestrator",
                }) ?? throw new InvalidOperationException("The red-develop repair card could not be created.");
            }
            else
            {
                cardId = priorCard.Id;
                if (recurring == RecurringRepairCardAction.Reopen)
                {
                    if (!_mutations.AppendContinuationNote(priorCard.Id, prompt, project.StorageLocation))
                        throw new InvalidOperationException("The recurring repair card could not receive new evidence.");
                    var moved = _states.MoveJob(priorCard.Id, TaskStates.Preparation, project.StorageLocation,
                        expectedSourceState: priorCard.State, reason: "Recurring red-develop fingerprint.");
                    if (moved.Status != MoveJobStatus.Success)
                        throw new InvalidOperationException("The recurring repair card could not reopen: " + moved.Message);
                }
                else
                    _logger.LogInformation(
                        "Post-merge incident {Fingerprint} for {Project} attaches to repair card {Card} in {Lane}; its lane and attempt stay",
                        fingerprint, project.Id, priorCard.Id, priorCard.State);
            }
            var incident = new PostMergeIncident(fingerprint, cardId, action.ToString(), firstRed,
                null, failure?.AttributedMerge, failure?.FailingTests ?? [], failure?.FailingPaths ?? [],
                false, OriginalTaskId: failure?.AttributedTaskId);
            _store.UpdateIncidents(project.Id, items => [.. items, incident]);
            if (decision.Action == RedDevelopRepairAction.AskOwner) AskOwner(project, incident, decision, run);
        }
    }

    /// <summary>
    /// The scheduled runner polls state even while no new merge arrives, so an
    /// incident reaches its owner question without a new receipt.
    /// </summary>
    public void AskOwnerWhenOverdue(ProjectRecord project)
    {
        lock (_gate)
        {
            var state = _store.Read(project.Id);
            var typedIncidents = (state.Incidents ?? []).Where(item => item.TestId is not null && item.ResolvedAtUtc is null).ToArray();
            if (typedIncidents.Length > 0)
            {
                var evidence = typedIncidents.Select(incident => state.Runs.LastOrDefault(run => run.TestVerdicts?.Any(test =>
                    test.Test == incident.TestId && Fingerprint(run, test) == incident.Fingerprint) == true))
                    .OfType<PostMergeRunRecord>().DistinctBy(run => run.Id).ToArray();
                foreach (var receipt in evidence) ApplyTests(project, receipt, overdue: true);
                return;
            }
            if (state.Runs.LastOrDefault(run => run.Status is "red" or "green") is { TestVerdicts: not null } typed)
            {
                ApplyTests(project, typed, overdue: true);
                return;
            }
            if (state.DevelopState == "red"
                && state.Runs.LastOrDefault(run => run.Status is "red" or "green") is
                    { Status: "red" } red)
            {
                // A receipt can be durable before card creation succeeds. The
                // next scheduled check completes the idempotent card step.
                Apply(project, red);
                state = _store.Read(project.Id);
            }
            if (state.DevelopState != "red") return;
            foreach (var incident in state.Incidents ?? [])
            {
                if (incident.ResolvedAtUtc is not null || incident.OwnerAskedAtUtc is not null) continue;
                var latest = LatestRed(state, incident.Fingerprint);
                var failure = latest?.Failure;
                var facts = failure is not null
                    ? new RedDevelopRepairFacts(failure.AttributedMerge is null ? 0 : 1,
                        failure.RevertAppliesCleanly, failure.LaterMergeDependsOnSuspect, TimeSpan.Zero)
                    : incident.Action == nameof(RedDevelopRepairAction.Revert)
                        ? new RedDevelopRepairFacts(1, true, false, TimeSpan.Zero)
                        : new RedDevelopRepairFacts(0, false, false, TimeSpan.Zero);
                var decision = MonolithTransitionAdapter.RedDevelopRepair(facts with
                {
                    IncidentAge = _clock.GetUtcNow() - incident.FirstRedAtUtc,
                    RepairState = _scanner.FindJob(incident.RepairCardId, project.StorageLocation)?.State,
                });
                if (decision.Action == RedDevelopRepairAction.AskOwner)
                    AskOwner(project, incident, decision, latest);
            }
        }
    }

    /// <summary>
    /// Records the one owner question of the incident. The Needs you item is
    /// projected from it (<c>OperatorPostMergeRedIncidentFactSource</c>); the
    /// fingerprint dedupes it. No lane, attempt or setting changes here.
    /// </summary>
    private void AskOwner(ProjectRecord project, PostMergeIncident incident, RedDevelopRepairDecision decision,
        PostMergeRunRecord? latestRed)
    {
        if (incident.OwnerAskedAtUtc is not null || decision.Question is not { } question) return;
        var asked = new PostMergeOwnerQuestion(
            question.Options.Select(option => option.ToString()).ToArray(),
            question.Recommended.ToString(), question.RecommendationReason,
            latestRed?.Id, latestRed?.TipSha);
        var now = _clock.GetUtcNow();
        _store.UpdateIncidents(project.Id, items => items.Select(item => item.Fingerprint == incident.Fingerprint
            && item.ResolvedAtUtc is null && item.OwnerAskedAtUtc is null
                ? item with { OwnerAskedAtUtc = now, OwnerQuestion = asked }
                : item).ToArray());
        _logger.LogInformation(
            "Post-merge incident {Fingerprint} for {Project} has been red for two hours; owner asked, recommended {Recommended}",
            incident.Fingerprint, project.Id, asked.Recommended);
    }

    private static PostMergeRunRecord? LatestRed(PostMergeProjectState state, string fingerprint)
        => state.Runs.LastOrDefault(run => run.Status == "red"
            && (run.Failure?.Fingerprint ?? FallbackFingerprint(run)) == fingerprint)
            ?? state.Runs.LastOrDefault(run => run.Status == "red");

    private void CloseOnGreen(ProjectRecord project, PostMergeProjectState state, PostMergeRunRecord run)
    {
        var open = (state.Incidents ?? []).Where(item => item.ResolvedAtUtc is null).ToArray();
        if (open.Length == 0) return;
        var at = run.FinishedAtUtc ?? _clock.GetUtcNow();
        // Attribution and a recommended repair action do not prove that any
        // published commit reverted the original card. Resolving an incident
        // must not mutate completed suspects, including historical incidents.
        _store.UpdateIncidents(project.Id, items => items.Select(item => item.ResolvedAtUtc is null
            ? item with { ResolvedAtUtc = at }
            : item).ToArray());
        _logger.LogInformation("Post-merge green resolved {Count} incidents for {Project}; original cards retain their lanes",
            open.Length, project.Id);
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
            + (failure?.NoRelevantMerges == true
                ? "Attribution: environmental or flaky suspect. No merge in the window has changed paths that can explain this failure. No merge is blamed.\n"
                : "")
            + $"Start conditions: {run.StartOccupiedSlots?.ToString() ?? "unknown"} occupied slots, "
            + $"load per core {run.StartLoadPerCore?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}\n"
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
