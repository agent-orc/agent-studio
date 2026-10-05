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
/// A request succeeds only when every link is written. A failed link write
/// returns null; the decision card already names the blocked card, so the
/// next request for the same question finds it, reuses it, and retries the
/// missing writes instead of raising a second card.
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
        {
            var reusedKey = reused.Key ?? reused.Id;
            if (!Attach(reused, blocked))
            {
                _logger.LogWarning("decision-card-request-link-failed key={Key} blocked={Blocked}",
                    reusedKey, blockedKey ?? "");
                return null;
            }
            return new(reused.Id, reusedKey, Created: false);
        }

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

        if (blocked is not null && !EnsureDependsOn(blocked, key))
        {
            _logger.LogWarning("decision-card-request-link-failed key={Key} blocked={Blocked}", key, blockedKey ?? "");
            return null;
        }
        _logger.LogInformation("decision-card-requested key={Key} blocked={Blocked} options={Options}",
            key, blockedKey ?? "", content.Options.Count);
        return new(jobId, key, Created: true);
    }

    /// <summary>
    /// Makes a further blocked card wait on an open decision: it becomes a
    /// dependant and apply target, and gets a <c>dependsOn</c> edge. The
    /// decision is read again under the decision write gate, so a choice taken
    /// meanwhile is never overwritten. False when the decision is no longer
    /// open or a write failed; every write is idempotent, so a retry is safe.
    /// </summary>
    public bool Attach(TaskInfo decisionCard, TaskInfo blocked)
    {
        var blockedKey = blocked.Key ?? blocked.Id;
        DecisionCardService.WriteGate.Wait();
        try
        {
            var current = _scanner.FindJob(decisionCard.Id, decisionCard.WatchPath);
            if (current is null || !TaskKinds.IsDecision(current.Kind) || current.Decision is not { } decision
                || !DecisionStatuses.IsOpen(decision.Status)) return false;
            if (!Names(decision.Dependants, blockedKey) || !Names(decision.AppliesTo, blockedKey))
            {
                var linked = decision with
                {
                    Dependants = [.. decision.Dependants.Append(blockedKey).Distinct(StringComparer.OrdinalIgnoreCase)],
                    AppliesTo = [.. decision.AppliesTo.Append(blockedKey).Distinct(StringComparer.OrdinalIgnoreCase)],
                };
                if (!_mutations.SetDecisionContent(current.Id, linked, current.WatchPath)) return false;
            }
            return EnsureDependsOn(blocked, current.Key ?? current.Id);
        }
        finally
        {
            DecisionCardService.WriteGate.Release();
        }
    }

    /// <summary>Adds the blocked card's <c>dependsOn</c> edge to the decision unless it exists; false when the write failed.</summary>
    private bool EnsureDependsOn(TaskInfo blocked, string decisionKey)
    {
        var current = _scanner.FindJob(blocked.Id, blocked.WatchPath) ?? blocked;
        var refs = current.References ?? new TaskReferences();
        if (refs.DependsOn.Any(edge => string.Equals(edge.Key, decisionKey, StringComparison.OrdinalIgnoreCase)))
            return true;
        return _mutations.SetTaskReferences(current.Id, refs with
        {
            DependsOn = [.. refs.DependsOn, new TaskDependencyReference(decisionKey)],
        }, current.WatchPath);
    }

    /// <summary>
    /// An open decision for the same question the blocked card already waits
    /// on, or one that names it as apply target but lost its <c>dependsOn</c>
    /// edge to a failed write.
    /// </summary>
    private TaskInfo? PendingDecisionFor(TaskInfo blocked, string question)
    {
        var current = _scanner.FindJob(blocked.Id, blocked.WatchPath) ?? blocked;
        var blockedKey = current.Key ?? current.Id;
        var edges = (current.References?.DependsOn ?? [])
            .Select(edge => _scanner.FindJob(edge.Key, current.WatchPath));
        var named = _scanner.ScanAllAutomationJobs()
            .Where(card => string.Equals(card.WatchPath, current.WatchPath, StringComparison.OrdinalIgnoreCase)
                && TaskKinds.IsDecision(card.Kind)
                && Names(card.Decision?.AppliesTo, blockedKey));
        return edges.Concat(named).FirstOrDefault(target => target is not null
            && TaskKinds.IsDecision(target.Kind)
            && DecisionStatuses.IsOpen(target.Decision?.Status)
            && string.Equals(target.Decision?.Question?.Trim(), question?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool Names(IEnumerable<string>? keys, string key) =>
        keys?.Any(value => string.Equals(value?.Trim(), key, StringComparison.OrdinalIgnoreCase)) == true;
}
