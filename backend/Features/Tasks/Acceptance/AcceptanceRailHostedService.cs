using System.Collections.Concurrent;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tasks;

public sealed record AcceptanceRailSnapshot
{
    public bool Enabled { get; init; }
    public DateTime? LastRunAtUtc { get; init; }
    public int HumanReviewDepth { get; init; }
    public int EscalatedDepth { get; init; }
    public int Held { get; init; }
    public int Accepted { get; init; }
    public int Requeued { get; init; }
    public int Escalated { get; init; }
    public int Failed { get; init; }

    /// <summary>
    /// Cards the rail deliberately did not act on this pass because their facts
    /// are unchanged since the attempt that was refused (AGT-2856).
    /// </summary>
    public int Suppressed { get; init; }
    public int BounceEligible { get; init; }
    public int BounceDeferred { get; init; }
    public int BounceShadowed { get; init; }
    public int BounceFalseEligibility { get; init; }
    public string? PlatformProblem { get; init; }
}

/// <summary>
/// Durable deterministic owner for routine Human Review acceptance and
/// integration-conflict requeue. It runs independently of every orchestrator
/// session and makes no model calls. AGT-3060: the rule is the AI Operator
/// decider <c>acceptance-rail</c> (<see cref="AcceptanceRailDecisionPolicy"/>);
/// the <c>OperatorDecision</c> switch selects whether the monolith decides it in
/// process or the engine does. This service collects the facts and applies the
/// decision or the engine receipt.
/// </summary>
public sealed class AcceptanceRailHostedService : BackgroundService, IAiOperatorEffect
{
    private static readonly object BounceClaimGate = new();
    private readonly TaskScannerService _scanner;
    private readonly IntegrationGenerationReconcileSweep? _generationReconcile;
    private readonly AttemptAuthorityService? _attemptAuthority;
    private readonly TaskIntegrationStatusService _integrationStatus;
    private readonly TaskTransitionService _transitions;
    private readonly TaskIntegrationRecoveryService _recovery;
    private readonly HumanReviewEscalation _humanReviewEscalation;
    private readonly TimelineLog _timeline;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AcceptanceRailHostedService> _logger;
    private readonly AiOperatorDecisionGateway _decisions;
    private readonly object _snapshotGate = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;

    /// <summary>
    /// Per card, the fingerprint of the last attempt that was refused. The rail
    /// re-attempts only when a new fact changes the fingerprint (AGT-2856), so
    /// a refusal stays a decision instead of becoming a per-interval retry. In
    /// memory on purpose: a backend restart is a new fact too and costs exactly
    /// one re-evaluation per card.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _refusedAttempts = new(StringComparer.Ordinal);
    private AcceptanceRailSnapshot _current = new()
    {
        Enabled = AcceptanceRailDefaults.Enabled,
    };

    public AcceptanceRailHostedService(
        TaskScannerService scanner,
        TaskIntegrationStatusService integrationStatus,
        TaskTransitionService transitions,
        TaskIntegrationRecoveryService recovery,
        HumanReviewEscalation humanReviewEscalation,
        TimelineLog timeline,
        IConfiguration configuration,
        ILogger<AcceptanceRailHostedService> logger,
        IntegrationGenerationReconcileSweep? generationReconcile = null,
        AttemptAuthorityService? attemptAuthority = null,
        AiOperatorDecisionGateway? decisions = null)
    {
        _decisions = decisions ?? AiOperatorDecisionGateway.ForTestHost(configuration);
        _scanner = scanner;
        _generationReconcile = generationReconcile;
        _attemptAuthority = attemptAuthority;
        _integrationStatus = integrationStatus;
        _transitions = transitions;
        _recovery = recovery;
        _humanReviewEscalation = humanReviewEscalation;
        _timeline = timeline;
        _configuration = configuration;
        _logger = logger;
    }

    public AcceptanceRailSnapshot Current
    {
        get
        {
            lock (_snapshotGate)
            {
                var last = _current.LastRunAtUtc ?? _startedAtUtc;
                return _current.Enabled && DateTime.UtcNow - last > TimeSpan.FromMinutes(5)
                    ? _current with { PlatformProblem = "integration-bounce-worker-unavailable" }
                    : _current;
            }
        }
    }

    public string Decider => AiOperatorDeciders.AcceptanceRail;

