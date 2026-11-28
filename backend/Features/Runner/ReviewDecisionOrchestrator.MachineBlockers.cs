using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

public sealed partial class ReviewDecisionOrchestrator
{
    // One receipt per source generation. An unchanged wait never rewrites its deadline.
    private sealed record MachineBlockerReceipt(
        string Subject, string Kind, string Reference, DateTimeOffset OpenedAt,
        DateTimeOffset Deadline, MachineBlockerAction Action, string Reason,
        string? RepairKey = null, IReadOnlyList<string>? CapableHosts = null, IReadOnlyList<string>? Evidence = null);

    private static readonly JsonSerializerOptions MachineBlockerJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static string MachineBlockerPath(TaskInfo task)
        => Path.Combine(TaskPaths.LogsDir(task.FolderPath), "machine-blocker.json");

    private static MachineBlockerReceipt? ReadMachineBlocker(TaskInfo task)
    {
        var path = MachineBlockerPath(task);
        return File.Exists(path) ? JsonSerializer.Deserialize<MachineBlockerReceipt>(File.ReadAllText(path), MachineBlockerJson) : null;
    }

    private static void WriteMachineBlocker(TaskInfo task, MachineBlockerReceipt receipt)
        => new AgentStudio.Persistence.AtomicJsonFileWriter().Write(MachineBlockerPath(task),
            JsonSerializer.Serialize(receipt, MachineBlockerJson));

    private static readonly TimeSpan BlockerInventoryRefreshInterval = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _blockerInventoryGate = new(1, 1);
    private BlockerInventoryObservation? _blockerInventory;

    private sealed record BlockerInventoryObservation(
        string Authority, long ReadAt, IReadOnlyList<RunnerCapabilitySnapshotDto>? Hosts);

    private async Task<IReadOnlyList<RunnerCapabilitySnapshotDto>?> ReadBlockerHostFactsAsync(CancellationToken ct)
    {
        if (!AgentStudio.Host.TaskServerPlaneProxy.TryGetBaseUri(_configuration, out var authority))
            return _blockerExecutors?.ListCapabilitySnapshots();

        var authorityUri = authority!.AbsoluteUri;
        // Share successes and outages across the tick and concurrent per-card workers.
        // This only caches observations; each card still checks live time and review authority.
        await _blockerInventoryGate.WaitAsync(ct);
        try
        {
            if (_blockerInventory is { } cached && cached.Authority == authorityUri
                && _gateTime.GetElapsedTime(cached.ReadAt) < BlockerInventoryRefreshInterval)
                return cached.Hosts;

            var hosts = await FetchBlockerHostFactsAsync(ct);
            ct.ThrowIfCancellationRequested();
            _blockerInventory = new(authorityUri, _gateTime.GetTimestamp(), hosts);
            return hosts;
        }
        finally
        {
            _blockerInventoryGate.Release();
        }
    }

    private async Task<IReadOnlyList<RunnerCapabilitySnapshotDto>?> FetchBlockerHostFactsAsync(CancellationToken ct)
    {
        if (_blockerHttpClients is null) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await _blockerHttpClients.CreateClient(AgentStudio.Host.TaskServerPlaneProxy.ClientName)
                .GetFromJsonAsync<RunnerCapabilitySnapshotDto[]>("/api/v1/management/remote-hosts", deadline.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Machine blocker capability inventory unavailable; the bounded wait retains unknown feasibility");
            return null;
        }
    }

