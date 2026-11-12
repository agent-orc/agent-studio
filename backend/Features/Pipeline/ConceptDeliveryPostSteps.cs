using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>
/// The concept post-steps of a reviewed Remote delivery: Dossier placement,
/// deterministic concept review, and the human sight-review marker.
/// </summary>
/// <remarks>
/// <para>
/// Delivery contract: a concept card integrates through the same reviewed
/// task-branch merge as every Remote delivery (<see cref="RemoteDeliveryIntegrationCoordinator"/>
/// and <see cref="MergeIntoDevelopRunner"/>). Before that merge, this step
/// proves that the delivery changed exactly one <c>docs/&lt;slug&gt;/</c>
/// directory and that the Dossier passes <see cref="ConceptWorkbenchContract"/>;
/// a delivery that fails is refused and nothing is merged. After the merge it
/// writes the placement record (<see cref="ConceptWorkbenchStore"/>) for the
/// integrated commit, re-runs the concept review on that commit, and leaves
/// the <c>concept-sight-review</c> marker. None of this depends on the
/// ReviewDecisionOrchestrator setting.
/// </para>
/// <para>
/// The Dossier is read from git objects through <see cref="ConceptDossierSnapshot"/>:
/// the merge runs in the Studio-owned integration worktree and never touches
/// the developer checkout. The integrated copy is kept in the card for
/// promotion (<see cref="ConceptWorkbenchStore.SnapshotRoot"/>).
/// </para>
/// </remarks>
public sealed class ConceptDeliveryPostSteps
{
    private readonly GitService _git;
    private readonly TaskScannerService _scanner;
    private readonly PipelineExecutionLog _pipelineLog;
    private readonly TimelineLog _timeline;
    private readonly ProjectSettingsService? _settings;
    private readonly OrchestratorChatLog? _chatLog;
    private readonly ILogger<ConceptDeliveryPostSteps> _logger;

    public ConceptDeliveryPostSteps(
        GitService git,
        TaskScannerService scanner,
        PipelineExecutionLog pipelineLog,
        TimelineLog timeline,
        ILogger<ConceptDeliveryPostSteps> logger,
        ProjectSettingsService? settings = null,
        OrchestratorChatLog? chatLog = null)
    {
        _git = git;
        _scanner = scanner;
        _pipelineLog = pipelineLog;
        _timeline = timeline;
        _logger = logger;
        _settings = settings;
        _chatLog = chatLog;
    }

    /// <summary>Park-reason prefix of a refused concept delivery.</summary>
    public const string PlacementParkPrefix = "Dossier placement failed: ";

    /// <summary>Park-reason prefix of an integrated Dossier that failed concept review.</summary>
    public const string ReviewParkPrefix = "Concept review failed: ";

    /// <summary>The concept card this request integrates, or null for every other card.</summary>
    public TaskInfo? ConceptCard(RemoteDeliveryIntegrationRequest request)
        => _scanner.FindJob(request.JobId, request.WatchPath) is { } job && TaskModes.IsConcept(job.Mode)
            ? job
            : null;

