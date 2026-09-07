using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.Tags;
using AgentStudio.Tasks;

namespace AgentStudio.Watcher;

/// <summary>Outcome of drafting a proposal for a ready case: either a new proposal card, a comment on an existing one, or nothing (contingent exhausted).</summary>
public enum WatcherDraftOutcome
{
    Created,
    Commented,
    ContingentExhausted,
    CreationFailed,
}

public sealed record WatcherDraftResult(WatcherDraftOutcome Outcome, WatcherProposal? Proposal);

/// <summary>
/// W2 output: turns a case that survived persistence + evidence + (optional)
/// analysis into a ticket proposal written through the normal task API into
/// the proposal state (§10.3), or a comment on an already-open proposal card
/// for the same fingerprint. Model recommendation is computed from the
/// repository's routing policy but only *applied* to the card at approval
/// time (§10.4 "approve... moves to Ready with the recommended model") -
/// drafting never assigns a CLI/model pin a human hasn't seen yet.
/// </summary>
public sealed class WatcherProposalDraftingService
{
    private static readonly string[] TerminalLanes = [TaskStates.Completed, TaskStates.Archive];

    private readonly TaskMutationService _mutations;
    private readonly TaskScannerService _scanner;
    private readonly TimelineLog _timeline;
    private readonly WatcherProposalStore _proposals;
    private readonly WatcherContingentService _contingent;
    private readonly TagRegistryService _tags;
    private readonly ModelRoutingPolicyRegistry _modelRouting;
    private readonly ILogger<WatcherProposalDraftingService> _logger;

    private const string ProposalTag = "watcher-proposal";

    public WatcherProposalDraftingService(
        TaskMutationService mutations,
        TaskScannerService scanner,
        TimelineLog timeline,
        WatcherProposalStore proposals,
        WatcherContingentService contingent,
        TagRegistryService tags,
        ModelRoutingPolicyRegistry modelRouting,
        ILogger<WatcherProposalDraftingService> logger)
    {
        _mutations = mutations;
        _scanner = scanner;
        _timeline = timeline;
        _proposals = proposals;
        _contingent = contingent;
        _tags = tags;
        _modelRouting = modelRouting;
        _logger = logger;
        EnsureTagRegistered();
    }