    private async Task<bool> TryResolveMachineBlockerAsync(
        string workspace, WatchPathEntry entry, PendingDecision pending, string reason,
        CancellationToken ct)
    {
        var dependency = MonolithTransitionAdapter.IntegrationBlockerDependency("needs-human-input", reason);
        var verification = MonolithTransitionAdapter.IsVerificationBlocker(reason);
        if (dependency is null && !verification) return false;
        var current = _scanner.FindJob(pending.Job.Id, entry.Path);
        if (current is null || current.State != TaskStates.AutoReview
            || CurrentReviewCycleEpoch(current) != CurrentReviewCycleEpoch(pending.Job)) return true;

        var source = ReviewSubjectStore.Read(current.FolderPath);
        // Do not act on a subject that belongs to an earlier delivery or on an active review lease.
        if (source is not null && _attemptAuthority is not null
            && !ReviewSubjectStore.TryValidateCurrentAttempt(current.FolderPath, source, _attemptAuthority, out _)) return true;
        var review = _attemptAuthority?.GetTaskProjection(current.Key ?? current.TaskKey).CurrentReviewAttempt;
        if (review?.State == AttemptLifecycleState.Leased) return true;
        var choiceMessage = pending.Kind == ReviewSignalKind.Blocked
            ? ReviewDecisionEvidenceReader.ReadBlockedMessage(current.FolderPath)
            : NeedsInputArtifact.TryRead(current.FolderPath)?.Message;
        var ownerHold = HasLatestOperatorLaneVerdict(current)
            || BlockedForkReader.Read(pending.Reason, choiceMessage) is not null
            || current.References.DependsOn.Any(edge => edge.ReleaseGate || edge.RequiresCompletion);
        var now = _gateTime.GetUtcNow();
        var reference = dependency ?? review?.Subject.SubjectId ?? "unreconstructed-verification";
        var subject = $"{source?.RunAttemptId ?? review?.SourceRunAttemptId}:{source?.ResultSha ?? review?.Subject.ExpectedResultSha}:{CurrentReviewCycleEpoch(current):O}:{pending.LineNumber}:{reference}";
        var prior = ReadMachineBlocker(current);
        if (prior?.Subject != subject) prior = null;
        var receipt = prior ?? new MachineBlockerReceipt(subject, dependency is null ? "verification-capability" : "integration-dependency",
            reference, now, now + (dependency is null ? IntegrationBlockerPolicy.CapabilityWait : IntegrationBlockerPolicy.DependencyWait),
            MachineBlockerAction.Wait, reason);
        var evaluation = dependency is not null
            ? EvaluateIntegrationBlocker(workspace, entry, current, dependency, ownerHold, now, receipt)
            : await EvaluateVerificationBlockerAsync(pending, review, reason, ownerHold, receipt, ct);
        if (evaluation is null) return true;
        (receipt, var disposition, now, var capableHosts) = evaluation;
        if (disposition.Action == MachineBlockerAction.HumanChoice) return pending.LineNumber == -2;
        receipt = receipt with { Action = disposition.Action, Reason = disposition.Reason, CapableHosts = capableHosts };
        if (JsonSerializer.Serialize(receipt, MachineBlockerJson) != JsonSerializer.Serialize(prior, MachineBlockerJson))
            WriteMachineBlocker(current, receipt);
        if (disposition.Action is MachineBlockerAction.Wait or MachineBlockerAction.PlaceVerification)
            return true; // The normal review claim route owns placement; this handler never executes hardware itself.

        if (disposition.Action == MachineBlockerAction.Requeue)
        {
            await RequeueMachineBlockerAsync(workspace, entry, current, dependency, disposition, receipt, now, ct);
            return true;
        }

        var repaired = await EnsureMachineBlockerRepairAsync(entry, current, reason, disposition, receipt, ct);
        if (repaired is null) return true;
        receipt = repaired;
        // An optional check can only be removed from a still-pending authoritative plan.
        if (disposition.Action == MachineBlockerAction.RecordUnavailable && review?.Subject.Plan is { } optionalPlan)
        {
            var replanned = _attemptAuthority!.ReplanPendingReview(review.AttemptId,
                RemoteReviewPlanBuilder.WithoutUnavailableOptionalCommands(optionalPlan, disposition.Reason));
            if (!replanned.Accepted)
                _logger.LogDebug("Machine blocker optional verification replan deferred for {AttemptId}: {Status} {Message}",
                    review.AttemptId, replanned.Status, replanned.Message);
            return true;
        }
        QuarantineMachineBlocker(workspace, entry, current, review, disposition, receipt, now);
        return true;
    }