    /// <summary>
    /// Runs the pre-merge placement check, then <paramref name="merge"/>, then
    /// the post-merge record, concept review and sight-review marker.
    /// </summary>
    public async Task<MergeIntoIntegrationResult> IntegrateAsync(
        TaskInfo card,
        RemoteDeliveryIntegrationRequest request,
        Func<Task<MergeIntoIntegrationResult>> merge)
    {
        EnsureConceptRun(card);
        var startedAt = DateTime.UtcNow;
        var check = CheckDelivery(card, request);
        if (check.Failure is { } failure)
            return RefusePlacement(card, request, startedAt, failure);

        var result = await merge().ConfigureAwait(false);
        if (!result.Outcome.IsSuccessfulIntegration())
            return result;

        // The card does not move while the coordinator integrates it, so the
        // request's folder is still the card's folder here.
        var current = _scanner.FindJob(card.Id, card.WatchPath) ?? card;
        var integratedSha = FirstSha(result.MergedSha, result.ContainmentSha, check.ResultSha!);
        var record = new ConceptWorkbenchRecord
        {
            RepoRelativeDirectory = check.Review!.RepoRelativeDirectory!,
            RepoRelativeEntrypoint = check.Review.RepoRelativeDirectory + "/" + ConceptWorkbenchContract.EntryFileName,
            Title = check.Review.Descriptor!.Title,
            PublishedAt = DateTime.UtcNow,
            CommitSha = integratedSha,
        };
        if (!ConceptWorkbenchStore.Write(current.FolderPath, record, _logger))
        {
            var unrecorded = $"The Dossier was integrated at {Short(integratedSha)}, but its placement record could not be written to the task.";
            RecordPlacement(current, startedAt, success: false, unrecorded);
            return result with { PostStepParkReason = PlacementParkPrefix + unrecorded };
        }
        AgentStudio.Tasks.ConceptDossierClosureStore.Clear(current.FolderPath, _logger);
        RecordPlacement(current, startedAt, success: true,
            $"{check.Review.Summary} Integrated into {request.IntegrationBranch} at {Short(integratedSha)} by the reviewed task-branch merge.");

        var review = ReviewIntegratedDossier(check.RepositoryRoot!, integratedSha, record, current);
        ConceptSightReviewHandoff.Record(_pipelineLog, _chatLog, _logger, current, review, record);
        if (review.IsComplete)
            _scanner.InvalidateCache(changedPath: SteerPendingMarker.PathFor(current.FolderPath));
        _logger.LogInformation(
            "concept_delivery_post_steps project={Project} job={JobId} path={Path} commit={Commit} complete={Complete}",
            request.Project, card.Id, record.RepoRelativeEntrypoint, integratedSha, review.IsComplete);
        return review.IsComplete
            ? result
            : result with { PostStepParkReason = ReviewParkPrefix + review.Summary };
    }

    private sealed record DeliveryCheck(
        string? Failure,
        string? RepositoryRoot = null,
        string? ResultSha = null,
        ConceptWorkbenchReview? Review = null);

    private DeliveryCheck CheckDelivery(TaskInfo card, RemoteDeliveryIntegrationRequest request)
    {
        var repositoryRoot = _git.ResolveRepoRootForWatchPath(card.WatchPath);
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
            return new("The project repository is unavailable, so the concept delivery could not be inspected.");

        var delivery = DeliveryRefResolver.Resolve(card.Id, card.FolderPath);
        var resultSha = request.ReviewedResultSha ?? delivery.ExpectedResultSha;
        if (!ReviewSubjectStore.IsValidResultSha(resultSha))
            return new("The concept delivery has no reviewed result SHA to inspect.");
        if (delivery.IsRemote)
        {
            var fetchError = _git.FetchFencedDeliveryForInspection(repositoryRoot, delivery.Ref, resultSha!);
            if (fetchError is not null)
                return new($"The concept delivery could not be fetched for inspection: {fetchError}");
        }

        var branch = _git.ResolveIntegrationBranch(repositoryRoot, request.IntegrationBranch);
        var baseRef = _git.IntegrationBaseRef(repositoryRoot, branch);
        var changed = _git.ChangedPathsAgainstMergeBase(repositoryRoot, baseRef, resultSha!);
        // A replayed integration (a restart between merge and lane move) finds
        // the result already on the integration line; its own change is then
        // read against the base the run started from.
        if (changed is { Count: 0 }
            && ReviewSubjectStore.Read(card.FolderPath)?.BaseSha is { } runBase
            && ReviewSubjectStore.IsValidResultSha(runBase))
            changed = _git.ChangedPathsAgainstMergeBase(repositoryRoot, runBase, resultSha!);
        if (changed is null)
            return new($"The changed files of {Short(resultSha!)} against {branch} could not be computed.");

        var containment = ConceptWorkbenchContract.ReviewContainment(changed);
        if (containment.RepoRelativeDirectory is not { } directory)
            return new(containment.Summary);

        // The reviewed commit is checked through the card's Dossier copy; the
        // post-merge step overwrites it with the integrated commit.
        var copyRoot = ConceptWorkbenchStore.SnapshotRoot(card.FolderPath);
        if (!ConceptDossierSnapshot.Write(_git, repositoryRoot, resultSha!, directory, copyRoot, out var readFailure))
            return new(readFailure);
        var review = ConceptWorkbenchContract.ReviewDirectory(copyRoot, directory, card.Key ?? card.Id);
        if (!review.IsComplete || review.Descriptor is null) return new(review.Summary);

        var entrypoint = directory + "/" + ConceptWorkbenchContract.EntryFileName;
        KeepStatusReference(card.FolderPath, entrypoint);
        var references = AgentStudio.Tasks.ConceptDossierContract.ReviewAgentReferences(
            card.FolderPath, entrypoint);
        if (references.Count > 0) return new(string.Join(" ", references));
        return new(null, repositoryRoot, resultSha, review);
    }