    public WatcherDraftResult Draft(
        string workspaceRoot,
        WatcherCase watcherCase,
        WatcherEvidencePack pack,
        WatcherAnalysisReceipt? analysis,
        WatcherContingentBudgets budgets,
        DateTime nowUtc)
    {
        var existingProposalJob = watcherCase.ProposalJobId != null
            ? _scanner.FindJob(watcherCase.ProposalJobId)
            : null;
        var openExistingCard = existingProposalJob != null && !TerminalLanes.Contains(existingProposalJob.State);

        if (openExistingCard)
        {
            if (!_contingent.CanCreateProposal(workspaceRoot, budgets, nowUtc))
                return new WatcherDraftResult(WatcherDraftOutcome.ContingentExhausted, null);

            _timeline.Append(
                existingProposalJob!.FolderPath,
                TimelineEventKinds.WatcherCaseLinked,
                TimelineActors.System,
                $"Watcher case {watcherCase.Id} recurred ({watcherCase.OccurrenceCount} occurrences, {watcherCase.SweepCount} sweeps): {watcherCase.LastSummary}",
                details: new Dictionary<string, string>
                {
                    ["caseId"] = watcherCase.Id,
                    ["fingerprint"] = watcherCase.Fingerprint,
                    ["detectorClass"] = watcherCase.DetectorClass,
                    ["evidenceDigest"] = pack.DigestSha256,
                });
            _contingent.RecordComment(workspaceRoot, nowUtc);

            var comment = new WatcherProposal
            {
                Id = _proposals.FindByCaseId(workspaceRoot, watcherCase.Id)?.Id ?? _proposals.AllocateProposalId(workspaceRoot),
                CaseId = watcherCase.Id,
                DetectorClass = watcherCase.DetectorClass,
                Fingerprint = watcherCase.Fingerprint,
                Project = watcherCase.Project,
                IsComment = true,
                CommentedJobId = existingProposalJob!.Id,
                Title = BuildTitle(watcherCase),
                Tags = [ProposalTag, $"watcher-{watcherCase.DetectorClass}"],
                CreatedAtUtc = nowUtc,
            };
            _proposals.Save(workspaceRoot, comment);
            return new WatcherDraftResult(WatcherDraftOutcome.Commented, comment);
        }

        if (!_contingent.CanCreateProposal(workspaceRoot, budgets, nowUtc))
            return new WatcherDraftResult(WatcherDraftOutcome.ContingentExhausted, null);

        var title = BuildTitle(watcherCase);
        var prompt = BuildPrompt(watcherCase, pack, analysis);
        var recommendation = RecommendModel(watcherCase, title, prompt);

        // A workspace-wide finding (quota probe silence/drift, not
        // attributable to one project) still needs a home card - the task
        // API has no project-less lane. Route it to the first registered
        // project; the prompt names the finding as workspace-wide so a human
        // does not mistake it for a bug in that specific project.
        var watchPath = string.Equals(watcherCase.Project, QuotaProbeSignalProbe.WorkspaceProject, StringComparison.Ordinal)
            ? _scanner.GetWatchPaths().FirstOrDefault()?.Path
            : _scanner.GetWatchPaths()
                .FirstOrDefault(w => string.Equals(w.Name, watcherCase.Project, StringComparison.OrdinalIgnoreCase))
                ?.Path;
        if (string.IsNullOrWhiteSpace(watchPath))
        {
            _logger.LogWarning(
                "Watcher proposal creation skipped for case {CaseId}: project '{Project}' is not a registered watch path",
                watcherCase.Id, watcherCase.Project);
            return new WatcherDraftResult(WatcherDraftOutcome.CreationFailed, null);
        }

        // CreateTaskRequest.Project expects a short code or PROJ-NNN id, not
        // the display name WatcherCase.Project carries (it matches
        // WatchPathEntry.Name for probe/activity correlation) - route by the
        // resolved WatchPath instead, the same way ConceptPromotionService does.
        var jobId = _mutations.CreateJob(new CreateTaskRequest
        {
            Id = $"watcher-{watcherCase.Fingerprint}",
            Title = title,
            PromptMarkdown = prompt,
            WatchPath = watchPath,
            TargetState = TaskStates.Preparation,
            Mode = TaskModes.Coding,
            TaskType = TaskTypes.Chore,
            Tags = [ProposalTag, $"watcher-{watcherCase.DetectorClass}"],
        });

        if (string.IsNullOrWhiteSpace(jobId))
        {
            _logger.LogWarning(
                "Watcher proposal creation failed for case {CaseId} (project {Project})",
                watcherCase.Id, watcherCase.Project);
            return new WatcherDraftResult(WatcherDraftOutcome.CreationFailed, null);
        }

        _contingent.RecordProposal(workspaceRoot, nowUtc);

        var proposal = new WatcherProposal
        {
            Id = _proposals.AllocateProposalId(workspaceRoot),
            CaseId = watcherCase.Id,
            DetectorClass = watcherCase.DetectorClass,
            Fingerprint = watcherCase.Fingerprint,
            Project = watcherCase.Project,
            JobId = jobId,
            Title = title,
            RecommendedModel = recommendation.Model,
            RecommendedThinkingLevel = recommendation.ThinkingLevel ?? "",
            Tags = [ProposalTag, $"watcher-{watcherCase.DetectorClass}"],
            CreatedAtUtc = nowUtc,
        };
        _proposals.Save(workspaceRoot, proposal);
        return new WatcherDraftResult(WatcherDraftOutcome.Created, proposal);
    }