    public async Task<AcceptanceRailSnapshot> RunOnceAsync(CancellationToken ct = default)
    {
        await _runGate.WaitAsync(ct);
        try { return await RunCoreAsync(ct); }
        finally { _runGate.Release(); }
    }

    /// <summary>Counters of one rail pass or one applied receipt.</summary>
    private sealed class RailTally
    {
        public int Held;
        public int Accepted;
        public int Requeued;
        public int Escalated;
        public int Failed;
        public int Suppressed;
        public int BounceEligible;
        public int BounceDeferred;
        public int BounceShadowed;
        public int BounceFalseEligibility;
        public int WaitingForEngine;
    }

    private async Task<AcceptanceRailSnapshot> RunCoreAsync(CancellationToken ct)
    {
        var options = AcceptanceRailOptions.FromConfiguration(_configuration);
        var allJobs = _scanner.ScanAllAutomationJobs();
        foreach (var ready in allJobs.Where(job => job.State == TaskStates.Ready))
            ReconcileReadyBounce(ready);
        var jobs = allJobs
            .Where(job => job.State is TaskStates.HumanReview or TaskStates.Escalated)
            .OrderBy(job => job.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(job => job.EnteredLaneAt)
            .ThenBy(job => job.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!options.Enabled)
            return Publish(new AcceptanceRailSnapshot { Enabled = false, LastRunAtUtc = DateTime.UtcNow });

        var humanReviewDepth = jobs.Count(job => job.State == TaskStates.HumanReview);
        var escalatedDepth = jobs.Count(job => job.State == TaskStates.Escalated);
        var statusByKey = _integrationStatus.BuildLookup(jobs);
        var tally = new RailTally();
        ForgetCardsOutsideRailLanes(jobs);

        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                statusByKey.TryGetValue(job.TaskKey, out var status);
                var deliveryChainOwns = DeliveryChainOwns(job, status);
                if (!deliveryChainOwns && status is not null && _generationReconcile?.Reconcile(job, status) == false)
                {
                    _logger.LogWarning("integration-generation-reconcile write failed for {TaskKey}", job.TaskKey);
                    tally.Failed++;
                    continue;
                }
                var recoveryBudget = CountConflictRequeues(job);
                var infrastructureUsed = CountInfrastructureRequeues(job);
                var facts = Facts(job, status, deliveryChainOwns, recoveryBudget, infrastructureUsed, options);
                var submission = _decisions.Submit(new AiOperatorDecisionRequest(job.ProjectName, job.Id, job.State,
                    new AiOperatorDecisionPayload(Decider, SubjectKey(job, facts), AcceptanceRail: facts),
                    Period: null));
                if (submission.WaitsForEngine) { tally.WaitingForEngine++; continue; }
                await ApplyDecisionAsync(job, status, RailDecision(submission.MonolithDecision!), recoveryBudget,
                    infrastructureUsed, options, tally, ct);
            }
            catch (Exception ex)
            {
                tally.Failed++;
                _logger.LogError(
                    ex,
                    "acceptance-rail-item-failed project={Project} job={JobId}",
                    job.ProjectName,
                    job.Id);
            }
        }

