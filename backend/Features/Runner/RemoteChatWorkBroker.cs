namespace AgentStudio.Runner;

/// <summary>
/// In-process compatibility broker for project-chat work claimed by an assigned
/// remote runner. The wire shape deliberately mirrors a fenced host work
/// permit: one runner claims an opaque work id, renews its claim while Codex is
/// active, and completes only with the matching claim token.
///
/// This is the migration seam to the durable host-orchestrator work-permit
/// contract. Studio never opens SSH or tries to address a runner filesystem.
/// </summary>
public sealed class RemoteChatWorkBroker
{
    private static readonly TimeSpan ClaimTtl = TimeSpan.FromMinutes(2);
    private readonly TimeSpan _unreachableBudget;
    private readonly object _gate = new();
    private readonly List<PendingRemoteChatWork> _work = [];
    private readonly Dictionary<string, DateTime> _lastPollByRunner = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Host, string Project), CompletedChatUsage> _completedUsage = [];
    private readonly Dictionary<string, CachedChatExecutionContext> _contexts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<RemoteChatWorkBroker> _logger;

    public RemoteChatWorkBroker(
        ILogger<RemoteChatWorkBroker> logger,
        TimeSpan? unreachableBudget = null)
    {
        _logger = logger;
        _unreachableBudget = unreachableBudget ?? TimeSpan.FromSeconds(10);
    }

    public async Task<RemoteChatWorkResult> EnqueueTurnAsync(
        RemoteChatWorkRoute route,
        string prompt,
        string model,
        string? thinkingLevel,
        CancellationToken ct,
        string cliType = CliTypes.Codex,
        string? configuredCliType = null,
        string? configuredModel = null,
        string? quotaFallbackReason = null)
    {
        var pending = PendingRemoteChatWork.Create(
            RemoteChatWorkKinds.Turn,
            route,
            prompt,
            model,
            thinkingLevel,
            cliType,
            configuredCliType,
            configuredModel,
            quotaFallbackReason);
        lock (_gate)
        {
            _work.Add(pending);
        }
        _logger.LogInformation(
            "remote-chat-work-queued workId={WorkId} project={Project} runner={Runner} kind={Kind}",
            pending.Id, route.ProjectName, route.RunnerId, pending.Kind);
        try
        {
            while (true)
            {
                var completed = await Task.WhenAny(
                    pending.Completion.Task,
                    Task.Delay(_unreachableBudget, ct)).ConfigureAwait(false);
                if (completed == pending.Completion.Task)
                    return await pending.Completion.Task.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (pending.State == PendingRemoteChatWorkState.Pending
                        && (!_lastPollByRunner.TryGetValue(route.RunnerId, out var lastPoll)
                            || DateTime.UtcNow - lastPoll >= _unreachableBudget))
                    {
                        _work.Remove(pending);
                        throw new RemoteChatHostUnreachableException(route.RunnerId, pending.CreatedAt);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_gate)
            {
                // A request cancelled before pickup must not become surprise
                // work on the host later. Once claimed, completion fencing owns
                // cleanup and the host is allowed to finish the already-started
                // CLI process.
                if (pending.State == PendingRemoteChatWorkState.Pending)
                    _work.Remove(pending);
            }
            throw;
        }
    }

    public void RequestInspection(RemoteChatWorkRoute route)
    {
        lock (_gate)
        {
            var alreadyPending = _work.Any(item =>
                item.Kind == RemoteChatWorkKinds.Inspect
                && string.Equals(item.Route.ProjectId, route.ProjectId, StringComparison.OrdinalIgnoreCase)
                && item.State is PendingRemoteChatWorkState.Pending or PendingRemoteChatWorkState.Claimed);
            if (alreadyPending) return;
            _work.Add(PendingRemoteChatWork.Create(
                RemoteChatWorkKinds.Inspect, route, prompt: null, model: null, thinkingLevel: null,
                cliType: CliTypes.Codex, configuredCliType: null, configuredModel: null,
                quotaFallbackReason: null));
        }
    }

    public ChatExecutionContext? GetContext(RemoteChatWorkRoute route)
    {
        lock (_gate)
        {
            if (!_contexts.TryGetValue(route.ProjectName, out var cached))
                return null;
            return (cached.Route with { ContextKey = null }) == (route with { ContextKey = null })
                ? cached.Context : null;
        }
    }

    public void RecordLocalFallback(RemoteChatWorkRoute route, ChatExecutionContext context)
    {
        lock (_gate)
            _contexts[route.ProjectName] = new CachedChatExecutionContext(route, context);
    }

    public RemoteChatWorkStatus? GetStatus(string projectName, string? contextKey)
    {
        lock (_gate)
        {
            var item = _work
                .Where(work => work.Kind == RemoteChatWorkKinds.Turn
                    && string.Equals(work.Route.ProjectName, projectName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(work.Route.ContextKey, contextKey, StringComparison.Ordinal))
                .OrderByDescending(work => work.CreatedAt)
                .FirstOrDefault();
            if (item is null) return null;
            return new RemoteChatWorkStatus(
                item.State == PendingRemoteChatWorkState.Claimed ? "running" : "queued",
                item.Route.RunnerId,
                item.ClaimedHost,
                item.CreatedAt,
                item.ClaimedAt,
                item.DeferredReason);
        }
    }

    public IReadOnlyList<RemoteChatUsage> GetUsage()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var active = _work
                .Where(work => work.Kind == RemoteChatWorkKinds.Turn
                    && work.State == PendingRemoteChatWorkState.Claimed)
                .GroupBy(work => (Host: work.ClaimedHost ?? work.Route.RunnerId,
                    Project: work.Route.ProjectName))
                .ToDictionary(group => group.Key, group => group.ToArray());
            return active.Keys.Union(_completedUsage.Keys)
                .Select(key =>
                {
                    var turns = active.GetValueOrDefault(key) ?? [];
                    var completed = _completedUsage.GetValueOrDefault(key);
                    var cpu = turns.Where(turn => turn.CpuPercent.HasValue)
                        .Select(turn => turn.CpuPercent!.Value).ToArray();
                    return new RemoteChatUsage(
                        key.Host, key.Project,
                        turns.Length,
                        turns.Count(turn => turn.Heavy
                            || turn.ClaimedAt is { } started
                            && now - started >= TimeSpan.FromSeconds(30)),
                        cpu.Length == 0 ? null : cpu.Sum(),
                        completed?.Tokens ?? 0,
                        completed?.CostUsd);
                })
                .OrderBy(row => row.HostName)
                .ThenBy(row => row.ProjectName)
                .ToArray();
        }
    }

    public RemoteChatWorkClaimResponse TryClaim(
        RemoteChatWorkClaimRequest request,
        Func<RemoteChatWorkCandidate, RemoteChatWorkClaimPreparation>? prepare = null)
    {
        lock (_gate)
        {
            _lastPollByRunner[request.RunnerId] = DateTime.UtcNow;
            _lastPollByRunner[request.RunnerName] = DateTime.UtcNow;
            RequeueExpiredClaimsLocked();
            var candidates = _work
                .Where(candidate => candidate.State == PendingRemoteChatWorkState.Pending)
                .Where(candidate => RunnerMatches(candidate.Route.RunnerId, request.RunnerId, request.RunnerName))
                .OrderBy(candidate => candidate.Kind == RemoteChatWorkKinds.Turn ? 0 : 1)
                .ThenBy(candidate => candidate.CreatedAt)
                .ToArray();
            PendingRemoteChatWork? item = null;
            string? deferredMessage = null;
            foreach (var candidate in candidates)
            {
                var preparation = prepare?.Invoke(new RemoteChatWorkCandidate(
                    candidate.Id,
                    candidate.Kind,
                    candidate.Route.ProjectName,
                    candidate.CliType));
                if (preparation is { CanClaim: false })
                {
                    deferredMessage ??= preparation.Message;
                    candidate.DeferredReason = preparation.Message;
                    continue;
                }

                item = candidate;
                break;
            }
            if (item == null)
            {
                return new RemoteChatWorkClaimResponse(
                    RemoteChatWorkClaimStatuses.Empty,
                    Message: deferredMessage);
            }

            item.State = PendingRemoteChatWorkState.Claimed;
            item.ClaimedBy = request.RunnerId;
            item.ClaimedHost = request.Hostname;
            item.DeferredReason = null;
            item.ClaimToken = Guid.NewGuid().ToString("N");
            item.ClaimedAt = DateTime.UtcNow;
            item.ClaimExpiresAt = DateTime.UtcNow + ClaimTtl;
            return new RemoteChatWorkClaimResponse(
                RemoteChatWorkClaimStatuses.Claimed,
                new RemoteChatWorkItem(
                    item.Id,
                    item.ClaimToken,
                    item.Kind,
                    item.Route.ProjectId,
                    item.Route.ProjectName,
                    item.Route.RepositoryUrl,
                    item.Route.DefaultBranch,
                    item.Prompt,
                    item.Model,
                    item.ThinkingLevel,
                    item.CreatedAt,
                    item.ClaimExpiresAt.Value,
                    item.CliType,
                    item.ConfiguredCliType,
                    item.ConfiguredModel,
                    item.QuotaFallbackReason));
        }
    }

    public bool Renew(RemoteChatWorkRenewRequest request)
    {
        lock (_gate)
        {
            var item = FindClaimLocked(request.WorkId, request.ClaimToken, request.RunnerId);
            if (item == null) return false;
            item.ClaimExpiresAt = DateTime.UtcNow + ClaimTtl;
            item.Heavy = request.Heavy;
            item.CpuPercent = request.CpuPercent;
            return true;
        }
    }

    public bool Complete(RemoteChatWorkCompletionRequest request)
    {
        PendingRemoteChatWork? item;
        lock (_gate)
        {
            item = FindClaimLocked(request.WorkId, request.ClaimToken, request.RunnerId);
            if (item == null) return false;
            item.State = PendingRemoteChatWorkState.Completed;
            if (item.Kind == RemoteChatWorkKinds.Turn && request.TokenUsage is { } usage)
            {
                var key = (item.ClaimedHost ?? item.Route.RunnerId, item.Route.ProjectName);
                var previous = _completedUsage.GetValueOrDefault(key);
                var estimate = TokenPricing.Estimate(
                    request.Model ?? item.Model,
                    usage.InputTokens, usage.OutputTokens,
                    usage.CacheReadTokens, usage.CacheCreationTokens,
                    DateTime.UtcNow);
                var tokens = (long)usage.InputTokens + usage.OutputTokens
                    + usage.CacheReadTokens + usage.CacheCreationTokens;
                _completedUsage[key] = new CompletedChatUsage(
                    (previous?.Tokens ?? 0) + tokens,
                    estimate.ModelKnown && (previous is null || previous.CostUsd.HasValue)
                        ? (previous?.CostUsd ?? 0) + estimate.Total
                        : null);
            }
            if (request.ExecutionContext != null)
                _contexts[item.Route.ProjectName] =
                    new CachedChatExecutionContext(item.Route, request.ExecutionContext);
            _work.Remove(item);
        }

        var result = new RemoteChatWorkResult(
            request.Success,
            request.ReplyText ?? "",
            request.Model ?? item.Model ?? "",
            request.TokenUsage,
            request.ErrorMessage,
            request.ExecutionContext,
            item.CliType,
            item.ConfiguredCliType,
            item.ConfiguredModel,
            item.QuotaFallbackReason)
        {
            QueuedAt = item.CreatedAt,
            StartedAt = item.ClaimedAt,
            FinishedAt = DateTime.UtcNow,
        };
        item.Completion.TrySetResult(result);
        _logger.LogInformation(
            "remote-chat-work-completed workId={WorkId} project={Project} runner={Runner} kind={Kind} success={Success} path={Path}",
            item.Id, item.Route.ProjectName, request.RunnerId, item.Kind, request.Success,
            request.ExecutionContext?.RepoPath ?? "(unknown)");
        return true;
    }

    private PendingRemoteChatWork? FindClaimLocked(string workId, string claimToken, string runnerId)
        => _work.FirstOrDefault(item =>
            item.State == PendingRemoteChatWorkState.Claimed
            && string.Equals(item.Id, workId, StringComparison.Ordinal)
            && string.Equals(item.ClaimToken, claimToken, StringComparison.Ordinal)
            && string.Equals(item.ClaimedBy, runnerId, StringComparison.OrdinalIgnoreCase));

    private void RequeueExpiredClaimsLocked()
    {
        var now = DateTime.UtcNow;
        foreach (var item in _work.Where(item =>
                     item.State == PendingRemoteChatWorkState.Claimed
                     && item.ClaimExpiresAt <= now))
        {
            _logger.LogWarning(
                "remote-chat-work-claim-expired workId={WorkId} project={Project} runner={Runner}",
                item.Id, item.Route.ProjectName, item.ClaimedBy);
            item.State = PendingRemoteChatWorkState.Pending;
            item.ClaimedBy = null;
            item.ClaimToken = null;
            item.ClaimExpiresAt = null;
        }
    }

    private static bool RunnerMatches(string assigned, string runnerId, string runnerName)
        => string.Equals(assigned, runnerId, StringComparison.OrdinalIgnoreCase)
           || string.Equals(assigned, runnerName, StringComparison.OrdinalIgnoreCase);

    private enum PendingRemoteChatWorkState
    {
        Pending,
        Claimed,
        Completed,
    }

    private sealed class PendingRemoteChatWork
    {
        public required string Id { get; init; }
        public required string Kind { get; init; }
        public required RemoteChatWorkRoute Route { get; init; }
        public string? Prompt { get; init; }
        public string? Model { get; init; }
        public string? ThinkingLevel { get; init; }
        public string CliType { get; init; } = CliTypes.Codex;
        public string? ConfiguredCliType { get; init; }
        public string? ConfiguredModel { get; init; }
        public string? QuotaFallbackReason { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required TaskCompletionSource<RemoteChatWorkResult> Completion { get; init; }
        public PendingRemoteChatWorkState State { get; set; }
        public string? ClaimedBy { get; set; }
        public string? ClaimedHost { get; set; }
        public string? DeferredReason { get; set; }
        public string? ClaimToken { get; set; }
        public DateTime? ClaimExpiresAt { get; set; }
        public DateTime? ClaimedAt { get; set; }
        public bool Heavy { get; set; }
        public double? CpuPercent { get; set; }

        public static PendingRemoteChatWork Create(
            string kind,
            RemoteChatWorkRoute route,
            string? prompt,
            string? model,
            string? thinkingLevel,
            string cliType,
            string? configuredCliType,
            string? configuredModel,
            string? quotaFallbackReason) =>
            new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = kind,
                Route = route,
                Prompt = prompt,
                Model = model,
                ThinkingLevel = thinkingLevel,
                CliType = cliType,
                ConfiguredCliType = configuredCliType,
                ConfiguredModel = configuredModel,
                QuotaFallbackReason = quotaFallbackReason,
                CreatedAt = DateTime.UtcNow,
                Completion = new TaskCompletionSource<RemoteChatWorkResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously),
                State = PendingRemoteChatWorkState.Pending,
            };
    }

    private sealed record CachedChatExecutionContext(
        RemoteChatWorkRoute Route,
        ChatExecutionContext Context);

    private sealed record CompletedChatUsage(long Tokens, decimal? CostUsd);
}