    private ModelRoutingRecommendation RecommendModel(WatcherCase watcherCase, string title, string prompt)
    {
        try
        {
            return _modelRouting.Recommend(TaskTypes.Chore, FallbackCatalogue, economyMode: false, title, prompt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Watcher model recommendation failed for case {CaseId}; leaving it unresolved", watcherCase.Id);
            return new ModelRoutingRecommendation { Model = "(unavailable)", ThinkingLevel = null, Reason = "Routing catalogue unavailable." };
        }
    }

    /// <summary>
    /// A tier-only catalogue (no live CLI process): the routing policy's own
    /// tier model ids, always available. Building proposal drafts is a
    /// background, offline-friendly operation - it must not depend on a live
    /// CLI subprocess succeeding just to attach a display recommendation.
    /// </summary>
    private static readonly CliModelCatalog FallbackCatalogue = new()
    {
        Models =
        [
            new CliModelInfo { Id = ModelIds.Gpt54Mini, Label = "GPT-5.4 Mini", Available = true, ThinkingLevels = ["low", "medium", "high"], DefaultThinkingLevel = "high" },
            new CliModelInfo { Id = "gpt-5.6-terra", Label = "GPT-5.6 Terra", Available = true, ThinkingLevels = ["low", "medium", "high", "xhigh"], DefaultThinkingLevel = "medium" },
            new CliModelInfo { Id = ModelIds.Gpt56Sol, Label = "GPT-5.6 Sol", Available = true, ThinkingLevels = ["low", "medium", "high", "xhigh", "max"], DefaultThinkingLevel = "medium" },
        ],
    };

    private static string BuildTitle(WatcherCase watcherCase) =>
        $"[Watcher] {DetectorClassLabel(watcherCase.DetectorClass)}: {Truncate(watcherCase.LastSummary, 80)}";

    private static string DetectorClassLabel(string detectorClass) => detectorClass switch
    {
        WatcherDetectorClasses.Repetition => "Repeated failure",
        WatcherDetectorClasses.Contradiction => "Contradiction",
        WatcherDetectorClasses.Silence => "Missing signal",
        WatcherDetectorClasses.Drift => "Version drift",
        WatcherDetectorClasses.Hygiene => "Hygiene",
        _ => detectorClass,
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private string BuildPrompt(WatcherCase watcherCase, WatcherEvidencePack pack, WatcherAnalysisReceipt? analysis)
    {
        var evidence = string.Join("\n", pack.Signals.Select(s => $"- **{s.Label}**: {s.Value}"));
        var missing = pack.MissingEvidence.Count == 0
            ? "- (none)"
            : string.Join("\n", pack.MissingEvidence.Select(m => $"- {m}"));
        var affected = watcherCase.AffectedCards.Count == 0
            ? "(none attributed)"
            : string.Join(", ", watcherCase.AffectedCards);
        var analysisSection = analysis is { Ok: true }
            ? $"""

               ## Analysis ({analysis.Model} / {analysis.ThinkingLevel})
               {analysis.Summary}
               """
            : analysis is { Ok: false }
                ? $"\n\n## Analysis\nUnavailable: {analysis.Error}"
                : "";

        return $"""
            # Watcher finding: {DetectorClassLabel(watcherCase.DetectorClass)}

            ## Context
            - Case: `{watcherCase.Id}` (detector class `{watcherCase.DetectorClass}`, fingerprint `{watcherCase.Fingerprint}`)
            - Project: `{watcherCase.Project}`
            - First seen: `{watcherCase.FirstSeenUtc:O}`
            - Last seen: `{watcherCase.LastSeenUtc:O}`
            - Observed in {watcherCase.SweepCount} sweeps, {watcherCase.OccurrenceCount} total occurrences
            - Affected cards: {affected}
            - Evidence pack digest: `{pack.DigestSha256}`

            ## Evidence
            {evidence}

            ## Missing evidence
            {missing}
            {analysisSection}

            ## Changes
            Investigate the root cause named above and fix it so this fingerprint
            stops recurring. Do not treat this draft's wording as the fix - verify
            the evidence against the current code before changing anything.

            ## Acceptance
            - The recurring signal (`{watcherCase.Fingerprint}`) does not reproduce after the fix.
            - A regression test covers the failure mode described in the evidence above.
            """;
    }

    private void EnsureTagRegistered()
    {
        if (!_tags.Exists(ProposalTag))
        {
            try
            {
                _tags.Create(ProposalTag, "Watcher proposal", "amber", "Ticket proposal drafted by the Global Orchestrator Watcher (orchestrator-waechter §10).");
            }
            catch (Exception ex)
            {
                AgentStudio.Diagnostics.SilentCatch.Note(ex, "WatcherProposalDraftingService: tag already registered by a concurrent caller");
            }
        }
    }
}
