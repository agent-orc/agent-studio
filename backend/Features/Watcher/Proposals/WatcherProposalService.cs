using System.Globalization;

namespace AgentStudio.Watcher;

/// <summary>What one proposal attempt produced.</summary>
/// <param name="Case">The case after the attempt.</param>
/// <param name="Proposal">Null when the contingent or the task API refused.</param>
/// <param name="BlockedReason">Named dimension or error, for the backlog view.</param>
/// <param name="ModelCalls">Model calls this attempt spent.</param>
public sealed record WatcherProposalOutcome(
    WatcherCase Case,
    WatcherProposal? Proposal,
    string? BlockedReason,
    int ModelCalls);

/// <summary>
/// Open Watcher cards keyed by their fingerprint tag, built with one workspace
/// scan. Two cases in one sweep never share a fingerprint, so a snapshot taken
/// before the sweep proposes stays correct for the whole sweep.
/// </summary>
public sealed class WatcherOpenCardIndex
{
    private readonly IReadOnlyDictionary<string, TaskInfo> _byTag;

    public WatcherOpenCardIndex(IReadOnlyDictionary<string, TaskInfo> byTag)
    {
        _byTag = byTag;
    }

    public static WatcherOpenCardIndex Empty { get; } =
        new(new Dictionary<string, TaskInfo>(StringComparer.OrdinalIgnoreCase));

    public TaskInfo? Find(string fingerprintDigest) =>
        _byTag.GetValueOrDefault(WatcherTags.Fingerprint(fingerprintDigest));
}

