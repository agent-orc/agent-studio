namespace AgentStudio.Tasks;

/// <summary>A decision card raised on behalf of a card that cannot continue without it.</summary>
public sealed record DecisionCardRequest
{
    public string WatchPath { get; init; } = "";
    public string Title { get; init; } = "";
    public DecisionContent Content { get; init; } = new();
    /// <summary>The card the fork blocks; it becomes a dependant and the apply target.</summary>
    public TaskInfo? BlockedCard { get; init; }
    public string? PromptMarkdown { get; init; }
    public string CreationSource { get; init; } = TimelineActors.Orchestrator;
    public string CreatedBy { get; init; } = "Orchestrator";
}

/// <summary>The decision card a request produced, or the pending one it reused.</summary>
public sealed record DecisionCardRequestResult(string JobId, string Key, bool Created);

/// <summary>
/// Creates a decision card (kind <c>decision</c> with its options payload)
/// for a card that reached a fork, links the blocked card as dependant and
/// apply target, and gives it a <c>dependsOn</c> edge so the claim guard holds
/// it until the decision is taken. Shared by the runner's Blocked outcome and
/// the failure intervention service.
/// </summary>
public sealed class DecisionCardRequests
{
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly ILogger<DecisionCardRequests> _logger;

    public DecisionCardRequests(TaskScannerService scanner, TaskMutationService mutations,
        ILogger<DecisionCardRequests> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _logger = logger;
    }

    public DecisionCardRequestResult? Request(DecisionCardRequest request)
    {
        var blocked = request.BlockedCard;
        var blockedKey = blocked is null ? null : blocked.Key ?? blocked.Id;
        if (blocked is not null && PendingDecisionFor(blocked, request.Content.Question) is { } reused)
            return new(reused.Id, reused.Key ?? reused.Id, Created: false);

        var linked = blockedKey is null ? [] : new List<string> { blockedKey };
        var content = request.Content with
        {
            Dependants = [.. request.Content.Dependants ?? [], .. linked],
            AppliesTo = [.. request.Content.AppliesTo ?? [], .. linked],
        };
        var jobId = _mutations.CreateJob(new CreateTaskRequest
        {
            Title = request.Title,
            WatchPath = request.WatchPath,
            Kind = TaskKinds.Decision,
            Decision = content,
            PromptMarkdown = request.PromptMarkdown,
            TargetState = TaskStates.Preparation,
            CreationSource = request.CreationSource,
            CreatedBy = request.CreatedBy,
        });
        if (string.IsNullOrWhiteSpace(jobId)) return null;
        var created = _scanner.FindJob(jobId, request.WatchPath);
        var key = created?.Key ?? jobId;

        if (blocked is not null)
        {
            var current = _scanner.FindJob(blocked.Id, blocked.WatchPath) ?? blocked;
            var refs = current.References ?? new TaskReferences();
            if (!refs.DependsOn.Any(edge => string.Equals(edge.Key, key, StringComparison.OrdinalIgnoreCase)))
                _mutations.SetTaskReferences(current.Id, refs with
                {
                    DependsOn = [.. refs.DependsOn, new TaskDependencyReference(key)],
                }, current.WatchPath);
        }
        _logger.LogInformation("decision-card-requested key={Key} blocked={Blocked} options={Options}",
            key, blockedKey ?? "", content.Options.Count);
        return new(jobId, key, Created: true);
    }

    /// <summary>A pending decision the blocked card already waits on for the same question.</summary>
    private TaskInfo? PendingDecisionFor(TaskInfo blocked, string question)
    {
        var current = _scanner.FindJob(blocked.Id, blocked.WatchPath) ?? blocked;
        foreach (var edge in current.References?.DependsOn ?? [])
        {
            var target = _scanner.FindJob(edge.Key, current.WatchPath);
            if (target is not null && TaskKinds.IsDecision(target.Kind)
                && DecisionStatuses.IsOpen(target.Decision?.Status)
                && string.Equals(target.Decision?.Question?.Trim(), question?.Trim(), StringComparison.OrdinalIgnoreCase))
                return target;
        }
        return null;
    }
}