public static class RemoteChatWorkKinds
{
    public const string Inspect = "project-chat-inspect";
    public const string Turn = "project-chat-turn";
}

public static class RemoteChatWorkClaimStatuses
{
    public const string Claimed = "claimed";
    public const string Empty = "empty";
}

public sealed record RemoteChatWorkRoute(
    string RunnerId,
    string ProjectId,
    string ProjectName,
    string RepositoryUrl,
    string DefaultBranch,
    string? ContextKey = null);

public sealed record RemoteChatWorkStatus(
    string State,
    string RunnerId,
    string? HostName,
    DateTime QueuedAt,
    DateTime? StartedAt,
    string? Reason);

public sealed record RemoteChatUsage(
    string HostName,
    string ProjectName,
    int ActiveTurns,
    int HeavyTurns,
    double? CpuPercent,
    long Tokens,
    decimal? CostUsd);

public sealed class RemoteChatHostUnreachableException(string runnerId, DateTime queuedAt)
    : Exception($"Assigned runner '{runnerId}' did not poll for interactive chat work.")
{
    public DateTime QueuedAt { get; } = queuedAt;
}

public sealed record RemoteChatWorkClaimRequest(
    string RunnerId,
    string RunnerName,
    string Hostname,
    string? CapabilityInstanceId = null);

