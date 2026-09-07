using Contract = AgentStudio.TaskServer.Contracts;

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
    private readonly object _gate = new();
    private readonly List<PendingRemoteChatWork> _work = [];
    private readonly Dictionary<string, CachedChatExecutionContext> _contexts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<RemoteChatWorkBroker> _logger;
    private readonly QuotaService? _quota;
    private readonly CliQuotaCapsService? _quotaCaps;
    private readonly CliQuotaFallbackService? _quotaFallback;
    private readonly CliQuotaWaitPolicyService? _quotaWaitPolicy;
    private readonly OrchestratorLog? _orchestratorLog;
    private readonly V1ReviewExecutorRegistry? _capabilityRegistry;

    public RemoteChatWorkBroker(
        ILogger<RemoteChatWorkBroker> logger,
        QuotaService? quota = null,
        CliQuotaCapsService? quotaCaps = null,
        CliQuotaFallbackService? quotaFallback = null,
        CliQuotaWaitPolicyService? quotaWaitPolicy = null,
        OrchestratorLog? orchestratorLog = null,
        V1ReviewExecutorRegistry? capabilityRegistry = null)
    {
        _logger = logger;
        _quota = quota;
        _quotaCaps = quotaCaps;
        _quotaFallback = quotaFallback;
        _quotaWaitPolicy = quotaWaitPolicy;
        _orchestratorLog = orchestratorLog;
        _capabilityRegistry = capabilityRegistry;
    }

    public async Task<RemoteChatWorkResult> EnqueueTurnAsync(
        RemoteChatWorkRoute route,
        string prompt,
        string model,
        string? thinkingLevel,
        CancellationToken ct)
    {
        var pending = PendingRemoteChatWork.Create(
            RemoteChatWorkKinds.Turn,
            route,
            prompt,
            CliTypes.Codex,
            model,
            thinkingLevel);
        lock (_gate)
        {
            _work.Add(pending);
        }
        _logger.LogInformation(
            "remote-chat-work-queued workId={WorkId} project={Project} runner={Runner} kind={Kind}",
            pending.Id, route.ProjectName, route.RunnerId, pending.Kind);
        try
        {
            return await pending.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
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
                RemoteChatWorkKinds.Inspect,
                route,
                prompt: null,
                cliType: null,
                model: null,
                thinkingLevel: null));
        }
    }

    public ChatExecutionContext? GetContext(RemoteChatWorkRoute route)
    {
        lock (_gate)
        {
            if (!_contexts.TryGetValue(route.ProjectName, out var cached))
                return null;
            return cached.Route == route ? cached.Context : null;
        }
    }

    public RemoteChatWorkClaimResponse TryClaim(RemoteChatWorkClaimRequest request)
    {
        PendingRemoteChatWork? claimed = null;
        var deferredDecisions = new List<(RemoteChatWorkRoute Route, QuotaAdmissionPlan Admission)>();
        var capabilityBlocks = new List<(PendingRemoteChatWork Work, string Reason)>();
        lock (_gate)
        {
            RequeueExpiredClaimsLocked();
            var candidates = _work
                .Where(candidate => candidate.State == PendingRemoteChatWorkState.Pending)
                .Where(candidate => RunnerMatches(candidate.Route.RunnerId, request.RunnerId, request.RunnerName))
                .OrderBy(candidate => candidate.Kind == RemoteChatWorkKinds.Turn ? 0 : 1)
                .ThenBy(candidate => candidate.CreatedAt)
                .ToArray();
            foreach (var item in candidates)
            {
                var admission = item.Kind == RemoteChatWorkKinds.Turn
                    ? PlanAdmission(
                        item.RequestedCliType ?? CliTypes.Codex,
                        item.RequestedModel ?? string.Empty,
                        item.RequestedThinkingLevel)
                    : null;
                if (admission is { ShouldLaunch: false })
                {
                    var fingerprint = AdmissionFingerprint(admission);
                    if (!string.Equals(item.LastDeferredAdmission, fingerprint, StringComparison.Ordinal))
                    {
                        item.LastDeferredAdmission = fingerprint;
                        deferredDecisions.Add((item.Route, admission));
                    }
                    continue;
                }

                var effectiveCli = admission?.CliType ?? item.RequestedCliType;
                var effectiveModel = admission?.Model ?? item.RequestedModel;
                var effectiveThinking = admission?.ThinkingLevel ?? item.RequestedThinkingLevel;
                if (item.Kind == RemoteChatWorkKinds.Turn
                    && _capabilityRegistry is not null)
                {
                    var cliType = CliTypes.Normalize(effectiveCli);
                    var required = new[]
                    {
                        Contract.ReviewCapabilities.CodingExecutor,
                        Contract.CapabilityProtocol.CliExecution(cliType),
                        Contract.CapabilityProtocol.ProviderAuthentication(cliType),
                    };
                    var capabilityAdmission = _capabilityRegistry.EvaluateCodingAdmission(
                        request.RunnerId.Trim(),
                        request.CapabilityInstanceId,
                        required);
                    if (!capabilityAdmission.Eligible)
                    {
                        if (!string.Equals(
                                item.LastCapabilityBlock,
                                capabilityAdmission.Message,
                                StringComparison.Ordinal))
                        {
                            item.LastCapabilityBlock = capabilityAdmission.Message;
                            capabilityBlocks.Add((item, capabilityAdmission.Message ?? "Capability admission failed."));
                        }
                        continue;
                    }
                }

                item.LastDeferredAdmission = null;
                item.LastCapabilityBlock = null;
                item.CliType = effectiveCli;
                item.Model = effectiveModel;
                item.ThinkingLevel = effectiveThinking;
                item.QuotaAdmission = admission;
                item.State = PendingRemoteChatWorkState.Claimed;
                item.ClaimedBy = request.RunnerId;
                item.ClaimToken = Guid.NewGuid().ToString("N");
                item.ClaimExpiresAt = DateTime.UtcNow + ClaimTtl;
                claimed = item;
                break;
            }
        }

        foreach (var (route, admission) in deferredDecisions)
            RecordAdmission(route, admission);
        foreach (var (work, reason) in capabilityBlocks)
        {
            _logger.LogWarning(
                "remote-chat-work-claim-skipped-capability workId={WorkId} project={Project} runner={Runner} reason={Reason}",
                work.Id,
                work.Route.ProjectName,
                request.RunnerId,
                reason);
        }
        if (claimed is null)
            return new RemoteChatWorkClaimResponse(RemoteChatWorkClaimStatuses.Empty);

        if (claimed.QuotaAdmission is not null)
        {
            _quotaFallback!.RecordAdmission(
                claimed.RequestedCliType ?? CliTypes.Codex,
                claimed.RequestedModel,
                claimed.RequestedThinkingLevel,
                claimed.QuotaAdmission,
                DateTime.UtcNow);
            RecordAdmission(claimed.Route, claimed.QuotaAdmission);
        }
        return new RemoteChatWorkClaimResponse(
            RemoteChatWorkClaimStatuses.Claimed,
            new RemoteChatWorkItem(
                claimed.Id,
                claimed.ClaimToken!,
                claimed.Kind,
                claimed.Route.ProjectId,
                claimed.Route.ProjectName,
                claimed.Route.RepositoryUrl,
                claimed.Route.DefaultBranch,
                claimed.Prompt,
                claimed.Model,
                claimed.ThinkingLevel,
                claimed.CreatedAt,
                claimed.ClaimExpiresAt!.Value,
                claimed.CliType));
    }

    public bool Renew(RemoteChatWorkRenewRequest request)
    {
        lock (_gate)
        {
            var item = FindClaimLocked(request.WorkId, request.ClaimToken, request.RunnerId);
            if (item == null) return false;
            item.ClaimExpiresAt = DateTime.UtcNow + ClaimTtl;
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
            item.QuotaAdmission);
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

    private QuotaAdmissionPlan? PlanAdmission(
        string cliType,
        string model,
        string? thinkingLevel)
    {
        if (_quota is null || _quotaCaps is null || _quotaFallback is null) return null;
        return QuotaAdmissionPlanner.Plan(
            cliType,
            model,
            thinkingLevel,
            _quotaFallback,
            _quotaCaps,
            cli => string.IsNullOrWhiteSpace(cli) ? null : _quota.GetCachedFor(cli),
            DateTime.UtcNow,
            occupiedSlots: 0,
            _quotaWaitPolicy?.Resolve(project: null),
            QuotaAdmissionContext.ForTask(
                QuotaExecutionPath.OrchestratorChat,
                taskType: null,
                thinkingLevel));
    }

    private static string AdmissionFingerprint(QuotaAdmissionPlan admission) =>
        $"{admission.Outcome}|{admission.CliType}|{admission.Model}|{admission.ThinkingLevel}|" +
        $"{admission.NextResetAt:O}|{admission.Reason}";

    private void RecordAdmission(RemoteChatWorkRoute route, QuotaAdmissionPlan admission)
    {
        _logger.Log(
            admission.IsFallback || admission.IsDeferred ? LogLevel.Warning : LogLevel.Information,
            "cli_quota_admission_decision path=remote-project-chat project={Project} outcome={Outcome} requestedCli=codex effectiveCli={EffectiveCli} model={Model} isFallback={IsFallback} reason={Reason}",
            route.ProjectName,
            admission.Outcome,
            admission.CliType,
            admission.Model,
            admission.IsFallback,
            admission.Reason);
        if (admission.Outcome == QuotaAdmissionOutcome.LaunchPrimary
            || string.IsNullOrWhiteSpace(route.WatchPath)) return;
        _orchestratorLog?.Append(route.WatchPath!, new OrchestratorLogEntry
        {
            Kind = OrchestratorLogKinds.Decision,
            Topic = OrchestratorLogTopics.LoadDistribution,
            Summary = admission.Reason,
            Reasoning = QuotaAdmissionPlanner.DescribeLoadNumbers(admission),
        });
    }

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
        public string? RequestedCliType { get; init; }
        public string? RequestedModel { get; init; }
        public string? RequestedThinkingLevel { get; init; }
        public string? CliType { get; set; }
        public string? Model { get; set; }
        public string? ThinkingLevel { get; set; }
        public QuotaAdmissionPlan? QuotaAdmission { get; set; }
        public required DateTime CreatedAt { get; init; }
        public required TaskCompletionSource<RemoteChatWorkResult> Completion { get; init; }
        public PendingRemoteChatWorkState State { get; set; }
        public string? ClaimedBy { get; set; }
        public string? ClaimToken { get; set; }
        public DateTime? ClaimExpiresAt { get; set; }
        public string? LastDeferredAdmission { get; set; }
        public string? LastCapabilityBlock { get; set; }

        public static PendingRemoteChatWork Create(
            string kind,
            RemoteChatWorkRoute route,
            string? prompt,
            string? cliType,
            string? model,
            string? thinkingLevel) =>
            new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = kind,
                Route = route,
                Prompt = prompt,
                RequestedCliType = cliType,
                RequestedModel = model,
                RequestedThinkingLevel = thinkingLevel,
                CreatedAt = DateTime.UtcNow,
                Completion = new TaskCompletionSource<RemoteChatWorkResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously),
                State = PendingRemoteChatWorkState.Pending,
            };
    }

    private sealed record CachedChatExecutionContext(
        RemoteChatWorkRoute Route,
        ChatExecutionContext Context);
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
    string? WatchPath = null);

public sealed record RemoteChatWorkClaimRequest(
    string RunnerId,
    string RunnerName,
    string Hostname,
    string? CapabilityInstanceId = null);

public sealed record RemoteChatWorkClaimResponse(
    string Status,
    RemoteChatWorkItem? Work = null);

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
    string? CliType = null);

public sealed record RemoteChatWorkRenewRequest(
    string WorkId,
    string ClaimToken,
    string RunnerId);

public sealed record RemoteChatWorkCompletionRequest(
    string WorkId,
    string ClaimToken,
    string RunnerId,
    bool Success,
    string? ReplyText,
    string? Model,
    OrchestratorTokenUsage? TokenUsage,
    string? ErrorMessage,
    ChatExecutionContext? ExecutionContext);

public sealed record RemoteChatWorkResult(
    bool Success,
    string ReplyText,
    string Model,
    OrchestratorTokenUsage? TokenUsage,
    string? ErrorMessage,
    ChatExecutionContext? ExecutionContext,
    string? CliType = null,
    QuotaAdmissionPlan? QuotaAdmission = null);

public sealed record ChatExecutionContext(
    string ExecutionKind,
    string HostName,
    string? RepoPath,
    string? Branch,
    string? HeadSha,
    string State,
    DateTime CapturedAt);
