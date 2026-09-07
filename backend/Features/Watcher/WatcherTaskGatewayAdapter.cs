namespace AgentStudio.Watcher;

/// <summary>
/// The live <see cref="IWatcherTaskGateway"/>. Every write goes through the
/// ordinary task API surface - <see cref="TaskMutationService"/> for creation
/// and tags, <see cref="TaskTransitionService"/> for the one approved lane
/// move, <see cref="TimelineLog"/> for comments - so fences, permissions,
/// budgets, and the state machine keep their ownership.
/// </summary>
public sealed class WatcherTaskGatewayAdapter : IWatcherTaskGateway
{
    /// <summary>Lanes in which a card is no longer live work for the fingerprint that produced it.</summary>
    private static readonly HashSet<string> ClosedLanes = new(StringComparer.Ordinal)
    {
        TaskStates.Completed,
        TaskStates.Archive,
    };

    /// <summary>Recorded on every Watcher-authored timeline row so the audit can be filtered by source.</summary>
    public const string Source = "global-watcher";

    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskTransitionService _transitions;
    private readonly TimelineLog _timeline;
    private readonly ILogger<WatcherTaskGatewayAdapter> _logger;

    public WatcherTaskGatewayAdapter(
        TaskScannerService scanner,
        TaskMutationService mutations,
        TaskTransitionService transitions,
        TimelineLog timeline,
        ILogger<WatcherTaskGatewayAdapter> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _transitions = transitions;
        _timeline = timeline;
        _logger = logger;
    }

    public IReadOnlyList<string> OpenCards(string? project, IReadOnlyCollection<string> taskKeys)
    {
        if (taskKeys.Count == 0) return [];
        var wanted = new HashSet<string>(taskKeys, StringComparer.OrdinalIgnoreCase);
        return _scanner.ScanAllJobs()
            .Where(job => wanted.Contains(job.TaskKey) || wanted.Contains(job.Id))
            .Where(job => project is null
                          || string.Equals(job.ProjectName, project, StringComparison.OrdinalIgnoreCase))
            .Where(job => !ClosedLanes.Contains(job.State))
            .Select(job => job.TaskKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public WatcherCardCreation? CreateProposalCard(
        string project,
        WatcherCardDraft draft,
        WatcherModelRecommendation recommendation,
        string caseId)
    {
        var watchPath = WatchPath(project);
        if (watchPath is null)
        {
            _logger.LogWarning("watcher-proposal-no-watch-path project={Project} case={CaseId}", project, caseId);
            return null;
        }

        var createdId = _mutations.CreateJob(new CreateTaskRequest
        {
            Title = draft.Title,
            WatchPath = watchPath,
            // The proposal state of section 10.4. Never Ready: a proposal that
            // could start itself would not be a proposal.
            TargetState = TaskStates.Preparation,
            TaskType = draft.TaskType,
            Tags = [.. draft.Tags],
            PromptMarkdown = draft.Prompt,
            Model = recommendation.Model,
            ThinkingLevel = recommendation.ThinkingLevel,
            // The route is a policy recommendation, not an operator pin, so the
            // card stays open to re-qualification until a human confirms it.
            ModelExplicit = false,
            ThinkingLevelExplicit = false,
        });

        if (createdId is null)
        {
            _logger.LogWarning("watcher-proposal-create-refused project={Project} case={CaseId}", project, caseId);
            return null;
        }

        var created = _scanner.FindJob(createdId, watchPath);
        if (created is null) return new WatcherCardCreation(createdId, createdId);

        if (draft.References.Count > 0)
        {
            _mutations.SetTaskReferences(
                createdId,
                new TaskReferences { RelatedTo = [.. draft.References] },
                watchPath);
        }

        _timeline.Append(
            created.FolderPath,
            TimelineEventKinds.WatcherCommented,
            TimelineActors.System,
            $"The Global Watcher drafted this card from case {caseId}.",
            details: new Dictionary<string, string>
            {
                ["source"] = Source,
                ["caseId"] = caseId,
                ["recommendedModel"] = recommendation.Model,
                ["policyTier"] = recommendation.Tier,
            });

        return new WatcherCardCreation(created.Id, created.TaskKey);
    }

    public bool AppendComment(string project, string taskKey, string caseId, string summary, string body)
    {
        var job = _scanner.ScanAllJobs().FirstOrDefault(candidate =>
            (string.Equals(candidate.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)
             || string.Equals(candidate.Id, taskKey, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(project)
                || string.Equals(candidate.ProjectName, project, StringComparison.OrdinalIgnoreCase)));
        if (job is null)
        {
            _logger.LogWarning("watcher-comment-target-missing task={TaskKey} case={CaseId}", taskKey, caseId);
            return false;
        }

        return _timeline.Append(
            job.FolderPath,
            TimelineEventKinds.WatcherCommented,
            TimelineActors.System,
            summary,
            details: new Dictionary<string, string>
            {
                ["source"] = Source,
                ["caseId"] = caseId,
                ["body"] = body,
            });
    }

    public async Task<bool> PromoteToReadyAsync(
        string project,
        string taskId,
        WatcherModelRecommendation recommendation,
        string decidedBy,
        CancellationToken ct)
    {
        var watchPath = WatchPath(project);
        var job = _scanner.FindJob(taskId, watchPath);
        if (job is null)
        {
            _logger.LogWarning("watcher-approve-target-missing task={TaskId} project={Project}", taskId, project);
            return false;
        }

        // The proposal tag is what marks a card as unapproved. Approval drops
        // it so the card is ordinary work from here on; the class tag stays as
        // provenance.
        var tags = job.Tags
            .Where(tag => !string.Equals(tag, WatcherTags.Proposal, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _mutations.SetJobTags(job.Id, tags, job.WatchPath);
        _mutations.SetJobModel(job.Id, recommendation.Model, job.WatchPath);
        if (recommendation.ThinkingLevel is { Length: > 0 })
            _mutations.SetJobThinkingLevel(job.Id, recommendation.ThinkingLevel, job.WatchPath);

        var outcome = await _transitions.MoveAsync(
            job.Id,
            TaskStates.Ready,
            job.WatchPath,
            ct,
            cause: decidedBy,
            reason: $"An operator approved the Watcher proposal and selected {recommendation.Model}.",
            expectedSourceState: TaskStates.Preparation);

        if (outcome.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "watcher-approve-refused task={TaskId} status={Status} message={Message}",
                job.Id,
                outcome.Status,
                outcome.Message);
            return false;
        }

        return true;
    }

    private string? WatchPath(string project)
        => _scanner.GetWatchPaths()
            .FirstOrDefault(entry => string.Equals(entry.Name, project, StringComparison.OrdinalIgnoreCase))
            ?.Path;
}