    /// <summary>
    /// On the Remote path the card's <c>status.md</c> is the backend-owned
    /// result summary, not an agent file. It names the Dossier that the
    /// agent's <c>results/deliverables.md</c> names, exactly as summary
    /// generation preserves it, so a degraded or pending summary cannot
    /// refuse a complete delivery.
    /// </summary>
    private void KeepStatusReference(string folder, string entrypoint)
    {
        var named = AgentStudio.Tasks.ConceptDossierContract.Read(folder);
        if (!string.Equals(named.RepoRelativePath, entrypoint, StringComparison.OrdinalIgnoreCase)) return;
        var statusPath = Path.Combine(folder, "status.md");
        try
        {
            var current = File.Exists(statusPath) ? File.ReadAllText(statusPath) : string.Empty;
            var updated = AgentStudio.Tasks.ConceptDossierContract.PreserveReferenceInStatus(current, entrypoint);
            if (!string.Equals(updated, current, StringComparison.Ordinal))
                File.WriteAllText(statusPath, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not keep the Dossier reference in {StatusPath}", statusPath);
        }
    }

    /// <summary>
    /// Stores the Dossier's contract files at the integrated commit in the
    /// card (<see cref="ConceptWorkbenchStore.SnapshotRoot"/>) and reviews that
    /// copy. Promotion reads the same copy while the developer checkout does
    /// not carry the Dossier, without a git read on its request path.
    /// </summary>
    private ConceptWorkbenchReview ReviewIntegratedDossier(
        string repositoryRoot,
        string integratedSha,
        ConceptWorkbenchRecord record,
        TaskInfo card)
    {
        var snapshotRoot = ConceptWorkbenchStore.SnapshotRoot(card.FolderPath);
        return ConceptDossierSnapshot.Write(
                _git, repositoryRoot, integratedSha, record.RepoRelativeDirectory, snapshotRoot, out var readFailure)
            ? ConceptWorkbenchContract.ReviewDirectory(snapshotRoot, record.RepoRelativeDirectory, card.Key ?? card.Id)
            : new ConceptWorkbenchReview(false, null, record.RepoRelativeDirectory, null, [readFailure!]);
    }

    private MergeIntoIntegrationResult RefusePlacement(
        TaskInfo card,
        RemoteDeliveryIntegrationRequest request,
        DateTime startedAt,
        string failure)
    {
        RecordPlacement(card, startedAt, success: false, failure);
        var now = DateTime.UtcNow;
        // The refusal is this delivery's integration outcome. Its typed merge
        // row is what the card's integration status projects, so the
        // delivery-chain reconciler escalates the card with this reason
        // instead of leaving it integrating without an owner.
        _pipelineLog.RecordStep(card.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            StartedAt = startedAt,
            CompletedAt = now,
            Verdict = AcceptedIntegrationFailureCodes.ConceptDossierRejected,
            VerdictSummary = "Dossier placement refused the concept delivery; nothing was merged.",
            Reason = PlacementParkPrefix + failure,
            FailureCode = AcceptedIntegrationFailureCodes.ConceptDossierRejected,
        });
        _pipelineLog.RecordStep(card.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.ConceptReviewStepId,
            Kind = StepKind.Orchestrator,
            Status = PipelineStepStatus.Skipped,
            StartedAt = now,
            CompletedAt = now,
            Verdict = "not-placed",
            Reason = "Dossier placement refused the delivery, so there is no integrated Dossier to review.",
        });
        _timeline.Append(
            card.FolderPath,
            TimelineEventKinds.IntegrationFailed,
            TimelineActors.System,
            $"Dossier placement refused the concept delivery before the task-branch merge; nothing was merged. {failure}",
            details: new Dictionary<string, string>
            {
                ["outcome"] = AcceptedIntegrationFailureCodes.ConceptDossierRejected,
                ["integrationBranch"] = request.IntegrationBranch,
                ["detail"] = failure,
                ["stage"] = RemoteDeliveryIntegrationCoordinator.PreHumanReviewStage,
            });
        _logger.LogWarning(
            "concept_delivery_refused project={Project} job={JobId} reason={Reason}",
            request.Project, card.Id, failure);
        return MergeIntoIntegrationResult.Failed(AcceptedIntegrationFailureCodes.ConceptDossierRejected, failure)
            with { PostStepParkReason = PlacementParkPrefix + failure };
    }

    private void RecordPlacement(TaskInfo card, DateTime startedAt, bool success, string summary)
        => _pipelineLog.RecordStep(card.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.ConceptWorkbenchPlacementStepId,
            Kind = StepKind.Tool,
            Status = success ? PipelineStepStatus.Passed : PipelineStepStatus.Failed,
            StartedAt = startedAt,
            CompletedAt = DateTime.UtcNow,
            Verdict = success ? "workbench-published" : "workbench-invalid",
            VerdictSummary = summary,
            Reason = success ? null : summary,
            FailureCode = success ? null : AcceptedIntegrationFailureCodes.ConceptDossierRejected,
        });

    private void EnsureConceptRun(TaskInfo card)
    {
        var settings = PipelineTypeSettings.ForTask(_settings?.Get(card.ProjectName), card);
        _pipelineLog.EnsureRun(
            card.FolderPath,
            ProjectPipelineOrder.Apply(PipelineCatalogue.Concept, settings),
            card.ProjectName,
            card.Id);
    }

    private static string FirstSha(params string?[] candidates)
        => candidates.First(ReviewSubjectStore.IsValidResultSha)!;

    private static string Short(string sha) => sha[..Math.Min(8, sha.Length)];
}