public sealed record RemoteChatWorkClaimResponse(
    string Status,
    RemoteChatWorkItem? Work = null,
    string? Message = null);

public sealed record RemoteChatWorkCandidate(
    string WorkId,
    string Kind,
    string ProjectName,
    string CliType);

public sealed record RemoteChatWorkClaimPreparation(
    bool CanClaim,
    string? Message = null);

public sealed record RemoteChatWorkItem(
    string WorkId,
    string ClaimToken,
    string Kind,
    string ProjectId,
    string ProjectName,
    string RepositoryUrl,
    string DefaultBranch,
    string? Prompt,
    string? Model,
    string? ThinkingLevel,
    DateTime CreatedAt,
    DateTime ClaimExpiresAt,
    string? CliType = null,
    string? ConfiguredCliType = null,
    string? ConfiguredModel = null,
    string? QuotaFallbackReason = null);

public sealed record RemoteChatWorkRenewRequest(
    string WorkId,
    string ClaimToken,
    string RunnerId,
    bool Heavy = false,
    double? CpuPercent = null);

public sealed record RemoteChatWorkCompletionRequest(
    string WorkId,
    string ClaimToken,
    string RunnerId,
    bool Success,
    string? ReplyText,
    string? Model,
    OrchestratorTokenUsage? TokenUsage,
    string? ErrorMessage,
    ChatExecutionContext? ExecutionContext,
    string? CliType = null,
    string? ConfiguredCliType = null,
    string? ConfiguredModel = null,
    string? QuotaFallbackReason = null);

public sealed record RemoteChatWorkResult(
    bool Success,
    string ReplyText,
    string Model,
    OrchestratorTokenUsage? TokenUsage,
    string? ErrorMessage,
    ChatExecutionContext? ExecutionContext,
    string? CliType = null,
    string? ConfiguredCliType = null,
    string? ConfiguredModel = null,
    string? QuotaFallbackReason = null)
{
    public DateTime? QueuedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
}

public sealed record ChatExecutionContext(
    string ExecutionKind,
    string HostName,
    string? RepoPath,
    string? Branch,
    string? HeadSha,
    string State,
    DateTime CapturedAt);