    private sealed record MachineBlockerEvaluation(
        MachineBlockerReceipt Receipt, MachineBlockerDisposition Disposition,
        DateTimeOffset ObservedAt, IReadOnlyList<string> CapableHosts);

    private MachineBlockerEvaluation EvaluateIntegrationBlocker(
        string workspace, WatchPathEntry entry, TaskInfo current, string dependency,
        bool ownerHold, DateTimeOffset now, MachineBlockerReceipt receipt)
    {
        var index = _scanner.GetReferenceIndex();
        var parent = index.Resolve(dependency);
        var proposed = current.References with
        {
            DependsOn = current.References.DependsOn.Any(edge => edge.Key.Equals(dependency, StringComparison.OrdinalIgnoreCase))
                ? current.References.DependsOn : [.. current.References.DependsOn, new TaskDependencyReference(dependency)],
        };
        var valid = TaskReferenceValidator.Validate(current.Key ?? current.Id, proposed, index.KnownKeys,
            index.DependsOnGraph, current.References.Workbenches.ToHashSet(StringComparer.OrdinalIgnoreCase)).IsValid;
        var integration = parent is null ? null : MachineBlockerIntegrationReader is { } read
            ? read(parent) : _integrationStatus?.BuildLookup([parent]).GetValueOrDefault(parent.TaskKey);
        var branch = TaskIntegrationBranch.Resolve(current, _projectSettings?.Get(entry.Name)?.IntegrationBranch ?? "develop");
        var integrated = integration?.Status == IntegrationStatuses.Integrated
            && string.Equals(integration.IntegrationBranch, branch, StringComparison.OrdinalIgnoreCase);
        receipt = receipt with { Evidence = [$"dependency={dependency}; status={integration?.Status ?? "unavailable"}; "
            + $"delivery={integration?.DeliveryRef}; target={branch}; targetFingerprint={integration?.TargetRefFingerprint}; "
            + $"pendingRecovery={parent is not null && OverlapComparisonSet.IsDeliveryRecovery(parent)}"] };
        var disposition = MonolithTransitionAdapter.ResolveIntegrationBlocker(new(
            parent is not null, parent is not null && !TaskModes.IsReadOnly(parent.Mode) && !TaskKinds.IsDecision(parent.Kind),
            valid, integrated, integration?.Status == IntegrationStatuses.ConflictSkipped
                && parent is not null && !OverlapComparisonSet.IsDeliveryRecovery(parent),
            ownerHold, now, receipt.Deadline,
            CardRoundBudget.Evaluate(ReviewDecisionLog.ReadAll(workspace, entry.Name),
                _timeline?.ReadAll(current.FolderPath) ?? [], current.Id,
                CardRoundBudget.ResolveAllowed(_configuration)).Exhausted));
        return new(receipt, disposition, now, []);
    }