/// <summary>
/// Turns a persistent case into a ticket proposal through the normal task API.
/// </summary>
/// <remarks>
/// <para>
/// Everything this service writes is either a card in the proposal state
/// (<c>1-preparation</c>, tagged <c>watcher-proposal</c>) or a comment on a card
/// that already carries the fingerprint. It never moves a card to Ready, never
/// touches Git, and never writes into the workspace outside the task API.
/// </para>
/// <para>
/// Contingent admission happens before any spend, and every unit spent is
/// booked into the ledger immediately, so a crash mid-proposal cannot produce
/// unbooked cost.
/// </para>
/// </remarks>
public sealed class WatcherProposalService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TimelineLog _timeline;
    private readonly WatcherStore _store;
    private readonly WatcherBusPublisher _publisher;
    private readonly ModelRoutingPolicyRegistry _routing;
    private readonly IModelRoutingModeProvider _routingMode;
    private readonly IWatcherAnalyst _analyst;
    private readonly ILogger<WatcherProposalService> _logger;

    public WatcherProposalService(
        TaskScannerService scanner,
        TaskMutationService mutations,
        TimelineLog timeline,
        WatcherStore store,
        WatcherBusPublisher publisher,
        ModelRoutingPolicyRegistry routing,
        IModelRoutingModeProvider routingMode,
        IWatcherAnalyst analyst,
        ILogger<WatcherProposalService> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _timeline = timeline;
        _store = store;
        _publisher = publisher;
        _routing = routing;
        _routingMode = routingMode;
        _analyst = analyst;
        _logger = logger;
    }

    /// <summary>
    /// Propose a ticket for one persistent case. Returns the case unchanged with
    /// a blocked reason when the contingent is exhausted; the case stays
    /// countable and visible either way.
    /// </summary>
    /// <param name="openCards">
    /// Fingerprint index taken once per sweep. Creating a card invalidates the
    /// task index, so looking the fingerprint up per proposal would rebuild the
    /// whole workspace scan once per proposal.
    /// </param>
    public async Task<WatcherProposalOutcome> ProposeAsync(
        WatcherCase item,
        WatcherOptions options,
        DateTime nowUtc,
        WatcherOpenCardIndex? openCards = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(options);

        var pack = WatcherEvidencePackBuilder.Build(item, options.EvidenceCharacterBudget);
        var index = openCards ?? OpenCards();
        var existing = index.Find(item.FingerprintDigest);
        var kind = existing is null ? WatcherProposalKinds.NewCard : WatcherProposalKinds.Comment;
        var contingentKind = kind == WatcherProposalKinds.NewCard
            ? WatcherContingentKinds.Proposal
            : WatcherContingentKinds.Comment;

        var admission = Admit(contingentKind, options, nowUtc);
        if (!admission.Allowed)
            return Backlog(item, admission.Reason!, nowUtc, 0);

        var (analysis, modelCalls) = await AnalyseAsync(item, pack, options, nowUtc, ct);

        var draft = WatcherProposalDraftBuilder.Build(
            item,
            pack,
            analysis,
            overlappingCards: existing is null ? [] : [existing.TaskKey]);
        var recommendation = WatcherModelRouting.Recommend(
            _routing,
            _routingMode.EconomyMode,
            item.DetectorClass,
            draft.Title,
            draft.PromptMarkdown);

        var proposal = new WatcherProposal
        {
            Id = WatcherIdentity.ProposalId(item.Id, item.EvidenceDigest),
            CaseId = item.Id,
            Fingerprint = item.Fingerprint,
            FingerprintDigest = item.FingerprintDigest,
            DetectorClass = item.DetectorClass,
            DetectorRule = item.DetectorRule,
            Project = item.Project,
            Kind = kind,
            Draft = draft,
            Recommendation = recommendation,
            EvidenceDigest = item.EvidenceDigest,
            Evidence = pack.Items,
            ModelCalls = analysis?.Calls ?? [],
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };

        proposal = kind == WatcherProposalKinds.Comment
            ? CommentOn(existing!, item, pack, proposal)
            : CreateCard(item, options, proposal);

        if (proposal.CreatedTaskKey is null && proposal.CommentedOnTaskKey is null)
        {
            // The task API refused. Nothing was spent on the board, so the case
            // stays eligible and the next sweep tries again.
            return new WatcherProposalOutcome(item, null, "The task API refused the proposal.", modelCalls);
        }

        _store.Record(new WatcherContingentEntry
        {
            AtUtc = nowUtc,
            Kind = contingentKind,
            CaseId = item.Id,
        });
        _store.Upsert(proposal);

        var updated = item with
        {
            State = WatcherCaseStates.Proposed,
            ProposalId = proposal.Id,
            UpdatedAtUtc = nowUtc,
        };
        _store.Upsert(updated);

        await _publisher.AnalysisCompleteAsync(updated, proposal, ct);
        await _publisher.DecisionRequiredAsync(updated, proposal, ct);

        _logger.LogInformation(
            "watcher-proposal case={CaseId} proposal={ProposalId} kind={Kind} detector={DetectorClass} card={TaskKey} tier={Tier} model={Model}",
            updated.Id,
            proposal.Id,
            proposal.Kind,
            proposal.DetectorClass,
            proposal.CreatedTaskKey ?? proposal.CommentedOnTaskKey,
            recommendation.Tier,
            recommendation.Model);

        return new WatcherProposalOutcome(updated, proposal, null, modelCalls);
    }

    private async Task<(WatcherAnalysis? Analysis, int Calls)> AnalyseAsync(
        WatcherCase item,
        WatcherEvidencePack pack,
        WatcherOptions options,
        DateTime nowUtc,
        CancellationToken ct)
    {
        // Section 5: no model for detection, and a strong call only on a
        // declared uncertainty. A hygiene case whose validator already named the
        // cause never reaches here.
        if (!options.AnalysisEnabled || !item.UncertainCause) return (null, 0);

        var admission = Admit(
            WatcherContingentKinds.ModelCall, options, nowUtc, pack.EstimatedTokens);
        if (!admission.Allowed)
        {
            _logger.LogInformation(
                "watcher-analysis-skipped case={CaseId} reason={Reason}", item.Id, admission.Reason);
            return (null, 0);
        }

        WatcherAnalysis? analysis;
        try
        {
            analysis = await _analyst.AnalyseAsync(pack, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "watcher-analysis-failed case={CaseId}", item.Id);
            return (null, 0);
        }

        // Book every call the analyst actually made, including a failed one:
        // an attempt that produced no answer still consumed the budget.
        foreach (var call in analysis?.Calls ?? [])
        {
            _store.Record(new WatcherContingentEntry
            {
                AtUtc = call.AtUtc,
                Kind = WatcherContingentKinds.ModelCall,
                Tokens = call.InputTokens + call.OutputTokens,
                CostUsd = call.CostUsd,
                PriceKnown = call.PriceKnown,
                CaseId = item.Id,
                Model = call.Model,
            });
        }

        return (analysis, analysis?.Calls.Count ?? 0);
    }

    private WatcherProposal CreateCard(WatcherCase item, WatcherOptions options, WatcherProposal proposal)
    {
        var watchPath = ResolveWatchPath(item, options);
        if (watchPath is null)
        {
            _logger.LogWarning("watcher-proposal-refused case={CaseId} reason=no-watch-path", item.Id);
            return proposal;
        }

        var jobId = _mutations.CreateJob(new CreateTaskRequest
        {
            Title = proposal.Draft.Title,
            WatchPath = watchPath,
            PromptMarkdown = proposal.Draft.PromptMarkdown,
            // The proposal state of section 10.4. Never Ready: an operator
            // decision is the only path from here into the run queue.
            TargetState = TaskStates.Preparation,
            TaskType = proposal.Draft.TaskType,
            Tags = proposal.Draft.Tags.ToList(),
            // A recommended model without its CLI is not actionable: thinking
            // levels and model ids only normalise against a CLI. The routing
            // policy's tiers are the pipeline default CLI's catalogue.
            CliType = PipelineStepModelDefaults.DefaultCli,
            Model = proposal.Recommendation.Model,
            ThinkingLevel = proposal.Recommendation.ThinkingLevel,
            // The route is a recommendation from the policy, not an operator
            // pin, so qualification may still replace it when the card runs.
            ModelExplicit = false,
            ThinkingLevelExplicit = false,
        });
        if (jobId is null)
        {
            _logger.LogWarning("watcher-proposal-refused case={CaseId} reason=create-refused", item.Id);
            return proposal;
        }

        var created = _scanner.FindJob(jobId, watchPath);
        if (created is null)
        {
            _logger.LogWarning("watcher-proposal-refused case={CaseId} reason=created-card-not-found", item.Id);
            return proposal;
        }

        if (proposal.Draft.RelatedTo.Count > 0)
        {
            _mutations.SetTaskReferences(
                jobId,
                new TaskReferences { RelatedTo = proposal.Draft.RelatedTo.ToList() },
                watchPath);
        }

        AppendAudit(created, TimelineEventKinds.WatcherProposed, item, proposal,
            $"The global Watcher proposed this card from case {item.Id}.");

        return proposal with { CreatedTaskKey = created.TaskKey };
    }

    private WatcherProposal CommentOn(
        TaskInfo card,
        WatcherCase item,
        WatcherEvidencePack pack,
        WatcherProposal proposal)
    {
        // Section 10.3: a fingerprint that already has an open card gets a
        // comment, not a duplicate card.
        var appended = _mutations.AppendContinuationNote(
            card.Id,
            WatcherProposalDraftBuilder.Comment(item, pack),
            card.WatchPath);
        if (!appended)
        {
            _logger.LogWarning(
                "watcher-comment-refused case={CaseId} card={TaskKey}", item.Id, card.TaskKey);
            return proposal;
        }

        AppendAudit(card, TimelineEventKinds.WatcherProposed, item, proposal,
            $"The global Watcher appended case {item.Id} to this card instead of creating a duplicate.");

        return proposal with { CommentedOnTaskKey = card.TaskKey };
    }

    /// <summary>
    /// Index the open Watcher cards by their fingerprint tag with one scan.
    /// Terminal lanes are excluded so a finished card does not swallow a
    /// returning problem.
    /// </summary>
    public WatcherOpenCardIndex OpenCards()
    {
        var byTag = _scanner.ScanAllAutomationJobs()
            .Where(job => job.State is not (TaskStates.Completed or TaskStates.Archive))
            .SelectMany(job => job.Tags
                .Where(tag => tag.StartsWith(WatcherTags.FingerprintPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(tag => (tag, job)))
            .GroupBy(pair => pair.tag, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(pair => pair.job)
                    .OrderBy(job => job.TaskKey, StringComparer.OrdinalIgnoreCase)
                    .First(),
                StringComparer.OrdinalIgnoreCase);
        return new WatcherOpenCardIndex(byTag);
    }

    private string? ResolveWatchPath(WatcherCase item, WatcherOptions options)
    {
        var watchPaths = _scanner.GetWatchPaths();
        if (watchPaths.Count == 0) return null;

        var named = item.Project ?? options.ProposalProject;
        if (!string.IsNullOrWhiteSpace(named))
        {
            var match = watchPaths.FirstOrDefault(entry =>
                string.Equals(entry.Name, named, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.Path;
            _logger.LogWarning(
                "watcher-proposal-project-unresolved case={CaseId} project={Project} fallback={Fallback}",
                item.Id,
                named,
                watchPaths[0].Name);
        }
        else if (item.Project is null && options.ProposalProject is null)
        {
            _logger.LogInformation(
                "watcher-proposal-project-default case={CaseId} fallback={Fallback} reason=workspace-wide-case",
                item.Id,
                watchPaths[0].Name);
        }

        return watchPaths[0].Path;
    }

    private void AppendAudit(
        TaskInfo card,
        string kind,
        WatcherCase item,
        WatcherProposal proposal,
        string summary)
    {
        _timeline.Append(
            card.FolderPath,
            kind,
            TimelineActors.System,
            summary,
            details: new Dictionary<string, string>
            {
                ["caseId"] = item.Id,
                ["proposalId"] = proposal.Id,
                ["detectorClass"] = item.DetectorClass,
                ["detectorRule"] = item.DetectorRule,
                ["fingerprint"] = item.Fingerprint,
                ["evidenceDigest"] = item.EvidenceDigest,
                ["occurrences"] = item.Occurrences.ToString(CultureInfo.InvariantCulture),
                ["recommendedTier"] = proposal.Recommendation.Tier,
                ["recommendedModel"] = proposal.Recommendation.Model,
                ["modelCalls"] = proposal.ModelCalls.Count.ToString(CultureInfo.InvariantCulture),
            });
    }

    private WatcherContingentVerdict Admit(
        string kind,
        WatcherOptions options,
        DateTime nowUtc,
        long estimatedTokens = 0)
    {
        var usage = WatcherContingentPolicy.Usage(_store.Ledger(), nowUtc);
        return WatcherContingentPolicy.Admit(kind, options.Contingent, usage, estimatedTokens);
    }

    private WatcherProposalOutcome Backlog(
        WatcherCase item,
        string reason,
        DateTime nowUtc,
        int modelCalls)
    {
        // Counting continues. The case stays visible as unanalysed backlog and
        // becomes eligible again as soon as the budget window rolls over.
        var updated = item.State == WatcherCaseStates.Backlogged
            ? item
            : _store.Upsert(item with { State = WatcherCaseStates.Backlogged, UpdatedAtUtc = nowUtc });
        return new WatcherProposalOutcome(updated, null, reason, modelCalls);
    }
}