/// <summary>
/// The concept-review row and the sight-review hand-off shared by the Remote
/// delivery post-steps and the local Auto Review path, so both write the same
/// pipeline rows, marker, and chat note.
/// </summary>
public static class ConceptSightReviewHandoff
{
    public const string SightReviewQuestion =
        "Review the concept Dossier, then promote its implementation cards or finish with no implementation.";

    public static void Record(
        PipelineExecutionLog? pipelineLog,
        OrchestratorChatLog? chatLog,
        ILogger? logger,
        TaskInfo card,
        ConceptWorkbenchReview review,
        ConceptWorkbenchRecord? record)
    {
        var now = DateTime.UtcNow;
        pipelineLog?.RecordStep(card.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.ConceptReviewStepId,
            Kind = StepKind.Orchestrator,
            Status = review.IsComplete ? PipelineStepStatus.Passed : PipelineStepStatus.Failed,
            StartedAt = now,
            CompletedAt = now,
            Verdict = review.IsComplete ? "concept-complete" : "concept-incomplete",
            VerdictSummary = review.Summary,
            Reason = review.IsComplete ? null : review.Summary,
        });
        if (!review.IsComplete || record is null) return;

        SteerPendingMarker.Write(card.FolderPath, new SteerPendingRecord
        {
            WaitStartedAt = now,
            Kind = SteerPendingKinds.ConceptSightReview,
            Question = SightReviewQuestion,
            CliType = card.CliType,
        }, logger);
        pipelineLog?.RecordStep(card.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.ConceptSightReviewGateStepId,
            Kind = StepKind.Orchestrator,
            Status = PipelineStepStatus.Running,
            StartedAt = now,
            Verdict = "awaiting-sight-review",
            VerdictSummary = "Concept delivered successfully; awaiting human sight review.",
        });
        chatLog?.Append(
            card,
            OrchestratorMessageKind.Decision,
            $"Concept delivered: `{record.RepoRelativeEntrypoint}`. Awaiting sight review; NeedsInput is expected and is not an escalation.");
    }
}