    private async Task<MachineBlockerEvaluation?> EvaluateVerificationBlockerAsync(
        PendingDecision pending, AgentStudio.Shared.ReviewAttemptDto? review, string reason,
        bool ownerHold, MachineBlockerReceipt receipt, CancellationToken ct)
    {
        var plan = review?.Subject.Plan;
        var observedHosts = await ReadBlockerHostFactsAsync(ct);
        var now = _gateTime.GetUtcNow();
        if (review is not null)
        {
            var live = _attemptAuthority!.GetTaskProjection(review.TaskKey).CurrentReviewAttempt;
            if (live?.AttemptId != review.AttemptId || live.State != AttemptLifecycleState.Pending
                || live.LastFence != review.LastFence || live.AuthorityEpoch != review.AuthorityEpoch) return null;
        }
        var hosts = observedHosts ?? [];
        IReadOnlyList<string> capableHosts = plan is null ? [] : EvidenceGate.CapableVerificationHosts(plan, hosts, now.UtcDateTime, review?.Subject.CodingHostId);
        ownerHold |= plan is not null && capableHosts.Count == 0
            && EvidenceGate.HasOwnerHeldVerificationHost(plan, hosts, review?.Subject.CodingHostId);
        receipt = receipt with { Evidence = [
            $"inventory={(observedHosts is null ? "unavailable" : "observed")}; sourceFinding={reason}; review={review?.AttemptId}; result={review?.Subject.ExpectedResultSha}; "
                + $"requirements={string.Join(",", plan is null ? [] : ReviewLibraryStepPolicy.ExecutionCapabilities(plan))}",
            .. hosts.Select(host => $"host={host.HostId}; instance={host.InstanceId}; heartbeat={host.LastSeenAt:O}; "
                + $"admission={host.HostAdmission.AdmissionState}; "
                + string.Join(",", host.Capabilities.Select(capability =>
                    $"{capability.Key}:{capability.AdvertisedStatus}:{capability.HealthState}:{capability.FreshUntil:O}"))) ] };
        var disposition = MonolithTransitionAdapter.ResolveVerificationBlocker(new(
            plan is not null && review is not null && pending.LineNumber == -2
                && _attemptAuthority!.IsReviewedDeliveryCurrent(review.AttemptId),
            plan is null || EvidenceGate.CapableVerificationHosts(plan with
            {
                Commands = plan.Commands.Where(command => command.Required).ToArray(),
            }, hosts, now.UtcDateTime, review?.Subject.CodingHostId).Count == 0, capableHosts.Count > 0,
            ownerHold, now, receipt.Deadline));
        return new(receipt, disposition, now, capableHosts);
    }

    private async Task RequeueMachineBlockerAsync(
        string workspace, WatchPathEntry entry, TaskInfo current, string? dependency,
        MachineBlockerDisposition disposition, MachineBlockerReceipt receipt, DateTimeOffset now, CancellationToken ct)
    {
        var followUp = $"Platform correction for {dependency}: {disposition.Reason}\n"
            + $"Target branch: {TaskIntegrationBranch.Resolve(current, _projectSettings?.Get(entry.Name)?.IntegrationBranch ?? "develop")}.\n"
            + "Use the platform-assigned current integration base. Preserve the prior immutable result as evidence. "
            + "Do not repeat the agent-reported missing-integration assumption or perform platform-owned git operations.";
        followUp = await WriteFollowUpFileAsync(current, followUp, ct);
        var moved = MoveReissueToReadyTop(current, entry, "machine-integration-resolved", workspace, alreadyCharged: true);
        if (moved is null) return;
        WriteMachineBlocker(moved, receipt);
        AppendReviewDecision(workspace, new ReviewDecisionRecord(now.UtcDateTime, current.Id, entry.Name,
            ReviewDecisionKind.Reissue, "machine-blocker: " + dependency + ": " + disposition.Reason,
            "(platform integration facts)", "(no model call)", followUp), current.FolderPath, moved.FolderPath);
        _chatLog.AppendSupervisor(moved, "reissue", disposition.Reason);
    }