        var snapshot = Publish(new AcceptanceRailSnapshot
        {
            Enabled = true,
            LastRunAtUtc = DateTime.UtcNow,
            HumanReviewDepth = humanReviewDepth,
            EscalatedDepth = escalatedDepth,
            Held = tally.Held,
            Accepted = tally.Accepted,
            Requeued = tally.Requeued,
            Escalated = tally.Escalated,
            Failed = tally.Failed,
            Suppressed = tally.Suppressed,
            BounceEligible = tally.BounceEligible,
            BounceDeferred = tally.BounceDeferred,
            BounceShadowed = tally.BounceShadowed,
            BounceFalseEligibility = tally.BounceFalseEligibility,
        });
        _logger.LogInformation(
            "acceptance-rail-run humanReviewDepth={HumanReviewDepth} escalatedDepth={EscalatedDepth} held={Held} accepted={Accepted} requeued={Requeued} escalated={Escalated} failed={Failed} suppressed={Suppressed} waitingForEngine={WaitingForEngine} lastRunAtUtc={LastRunAtUtc}",
            snapshot.HumanReviewDepth,
            snapshot.EscalatedDepth,
            snapshot.Held,
            snapshot.Accepted,
            snapshot.Requeued,
            snapshot.Escalated,
            snapshot.Failed,
            snapshot.Suppressed,
            tally.WaitingForEngine,
            snapshot.LastRunAtUtc);
        return snapshot;
    }

    /// <summary>
    /// Applies the engine's <c>acceptance-rail</c> receipt under the rail's run
    /// gate. A card that left its lane or whose facts changed after submission
    /// is settled without an effect; the next pass asks again. A refused
    /// attempt is remembered like an in-process one (AGT-2856).
    /// </summary>
    public async Task ApplyAsync(AiOperatorApplication application, CancellationToken ct)
    {
        await _runGate.WaitAsync(ct);
        try
        {
            var options = AcceptanceRailOptions.FromConfiguration(_configuration);
            var job = _scanner.ScanAllAutomationJobs().FirstOrDefault(candidate =>
                string.Equals(candidate.ProjectName, application.ProjectId, StringComparison.Ordinal)
                && string.Equals(candidate.Id, application.TaskId, StringComparison.Ordinal));
            if (job is null || job.State != application.ExpectedSourceState) return;
            _integrationStatus.BuildLookup([job]).TryGetValue(job.TaskKey, out var status);
            var recoveryBudget = CountConflictRequeues(job);
            var infrastructureUsed = CountInfrastructureRequeues(job);
            var facts = Facts(job, status, DeliveryChainOwns(job, status), recoveryBudget, infrastructureUsed, options);
            if (SubjectKey(job, facts) != application.Payload.SubjectKey) return;
            await ApplyDecisionAsync(job, status, RailDecision(application.Decision), recoveryBudget,
                infrastructureUsed, options, new RailTally(), ct);
        }
        finally { _runGate.Release(); }
    }

    private bool DeliveryChainOwns(TaskInfo job, TaskIntegrationStatus? status)
        => _configuration.GetValue("DeliveryChain:Guarded", true)
            && job.State == TaskStates.HumanReview
            && AcceptanceIntegrationPolicy.IsIntegrationRequired(job)
            && status?.Status != IntegrationStatuses.Integrated;

    private static AcceptanceRailOperatorFacts Facts(TaskInfo job, TaskIntegrationStatus? status,
        bool deliveryChainOwns, IntegrationRecoveryBudgetUsage recoveryBudget,
        (int Count, DateTimeOffset? LastAt) infrastructureUsed, AcceptanceRailOptions options)
        => AcceptanceRailFacts.From(
            job,
            status,
            recoveryBudget.Used,
            options,
            DateTimeOffset.UtcNow,
            infrastructureUsed.Count,
            infrastructureUsed.LastAt,
            // The provider reset instant is not carried on the card, so the
            // rail backs off exponentially instead of waiting for a known
            // reset. The facts take the instant for the callers that know it.
            quotaResetAt: null,
            deliveryChainOwns: deliveryChainOwns);

    private static string SubjectKey(TaskInfo job, AcceptanceRailOperatorFacts facts)
        => AiOperatorDecisionGateway.FactsSubjectKey("acceptance-rail", job.Id, facts);

    /// <summary>The rail's action vocabulary for one AI Operator decision.</summary>
    internal static AcceptanceRailDecision RailDecision(AiOperatorDecision decision)
        => new(decision.Effect switch
        {
            AiOperatorEffects.AcceptDelivery => AcceptanceRailAction.Accept,
            AiOperatorEffects.RequeueRebase => AcceptanceRailAction.Requeue,
            AiOperatorEffects.RequeueInfrastructure => AcceptanceRailAction.RequeueInfrastructure,
            AiOperatorEffects.Escalate => AcceptanceRailAction.Escalate,
            _ => AcceptanceRailAction.Ignore,
        }, decision.ReasonCode);

    private async Task ApplyDecisionAsync(TaskInfo job, TaskIntegrationStatus? status,
        AcceptanceRailDecision decision, IntegrationRecoveryBudgetUsage recoveryBudget,
        (int Count, DateTimeOffset? LastAt) infrastructureUsed, AcceptanceRailOptions options,
        RailTally tally, CancellationToken ct)
    {
        var used = recoveryBudget.Used;
        if (decision.Reason == AcceptanceRailReasons.DeliveryChainOwned)
        {
            tally.Held++;
            return;
        }
        if (decision.Reason == AcceptanceRailReasons.OperatorHold)
        {
            if (status?.Status == IntegrationStatuses.ConflictSkipped
                && status.Failure?.RebaseRecoveryAvailable == true
                && ReviewSubjectStore.Read(job.FolderPath) is { } heldSubject)
            {
                IntegrationBounceObligationStore.Ensure(job.FolderPath,
                    IntegrationBounceObligationStore.Project(
                        job, heldSubject, status,
                        OperatorReviewRequeueService.ReadEpoch(job.FolderPath),
                        used, "operator-hold", "operator-hold",
                        status.Failure.Reason));
                tally.BounceDeferred++;
            }
            tally.Held++;
            return;
        }

        // AGT-2856: the same facts always produce the same outcome, so a
        // refused attempt is not retried until a new fact arrives. This
        // is what keeps a card that cannot be accepted from re-running
        // the move - and re-logging the refusal - on every interval.
        var fingerprint = AcceptanceRailAttemptPolicy.Fingerprint(
            job, status, decision, _integrationStatus.ReadLatestMergeStep(job)?.Reason);
        if (decision.Action != AcceptanceRailAction.Ignore
            && !AcceptanceRailAttemptPolicy.ShouldAttempt(fingerprint, LastRefusal(job)))
        {
            tally.Suppressed++;
            return;
        }

        switch (decision.Action)
        {
            case AcceptanceRailAction.Accept:
                // In the guarded chain acceptance binds a human
                // judgement to the already integrated result. The
                // rail may diagnose and recover, but cannot supply
                // that judgement on the operator's behalf.
                if (_configuration.GetValue("DeliveryChain:Guarded", true))
                    tally.Held++;
                else if (await AcceptAsync(job, ct)) tally.Accepted++;
                else { tally.Failed++; RememberRefusal(job, fingerprint); }
                break;
            case AcceptanceRailAction.Requeue:
                if (status is null) { tally.Failed++; RememberRefusal(job, fingerprint); break; }
                var bounce = ProcessBounce(job, status, used + 1);
                if (bounce == "queued") { tally.Requeued++; tally.BounceEligible++; }
                else if (bounce == "shadow") { tally.BounceEligible++; tally.BounceShadowed++; }
                else if (bounce == "deferred") { tally.BounceEligible++; tally.BounceDeferred++; }
                else
                {
                    if (bounce == "false-eligibility") tally.BounceFalseEligibility++;
                    tally.Failed++;
                    RememberRefusal(job, fingerprint);
                }
                break;
            case AcceptanceRailAction.RequeueInfrastructure:
                if (status is not null
                    && await RequeueInfrastructureAsync(
                        job,
                        status,
                        infrastructureUsed.Count + 1,
                        options.MaxInfrastructureRequeues,
                        ct))
                {
                    tally.Requeued++;
                }
                else { tally.Failed++; RememberRefusal(job, fingerprint); }
                break;
            case AcceptanceRailAction.Escalate:
                if (job.State == TaskStates.Escalated && HasExhaustionReceipt(job))
                    break;
                if (decision.Reason == AcceptanceRailReasons.InfrastructureBudgetExhausted)
                {
                    if (await EscalateInfrastructureAsync(
                            job,
                            status,
                            infrastructureUsed.Count,
                            options.MaxInfrastructureRequeues,
                            ct))
                    {
                        tally.Escalated++;
                    }
                    else { tally.Failed++; RememberRefusal(job, fingerprint); }
                    break;
                }
                if (await EscalateAsync(job, recoveryBudget, options.MaxRequeues, ct)) tally.Escalated++;
                else { tally.Failed++; RememberRefusal(job, fingerprint); }
                break;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _scanner.WaitForFirstPublicationAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = AcceptanceRailOptions.FromConfiguration(_configuration);
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "acceptance-rail-run-failed");
            }

            await Task.Delay(options.Interval, stoppingToken);
        }
    }

    private string? LastRefusal(TaskInfo job)
        => _refusedAttempts.GetValueOrDefault(job.TaskKey);

    private void RememberRefusal(TaskInfo job, string fingerprint)
        => _refusedAttempts[job.TaskKey] = fingerprint;

    /// <summary>
    /// Drops ledger entries for cards that left the rail's lanes. Leaving the
    /// lane is itself a new fact, and it also keeps the ledger bounded by the
    /// current Human Review / Escalated depth.
    /// </summary>
    private void ForgetCardsOutsideRailLanes(IReadOnlyCollection<TaskInfo> jobs)
    {
        if (_refusedAttempts.IsEmpty) return;
        var present = jobs.Select(job => job.TaskKey).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _refusedAttempts.Keys)
            if (!present.Contains(key)) _refusedAttempts.TryRemove(key, out _);
    }

    private async Task<bool> AcceptAsync(TaskInfo job, CancellationToken ct)
    {
        var current = _scanner.FindJob(job.Id, job.WatchPath);
        if (current is null)
        {
            _logger.LogWarning(
                "acceptance-rail-accept-stale project={Project} job={JobId} watchPath={WatchPath}",
                job.ProjectName,
                job.Id,
                job.WatchPath);
            return false;
        }

        var outcome = await _transitions.MoveAsync(
            current.Id,
            TaskStates.Completed,
            current.WatchPath,
            ct,
            cause: TimelineActors.System,
            reason: "The acceptance rail accepted the Git-derived integrated delivery.",
            expectedSourceState: TaskStates.HumanReview,
            transitionCause: LaneChangeCauses.Accepted,
            transitionDetail: TaskIntegrationRecoveryService.AcceptanceRailSource);
        if (outcome.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "acceptance-rail-accept-refused project={Project} job={JobId} status={Status} message={Message}",
                job.ProjectName,
                job.Id,
                outcome.Status,
                outcome.Message);
            return false;
        }

        var moved = _scanner.FindJob(job.Id, job.WatchPath);
        if (moved is not null)
            AppendAction(moved, "accepted", "Accepted integrated delivery without a session-bound orchestrator tick.");
        return true;
    }

    private string ProcessBounce(
        TaskInfo job, TaskIntegrationStatus status, int retryNumber)
    {
        lock (BounceClaimGate)
            return ProcessBounceUnderClaim(job, status, retryNumber);
    }

    private string ProcessBounceUnderClaim(
        TaskInfo job, TaskIntegrationStatus status, int retryNumber)
    {
        var live = _scanner.FindJob(job.Id, job.WatchPath);
        if (live is null || live.State != job.State)
            return "deferred";
        var subject = ReviewSubjectStore.Read(job.FolderPath);
        if (subject is null || string.IsNullOrWhiteSpace(subject.ResultRef)
            || string.IsNullOrWhiteSpace(subject.RunAttemptId)
            || !ReviewSubjectStore.IsValidResultSha(subject.ResultSha)
            || !string.Equals(subject.TaskKey, job.Key ?? job.Id, StringComparison.OrdinalIgnoreCase)
            || (_attemptAuthority is not null
                && !ReviewSubjectStore.TryValidateCurrentAttempt(
                    job.FolderPath, subject, _attemptAuthority, out _))
            || (!string.IsNullOrWhiteSpace(status.DeliveryRef)
                && !string.Equals(subject.ResultRef, status.DeliveryRef, StringComparison.Ordinal)
                && !string.Equals(subject.ImmutableResultRef, status.DeliveryRef, StringComparison.Ordinal))
            || !job.Commits.Any(commit => string.Equals(
                commit.Sha, subject.ResultSha, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("acceptance-rail-requeue-refused project={Project} job={JobId} error={Error}",
                job.ProjectName, job.Id, "Reviewed delivery evidence is stale or incomplete.");
            return "false-eligibility";
        }

        var epoch = OperatorReviewRequeueService.ReadEpoch(job.FolderPath);
        var alreadyUsedEpoch = _timeline.ReadAll(job.FolderPath).Any(entry =>
            entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued
            && entry.Details?.GetValueOrDefault("automatic") == "true"
            && entry.Details?.GetValueOrDefault("attemptEpoch") == epoch.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        var bounceConfig = _configuration.GetSection(AcceptanceRailDefaults.BounceConfigurationSection);
        var globallyEnabled = bounceConfig.GetValue<bool?>("Enabled") ?? true;
        var projectEnabled = bounceConfig.GetSection("Projects").GetSection(job.ProjectName)
            .GetValue<bool?>("Enabled") ?? true;
        var shadowOnly = bounceConfig.GetValue<bool?>("ShadowOnly") ?? false;
        var route = alreadyUsedEpoch ? "guardian-required"
            : !globallyEnabled || !projectEnabled ? "operator-disabled"
            : shadowOnly ? "shadow" : "automatic";
        var proposal = IntegrationBounceObligationStore.Project(
            job, subject, status, epoch, retryNumber - 1, "none", route,
            status.Failure?.Reason);
        if (proposal.MechanicalRoute == "operator") return "false-eligibility";
        var obligation = IntegrationBounceObligationStore.Ensure(job.FolderPath, proposal);
        if (obligation.State == "queued") return "deferred";
        if (route == "shadow") return "shadow";
        if (route != "automatic")
        {
            IntegrationBounceObligationStore.Update(job.FolderPath,
                obligation with { State = "deferred", RouteDecision = route });
            return "deferred";
        }

        var result = _recovery.Queue(job, status,
            status.Failure?.Code ?? AcceptedIntegrationFailureCodes.MergeConflict,
            TaskIntegrationRecoveryService.AcceptanceRailSource, retryNumber, obligation);
        if (!result.Queued)
        {
            _logger.LogWarning("integration-bounce-refused task={TaskKey} error={Error}", job.TaskKey, result.Error);
            var liveAfterFailure = _scanner.FindJob(job.Id, job.WatchPath);
            if (liveAfterFailure?.State == TaskStates.Ready)
                return "deferred"; // A restart pass repairs the post-move receipt.
            if (liveAfterFailure is not null)
            {
                var latestPath = Path.Combine(TaskPaths.LogsDir(liveAfterFailure.FolderPath),
                    "integration-bounce", obligation.IdempotencyKey + ".json");
                var latest = IntegrationBounceObligationStore.Read(latestPath) ?? obligation;
                IntegrationBounceObligationStore.Update(liveAfterFailure.FolderPath,
                    latest with { State = "deferred", RouteDecision = "operator-error" });
            }
            return "failed";
        }
        var moved = _scanner.FindJob(job.Id, job.WatchPath);
        if (moved is null) return "failed";
        var routedObligation = IntegrationBounceObligationStore.Read(Path.Combine(
            TaskPaths.LogsDir(moved.FolderPath), "integration-bounce",
            obligation.IdempotencyKey + ".json"));
        if (routedObligation is null) return "failed";
        IntegrationBounceObligationStore.Update(moved.FolderPath, routedObligation with
        {
            State = "queued",
            RouteDecision = "automatic",
            ClaimedAtUtc = DateTimeOffset.UtcNow,
        });
        AppendAction(moved, "requeued", $"Queued deterministic integration recovery retry {retryNumber}.", retryNumber);
        return "queued";
    }

    private void ReconcileReadyBounce(TaskInfo ready)
    {
        if (ready.PendingIntent?.SavedReason?.StartsWith(
                TaskIntegrationRecoveryService.AcceptanceRailSource + ":",
                StringComparison.Ordinal) != true)
            return;
        var directory = Path.Combine(TaskPaths.LogsDir(ready.FolderPath), "integration-bounce");
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var obligation = IntegrationBounceObligationStore.Read(path);
            if (obligation is null || obligation.State == "queued"
                || obligation.RouteDecision is not ("automatic" or "shadow")
                || ready.PendingIntent?.Prompt?.Contains(
                    obligation.ResultSha, StringComparison.OrdinalIgnoreCase) != true
                || ready.PendingIntent?.Prompt?.Contains(
                    obligation.ResultRef, StringComparison.Ordinal) != true)
                continue;
            var events = _timeline.ReadAll(ready.FolderPath);
            var recorded = events.Any(entry =>
                entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued
                && entry.Details?.GetValueOrDefault("attemptEpoch")
                    == obligation.OperatorReviewEpoch.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                && entry.Details?.GetValueOrDefault("resultSha") == obligation.ResultSha);
            if (!recorded)
            {
                _timeline.Append(ready.FolderPath,
                    TimelineEventKinds.IntegrationRecoveryQueued,
                    TimelineActors.System,
                    "Recovered the queued integration bounce after a backend restart.",
                    details: new Dictionary<string, string>
                    {
                        ["automatic"] = "true",
                        ["source"] = TaskIntegrationRecoveryService.AcceptanceRailSource,
                        ["attemptEpoch"] = obligation.OperatorReviewEpoch.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        ["resultSha"] = obligation.ResultSha,
                        ["deliveryRef"] = obligation.ResultRef,
                        ["reason"] = obligation.FailureCode,
                        ["previousRoute"] = obligation.PreviousRoute ?? "unknown",
                        ["selectedRoute"] = obligation.SelectedRoute ?? "unknown",
                        ["routeReason"] = obligation.RouteReason ?? "receipt unavailable",
                        ["policyVersion"] = obligation.PolicyVersion ?? "unknown",
                        ["operatorPinPresent"] = obligation.OperatorPinPresent.ToString().ToLowerInvariant(),
                    });
            }
            IntegrationBounceObligationStore.Update(ready.FolderPath, obligation with
            {
                State = "queued",
                RouteDecision = "automatic",
                ClaimedAtUtc = DateTimeOffset.UtcNow,
            });
            AppendAction(ready, "requeued", "Recovered queued integration bounce after restart.");
        }
    }

    /// <summary>
    /// Replays a card whose last failure was attributed to the host or the
    /// provider account (AGT-2749). The delivery is unchanged, so this path must
    /// not write a rebase steer: the card only goes back to
    /// <see cref="TaskStates.AutoReview"/> to be verified again.
    /// </summary>
    private async Task<bool> RequeueInfrastructureAsync(
        TaskInfo job,
        TaskIntegrationStatus status,
        int retryNumber,
        int maximum,
        CancellationToken ct)
    {
        var failureClass = status.Failure?.FailureClass ?? RunFailureClass.Unknown;
        var signature = status.Failure?.FailureSignature ?? RunFailureSignatures.Unclassified;
        var reason = $"The last integration failure is {ClassText(failureClass)} ({signature}), not a verdict on the change. "
                     + $"Requeued to {TaskStates.AutoReview} as retry {retryNumber}/{maximum}.";

        // Open the durable resume point before exposing the card in Auto Review.
        // Keep the failed verdict so a refused move can restore it.
        var previousSettlement = RemoteDeliverySettlementStore.Read(job.FolderPath);
        if (!RemoteDeliverySettlementStore.RequeueInfrastructure(job.FolderPath))
        {
            _logger.LogWarning("acceptance-rail-infrastructure-settlement-reset-failed project={Project} job={JobId}",
                job.ProjectName, job.Id);
            return false;
        }

        var movedToAutoReview = false;
        try
        {
            var outcome = await _transitions.MoveAsync(
                job.Id,
                TaskStates.AutoReview,
                job.WatchPath,
                ct,
                cause: TimelineActors.System,
                reason: reason,
                expectedSourceState: job.State,
                transitionCause: LaneChangeCauses.ReviewInfrastructure,
                transitionDetail: TaskIntegrationRecoveryService.AcceptanceRailSource);
            movedToAutoReview = outcome.Status == MoveJobStatus.Success;
            if (!movedToAutoReview)
            {
                _logger.LogWarning(
                    "acceptance-rail-infrastructure-requeue-refused project={Project} job={JobId} status={Status} message={Message}",
                    job.ProjectName,
                    job.Id,
                    outcome.Status,
                    outcome.Message);
                return false;
            }
        }
        finally
        {
            if (!movedToAutoReview && previousSettlement is not null)
            {
                // A failed MoveAsync can still have moved the folder before a
                // later write failed. Leave that card pending for the resume
                // sweep; restore the error only while it is in Human Review.
                try
                {
                    var current = _scanner.FindJob(job.Id, job.WatchPath);
                    if (current?.State == TaskStates.HumanReview
                        && Directory.Exists(current.FolderPath))
                    {
                        RemoteDeliverySettlementStore.Write(current.FolderPath, previousSettlement);
                    }
                    else if (current?.State != TaskStates.AutoReview)
                    {
                        _logger.LogCritical(
                            "acceptance-rail-infrastructure-settlement-rollback-unavailable project={Project} job={JobId} state={State}",
                            job.ProjectName, job.Id, current?.State ?? "missing");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex,
                        "acceptance-rail-infrastructure-settlement-rollback-failed project={Project} job={JobId}",
                        job.ProjectName, job.Id);
                }
            }
        }

        var moved = _scanner.FindJob(job.Id, job.WatchPath);
        if (moved is null) return false;
        if (previousSettlement is not null)
            AgentStudio.Runner.AutoReviewResumeRetryStore.Clear(
                moved.FolderPath, previousSettlement.ReviewAttemptId);
        AppendAction(moved, AcceptanceRailReceipts.InfrastructureRequeueAction, reason, retryNumber);
        return true;
    }

    private async Task<bool> EscalateInfrastructureAsync(
        TaskInfo job,
        TaskIntegrationStatus? status,
        int used,
        int maximum,
        CancellationToken ct)
    {
        var failureClass = status?.Failure?.FailureClass ?? RunFailureClass.Unknown;
        var signature = status?.Failure?.FailureSignature ?? RunFailureSignatures.Unclassified;
        var reason = $"Integration recovery stopped after {used}/{maximum} {ClassSlug(failureClass)} requeues ({signature}). "
                     + "Fix the host or the account, then requeue the card.";
        var category = failureClass == RunFailureClass.Quota
            ? HumanReviewEscalationCategories.QuotaExhausted
            : HumanReviewEscalationCategories.Environmental;
        var outcome = await _humanReviewEscalation.EscalateAsync(
            job.Id,
            job.WatchPath,
            job.ProjectName,
            category,
            reason,
            ct);
        if (outcome.Status != MoveJobStatus.Success) return false;

        var moved = _scanner.FindJob(job.Id, job.WatchPath);
        if (moved is not null)
            AppendAction(moved, "escalated", reason, used);
        return true;
    }

    private static string ClassText(RunFailureClass failureClass) => failureClass switch
    {
        RunFailureClass.Quota => "a provider quota fault",
        RunFailureClass.Infrastructure => "an infrastructure fault",
        _ => "an unclassified fault",
    };

    private static string ClassSlug(RunFailureClass failureClass)
        => failureClass.ToString().ToLowerInvariant();

    private async Task<bool> EscalateAsync(
        TaskInfo job,
        IntegrationRecoveryBudgetUsage budget,
        int maximum,
        CancellationToken ct)
    {
        var reason = budget.ExhaustedReason(maximum);
        var outcome = await _humanReviewEscalation.EscalateAsync(
            job.Id,
            job.WatchPath,
            job.ProjectName,
            HumanReviewEscalationCategories.IntegrationRecoveryExhausted,
            reason,
            ct);
        if (outcome.Status != MoveJobStatus.Success) return false;

        var moved = _scanner.FindJob(job.Id, job.WatchPath);
        if (moved is not null)
            AppendAction(moved, "escalated", reason, budget.Used);
        return true;
    }

    private IntegrationRecoveryBudgetUsage CountConflictRequeues(TaskInfo job)
        => IntegrationRecoveryBudget.Count(
            _timeline.ReadAll(job.FolderPath),
            ReviewSubjectStore.Read(job.FolderPath));

    /// <summary>
    /// Infrastructure replays already spent on this card, plus the instant of the
    /// last one. An infrastructure requeue writes no recovery intent, so the rail
    /// counts its own receipts instead of the recovery-queued events.
    /// </summary>
    private (int Count, DateTimeOffset? LastAt) CountInfrastructureRequeues(TaskInfo job)
        => AcceptanceRailReceipts.CountInfrastructureRequeues(_timeline, job.FolderPath);

    private bool HasExhaustionReceipt(TaskInfo job)
        => _timeline.ReadAll(job.FolderPath).Any(entry =>
            entry.Kind == TimelineEventKinds.AcceptanceRailActed
            && entry.Details?.GetValueOrDefault("action") == "escalated");

    private void AppendAction(
        TaskInfo job,
        string action,
        string summary,
        int? retryNumber = null)
    {
        var details = new Dictionary<string, string>
        {
            ["action"] = action,
            ["source"] = TaskIntegrationRecoveryService.AcceptanceRailSource,
        };
        if (retryNumber is not null)
            details["retryNumber"] = retryNumber.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        _timeline.Append(
            job.FolderPath,
            TimelineEventKinds.AcceptanceRailActed,
            TimelineActors.System,
            summary,
            details: details);
    }

    private AcceptanceRailSnapshot Publish(AcceptanceRailSnapshot snapshot)
    {
        lock (_snapshotGate)
        {
            _current = snapshot;
            return _current;
        }
    }
}

public static class AcceptanceRailEndpoints
{
    public static void MapAcceptanceRailEndpoints(this WebApplication app)
    {
        app.MapGet("/api/pipeline/acceptance-rail", (
            AcceptanceRailHostedService rail) => Results.Ok(rail.Current));
        app.MapGet("/api/pipeline/integration-bounce/metrics", (
            TaskScannerService scanner, AcceptanceRailHostedService rail) => Results.Ok(
                IntegrationBounceObligationStore.Measure(
                    scanner.ScanAllAutomationJobsWithArchive(),
                    rail.Current.BounceFalseEligibility)));
    }
}