    private async Task<MachineBlockerReceipt?> EnsureMachineBlockerRepairAsync(
        WatchPathEntry entry, TaskInfo current, string reason, MachineBlockerDisposition disposition,
        MachineBlockerReceipt receipt, CancellationToken ct)
    {
        var repairKey = receipt.RepairKey;
        if (string.IsNullOrWhiteSpace(repairKey))
        {
            if (_failureInterventions is not null)
            {
                var fingerprint = AttemptAuthorityService.Hash(entry.Name + ":" + receipt.Kind + ":" + receipt.Reference);
                var repair = _failureInterventions.RaiseCause(current,
                    new FailureCommandEvidence(receipt.Kind, StderrTail: reason + "\n" + disposition.Reason,
                        EvidencePointers: ["logs/machine-blocker.json"], OccurredAt: receipt.OpenedAt.UtcDateTime),
                    new FailureClassificationResult(FailureDomains.Infrastructure, receipt.Kind, fingerprint,
                        receipt.Reference, true, disposition.Reason));
                repairKey = repair.Intervention.FollowUpKey;
            }
            else
            {
                // A deterministic creation identity also makes recovery safe in minimal hosts.
                var repairId = "machine-repair-" + AttemptAuthorityService.Hash(entry.Name + ":" + receipt.Kind + ":" + receipt.Reference)[..20];
                var existing = _taskAccess.FindJob(repairId, entry.Path);
                if (existing is null)
                {
                    var created = await _taskAccess.MutateAsync(new TaskMutationRequest
                    {
                        Kind = TaskMutationKind.Create,
                        CreateRequest = new AgentStudio.Shared.CreateTaskRequest
                        {
                            Id = repairId, Title = $"Repair {receipt.Kind}: {receipt.Reference}", WatchPath = entry.Path,
                            TargetState = TaskStates.Preparation, CreationSource = TimelineActors.Orchestrator,
                            CreatedBy = "Orchestrator", NoBranchExpected = true, RequiresIntegration = false,
                            PromptMarkdown = $"# Repair {receipt.Kind}\n\nSource: {current.Key ?? current.Id}.\n\n{reason}\n\n{disposition.Reason}\n\n"
                                + "Reconstruct the source finding and platform facts. Restore integration or verification capability. "
                                + "Do not fabricate mandatory evidence. The original delivery is quarantined; do not repeatedly retry it.",
                        },
                    }, ct);
                    if (created.Status != TaskMutationStatus.Applied) return null;
                    existing = created.Job;
                }
                repairKey = existing?.Key ?? repairId;
            }
            receipt = receipt with { RepairKey = repairKey };
            WriteMachineBlocker(current, receipt);
        }
        return receipt;
    }

    private void QuarantineMachineBlocker(
        string workspace, WatchPathEntry entry, TaskInfo current, AgentStudio.Shared.ReviewAttemptDto? review,
        MachineBlockerDisposition disposition, MachineBlockerReceipt receipt, DateTimeOffset now)
    {
        var repairKey = receipt.RepairKey;
        var quarantineReason = HumanReviewEscalation.FormatReason(IntegrationBlockerPolicy.QuarantineCategory,
            $"{disposition.Reason} Repair: {repairKey}. This is a machine-owned quarantine; acceptance evidence is unsatisfied.");
        MoveJobOutcome? move = null;
        void ApplyQuarantine() => move = GuardedMoveJob(current.Id, TaskStates.Escalated, entry.Path, reason: quarantineReason,
            expectedSourceState: TaskStates.AutoReview, transitionCause: LaneChangeCauses.Escalated,
            transitionDetail: IntegrationBlockerPolicy.QuarantineCategory);
        if (review is not null)
        {
            // Revocation and claims serialize at the authority. The continuation fence then
            // orders the lane effect against a new review or coding generation for this task.
            var revoked = _attemptAuthority!.SupersedeOpenReviewAttempts(key =>
            {
                if (key != review.TaskKey) return null;
                var live = _attemptAuthority.GetTaskProjection(key).CurrentReviewAttempt;
                return live?.AttemptId == review.AttemptId && live.State == AttemptLifecycleState.Pending
                    && live.LastFence == review.LastFence && live.AuthorityEpoch == review.AuthorityEpoch
                    ? quarantineReason : null;
            });
            if (!revoked.Any(attempt => attempt.AttemptId == review.AttemptId)
                || !_attemptAuthority.TryApplyForCurrentReviewedDelivery(review.AttemptId, ApplyQuarantine)) return;
        }
        else ApplyQuarantine();
        if (move is null) return;
        if (move.Status != MoveJobStatus.Success) return;
        var quarantined = current with { FolderPath = move.NewFolderPath ?? current.FolderPath, State = TaskStates.Escalated };
        WriteMachineBlocker(quarantined, receipt);
        AppendReviewDecision(workspace, new ReviewDecisionRecord(now.UtcDateTime, current.Id, entry.Name,
            ReviewDecisionKind.Escalate, quarantineReason, "(platform blocker facts)", "(no model call)",
            $"repair {repairKey}"), current.FolderPath, quarantined.FolderPath);
        _chatLog.AppendSupervisor(quarantined, "machine-quarantine", quarantineReason);
    }

}
