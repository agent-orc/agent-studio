using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

/// <summary>
/// Runs exactly one task end-to-end on the remote host (RM-5 MVP). The lifecycle:
/// acquire the fenced lease, start heartbeating, prepare the git working tree,
/// spawn the agent CLI with the fetched prompt, ship its output to the server,
/// publish the Git result and fenced completion, upload bounded results/ evidence,
/// and always release the lease. Before removing
/// a worktree it salvages changes to a generation-scoped ref on origin.
/// </summary>
public sealed class RemoteTaskRunner
{
    internal const int MaxEnvironmentPreparationAttempts = 3;

    private readonly RunnerOptions _options;
    private readonly TaskServerClient _client;
    private readonly Action<string> _log;
    private readonly RunnerStateStore _state;
    private readonly RunnerProcessInventoryTracker _inventory;

    public RemoteTaskRunner(
        RunnerOptions options,
        TaskServerClient client,
        Action<string> log,
        RunnerStateStore? state = null,
        RunnerProcessInventoryTracker? inventory = null)
    {
        _options = options;
        _client = client;
        _log = log;
        _state = state ?? new RunnerStateStore(options.StateDir);
        _inventory = inventory ?? new RunnerProcessInventoryTracker();
    }

    /// <returns>Process exit code: 0 on a clean handoff, non-zero when the run could not complete.</returns>
    public async Task<int> RunAsync(string taskKey, CancellationToken shutdown)
    {
        // Connectivity preflight: over a reverse tunnel the Task Server is only
        // reachable while the tunnel is up. Probe /healthz first so a dropped
        // connection is reported once, cleanly, as a connection-lost diagnostic
        // that names the tunnel - instead of surfacing as a raw transport error
        // buried in register/lease and reading like a task launch failure.
        var health = await _client.ProbeHealthAsync(shutdown);
        if (health is not null)
        {
            _log($"connection lost: cannot reach the task server at {_options.ServerUrl} ({health}). " +
                 "Verify the reverse tunnel / autossh service is up (agent-host --health-check) before assigning tasks.");
            return 4;
        }
        _log("preflight ok: task server reachable");

        // Register the runner identity first: the server's X-Client-Id boundary
        // rejects every write (lease, logs, artifacts, completion) from an
        // unregistered id with 401, so this must precede the lease acquire.
        var clientId = await _client.RegisterAsync(_options.RunnerName, "service", shutdown);
        _log($"registered runner identity '{_options.RunnerName}' as client '{clientId}'");
        await new DurableHandoffRecovery(_options, _client, _log).RecoverAllAsync(shutdown);

        _log($"acquiring lease for task '{taskKey}' as runner '{_options.RunnerId}' ({_options.RunnerName})");
        var acquire = await _client.AcquireLeaseAsync(new RunLeaseAcquireRequest(
            taskKey, _options.RunnerId, _options.RunnerName, _options.Hostname,
            Environment.ProcessId, _options.BackendName, _options.TtlSeconds,
            LeaseInstanceId: _client.RunnerInstanceId), shutdown);

        if (!acquire.Granted || acquire.Lease is null)
        {
            _log($"lease not granted: {acquire.Outcome} - {acquire.Message}");
            return 2;
        }

        var lease = acquire.Lease;
        _log($"lease {lease.LeaseId} granted, fencing token {lease.FencingToken}, expires {lease.ExpiresAt:o}");

        return await RunClaimedAsync(taskKey, lease, shutdown);
    }

    /// <summary>Runs a daemon-claimed task using the lease minted by the atomic claim endpoint.</summary>
    public async Task<int> RunClaimedAsync(
        string taskKey,
        RunLeaseInfoDto lease,
        CancellationToken shutdown,
        string? projectId = null,
        string? repositoryUrl = null,
        string? defaultBranch = null,
        string? taskKind = null,
        string? runId = null,
        string? leaseInstanceId = null,
        RunSpecDto? runSpec = null,
        CancellationToken daemonShutdown = default,
        // AGT-2870: the salvage a previous round of this card left behind. The
        // server sends it with the claim, so the worktree below starts on the
        // rescued work instead of on the integration branch.
        string? continuationBaseRef = null,
        string? continuationBaseSha = null)
    {
        var isProjectClone = !string.IsNullOrWhiteSpace(projectId);
        if (isProjectClone && string.IsNullOrWhiteSpace(repositoryUrl))
        {
            _log(
                $"remote-runner-project-not-remote-capable projectId={projectId ?? "unknown"} " +
                $"task={taskKey} reason=repository-url-not-configured");
            await ReleaseAsync(lease, CancellationToken.None);
            return 2;
        }

        _log($"running claimed task '{taskKey}' with lease {lease.LeaseId}, fencing token {lease.FencingToken}");

        var workspace = new GitWorkspace(
            _options,
            taskKey,
            _log,
            projectId,
            repositoryUrl,
            defaultBranch,
            isProjectClone,
            sourceRunAttemptId: runId ?? lease.AttemptId ?? lease.LeaseId,
            fencingToken: lease.FencingToken,
            continuationBaseRef: continuationBaseRef,
            continuationBaseSha: continuationBaseSha);
        var slot = _state.Create(
            taskKey, lease, workspace.RepoPath, runId, leaseInstanceId,
            projectId, repositoryUrl, defaultBranch, taskKind, runSpec);
        return await RunPersistedAsync(
            slot,
            workspace,
            shutdown,
            reattach: false,
            daemonShutdown);
    }

    /// <summary>Continue a positively verified detached process from durable host state.</summary>
    public async Task<int> ReattachAsync(
        PersistedRunnerSlot slot,
        CancellationToken stopRun,
        CancellationToken daemonShutdown = default)
    {
        _log($"reattaching task '{slot.TaskKey}' attempt {slot.AttemptId} pid={slot.ProcessId} worktree={slot.WorktreePath}");
        // Restore the recorded base SHA: this process never prepared the worktree,
        // and without it the completion would be assembled with no envelope trio
        // after every daemon restart.
        var workspace = WorkspaceFor(slot);
        return await RunPersistedAsync(
            slot,
            workspace,
            stopRun,
            reattach: true,
            daemonShutdown);
    }

    public async Task<bool> ReleaseDeadAsync(PersistedRunnerSlot slot, string reason)
    {
        _log($"releasing dead persisted attempt task={slot.TaskKey} attempt={slot.AttemptId}: {reason}");
        var authorityExhausted = string.Equals(
            slot.Phase,
            "authority-deadline-exhausted",
            StringComparison.Ordinal);
        // A slot whose worker died still owns the only copy of that attempt's
        // work. Salvage it under the attempt's own generation ref before the
        // lease goes: after the release the next claim can only quarantine it.
        var handoff = authorityExhausted
            ? LostWorkerHandoff.None
            : await SalvageLostWorkerAsync(slot, WorkspaceFor(slot), reason, shipper: null);
        var outcome = authorityExhausted
            ? "authority-deadline-exhausted"
            : LostWorkerRecoveryPolicy.ReleaseOutcome;
        if (await ReleaseWithRetryAsync(slot.Lease, outcome, handoff))
        {
            _state.Delete(slot);
            return true;
        }

        _log($"dead attempt state retained for release retry: {slot.TaskKey}");
        return false;
    }

    /// <summary>
    /// End the worker of an attempt an operator stopped, and compose the outcome
    /// the card will carry. The worker's process tree is its cgroup, so the
    /// cgroup kill is what a forking agent cannot escape; the reaper covers a
    /// host without a delegated subtree.
    /// </summary>
    private async Task<RunOutcome> StopRequestedAsync(
        PersistedRunnerSlot slot,
        GitWorkspace workspace,
        LogShipper shipper,
        RunStopDirectiveDto directive,
        Task heartbeatTask,
        bool epicPlanning)
    {
        await SafeAwait(heartbeatTask);
        DurableAgentProcess.Attach(slot).Kill();
        var killed = WorkerCgroup.ReleaseFor(slot.WorkerDirectory);
        if (!epicPlanning && Directory.Exists(workspace.RepoPath))
            await WorktreeProcessReaper.ReapAsync(workspace.RepoPath, _log, CancellationToken.None);
        var line =
            $"[runner] operator-stop attempt={slot.AttemptId} reason={directive.Reason} " +
            $"requestedBy={directive.RequestedBy ?? "unknown"} terminatedProcesses={killed}";
        _log(line);
        shipper.Add("system", line);
        try { await shipper.FlushAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"operator-stop evidence could not be shipped: {ex.Message}");
        }
        return new RunOutcome(
            RunOutcomeKind.Stopped,
            $"The run was stopped by an operator ({directive.Reason}).");
    }

    /// <summary>
    /// Preserve the stopped attempt's work on its generation-scoped salvage ref.
    /// A salvage failure must not swallow the stop itself: the card is handed
    /// back either way, with the retained worktree named in the journal.
    /// </summary>
    private async Task<WorktreeTeardownResult> SecureStoppedWorktreeAsync(
        PersistedRunnerSlot slot,
        GitWorkspace workspace)
    {
        if (!Directory.Exists(workspace.RepoPath)) return WorktreeTeardownResult.NoWork;
        try
        {
            return await workspace.TeardownAsync(
                RunOutcomeKind.Stopped.ToString(),
                slot.RunId ?? slot.Lease.AttemptId ?? slot.AttemptId,
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log(
                $"operator-stop-salvage-failed task={slot.TaskKey} attempt={slot.AttemptId} " +
                $"path={workspace.RepoPath} error={ex.Message}; worktree retained");
            return WorktreeTeardownResult.NoWork;
        }
    }

    /// <summary>
    /// Inventory whatever evidence the stopped run already wrote without
    /// allowing result-file I/O to block the code handoff. The empty manifest
    /// remains a valid immutable-envelope identity; the inventory failure is
    /// reported after delivery as a partial artifact outcome.
    /// </summary>
    private async Task<ArtifactTransferPlan> PrepareResultsSafeAsync(
        string taskKey,
        string attemptId,
        ArtifactTransferLimitsResponse limits,
        DurableRunOutbox? outbox)
    {
        try
        {
            return await PrepareResultsAsync(
                taskKey, attemptId, limits, outbox, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var issue = new ArtifactTransferIssue(
                "results/",
                0,
                $"result inventory failed ({OneLine(ex.Message)})",
                ArtifactTransferOutcomes.TransferFailed);
            var manifest = BuildArtifactManifest([]);
            outbox?.Enqueue("artifact-manifest", manifest.Json);
            _log($"result artifact inventory failed task={taskKey}; delivery will continue: {OneLine(ex.Message)}");
            return new ArtifactTransferPlan(limits, [], [issue], manifest);
        }
    }

    /// <summary>
    /// The workspace of a persisted slot, restored with the same generation
    /// identity the slot was claimed under. Both reattachment and lost-worker
    /// salvage need it, and both must keep publishing under the attempt's own
    /// fenced refs rather than an anonymous one.
    /// </summary>
    private GitWorkspace WorkspaceFor(PersistedRunnerSlot slot)
        => new(
            _options, slot.TaskKey, _log, slot.ProjectId, slot.RepositoryUrl, slot.DefaultBranch,
            restoredBaseSha: slot.BaseSha,
            sourceRunAttemptId: slot.RunId ?? slot.Lease.AttemptId ?? slot.AttemptId,
            fencingToken: slot.Lease.FencingToken);

    /// <summary>
    /// Preserve and describe what a lost detached worker left behind: the crash
    /// evidence it wrote, the counters of the cgroup it died in, and the salvage
    /// ref its work is published under. Never throws - a failed salvage retains
    /// the worktree for the next pickup's quarantine path, which is still better
    /// than blocking the release the card is waiting for.
    /// </summary>
    private async Task<LostWorkerHandoff> SalvageLostWorkerAsync(
        PersistedRunnerSlot slot,
        GitWorkspace workspace,
        string detail,
        LogShipper? shipper)
    {
        var attemptId = slot.RunId ?? slot.Lease.AttemptId ?? slot.AttemptId;
        var evidence = WorkerCrashEvidenceReader.Read(slot.WorkerDirectory);
        foreach (var line in evidence.Describe(attemptId, detail))
        {
            _log(line);
            shipper?.Add("system", line);
        }
        if (shipper is not null)
        {
            try { await shipper.FlushAsync(CancellationToken.None); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"worker-lost evidence could not be shipped: {ex.Message}");
            }
        }

        var action = LostWorkerRecoveryPolicy.Decide(
            Directory.Exists(workspace.RepoPath),
            readOnlyCheckout: string.Equals(slot.TaskKind, "epic", StringComparison.OrdinalIgnoreCase),
            hasFencedGeneration: true);
        if (action == LostWorkerRecoveryAction.None)
            return new LostWorkerHandoff(null, null, evidence.CrashLine);

        try
        {
            var teardown = await workspace.TeardownAsync(
                LostWorkerRecoveryPolicy.Outcome,
                attemptId,
                CancellationToken.None);
            var branch = teardown.Reconciliation?.RecoveryBranch ?? teardown.Branch;
            var sha = teardown.Reconciliation?.RecoveryCommitSha ?? teardown.ResultSha;
            if (string.IsNullOrWhiteSpace(branch) || string.IsNullOrWhiteSpace(sha))
            {
                _log(
                    $"worker-lost-salvage-empty task={slot.TaskKey} attempt={attemptId}; " +
                    "the worktree held no work to continue from");
                return new LostWorkerHandoff(null, null, evidence.CrashLine);
            }
            _log(
                $"worker-lost-salvaged task={slot.TaskKey} attempt={attemptId} " +
                $"fence={slot.Lease.FencingToken} ref=refs/heads/{branch} sha={sha}");
            return new LostWorkerHandoff(branch, sha, evidence.CrashLine);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log(
                $"worker-lost-salvage-failed task={slot.TaskKey} attempt={attemptId} " +
                $"path={workspace.RepoPath} error={ex.Message}; worktree retained");
            return new LostWorkerHandoff(null, null, evidence.CrashLine);
        }
    }

    /// <summary>
    /// Purges a deferred finalization whose delivery has meanwhile reached the
    /// Task Server on the durable plane. The outbox recovery pass owns that
    /// replay, so re-driving the worker here would journal and upload the same
    /// artifacts a second time under fresh sequences.
    /// </summary>
    public async Task<bool> ReleaseSettledAsync(PersistedRunnerSlot slot, string reason)
    {
        _log($"releasing settled persisted attempt task={slot.TaskKey} attempt={slot.AttemptId}: {reason}");
        if (await ReleaseWithRetryAsync(slot.Lease, "runner-finalization-settled"))
        {
            _state.Delete(slot);
            return true;
        }

        _log($"settled attempt state retained for release retry: {slot.TaskKey}");
        return false;
    }

    private async Task<int> RunPersistedAsync(
        PersistedRunnerSlot slot,
        GitWorkspace workspace,
        CancellationToken shutdown,
        bool reattach,
        CancellationToken daemonShutdown = default)
    {
        var taskKey = slot.TaskKey;
        var lease = slot.Lease;
        var inventoryRunId = slot.RunId ?? slot.AttemptId;
        using var inventoryRegistration = _inventory.Track(
            inventoryRunId,
            taskKey,
            workspace.RepoPath);
        if (slot.ProcessId is > 0)
            _inventory.AttachProcess(inventoryRunId, slot.ProcessId.Value);

        var outbox = _client.UsesDurableTaskServer
            ? DurableRunOutbox.Open(
                Path.Combine(_options.WorkDir, "outbox"),
                _client.OutboxAuthority(taskKey))
            : null;
        using var activeOutbox = outbox?.MarkActive();
        using var stopRun = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var authority = _client.UsesDurableTaskServer
            ? DurableLeaseAuthority.Open(
                slot.WorkerDirectory,
                lease.ExpiresAt,
                TimeSpan.FromSeconds(Math.Max(5, _options.HeartbeatSeconds)),
                initiallyConfirmed: !reattach)
            : null;
        var heartbeat = new LeaseHeartbeat(
            _client,
            _options,
            lease,
            _log,
            inventory: _inventory,
            authority: authority);
        using var heartbeatShutdown = CancellationTokenSource.CreateLinkedTokenSource(
            shutdown,
            daemonShutdown);
        var heartbeatTask = heartbeat.RunAsync(stopRun, heartbeatShutdown.Token);

        outbox?.Enqueue("status", JsonSerializer.Serialize(
            new { phase = "claimed", taskKey },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var shipper = new LogShipper(
            _client,
            taskKey,
            lease,
            _log,
            outbox,
            authority);
        var shipperTask = shipper.RunAsync(TimeSpan.FromSeconds(5), stopRun.Token);
        var artifactLimits = await _client.GetArtifactTransferLimitsAsync(taskKey, stopRun.Token);
        _log(
            $"result-artifact-budget task={taskKey} "
            + $"fileBytes={artifactLimits.MaxFileBytes} totalBytes={artifactLimits.MaxTotalBytes} "
            + $"requestBytes={artifactLimits.MaxRequestBodyBytes}");

        var outcome = new RunOutcome(RunOutcomeKind.Unknown, "Runner ended before a terminal outcome was recorded.");
        var outcomeDecision = ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            lease.AttemptId ?? lease.LeaseId,
            ExecutionAttemptKind.Coding,
            DurableOutputState: DurableOutputState.Missing));
        var epicPlanning = string.Equals(slot.TaskKind, "epic", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<string> outputLines = [];
        var sourceMutated = false;
        var handedBack = false;
        var teardownAttempted = false;
        var releaseOnly = false;
        var daemonHandedOff = false;
        var lostWorker = LostWorkerHandoff.None;
        // AGT-2869: a finalization that could not reach a restarting Task
        // Server is deferred, not abandoned. The slot stays persisted in
        // "finalizing" and the daemon's poll loop re-drives this exact attempt.
        var finalizationDeferred = false;
        var finalizationRetries = slot.Finalization?.Attempts ?? 0;
        var securedTeardown = slot.Finalization?.Teardown;
        DurableArtifactManifest? artifactManifest = null;
        ArtifactTransferPlan? artifactPlan = null;
        if (finalizationRetries > 0)
        {
            // "retry=N" counts the finalization attempts that already failed,
            // so the in-flight line, the journal, and the completion all quote
            // the same number.
            var pending = slot.Finalization!;
            _log(
                $"coding-finalization-redrive task={taskKey} attempt={slot.AttemptId} "
                + $"retry={finalizationRetries} pendingSince={pending.PendingSinceUtc:o} "
                + $"lastReason={pending.LastReason}");
            shipper.Add(
                "system",
                $"[runner] finalization-retry {finalizationRetries} "
                + $"pendingSince={pending.PendingSinceUtc:O} lastReason={pending.LastReason}");
        }
        try
        {
            var execution = reattach
                ? await AwaitDetachedAsync(
                    slot,
                    workspace,
                    shipper,
                    outbox,
                    stopRun.Token,
                    daemonShutdown,
                    operatorStopRequested: () => heartbeat.StopRequest is not null)
                : await ExecuteAsync(
                    slot,
                    workspace,
                    shipper,
                    outbox,
                    artifactLimits,
                    stopRun,
                    shutdown,
                    daemonShutdown,
                    epicPlanning,
                    operatorStopRequested: () => heartbeat.StopRequest is not null);
            outcome = execution.Outcome;
            outcomeDecision = execution.Decision;
            outputLines = execution.OutputLines;
            await shipper.FlushAsync(stopRun.Token);
            NeedsInputArtifactWriter.Write(
                ResultsDir(taskKey),
                outcome,
                lease.AttemptId ?? lease.LeaseId,
                workspace.WorkBranch);
            artifactPlan = await PrepareResultsSafeAsync(
                taskKey, outbox?.Authority.RunId ?? lease.AttemptId ?? lease.LeaseId,
                artifactLimits, outbox);
            artifactManifest = artifactPlan.Manifest;

            if (heartbeat.LeaseLost)
            {
                _log("lease was lost mid-run; skipping completion so the takeover holder owns the outcome");
                return 3;
            }

            teardownAttempted = true;
            WorktreeTeardownResult teardown;
            ResultHandoffAck? handoffAcknowledgement = null;
            string? envelopeDigest = null;
            if (epicPlanning)
            {
                // Epic planning is source-read-only: verify no mutation and
                // discard the detached checkout without salvage or a coding
                // branch. The mutation verdict rides the additive completion
                // fields; develop's salvage protocol stays untouched.
                sourceMutated = await workspace.TeardownReadOnlyAsync(CancellationToken.None);
                teardown = WorktreeTeardownResult.NoWork;
            }
            else if (outbox is not null)
            {
                teardown = securedTeardown ?? await SecureForHandoffWithRetryAsync(
                    taskKey,
                    workspace,
                    outcome,
                    outbox,
                    stopRun.Token);
                securedTeardown = teardown;
                var dependencyIdentities = await workspace.ReadDependencyIdentitiesAsync(shutdown);
                var repositoryId = !string.IsNullOrWhiteSpace(slot.ProjectId)
                    ? slot.ProjectId
                    : throw new InvalidOperationException(
                        "Durable result handoff requires the Task Server repository identity.");
                var envelope = new ImmutableResultEnvelope(
                    repositoryId,
                    outbox.Authority.RunId,
                    workspace.BaseSha
                    ?? throw new InvalidOperationException("Durable result handoff has no recorded base SHA."),
                    teardown.ResultSha
                    ?? throw new InvalidOperationException("Durable result handoff has no result SHA."),
                    teardown.ImmutableResultRef,
                    null,
                    artifactManifest.Digest,
                    dependencyIdentities.Submodules,
                    dependencyIdentities.LfsObjects,
                    workspace.RepositoryUrl);
                envelopeDigest = ResultEnvelopeDigest.Compute(envelope);
                outbox.Enqueue("git-facts", JsonSerializer.Serialize(
                    new DurableGitFactsPayload(
                        repositoryId,
                        envelope.BaseSha,
                        envelope.ResultSha,
                        teardown.ImmutableResultRef,
                        teardown.Reconciliation,
                        teardown.Reconciliation?.Kind == "divergent"
                            ? "inspect-preserved-divergent-tips"
                            : null),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                var finalItem = outbox.Enqueue(
                    "final-result",
                    JsonSerializer.Serialize(
                        envelope,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                await authority!.WaitForConfirmedAsync(stopRun.Token);
                await ReplayBeforeAsync(
                    outbox,
                    finalItem.Sequence,
                    stopRun.Token);
                handoffAcknowledgement = await _client.AcknowledgeResultHandoffAsync(
                    outbox.Authority,
                    finalItem,
                    envelope,
                    stopRun.Token);
                outbox.RecordHandoffAcknowledgement(handoffAcknowledgement);
                await ReportOutboxSafeAsync(outbox, stopRun.Token);
                await workspace.TeardownAfterHandoffAsync(
                    teardown,
                    outbox.HandoffAcknowledgement
                    ?? throw new InvalidDataException(
                        "Durable handoff acknowledgement was not persisted."),
                    outbox.Authority.RunId,
                    envelopeDigest,
                    CancellationToken.None);
            }
            else
            {
                // A re-driven finalization finds the worktree already removed by
                // the attempt that failed afterwards. Reusing that attempt's
                // secured delivery keeps the retry from recording a completion
                // without the refs it has already pushed.
                teardown = securedTeardown ?? await workspace.TeardownAsync(
                    outcome.Kind.ToString(),
                    lease.AttemptId,
                    CancellationToken.None);
                securedTeardown = teardown;
            }
            outcomeDecision = WithDurableOutput(outcomeDecision, teardown);
            if (finalizationRetries > 0)
            {
                // Delivery evidence: the card must say that this completion is
                // the late one, not a second run.
                var delivered =
                    $"[runner] finalization-delivered retries={finalizationRetries} "
                    + $"pendingSince={slot.Finalization!.PendingSinceUtc:O}";
                shipper.Add("system", delivered);
                await shipper.FlushAsync(stopRun.Token);
            }
            if (outbox is not null)
            {
                // The isolated checkout has now either been removed after a
                // Task-Server-acknowledged immutable handoff or torn down by the
                // read-only path. This is the host-side containment work, so it
                // must be acknowledged before fenced run completion.
                await _client.CompleteHostPostProcessingAsync(
                    taskKey,
                    envelopeDigest,
                    stopRun.Token);
            }
            if (outbox is not null && !epicPlanning)
            {
                // AGT-2820: the durable plane completes through the outbox and
                // never reaches CompleteOrReconcileAsync, so the missing-sentinel
                // incident is decided here as well. Same pure policy and the same
                // admission conditions; only the transport differs. Without this
                // the incident existed on the legacy plane alone, and the plane
                // the fleet actually runs on reported a bare
                // ProtocolInconclusive with no incident on the card.
                var durableCompletion = BuildDurableCompletion(
                    outcome,
                    outcomeDecision,
                    teardown,
                    workspace.BaseSha,
                    envelopeDigest,
                    _options.Hostname);
                if (durableCompletion.GateItems is { Count: > 0 })
                {
                    _log(
                        $"remote-runner-missing-terminal-sentinel task={taskKey} " +
                        $"plane=durable ref={teardown.DeliveryProof!.Ref} " +
                        $"sha={teardown.DeliveryProof.CommitSha}; routing to review as an incident");
                }
                var completion = outbox.Enqueue(
                    "completion",
                    JsonSerializer.Serialize(
                        durableCompletion,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                await authority!.WaitForConfirmedAsync(stopRun.Token);
                await _client.SendOutboxItemAsync(
                    outbox.Authority,
                    completion,
                    stopRun.Token);
                outbox.Acknowledge(completion.Sequence);
                outbox.RecordHandoffState("completed", envelopeDigest);
                await ReportOutboxSafeAsync(outbox, stopRun.Token);
            }
            else
            {
                await CompleteOrReconcileAsync(
                    taskKey,
                    lease,
                    outcome,
                    outcomeDecision,
                    teardown,
                    workspace.RepositoryUrl,
                    workspace.BaseSha,
                    workspace.IntegrationBranchRef,
                    artifactManifest?.Digest,
                    outputLines,
                    sourceMutated,
                    shutdown);
            }
            handedBack = true;
            _log(
                $"task '{taskKey}' handed back to the local board: {outcome.Kind}"
                + (finalizationRetries > 0
                    ? $"; finalizationRetries={finalizationRetries}"
                    : string.Empty));
            await TransferResultsSafeAsync(taskKey, lease, artifactPlan, shipper, outbox);
            return outcome.Kind is RunOutcomeKind.Done or RunOutcomeKind.NoOp ? 0 : 1;
        }
        catch (DetachedWorkerLostException ex)
        {
            releaseOnly = true;
            _log($"detached worker lost; attempt will be released to Ready: {ex.Message}");
            // This process still holds the attempt identity and the fence, so
            // the work is published under the attempt's own salvage ref and
            // named on the release. Ordering is load-bearing: after the release
            // the card can be claimed again, and the next claim would only be
            // able to quarantine whatever is still on disk.
            lostWorker = await SalvageLostWorkerAsync(slot, workspace, ex.Message, shipper);
            teardownAttempted = true;
            return 3;
        }
        catch (RemoteClaimPreparationException ex)
        {
            outcome = new RunOutcome(RunOutcomeKind.EnvironmentFailure, ex.Message);
            shipper.Add("system", $"[runner] remote-claim-environment-failed: {ex.Message}");
            await shipper.FlushAsync(CancellationToken.None);
            if (!heartbeat.LeaseLost)
            {
                teardownAttempted = true;
                var failedTeardown = WorktreeTeardownResult.NoWork;
                if (Directory.Exists(workspace.RepoPath))
                {
                    if (epicPlanning)
                        sourceMutated = await workspace.TeardownReadOnlyAsync(CancellationToken.None);
                    else
                        failedTeardown = await workspace.TeardownAsync(
                            outcome.Kind.ToString(),
                            lease.AttemptId,
                            CancellationToken.None);
                }
                outcomeDecision = WithDurableOutput(outcomeDecision, failedTeardown);
                await _client.CompleteHostPostProcessingAsync(
                    taskKey,
                    evidenceHash: null,
                    CancellationToken.None);
                await CompleteAsync(
                    taskKey,
                    lease,
                    outcome,
                    outcomeDecision,
                    failedTeardown,
                    workspace.RepositoryUrl,
                    baseSha: null,
                    workspace.IntegrationBranchRef,
                    artifactManifestDigest: null,
                    outputLines,
                    sourceMutated: false,
                    CancellationToken.None);
                handedBack = true;
            }
            return 1;
        }
        catch (WorktreeSalvageException ex)
        {
            if (outbox is not null)
            {
                outbox.RecordHandoffState("transfer-recovery");
                await ReportOutboxSafeAsync(outbox, CancellationToken.None);
                _log($"result transfer remains recoverable without a new coding attempt: {ex.Message}");
                return 4;
            }
            await ReportUnsecuredWorktreeAsync(taskKey, lease, ex);
            handedBack = true;
            return 1;
        }
        catch (Exception ex) when (
            RemoteRunnerDaemon.IsTransientServerFault(ex)
            && !shutdown.IsCancellationRequested
            && !daemonShutdown.IsCancellationRequested
            && heartbeat.StopRequest is null
            && !heartbeat.LeaseLost)
        {
            var latest = _state.LoadAll().FirstOrDefault(item =>
                             string.Equals(item.AttemptId, slot.AttemptId, StringComparison.Ordinal))
                         ?? slot;
            var durableResultReady = DurableAgentProcess
                .InspectForReattach(latest)
                .Result is not null;
            if (!FinalizationRetryPolicy.CanDefer(
                    latest.FinalizationStage,
                    durableResultReady))
            {
                // This transport fault happened before the worker established
                // the durable result boundary. It is not safe to invent a
                // finalization retry that the poll loop can never deliver.
                throw;
            }

            // AGT-2869: the Task Server is restarting (connection refused or
            // reset, a prematurely ended response, 502/503/504). The worker's
            // result is on disk and this attempt's authority is persisted, so
            // the slot stays in "finalizing" and the daemon's own poll loop
            // re-drives the very same idempotent steps once the server answers
            // again. Releasing or tearing down here would strand the delivery.
            finalizationDeferred = true;
            var reason = DescribeTransportFault(ex);
            var pending = FinalizationRetryPolicy.Schedule(
                latest.Finalization ?? slot.Finalization,
                reason,
                securedTeardown,
                DateTime.UtcNow);
            _state.Save(latest with
            {
                Phase = FinalizationRetryPolicy.Phase,
                Finalization = pending,
            });
            _log(
                $"coding-finalization-deferred task={taskKey} attempt={slot.AttemptId} "
                + $"retry={pending.Attempts} pendingSince={pending.PendingSinceUtc:o} "
                + $"nextAttempt={pending.NextAttemptAtUtc:o} reason={reason}");
            return 5;
        }
        catch (OperationCanceledException) when (
            heartbeat.StopRequest is not null && !heartbeat.LeaseLost)
        {
            // An operator stop is the one cancellation this runner still owns
            // the lease for, so it ends the attempt itself: kill the worker's
            // process tree, salvage what the agent already wrote, and hand back
            // 'Stopped' with the salvage named. A queued follow-up then starts
            // the next round on that work instead of on the integration branch.
            outcome = await StopRequestedAsync(
                slot,
                workspace,
                shipper,
                heartbeat.StopRequest!,
                heartbeatTask,
                epicPlanning);
            teardownAttempted = true;
            var stopTeardown = epicPlanning
                ? WorktreeTeardownResult.NoWork
                : await SecureStoppedWorktreeAsync(slot, workspace);
            outcomeDecision = WithDurableOutput(outcomeDecision, stopTeardown);
            artifactPlan = await PrepareResultsSafeAsync(
                taskKey, outbox?.Authority.RunId ?? lease.AttemptId ?? lease.LeaseId,
                artifactLimits, outbox);
            artifactManifest = artifactPlan?.Manifest;
            await CompleteAsync(
                taskKey,
                lease,
                outcome,
                outcomeDecision,
                stopTeardown,
                workspace.RepositoryUrl,
                workspace.BaseSha,
                workspace.IntegrationBranchRef,
                artifactManifest?.Digest,
                outputLines,
                sourceMutated,
                CancellationToken.None);
            handedBack = true;
            await TransferResultsSafeAsync(taskKey, lease, artifactPlan, shipper, outbox);
            _log($"task '{taskKey}' handed back after an operator stop: {outcome.Kind}");
            return 0;
        }
        catch (OperationCanceledException) when (
            daemonShutdown.IsCancellationRequested
            && !heartbeat.LeaseLost)
        {
            await SafeAwait(heartbeatTask);
            daemonHandedOff = await HandOffForDaemonRestartAsync(slot, authority);
            if (!daemonHandedOff)
            {
                releaseOnly = true;
                _log(
                    $"coding daemon handoff could not prove a live worker task={taskKey} " +
                    $"attempt={slot.AttemptId}; releasing the persisted slot");
                return 3;
            }
            return 0;
        }
        catch (OperationCanceledException) when (heartbeat.LeaseLost)
        {
            var phase = authority?.Snapshot.Detail?.Contains(
                "deadline exhausted",
                StringComparison.OrdinalIgnoreCase) == true
                ? "authority-deadline-exhausted"
                : "lease-authority-rejected";
            var latestSlot = _state.LoadAll().FirstOrDefault(item =>
                                 string.Equals(
                                     item.AttemptId,
                                     slot.AttemptId,
                                     StringComparison.Ordinal))
                             ?? slot;
            _state.Save(latestSlot with { Phase = phase });
            if (outbox is not null
                && !outbox.Items.Any(item => item.Kind == "terminal"))
            {
                outbox.Enqueue(
                    "terminal",
                    JsonSerializer.Serialize(
                        new DurableTerminalPayload(
                            "LeaseLoss",
                            phase == "authority-deadline-exhausted"
                                ? "Local autonomy deadline exhausted; contained process generation death was proven."
                                : "Task Server rejected the fenced lease; contained process generation death was proven."),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                outbox.RecordHandoffState(phase);
            }
            _log(
                $"lease-loss terminal journaled task={taskKey} phase={phase}; " +
                "no replacement starts from this execution path");
            return 3;
        }
        finally
        {
            stopRun.Cancel();
            await SafeAwait(heartbeatTask);
            // A generation whose authority is known dead may retain useful
            // local content, but it must not publish a delivery candidate.
            // Preserve that content under a generation-specific quarantine ref.
            if (!daemonHandedOff
                && !finalizationDeferred
                && heartbeat.LeaseLost
                && !epicPlanning
                && Directory.Exists(workspace.RepoPath))
            {
                try
                {
                    teardownAttempted = true;
                    await workspace.TeardownToQuarantineAsync(
                        outcome.Kind.ToString(),
                        slot.RunId ?? lease.AttemptId ?? lease.LeaseId,
                        CancellationToken.None);
                    _log(
                        $"lease-loss worktree quarantined task={taskKey} " +
                        $"attempt={slot.RunId ?? lease.AttemptId ?? lease.LeaseId} " +
                        $"fence={lease.FencingToken}");
                }
                catch (Exception ex)
                {
                    _log(
                        $"lease-loss quarantine failed; retained worktree task={taskKey} " +
                        $"path={workspace.RepoPath} error={ex.Message}");
                }
            }
            // This path covers shutdown, cancellation, quota death, and any
            // exception before the normal completion handoff. Salvage uses an
            // independent token because SIGINT has already cancelled the run.
            if (!daemonHandedOff
                && !finalizationDeferred
                && outbox is null
                && !teardownAttempted
                && Directory.Exists(workspace.RepoPath))
            {
                try
                {
                    // Every failed legacy handoff still crosses the same
                    // generation-scoped salvage boundary as a normal delivery.
                    // An artifact fault must never leave the only code copy as
                    // uncommitted files in a retained checkout.
                    teardownAttempted = true;
                    var teardown = epicPlanning
                        ? WorktreeTeardownResult.NoWork
                        : await workspace.TeardownAsync(
                            outcome.Kind.ToString(),
                            lease.AttemptId,
                            CancellationToken.None);
                    if (epicPlanning)
                        sourceMutated = await workspace.TeardownReadOnlyAsync(CancellationToken.None);
                    // A checkout is always secured before release, but a fault
                    // before the worker produced a result is still the legacy
                    // release path rather than an invented completion. Once a
                    // manifest exists, delivery had begun and the secured ref
                    // must be reported even if a later handoff step failed.
                    if (!handedBack
                        && !heartbeat.LeaseLost
                        && !releaseOnly
                        && artifactManifest is not null)
                    {
                        outcomeDecision = WithDurableOutput(outcomeDecision, teardown);
                        await CompleteOrReconcileAsync(
                            taskKey,
                            lease,
                            outcome,
                            outcomeDecision,
                            teardown,
                            workspace.RepositoryUrl,
                            workspace.BaseSha,
                            workspace.IntegrationBranchRef,
                            artifactManifest?.Digest,
                            outputLines,
                            sourceMutated,
                            CancellationToken.None);
                        handedBack = true;
                    }
                }
                catch (WorktreeSalvageException ex)
                {
                    // Even a lost lease cannot hide an unsecured host-local
                    // checkout. The gate is safety evidence, not an ownership
                    // claim over the run's successful outcome.
                    if (!handedBack)
                    {
                        await ReportUnsecuredWorktreeAsync(taskKey, lease, ex);
                        handedBack = true;
                    }
                }
                catch (Exception ex)
                {
                    _log($"task worktree teardown failed; worktree retained at {workspace.RepoPath}: {ex.Message}");
                }
            }

            // Completion is fenced by the live lease, so release only after the
            // normal or fail-closed handoff has finished.
            if (!daemonHandedOff && !finalizationDeferred && (outbox is null || handedBack))
            {
                var released = releaseOnly
                    ? await ReleaseWithRetryAsync(
                        lease,
                        lostWorker.HasSalvage
                            ? LostWorkerRecoveryPolicy.ReleaseOutcome
                            : "runner-process-missing",
                        lostWorker)
                    : await ReleaseAsync(lease, CancellationToken.None);
                if (released)
                    _state.Delete(slot);
            }
        }
    }

    private async Task<RemoteExecutionResult> ExecuteAsync(
        PersistedRunnerSlot slot, GitWorkspace workspace, LogShipper shipper,
        DurableRunOutbox? outbox, ArtifactTransferLimitsResponse artifactLimits,
        CancellationTokenSource stopRun,
        CancellationToken shutdown, CancellationToken daemonShutdown, bool epicPlanning,
        Func<bool> operatorStopRequested)
    {
        var taskKey = slot.TaskKey;
        var lease = slot.Lease;
        var resultsDir = ResultsDir(taskKey);
        if (Directory.Exists(resultsDir)) Directory.Delete(resultsDir, recursive: true);
        Directory.CreateDirectory(resultsDir);
        Func<CancellationToken, Task<string>> prepare = epicPlanning
            ? workspace.PrepareReadOnlyAsync
            : workspace.PrepareAsync;
        string branch;
        try
        {
            branch = await RetryEnvironmentPreparationAsync(
                prepare,
                _log,
                shutdown);
        }
        catch (WorktreeSalvageException)
        {
            throw;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (RemoteEnvironmentPreparationException ex)
        {
            throw new RemoteClaimPreparationException(
                DescribePreparationFailure(ex.InnerException ?? ex),
                ex);
        }
        catch (Exception ex)
        {
            throw new RemoteClaimPreparationException(DescribePreparationFailure(ex), ex);
        }
        shipper.Add("system", $"[runner] working tree ready on branch '{branch}'");
        var projectPreparation = epicPlanning
            ? ProjectPreparationResult.NotConfigured()
            : await ProjectPreparationExecutor.RunAsync(
                workspace.RepoPath,
                workspace.PreparationCachePath,
                Path.Combine(resultsDir, ProjectPreparationPaths.ManifestFileName),
                workspace.BaseSha,
                message => shipper.Add("system", "[runner] " + message),
                TimeSpan.FromMinutes(20),
                shutdown).ConfigureAwait(false);
        if (projectPreparation.Configured && !projectPreparation.Succeeded)
        {
            throw new RemoteClaimPreparationException(
                $"Repository preparation failed ({projectPreparation.FailureSignature ?? "unknown"}): " +
                (projectPreparation.FailureReason ?? "prepare command failed"),
                new InvalidOperationException(projectPreparation.FailureReason ?? "prepare command failed"));
        }
        shipper.Add("system", projectPreparation.Configured
            ? $"[runner] project preparation ready; manifest={ProjectPreparationPaths.ManifestFileName}; cacheHit={projectPreparation.CacheHit}"
            : "[runner] repository has no .agent-studio/project.yml; compatibility preparation remains active");
        if (outbox is not null && !epicPlanning)
        {
            outbox.Enqueue(
                "run-context",
                JsonSerializer.Serialize(
                    new DurableRunContextPayload(
                        slot.ProjectId
                        ?? throw new InvalidOperationException(
                            "Durable coding execution requires a repository identity."),
                        workspace.RepositoryUrl,
                        slot.DefaultBranch,
                        workspace.BaseSha
                        ?? throw new InvalidOperationException(
                            "Durable coding execution has no base SHA.")),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        if (workspace.PickupReconciliation is { } recovery)
        {
            shipper.Add("system",
                $"[runner] salvage-reconciliation kind={recovery.Kind} " +
                $"canonicalRef=refs/heads/{recovery.CanonicalBranch} canonicalSha={recovery.CanonicalCommitSha} " +
                $"localSha={recovery.LocalCommitSha} " +
                $"recoveryRef={(recovery.RecoveryBranch is null ? "none" : $"refs/heads/{recovery.RecoveryBranch}")} " +
                $"recoverySha={recovery.RecoveryCommitSha ?? "none"} " +
                $"authoritativeBaseRef=refs/heads/{recovery.AuthoritativeBaseBranch} " +
                $"authoritativeBaseSha={recovery.AuthoritativeBaseSha}");
        }

        var runSpec = slot.RunSpec;
        string prompt;
        if (epicPlanning)
        {
            var planning = await _client.GetEpicPlanningPromptAsync(new RemoteEpicPlanningPromptRequest(
                taskKey, lease.LeaseId, lease.FencingToken, _options.RunnerId, workspace.RepoPath), shutdown)
                ?? throw new InvalidOperationException("Server returned no Epic planning prompt.");
            prompt = planning.Prompt;
            shipper.Add("system", $"[runner] server-rendered Epic decomposition prompt; cli={planning.CliType ?? "default"} model={planning.Model ?? "default"} thinking={planning.ThinkingLevel ?? "default"}");
            // T0b: the planning endpoint has always answered with the Epic's CLI
            // selection and the runner has always thrown it away ("logged only").
            // The claim spec resolves the same source and additionally validates
            // the reasoning rung against the model, so it wins where it speaks;
            // the planning response fills whatever it leaves open, which is what a
            // server without T0b sends.
            runSpec = new RunSpecDto(
                runSpec?.CliType ?? planning.CliType,
                runSpec?.Model ?? planning.Model,
                runSpec?.ThinkingLevel ?? planning.ThinkingLevel,
                runSpec?.PermissionMode,
                runSpec?.ContextMode);
        }
        else
        {
            var taskPrompt = await _client.ReadTaskFileAsync(taskKey, "prompt.md", shutdown)
                             ?? throw new InvalidOperationException($"Task '{taskKey}' has no prompt.md to run.");
            prompt = RemoteRunPrompt.Build(
                taskPrompt,
                runSpec?.ModeFraming,
                ResultsDir(taskKey),
                artifactLimits);
            shipper.Add("system", string.IsNullOrWhiteSpace(runSpec?.ModeFraming)
                ? "[runner] results-dir context + remote-completion-protocol appended to task prompt"
                : "[runner] server-composed mode framing + results-dir context + remote-completion-protocol appended to task prompt");
        }

        // T0b proof line: which CLI, model and reasoning level this run actually
        // starts with, and whether that came from the card's spec or from the
        // host's RUNNER_CLI_* fallback. This is the line the migration's operating
        // evidence is filtered on, so it is written to the journal as well as to
        // the task's shipped log.
        var invocation = AgentCliProcess.Resolve(_options, runSpec);
        var specLine =
            $"[runner] spec cli={invocation.CliType} model={invocation.Model ?? "<cli-default>"} " +
            $"thinking={invocation.ThinkingLevel ?? "<cli-default>"} " +
            $"permission={runSpec?.PermissionMode ?? "<host-config>"} " +
            $"context={runSpec?.ContextMode ?? "<host-config>"} " +
            $"source={invocation.Source}" +
            (invocation.Note is null ? "" : $" note={invocation.Note}");
        _log(specLine);
        shipper.Add("system", specLine);
        // Plan §4 (Beobachtbarkeit): the engine line is the proof of which
        // execution path a run took and the filter for the T3 operating
        // evidence. The legacy engine keeps its historical spawning line.
        var engineLine = _options.ExecEngine == RunnerOptions.ExecEngineCar
            ? $"[runner] engine=car cli={invocation.CliType} model={invocation.Model ?? "<cli-default>"} " +
              $"thinking={invocation.ThinkingLevel ?? "<cli-default>"} " +
              $"permission={CodingAgentRunner.Model.CliPermissionModes.Normalize(runSpec?.PermissionMode)} " +
              $"context={CodingAgentRunner.Model.CliContextModes.Normalize(runSpec?.ContextMode)}"
            : $"[runner] spawning {invocation.FileName} {string.Join(' ', invocation.Arguments)}";
        _log(engineLine);
        shipper.Add("system", engineLine);
        slot = _state.Save(slot with
        {
            WorktreePath = workspace.RepoPath,
            // Only this process observed the prepared checkout's start commit; a
            // replacement daemon reattaching to the detached worker reads it back
            // from here to complete with a full Result-Envelope.
            BaseSha = workspace.BaseSha ?? slot.BaseSha,
            // Persist the spec this run actually starts with (an Epic run refines
            // it from the planning response), so a same-session resume or a
            // reattaching daemon relaunches the same CLI selection.
            RunSpec = runSpec,
            Phase = "launching",
        });
        DurableAgentProcess process;
        try
        {
            process = DurableAgentProcess.Start(
                _options, slot.WorkerDirectory, workspace.RepoPath, prompt, resultsDir,
                runSpec: runSpec,
                runId: slot.AttemptId,
                cleanContextKey: taskKey,
                // The agent runs this repository's own build, test and lint
                // commands. Without the preparation's cache binding its first
                // `--no-restore` build resolves against a package folder the
                // prepare restore never wrote to (TE-52).
                environment: projectPreparation.Environment,
                log: _log);
        }
        catch (Exception ex)
        {
            throw new RemoteClaimPreparationException(DescribePreparationFailure(ex), ex);
        }
        slot = _state.Save(slot with
        {
            ProcessId = process.ProcessId,
            ProcessStartedAtUtc = process.ProcessStartedAtUtc,
            WorktreePath = workspace.RepoPath,
            Phase = "running",
        });
        _inventory.AttachProcess(slot.RunId ?? slot.AttemptId, process.ProcessId);
        _log($"detached worker started task={taskKey} pid={process.ProcessId} attempt={slot.AttemptId}");
        var executed = await AwaitDetachedAsync(
            slot,
            workspace,
            shipper,
            outbox,
            stopRun.Token,
            daemonShutdown,
            operatorStopRequested: operatorStopRequested);
        // Only a terminal result proves that no command of this run will read the
        // per-run cache folders again. A daemon shutdown leaves the detached
        // worker running, so its folder stays and is reclaimed by age instead:
        // the handoff path throws out of the await above and skips this release.
        ProjectPreparationExecutor.ReleaseRunRoot(projectPreparation);
        return executed;
    }

    private async Task<RemoteExecutionResult> AwaitDetachedAsync(
        PersistedRunnerSlot slot,
        GitWorkspace workspace,
        LogShipper shipper,
        DurableRunOutbox? outbox,
        CancellationToken stopRun,
        CancellationToken daemonShutdown = default,
        int sameSessionResumeAttempts = 0,
        Func<bool>? operatorStopRequested = null)
    {
        var process = DurableAgentProcess.Attach(slot);
        var activeInvocation = AgentCliProcess.Resolve(_options, slot.RunSpec);
        ProviderAuthProbe.Shared.RecordRunStarted(activeInvocation.FileName);
        var providerRunRecorded = true;
        var sequence = slot.LastOutputSequence;
        using var waitStop = CancellationTokenSource.CreateLinkedTokenSource(
            stopRun,
            daemonShutdown);
        try
        {
            while (true)
            {
                daemonShutdown.ThrowIfCancellationRequested();
                var lines = process.ReadAfter(sequence);
                foreach (var line in lines)
                {
                    shipper.Add(line.Stream, line.Text);
                    sequence = Math.Max(sequence, line.Sequence);
                }
                if (lines.Count > 0 || shipper.PendingCount > 0)
                {
                    var flushed = await shipper.FlushAsync(stopRun);
                    if (outbox is not null || flushed)
                        slot = _state.Save(slot with { LastOutputSequence = sequence });
                }

                var observation = DurableAgentProcess.InspectForReattach(slot);
                if (observation.Result is { } result)
                {
                    slot = _state.Save(slot with
                    {
                        Phase = FinalizationRetryPolicy.Phase,
                        FinalizationStage = FinalizationRetryPolicy.ResultReadyStage,
                        LastOutputSequence = sequence,
                    });
                    ProviderAuthProbe.Shared.RecordRunCompleted(activeInvocation.FileName);
                    providerRunRecorded = false;
                    ReportWorkerEnvelope(slot, shipper);
                    var processResult = ProcessResultFrom(result);
                    var invocation = AgentCliProcess.Resolve(_options, slot.RunSpec);
                    var classified = result.TimedOut
                        ? ClassifyTimedOutResult(slot.Lease, workspace, result, sameSessionResumeAttempts)
                        : ClassifyProcessResult(
                            slot.Lease,
                            workspace,
                            processResult,
                            result.LaunchFailed,
                            sameSessionResumeAttempts,
                            invocation);
                    var providerAccess = ProviderAccessClassifier.Classify(
                        processResult.ExitCode,
                        processResult.StdOut,
                        processResult.StdErr);
                    var providerAuth = RecordProviderProcessResult(
                        ProviderAuthProbe.Shared,
                        invocation.FileName,
                        processResult,
                        classified.Decision.RawFacts,
                        evidenceId: slot.RunId ?? slot.AttemptId,
                        stopDirectiveRecorded: operatorStopRequested?.Invoke() == true,
                        daemonShutdownRecorded: daemonShutdown.IsCancellationRequested);
                    if (providerAccess.Kind == ProviderAccessEvidenceKind.AuthenticationFailure)
                    {
                        var provider = invocation.CliType;
                        var claimId = outbox?.Authority.RunId;
                        var diagnostic = result.StdErr
                            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                            .LastOrDefault()
                            ?? $"{provider} exited {result.ExitCode} with an authentication failure";
                        // Diagnosis only: the run's own classification and fenced
                        // completion below must survive a server that rejects or
                        // does not mount the capability route.
                        await CapabilityFailureReporter.TryReportAsync(
                            _client,
                            _log,
                            CapabilityProtocol.ProviderAuthentication(provider),
                            "ProviderUnauthorized",
                            diagnostic.Length <= 500 ? diagnostic : diagnostic[..500],
                            $"provider-auth:{claimId ?? slot.Lease.LeaseId}:{slot.Lease.FencingToken}",
                            "run",
                            claimId,
                            slot.Lease.FencingToken,
                            stopRun);
                        shipper.Add(
                            "system",
                            $"[runner] capability-failure capability={CapabilityProtocol.ProviderAuthentication(provider)} classification=ProviderUnauthorized");
                    }
                    else if (providerAccess.Kind == ProviderAccessEvidenceKind.RateLimited
                             && providerAuth.Status == ProviderAuthProbe.Limited)
                    {
                        shipper.Add(
                            "system",
                            $"[runner] provider-auth state=limited provider={invocation.CliType} until={providerAuth!.LimitedUntil:o}; claims wait for provider recovery, not sign-in");
                    }
                    else if (providerAccess.Kind == ProviderAccessEvidenceKind.TransientFailure)
                    {
                        shipper.Add(
                            "system",
                            $"[runner] provider-auth state=retrying provider={invocation.CliType}; last-good capability retained");
                    }
                    else if (providerAccess.Kind == ProviderAccessEvidenceKind.RequestRejected)
                    {
                        // A model request refusal proves neither logout nor a
                        // transient auth failure. Leave provider-auth capability
                        // state exactly as it was and let the typed outcome drive
                        // a model-specific continuation.
                        shipper.Add(
                            "system",
                            $"[runner] provider rejected model request provider={invocation.CliType}; provider-auth capability unchanged");
                    }
                    if (classified.Decision.RecoveryAction == ExecutionRecoveryAction.ResumeSameSession
                        && sameSessionResumeAttempts < ExecutionOutcomeAdapter.MaxSameSessionResumeAttempts)
                    {
                        var sessionId = classified.Decision.RawFacts.SessionId!;
                        // The car engine resumes through CliRunRequest.ResumeSessionId
                        // (the descriptor knows the handshake); the legacy engine keeps
                        // substituting RUNNER_CLI_RESUME_ARGS. The gate stays the same
                        // on both engines: no configured resume template, no resume.
                        var carEngine = _options.ExecEngine == RunnerOptions.ExecEngineCar;
                        var resumeArgs = carEngine
                            ? null
                            : _options.CliResumeArgs!
                                .Replace("{sessionId}", sessionId, StringComparison.Ordinal);
                        shipper.Add(
                            "system",
                            $"[runner] bounded same-session resume 1/{ExecutionOutcomeAdapter.MaxSameSessionResumeAttempts}; session={sessionId}");
                        var resumeSlot = _state.Save(slot with
                        {
                            WorkerDirectory = Path.Combine(slot.WorkerDirectory, "resume-1"),
                            ProcessId = null,
                            ProcessStartedAtUtc = null,
                            LastOutputSequence = 0,
                            Phase = "launching",
                        });
                        var resumed = DurableAgentProcess.Start(
                            _options,
                            resumeSlot.WorkerDirectory,
                            workspace.RepoPath,
                            "Continue the interrupted attempt from the durable workspace state. Complete the requested work, verify it, and end with exactly one required [[TASK_*]] terminal sentinel.",
                            ResultsDir(slot.TaskKey),
                            resumeArgs is null ? null : AgentCliProcess.SplitArgs(resumeArgs),
                            // RUNNER_CLI_RESUME_ARGS carries only the resume
                            // handshake; the card's model / reasoning selection
                            // must survive the second attempt too.
                            resumeSlot.RunSpec,
                            runId: resumeSlot.AttemptId,
                            resumeSessionId: carEngine ? sessionId : null,
                            cleanContextKey: resumeSlot.TaskKey,
                            // The resumed attempt is the same run and keeps the
                            // same preparation cache binding, which a reattaching
                            // daemon can only read back from the first attempt's
                            // own worker specification.
                            environment: DurableAgentProcess.TryReadEnvironment(slot.WorkerDirectory),
                            log: _log);
                        resumeSlot = _state.Save(resumeSlot with
                        {
                            ProcessId = resumed.ProcessId,
                            ProcessStartedAtUtc = resumed.ProcessStartedAtUtc,
                            Phase = "running",
                        });
                        _inventory.AttachProcess(
                            resumeSlot.RunId ?? resumeSlot.AttemptId,
                            resumed.ProcessId);
                        return await AwaitDetachedAsync(
                            resumeSlot,
                            workspace,
                            shipper,
                            outbox,
                            stopRun,
                            daemonShutdown,
                            sameSessionResumeAttempts + 1,
                            operatorStopRequested);
                    }

                    shipper.Add(
                        "system",
                        $"[runner] CLI exited {classified.Decision.RawFacts.ExitCode?.ToString() ?? "without an exit code"}; launchFailed={classified.Decision.RawFacts.LaunchFailed} typedOutcome={classified.Decision.Outcome} recovery={classified.Decision.RecoveryAction} classifier={classified.Decision.ClassifierVersion} legacyOutcome={classified.Outcome.Kind}");
                    outbox?.Enqueue(
                        "terminal",
                        JsonSerializer.Serialize(
                            new DurableTerminalPayload(
                                classified.Decision.Outcome.ToString(),
                                classified.Outcome.Reason),
                            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    return classified;
                }

                if (!observation.IsLive)
                    throw new DetachedWorkerLostException(
                        $"Detached worker disappeared before recording a result: {observation.Detail}");
                await Task.Delay(TimeSpan.FromMilliseconds(250), waitStop.Token);
            }
        }
        catch (OperationCanceledException) when (
            daemonShutdown.IsCancellationRequested
            && !stopRun.IsCancellationRequested)
        {
            // A daemon replacement hands this exact worker to the next process.
            // The outer lifecycle extends and persists the lease before exit.
            throw;
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            await WorktreeProcessReaper.ReapAsync(
                workspace.RepoPath,
                _log,
                CancellationToken.None);
            throw;
        }
        finally
        {
            if (providerRunRecorded)
                ProviderAuthProbe.Shared.RecordRunCompleted(activeInvocation.FileName);
        }
    }

    /// <summary>
    /// AGT-2866 visibility: one line per finished worker naming the envelope it
    /// ran under and what it actually consumed, in the journal and in the run
    /// summary shipped with the delivery. A card that legitimately needed 2,000
    /// CPU seconds is then distinguishable from one that spun, without an
    /// operator having to have been watching at the time. Reading the counters
    /// has to happen before the cgroup is removed.
    /// </summary>
    private void ReportWorkerEnvelope(PersistedRunnerSlot slot, LogShipper shipper)
    {
        var envelope = WorkerResourceEnvelope.FromOptions(_options);
        var usage = WorkerCgroup.ReadUsageFor(slot.WorkerDirectory);
        // AGT-2868: the release kills whatever the run left behind in its own
        // cgroup, so the count has to be taken before the line is composed.
        var leftovers = WorkerCgroup.ReleaseFor(slot.WorkerDirectory);
        var line = usage is null
            ? $"[runner] worker-envelope attempt={slot.AttemptId} applied=no {envelope.Describe()}; "
              + "no per-worker cgroup on this host, so CPU seconds and peak tasks were not measured"
            : $"[runner] worker-envelope attempt={slot.AttemptId} applied=yes "
              + $"{envelope.Describe()} {usage.Describe()} killedLeftovers={leftovers}";
        _log(line);
        shipper.Add("system", line);
    }

    private async Task<bool> HandOffForDaemonRestartAsync(
        PersistedRunnerSlot originalSlot,
        DurableLeaseAuthority? authority)
    {
        var slot = _state.LoadAll().FirstOrDefault(item =>
                       string.Equals(
                           item.AttemptId,
                           originalSlot.AttemptId,
                           StringComparison.Ordinal))
                   ?? originalSlot;
        var observation = DurableAgentProcess.InspectForReattach(slot);
        if (!observation.IsLive && observation.Result is null) return false;

        try
        {
            var renewed = await _client.RenewLeaseAsync(
                new RunLeaseHeartbeatRequest(
                    slot.TaskKey,
                    slot.Lease.LeaseId,
                    slot.Lease.FencingToken,
                    _options.RunnerId,
                    _options.HandoffLeaseTtlSeconds,
                    slot.Lease.AttemptId,
                    slot.Lease.AuthorityEpoch,
                    $"coding-handoff:{slot.AttemptId}:{Guid.NewGuid():N}"),
                CancellationToken.None);
            if (!renewed.Granted || renewed.Lease is null)
            {
                throw new InvalidOperationException(
                    $"{renewed.Outcome} - {renewed.Message ?? "lease was not granted"}");
            }

            slot = _state.Save(slot with
            {
                Lease = renewed.Lease,
                Phase = "handed-off",
            });
            authority?.Confirm(
                renewed.Lease.ExpiresAt,
                "planned coding daemon handoff renewed fenced authority");
            _log(
                $"coding handoff lease extended task={slot.TaskKey} " +
                $"attempt={slot.AttemptId} fence={slot.Lease.FencingToken} " +
                $"requestedTtlSeconds={_options.HandoffLeaseTtlSeconds} " +
                $"expiresAt={slot.Lease.ExpiresAt:O}");
        }
        catch (Exception exception)
        {
            slot = _state.Save(slot with { Phase = "handed-off" });
            _log(
                $"coding handoff lease extension failed task={slot.TaskKey} " +
                $"attempt={slot.AttemptId}: {exception.Message}; " +
                "the replacement daemon retains the persisted authority deadline");
        }

        _log(
            $"coding daemon handoff task={slot.TaskKey} attempt={slot.AttemptId} " +
            $"pid={slot.ProcessId?.ToString() ?? "result-ready"}; " +
            "detached worker left running for replacement reattachment");
        return true;
    }

    private RemoteExecutionResult ClassifyTimedOutResult(
        RunLeaseInfoDto lease,
        GitWorkspace workspace,
        DetachedJobResult result,
        int sameSessionResumeAttempts)
    {
        var decision = ExecutionOutcomeAdapter.Classify(Facts(
            lease,
            workspace,
            StdOut: result.StdOut,
            StdErr: result.StdErr,
            ExitCode: result.ExitCode,
            TimedOut: true,
            SameSessionResumeAttempts: sameSessionResumeAttempts));
        return new RemoteExecutionResult(
            new RunOutcome(RunOutcomeKind.Unknown, decision.Outcome.ToString()),
            result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            decision);
    }

    private RemoteExecutionResult ClassifyProcessResult(
        RunLeaseInfoDto lease,
        GitWorkspace workspace,
        ProcessResult result,
        bool launchFailed,
        int sameSessionResumeAttempts,
        AgentCliProcess.CliInvocation? invocation = null)
    {
        var provider = ProviderOutputEvidenceExtractor.Extract(result.StdOut);
        // Resume stays gated on a configured RUNNER_CLI_RESUME_ARGS on BOTH
        // engines, even though the CAR descriptor could resume from the session
        // id alone. Production leaves that variable unset, so lifting the gate
        // here would make runs resume that never resumed before - a third
        // behaviour jump on top of the two T1 ships. It belongs to T2/T3, with a
        // parity scenario (P12) behind it.
        var sessionState = !string.IsNullOrWhiteSpace(provider.SessionId)
                           && !string.IsNullOrWhiteSpace(_options.CliResumeArgs)
            ? ExecutionSessionState.Resumable
            : string.IsNullOrWhiteSpace(provider.SessionId)
                ? ExecutionSessionState.Unsupported
                : ExecutionSessionState.Active;
        var factsAfterExit = BuildProcessFacts(
            lease,
            workspace,
            result,
            ProviderTerminalEvent: provider.TerminalEvent,
            FinalAssistantOutput: provider.FinalAssistantOutput,
            LaunchFailed: launchFailed,
            SessionState: sessionState,
            SessionId: provider.SessionId,
            SameSessionResumeAttempts: sameSessionResumeAttempts,
            EffectiveCliType: invocation?.CliType,
            EffectiveModel: invocation?.Model,
            EffectiveThinkingLevel: invocation?.ThinkingLevel,
            ObservedModels: provider.ObservedModels);
        var typed = ExecutionOutcomeAdapter.Classify(factsAfterExit);
        var sentinelOutcome = SentinelScanner.Scan(result.StdOut);
        var rawOutcome = BuildRunOutcome(typed, provider, sentinelOutcome, result.StdErr);
        var outcome = ApprovalOnlyNeedsInputPolicy.Classify(rawOutcome);
        if (rawOutcome.Kind == RunOutcomeKind.NeedsInput && outcome.Kind == RunOutcomeKind.Done)
            _log($"[runner] review-requested: reclassified approval-only NeedsInput as Done for {lease.TaskKey}");
        return new RemoteExecutionResult(
            outcome,
            result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            typed);
    }

    internal static RunOutcome BuildRunOutcome(
        ExecutionOutcomeDecision typed,
        ProviderOutputEvidence provider,
        RunOutcome sentinelOutcome,
        string? stdErr)
        => typed.Outcome switch
        {
            ExecutionOutcomeKind.SuccessfulCompletion when sentinelOutcome.Kind == RunOutcomeKind.NoOp
                => sentinelOutcome,
            ExecutionOutcomeKind.SuccessfulCompletion
                => new RunOutcome(RunOutcomeKind.Done, sentinelOutcome.Reason),
            ExecutionOutcomeKind.ExplicitAgentBlocker when sentinelOutcome.Kind is RunOutcomeKind.Blocked or RunOutcomeKind.NeedsInput
                => sentinelOutcome,
            ExecutionOutcomeKind.LaunchFailure
                => new RunOutcome(
                    RunOutcomeKind.EnvironmentFailure,
                    DescribePreparationFailure(stdErr ?? string.Empty)),
            _ => new RunOutcome(
                RunOutcomeKind.Unknown,
                provider.FailureMessage ?? typed.Detail ?? typed.Outcome.ToString()),
        };

    private async Task<ArtifactTransferPlan> PrepareResultsAsync(
        string taskKey,
        string attemptId,
        ArtifactTransferLimitsResponse limits,
        DurableRunOutbox? outbox,
        CancellationToken ct)
    {
        var resultsDir = ResultsDir(taskKey);
        var observed = ObserveResultFiles(resultsDir);
        var (selected, skipped) = ArtifactTransferPolicy.Select(resultsDir, observed, limits);
        if (skipped.Count > 0)
        {
            UpdateDeliverablesArtifactPolicy(resultsDir, skipped, limits);
            observed = ObserveResultFiles(resultsDir);
            (selected, skipped) = ArtifactTransferPolicy.Select(resultsDir, observed, limits);
        }

        var evidenceDir = AttemptEvidenceDir(_options.WorkDir, taskKey, attemptId);
        CopyResultEvidence(resultsDir, evidenceDir);
        observed = ObserveResultFiles(evidenceDir);
        (selected, skipped) = ArtifactTransferPolicy.Select(evidenceDir, observed, limits);

        var manifest = new List<ArtifactManifestEntry>();
        var prepared = new List<ArtifactTransferCandidate>();
        foreach (var file in selected)
        {
            var bytes = await File.ReadAllBytesAsync(file.FullPath, ct);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            manifest.Add(new ArtifactManifestEntry(file.RelativePath, sha, bytes.LongLength));
            prepared.Add(file with { SizeBytes = bytes.LongLength, Sha256 = sha });
        }
        manifest.AddRange(await BuildWithheldManifestEntriesAsync(evidenceDir, skipped, ct));
        var artifactManifest = BuildArtifactManifest(manifest);
        if (outbox is not null)
        {
            outbox.Enqueue("artifact-manifest", artifactManifest.Json);
            _log(
                $"durably journaled artifact manifest files={manifest.Count} skipped={skipped.Count}");
        }
        return new ArtifactTransferPlan(limits, prepared, skipped, artifactManifest);
    }

    private async Task TransferResultsSafeAsync(
        string taskKey,
        RunLeaseInfoDto lease,
        ArtifactTransferPlan? plan,
        LogShipper shipper,
        DurableRunOutbox? outbox)
    {
        if (plan is null) return;
        var issues = plan.Skipped.ToList();
        var uploaded = 0;
        foreach (var file in plan.Files)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(file.FullPath, CancellationToken.None);
                var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (bytes.LongLength != file.SizeBytes
                    || !string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    var changed = new ArtifactTransferIssue(
                        file.RelativePath,
                        bytes.LongLength,
                        "changed after artifact manifest preparation; skipped to preserve manifest integrity",
                        ArtifactTransferOutcomes.TransferFailed);
                    issues.Add(changed);
                    _log(
                        $"artifact-transfer outcome=ArtifactTransferFailed task={taskKey} "
                        + ArtifactFact(changed));
                    continue;
                }
                var upload = new RunnerArtifactUpload(file.RelativePath, Convert.ToBase64String(bytes));
                var response = await UploadArtifactWithRetryAsync(new ArtifactIngestRequest(
                    taskKey,
                    [upload],
                    RunnerId: lease.RunnerId,
                    LeaseId: lease.LeaseId,
                    FencingToken: lease.FencingToken,
                    AttemptId: lease.AttemptId,
                    Fence: lease.FencingToken,
                    AuthorityEpoch: lease.AuthorityEpoch,
                    IdempotencyKey: $"artifact:{lease.AttemptId}:{file.RelativePath}:{WireDigest.Hash(upload.ContentBase64)}"));
                ValidateArtifactAcknowledgement(taskKey, [upload], response);
                uploaded++;
            }
            catch (TaskServerException ex) when (ArtifactTransferPolicy.IsCapacityRejection(ex))
            {
                var reason = ex.StatusCode == 413
                    ? OneLine(ex.Message)
                    : "was refused because artifact storage is full (HTTP 507)";
                var issue = new ArtifactTransferIssue(
                    file.RelativePath,
                    file.SizeBytes,
                    reason,
                    ArtifactTransferOutcomes.ArtifactTooLarge);
                issues.Add(issue);
                var fact = ArtifactFact(issue);
                _log($"artifact-transfer outcome=ArtifactTooLarge task={taskKey} {fact}");
                shipper.Add("system", $"[runner] {fact}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var issue = new ArtifactTransferIssue(
                    file.RelativePath,
                    file.SizeBytes,
                    $"upload failed ({OneLine(ex.Message)})",
                    ArtifactTransferOutcomes.TransferFailed);
                issues.Add(issue);
                _log($"artifact-transfer outcome=ArtifactTransferFailed task={taskKey} {ArtifactFact(issue)}");
            }
        }

        try
        {
            await _client.UploadArtifactsAsync(new ArtifactIngestRequest(
                taskKey,
                [],
                RunnerId: lease.RunnerId,
                LeaseId: lease.LeaseId,
                FencingToken: lease.FencingToken,
                AttemptId: lease.AttemptId,
                Fence: lease.FencingToken,
                AuthorityEpoch: lease.AuthorityEpoch,
                IdempotencyKey: $"artifact-finalize:{lease.AttemptId}:{plan.Manifest.Digest}",
                FinalizeResult: true), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"artifact result-document finalization was non-fatal task={taskKey}: {OneLine(ex.Message)}");
        }

        var reportFailed = false;
        if (issues.Count > 0)
        {
            try
            {
                await shipper.FlushAsync(CancellationToken.None);
                await _client.ReportArtifactTransferAsync(new ArtifactTransferReportRequest(
                    taskKey,
                    "partial",
                    issues,
                    lease.RunnerId,
                    lease.LeaseId,
                    lease.FencingToken,
                    lease.AttemptId), CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                reportFailed = true;
                _log($"artifact partial-outcome report was non-fatal task={taskKey}: {OneLine(ex.Message)}");
            }
        }
        if (reportFailed || issues.Any(issue => issue.Outcome == ArtifactTransferOutcomes.TransferFailed))
        {
            outbox?.RecordHandoffState("artifact-replay");
            _log($"artifact replay retained task={taskKey} attempt={lease.AttemptId}");
        }
        _log($"artifact-transfer task={taskKey} artifacts={(issues.Count == 0 ? "complete" : "partial")} uploaded={uploaded} notTransferred={issues.Count}");
    }

    private async Task<ArtifactIngestResponse?> UploadArtifactWithRetryAsync(
        ArtifactIngestRequest request)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _client.UploadArtifactsAsync(request, CancellationToken.None);
            }
            catch (Exception ex) when (attempt < 3 && IsRetryableArtifactFault(ex))
            {
                _log($"artifact upload retry task={request.TaskKey} attempt={attempt + 1}/3 reason={OneLine(ex.Message)}");
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
            }
        }
    }

    private static bool IsRetryableArtifactFault(Exception exception)
        => exception is HttpRequestException or TimeoutException
           || exception is TaskServerException { StatusCode: 408 or 429 or >= 500 };

    internal static List<(string FullPath, string RelativePath, long SizeBytes)> ObserveResultFiles(
        string resultsDirectory)
        => !Directory.Exists(resultsDirectory)
            ? []
            : Directory.EnumerateFiles(resultsDirectory, "*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return (path, Path.GetRelativePath(resultsDirectory, path).Replace('\\', '/'), info.Length);
                })
                .ToList();

    internal static string AttemptEvidenceDir(string workDir, string taskKey, string attemptId)
        => Path.Combine(workDir, "evidence", GitWorkspace.SafeSegment(taskKey),
            GitWorkspace.SafeSegment(attemptId), "results");

    internal static void CopyResultEvidence(string resultsDirectory, string evidenceDirectory)
    {
        if (Directory.Exists(evidenceDirectory)) return;
        var staging = evidenceDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in ObserveResultFiles(resultsDirectory))
            {
                var target = Path.Combine(staging,
                    file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file.FullPath, target);
            }
            Directory.Move(staging, evidenceDirectory);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    internal static void UpdateDeliverablesArtifactPolicy(
        string resultsDirectory,
        IReadOnlyList<ArtifactTransferIssue> issues,
        ArtifactTransferLimitsResponse limits)
    {
        Directory.CreateDirectory(resultsDirectory);
        var path = Path.Combine(resultsDirectory, "deliverables.md");
        var existing = File.Exists(path) ? File.ReadAllText(path) : "# Deliverables\n";
        const string start = "<!-- artifact-transfer-policy:start -->";
        const string end = "<!-- artifact-transfer-policy:end -->";
        var section = new StringBuilder()
            .AppendLine(start)
            .AppendLine()
            .AppendLine("## Artifact transfer")
            .AppendLine()
            .AppendLine("- Status: `partial`")
            .AppendLine($"- Per-file budget: {ArtifactTransferPolicy.FormatMb(limits.MaxFileBytes)} MB")
            .AppendLine($"- Total budget: {ArtifactTransferPolicy.FormatMb(limits.MaxTotalBytes)} MB")
            .AppendLine("- Not transferred:");
        foreach (var issue in issues)
        {
            var relative = issue.Path.StartsWith("results/", StringComparison.Ordinal)
                ? issue.Path["results/".Length..]
                : issue.Path;
            var evidencePath = Path.Combine(resultsDirectory,
                relative.Replace('/', Path.DirectorySeparatorChar));
            var sha = "unavailable";
            if (File.Exists(evidencePath))
            {
                using var stream = File.OpenRead(evidencePath);
                sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            section.AppendLine($"  - `{issue.Path}` ({issue.SizeBytes} bytes, sha256 `{sha}`): {issue.Reason}.");
        }
        section.AppendLine().AppendLine(end);
        var pattern = Regex.Escape(start) + ".*?" + Regex.Escape(end);
        var updated = Regex.IsMatch(existing, pattern, RegexOptions.Singleline)
            ? Regex.Replace(existing, pattern, section.ToString().TrimEnd(), RegexOptions.Singleline)
            : existing.TrimEnd() + Environment.NewLine + Environment.NewLine + section;
        File.WriteAllText(path, updated.TrimEnd() + Environment.NewLine);
    }

    private static string ArtifactFact(ArtifactTransferIssue issue)
        => $"result artifact {Path.GetFileName(issue.Path)} "
           + $"{ArtifactTransferPolicy.FormatMb(issue.SizeBytes)} MB {issue.Reason}; not transferred";

    internal static void ValidateArtifactAcknowledgement(
        string taskKey,
        IReadOnlyList<RunnerArtifactUpload> uploads,
        ArtifactIngestResponse? response)
    {
        if (response is null)
            throw new InvalidDataException($"Task Server returned no artifact acknowledgement for '{taskKey}'.");

        var expected = uploads
            .Select(upload => upload.Path.Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        // A server that omits the list entirely acknowledged nothing, which is a
        // mismatch for any non-empty upload rather than a NullReferenceException
        // inside the validation that exists to report exactly that.
        var acknowledged = (response.Files ?? [])
            .Select(path => path.Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (response.Uploaded != expected.Count
            || !expected.SequenceEqual(acknowledged, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Task Server acknowledged {response.Uploaded}/{expected.Count} artifact(s) for '{taskKey}'.");
        }
    }

    private async Task<WorktreeTeardownResult> SecureForHandoffWithRetryAsync(
        string taskKey,
        GitWorkspace workspace,
        RunOutcome outcome,
        DurableRunOutbox outbox,
        CancellationToken shutdown)
    {
        for (var attempt = 1; ; attempt++)
        {
            shutdown.ThrowIfCancellationRequested();
            try
            {
                outbox.RecordHandoffState("transferring");
                await ReportOutboxSafeAsync(outbox, shutdown);
                return await workspace.SecureForHandoffAsync(
                    outcome.Kind.ToString(),
                    outbox.Authority.RunId,
                    shutdown);
            }
            catch (WorktreeSalvageException ex) when (!shutdown.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Max(2, attempt * 5)));
                outbox.Enqueue(
                    "transfer-recovery",
                    JsonSerializer.Serialize(
                        new
                        {
                            taskKey,
                            attempt,
                            delaySeconds = delay.TotalSeconds,
                            worktree = ex.WorktreePath,
                            ex.Branch,
                            ex.LocalCommitSha,
                            ex.RemoteCommitSha,
                            recoveryAction = "retry-transfer-without-coding",
                            error = ex.InnerException?.Message ?? ex.Message,
                        },
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                outbox.RecordHandoffState("transfer-recovery");
                await ReportOutboxSafeAsync(outbox, CancellationToken.None);
                _log($"result transfer failed; coding result retained, transfer-only retry {attempt} in {delay.TotalSeconds:0}s: {ex.InnerException?.Message ?? ex.Message}");
                await Task.Delay(delay, shutdown);
            }
        }
    }

    private async Task ReplayBeforeAsync(
        DurableRunOutbox outbox,
        long exclusiveSequence,
        CancellationToken ct)
    {
        foreach (var item in outbox.Pending.Where(item => item.Sequence < exclusiveSequence))
        {
            await _client.SendOutboxItemAsync(outbox.Authority, item, ct);
            outbox.Acknowledge(item.Sequence);
        }
    }

    private async Task ReportOutboxSafeAsync(
        DurableRunOutbox outbox,
        CancellationToken ct)
    {
        try
        {
            await _client.ReportOutboxAsync(
                _options.RunnerId,
                _client.RunnerInstanceId,
                outbox,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"outbox observability report deferred: {ex.Message}");
        }
    }

    internal static DurableArtifactManifest BuildArtifactManifest(
        IReadOnlyList<ArtifactManifestEntry> entries)
    {
        var ordered = entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
        var json = JsonSerializer.Serialize(
            ordered,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
        return new DurableArtifactManifest(digest, json);
    }

    internal static async Task<List<ArtifactManifestEntry>> BuildWithheldManifestEntriesAsync(
        string resultsDirectory,
        IReadOnlyList<ArtifactTransferIssue> skipped,
        CancellationToken ct)
    {
        var entries = new List<ArtifactManifestEntry>(skipped.Count);
        foreach (var issue in skipped)
        {
            var relative = issue.Path.StartsWith("results/", StringComparison.Ordinal)
                ? issue.Path["results/".Length..]
                : issue.Path;
            var fullPath = Path.Combine(resultsDirectory,
                relative.Replace('/', Path.DirectorySeparatorChar));
            await using var stream = File.OpenRead(fullPath);
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            entries.Add(new ArtifactManifestEntry(
                issue.Path, sha, stream.Length, "withheld", issue.Reason));
        }
        return entries;
    }

    private async Task CompleteAsync(
        string taskKey,
        RunLeaseInfoDto lease,
        RunOutcome outcome,
        ExecutionOutcomeDecision outcomeDecision,
        WorktreeTeardownResult teardown,
        string? repository,
        string? baseSha,
        string integrationBranch,
        string? artifactManifestDigest,
        IReadOnlyList<string> outputLines,
        bool sourceMutated,
        CancellationToken ct,
        IReadOnlyList<string>? gateItems = null)
    {
        var (envelopeBaseSha, envelopeResultRef, envelopeManifestDigest) =
            BuildEnvelopeCompletionFields(teardown, baseSha, artifactManifestDigest);
        var resp = await _client.CompleteRunAsync(new RemoteRunCompletionRequest(
            taskKey, lease.LeaseId, lease.FencingToken, _options.RunnerId,
            outcome.Kind.ToString(), outcome.Reason, _options.RunnerName,
            SalvageBranch: teardown.Branch,
            SalvageCommitSha: teardown.CommitSha,
            SalvageBranchUrl: teardown.BranchUrl,
            ResultSha: teardown.ResultSha,
            AttemptChainId: lease.LeaseId,
            Repository: repository,
            SalvageResolution: teardown.Reconciliation?.Kind,
            SalvageLocalCommitSha: teardown.Reconciliation?.LocalCommitSha,
            SalvageRecoveryBranch: teardown.Reconciliation?.RecoveryBranch,
            SalvageRecoveryCommitSha: teardown.Reconciliation?.RecoveryCommitSha,
            SalvageRecoveryBranchUrl: null,
            SalvageAuthoritativeBaseBranch: teardown.Reconciliation?.AuthoritativeBaseBranch,
            SalvageAuthoritativeBaseSha: teardown.Reconciliation?.AuthoritativeBaseSha,
            OutputLines: outputLines,
            SourceMutated: sourceMutated,
            AttemptId: lease.AttemptId,
            AuthorityEpoch: lease.AuthorityEpoch,
            IdempotencyKey: $"completion:{lease.AttemptId}:{outcome.Kind}:{teardown.ResultSha ?? "none"}",
            OutcomeDecision: outcomeDecision,
            BaseSha: envelopeBaseSha,
            ImmutableResultRef: envelopeResultRef,
            ArtifactManifestDigest: envelopeManifestDigest,
            IntegrationBranch: integrationBranch,
            NeedsInputMessage: outcome.NeedsInputMessage,
            GateItems: gateItems), ct);
        _log($"remote-runner-completion recorded: outcome {resp?.Outcome}, state {resp?.TargetState}, result-envelope {(envelopeResultRef is null ? "absent" : "attached")}");
    }

    private async Task CompleteOrReconcileAsync(
        string taskKey,
        RunLeaseInfoDto lease,
        RunOutcome outcome,
        ExecutionOutcomeDecision outcomeDecision,
        WorktreeTeardownResult teardown,
        string? repository,
        string? baseSha,
        string integrationBranch,
        string? artifactManifestDigest,
        IReadOnlyList<string> outputLines,
        bool sourceMutated,
        CancellationToken ct)
    {
        // AGT-2820: a run that delivered without a terminal sentinel used to be
        // reconciled into 5-human-review as "Completed out-of-band", where the
        // acceptance guard refused it for not being in the integration branch -
        // so the delivery was neither reviewed nor integrated. It is an
        // incident, and its delivery is unreviewed, so it is reported as a
        // delivery bound for the review lane with the incident named on the card.
        var incident = MissingSentinelIncidentFor(
            outcome, outcomeDecision, teardown, baseSha, _options.Hostname);
        if (incident is not null)
        {
            _log(
                $"remote-runner-missing-terminal-sentinel task={taskKey} " +
                $"cause=\"{incident.Cause}\" ref={teardown.DeliveryProof!.Ref} " +
                $"sha={teardown.DeliveryProof.CommitSha}; routing to review as an incident");
        }

        await CompleteAsync(
            taskKey,
            lease,
            incident is null ? outcome : outcome with
            {
                Kind = RunOutcomeKind.Done,
                Reason = incident.Reason,
            },
            outcomeDecision,
            teardown,
            repository,
            baseSha,
            integrationBranch,
            artifactManifestDigest,
            outputLines,
            sourceMutated,
            ct,
            incident is null ? null : [incident.GateItem]);
    }

    /// <summary>
    /// AGT-2820: the durable plane's completion payload, incident included.
    /// <para>
    /// The durable plane hands its completion to the Task Server through the
    /// outbox and never passes through <c>CompleteOrReconcileAsync</c>, so it
    /// needs its own call into the same pure incident policy. Until this
    /// existed, a run that delivered without a terminal sentinel reached the
    /// durable plane as a bare <c>ProtocolInconclusive</c> with the agent's own
    /// last words as the reason, and nothing on the card said the delivery was
    /// unreviewed - the exact failure this card was opened for, on the plane
    /// the fleet actually runs.
    /// </para>
    /// <para>
    /// The outcome itself is not rewritten here. The durable plane reports the
    /// typed <see cref="ExecutionOutcomeDecision"/>, which the Task Server
    /// re-validates against <c>CompleteRunRequest.Outcome</c> and which already
    /// routes an inconclusive run to the review lane. What was missing was the
    /// incident: the named cause, and a line on the card that refuses to read
    /// as an acceptance.
    /// </para>
    /// </summary>
    internal static DurableCompletionPayload BuildDurableCompletion(
        RunOutcome outcome,
        ExecutionOutcomeDecision outcomeDecision,
        WorktreeTeardownResult teardown,
        string? baseSha,
        string? envelopeDigest,
        string host)
    {
        var incident = MissingSentinelIncidentFor(
            outcome, outcomeDecision, teardown, baseSha, host);
        return new DurableCompletionPayload(
            outcomeDecision.Outcome.ToString(),
            incident?.Reason ?? outcome.Reason,
            envelopeDigest,
            outcomeDecision,
            outcome.NeedsInputMessage,
            teardown.Branch,
            teardown.CommitSha,
            incident is null ? null : [incident.GateItem]);
    }

    /// <summary>
    /// The incident this run is, or null when it is an ordinary completion. The
    /// admission conditions are the ones the retired out-of-band reconciliation
    /// used: an inconclusive protocol outcome whose delivery was nonetheless
    /// secured and proven against the registered project repository.
    /// </summary>
    internal static MissingSentinelIncident? MissingSentinelIncidentFor(
        RunOutcome outcome,
        ExecutionOutcomeDecision outcomeDecision,
        WorktreeTeardownResult teardown,
        string? baseSha,
        string host)
    {
        var proof = teardown.DeliveryProof;
        var verified = outcome.Kind == RunOutcomeKind.Unknown
                       && teardown.SecuredWork
                       && proof is not null
                       && IsCommitSha(baseSha)
                       && !string.IsNullOrWhiteSpace(teardown.ResultSha)
                       && !string.Equals(baseSha, teardown.ResultSha, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(
                           proof.CommitSha,
                           teardown.ResultSha,
                           StringComparison.OrdinalIgnoreCase);
        return MissingSentinelIncidentPolicy.Evaluate(
            outcomeDecision.Outcome,
            outcomeDecision.RawFacts,
            verified,
            proof?.Ref,
            proof?.CommitSha,
            host);
    }

    /// <summary>
    /// The server materialises a result envelope only from a complete trio
    /// (BaseSha + ImmutableResultRef + ArtifactManifestDigest) and rejects
    /// fields that fail ResultEnvelopeDigest.Validate. A partial or malformed
    /// set must therefore be omitted as a unit. The compatibility completion
    /// boundary then records delivery-failed and requeues once rather than
    /// accepting a review subject that cannot be materialized.
    /// </summary>
    internal static (string? BaseSha, string? ImmutableResultRef, string? ArtifactManifestDigest)
        BuildEnvelopeCompletionFields(
            WorktreeTeardownResult teardown,
            string? baseSha,
            string? artifactManifestDigest)
        => IsCommitSha(baseSha)
           && IsCommitSha(teardown.ResultSha)
           && !string.IsNullOrWhiteSpace(teardown.ImmutableResultRef)
           && IsManifestDigest(artifactManifestDigest)
            ? (baseSha, teardown.ImmutableResultRef, artifactManifestDigest)
            : (null, null, null);

    private static bool IsCommitSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static bool IsManifestDigest(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private async Task ReportUnsecuredWorktreeAsync(
        string taskKey,
        RunLeaseInfoDto lease,
        WorktreeSalvageException ex)
    {
        var failure = ex.InnerException?.Message.Replace('\r', ' ').Replace('\n', ' ').Trim()
                      ?? ex.Message;
        var workflowScopeMissing = GitPushProbe.IsWorkflowScopeFailure(failure);
        var gate = BuildUnsecuredWorktreeGate(_options.Hostname, ex);
        _log($"worktree-salvage-escalated task={taskKey} host={_options.Hostname} path={ex.WorktreePath} branch={ex.Branch} localSha={ex.LocalCommitSha ?? "unknown"} remoteSha={ex.RemoteCommitSha ?? "unknown"}");
        if (workflowScopeMissing)
        {
            try
            {
                await _client.ReportGitCapabilityAsync(
                    _client.ClientId,
                    new RunnerGitCapabilityRequest(
                        GitPushProbe.ReadyNoWorkflowScope,
                        GitPushProbe.WorkflowScopeFix(failure),
                        DateTime.UtcNow),
                    CancellationToken.None);
            }
            catch (Exception capabilityEx)
            {
                _log(
                    $"runner-git-workflow-capability-report-failed task={taskKey} " +
                    $"error={capabilityEx.Message}");
            }
        }
        try
        {
            await _client.CompleteRunAsync(new RemoteRunCompletionRequest(
                taskKey,
                lease.LeaseId,
                lease.FencingToken,
                _options.RunnerId,
                RunOutcomeKind.Blocked.ToString(),
                $"Remote runner retained unsecured worktree at {ex.WorktreePath}; intended branch {ex.Branch}.",
                _options.RunnerName,
                AttemptId: lease.AttemptId,
                AuthorityEpoch: lease.AuthorityEpoch,
                IdempotencyKey: $"completion:{lease.AttemptId}:worktree-blocked",
                GateItems: [gate]), CancellationToken.None);
        }
        catch (Exception reportEx)
        {
            _log($"worktree-salvage-escalation-failed task={taskKey} path={ex.WorktreePath} error={reportEx.Message}");
        }
    }

    internal static string BuildUnsecuredWorktreeGate(
        string hostname,
        WorktreeSalvageException ex)
    {
        var refs = $"canonical refs/heads/{ex.Branch} at {ex.RemoteCommitSha ?? "unknown"}; " +
                   $"retained local HEAD {ex.LocalCommitSha ?? "unknown"}";
        var failure = ex.InnerException?.Message.Replace('\r', ' ').Replace('\n', ' ').Trim()
                      ?? ex.Message;
        var remediation = GitPushProbe.IsWorkflowScopeFailure(failure)
            ? GitPushProbe.WorkflowScopeFix()
            : "Restore origin push access, publish the retained HEAD to a new ref, then requeue.";
        return $"worktree-blocked: host={hostname}; worktree={ex.WorktreePath}; branch={ex.Branch}; " +
               $"{refs}; failure={failure}. No ref was overwritten. Recovery recipe: {remediation}";
    }

    private async Task<bool> ReleaseAsync(
        RunLeaseInfoDto lease,
        CancellationToken ct,
        string outcome = "runner-process-missing",
        LostWorkerHandoff? handoff = null)
    {
        try
        {
            var resp = await _client.ReleaseLeaseAsync(new RunLeaseReleaseRequest(
                lease.TaskKey, lease.LeaseId, lease.FencingToken, _options.RunnerId,
                lease.AttemptId, lease.AuthorityEpoch,
                $"release:{lease.AttemptId}:{lease.LeaseId}",
                outcome,
                handoff?.SalvageBranch,
                handoff?.SalvageCommitSha,
                handoff?.Detail), ct);
            _log($"lease released: {resp.Outcome}");
            return string.Equals(resp.Outcome, "Released", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(resp.Outcome, "NotHeld", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(resp.Outcome, "Expired", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(resp.Outcome, "NotFound", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(resp.Outcome, "AlreadyReleased", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log($"lease release failed (server TTL will reclaim it): {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ReleaseWithRetryAsync(
        RunLeaseInfoDto lease,
        string outcome,
        LostWorkerHandoff? handoff = null)
    {
        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            using var requestDeadline = new CancellationTokenSource(
                TimeSpan.FromSeconds(_options.ServerRequestTimeoutSeconds));
            if (await ReleaseAsync(lease, requestDeadline.Token, outcome, handoff))
                return true;
            if (attempt == maximumAttempts)
                break;

            var delay = TaskServerConnectivityMonitor.RetryDelay(
                _options.PollSeconds,
                attempt);
            _log(
                $"lease release retry scheduled task={lease.TaskKey} " +
                $"attempt={attempt + 1}/{maximumAttempts} retrySeconds={delay.TotalSeconds:0}");
            await Task.Delay(delay);
        }

        return false;
    }

    private string ResultsDir(string taskKey)
        => Path.Combine(_options.WorkDir, "tasks", GitWorkspace.SafeSegment(taskKey), "results");

    private static readonly Regex CredentialedHttpUrl = new(
        @"(?<scheme>https?://)[^/@\s]+(?::[^/@\s]+)?@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// One short, credential-free line naming why the Task Server could not be
    /// reached. It is what the journal, the persisted slot, and the operator
    /// feed all quote, so it must stay one line.
    /// </summary>
    internal static string DescribeTransportFault(Exception exception)
    {
        var status = exception is TaskServerException server ? $"HTTP {server.StatusCode}: " : string.Empty;
        var inner = exception.InnerException?.Message;
        var message = string.IsNullOrWhiteSpace(inner) ? exception.Message : $"{exception.Message} ({inner})";
        var sanitized = CredentialedHttpUrl
            .Replace(message, "${scheme}***@")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        var line = $"{status}{sanitized}";
        return line.Length <= 300 ? line : line[..300];
    }

    private static string DescribePreparationFailure(Exception exception)
        => DescribePreparationFailure(exception.Message);

    private static string DescribePreparationFailure(string diagnostic)
    {
        var message = CredentialedHttpUrl
            .Replace(diagnostic, "${scheme}***@")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (message.StartsWith("git clone ", StringComparison.OrdinalIgnoreCase))
            return $"clone failed: {message}";
        if (message.StartsWith("git fetch ", StringComparison.OrdinalIgnoreCase))
            return $"fetch failed: {message}";
        return $"environment preparation failed: {message}";
    }

    private sealed class RemoteClaimPreparationException : Exception
    {
        public RemoteClaimPreparationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal static ExecutionOutcomeDecision WithDurableOutput(
        ExecutionOutcomeDecision decision,
        WorktreeTeardownResult teardown)
    {
        // ResultSha is the exact local result preserved by salvage. In a
        // divergence, CommitSha can name the canonical remote branch tip while
        // ResultSha names the separately published recovery result.
        var reference = teardown.ResultSha ?? teardown.CommitSha ?? teardown.Branch;
        var state = string.IsNullOrWhiteSpace(reference)
            ? decision.RawFacts.DurableOutputState
            : DurableOutputState.Acknowledged;
        return ExecutionOutcomeAdapter.WithUpdatedFacts(decision, decision.RawFacts with
        {
            DurableOutputState = state,
            DurableOutputReference = reference ?? decision.RawFacts.DurableOutputReference,
        });
    }

    /// <summary>
    /// Applies a completed run to the host-wide provider status without losing
    /// the termination facts already recorded by outcome classification. A
    /// provider-looking message from an operator stop, host shutdown, or
    /// signal-terminated process is run evidence, not provider-limit evidence.
    /// </summary>
    internal static ProviderAuthStatus RecordProviderProcessResult(
        ProviderAuthProbe providerAuth,
        string cliBinary,
        ProcessResult result,
        ExecutionRawFacts facts,
        string? evidenceId = null,
        bool stopDirectiveRecorded = false,
        bool daemonShutdownRecorded = false)
        => providerAuth.RecordProcessResult(
            cliBinary,
            result,
            evidenceId,
            operatorStopped: facts.OperatorCancelled || stopDirectiveRecorded,
            signal: facts.Signal,
            hostShutdown: facts.HostShutdown || daemonShutdownRecorded);

    internal static ProcessResult ProcessResultFrom(DetachedJobResult result)
        => new(result.ExitCode, result.StdOut, result.StdErr, result.Signal);

    internal static ExecutionRawFacts BuildProcessFacts(
        RunLeaseInfoDto lease,
        GitWorkspace workspace,
        ProcessResult result,
        string? ProviderTerminalEvent = null,
        string? FinalAssistantOutput = null,
        bool LaunchFailed = false,
        ExecutionSessionState SessionState = ExecutionSessionState.Unsupported,
        string? SessionId = null,
        int SameSessionResumeAttempts = 0,
        string? EffectiveCliType = null,
        string? EffectiveModel = null,
        string? EffectiveThinkingLevel = null,
        IReadOnlyList<string>? ObservedModels = null)
        => Facts(
            lease,
            workspace,
            ProviderTerminalEvent,
            FinalAssistantOutput,
            result.StdOut,
            result.StdErr,
            result.ExitCode,
            result.Signal,
            LaunchFailed: LaunchFailed,
            SessionState: SessionState,
            SessionId: SessionId,
            SameSessionResumeAttempts: SameSessionResumeAttempts,
            EffectiveCliType: EffectiveCliType,
            EffectiveModel: EffectiveModel,
            EffectiveThinkingLevel: EffectiveThinkingLevel,
            ObservedModels: ObservedModels);

    private static ExecutionRawFacts Facts(
        RunLeaseInfoDto lease,
        GitWorkspace workspace,
        string? ProviderTerminalEvent = null,
        string? FinalAssistantOutput = null,
        string? StdOut = null,
        string? StdErr = null,
        int? ExitCode = null,
        int? Signal = null,
        bool LaunchFailed = false,
        bool TimedOut = false,
        bool OomKilled = false,
        bool OperatorCancelled = false,
        bool HostShutdown = false,
        bool LeaseLost = false,
        ExecutionTransportState TransportState = ExecutionTransportState.Connected,
        ExecutionSessionState SessionState = ExecutionSessionState.Unsupported,
        string? SessionId = null,
        int SameSessionResumeAttempts = 0,
        int FreshSalvageAttempts = 0,
        string? EffectiveCliType = null,
        string? EffectiveModel = null,
        string? EffectiveThinkingLevel = null,
        IReadOnlyList<string>? ObservedModels = null)
        => new(
            lease.AttemptId ?? lease.LeaseId,
            ExecutionAttemptKind.Coding,
            ProviderTerminalEvent,
            FinalAssistantOutput,
            StdOut,
            StdErr,
            ExitCode,
            Signal,
            LaunchFailed,
            TimedOut,
            OomKilled,
            OperatorCancelled,
            HostShutdown,
            LeaseLost,
            TransportState,
            SessionState,
            SessionId,
            DurableOutputState.LocalOnly,
            workspace.RepoPath,
            SameSessionResumeAttempts,
            FreshSalvageAttempts,
            ReviewSubject: null,
            EffectiveCliType,
            EffectiveModel,
            EffectiveThinkingLevel,
            ObservedModels);

    internal static async Task<T> RetryEnvironmentPreparationAsync<T>(
        Func<CancellationToken, Task<T>> prepare,
        Action<string> log,
        CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(log);
        delay ??= Task.Delay;

        Exception? last = null;
        for (var attempt = 1; attempt <= MaxEnvironmentPreparationAttempts; attempt++)
        {
            try
            {
                return await prepare(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not WorktreeSalvageException)
            {
                last = ex;
                log(
                    $"remote-environment-preparation-failed attempt={attempt}/{MaxEnvironmentPreparationAttempts} " +
                    $"error={OneLine(ex.Message)}");
                if (attempt < MaxEnvironmentPreparationAttempts)
                    await delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }

        throw new RemoteEnvironmentPreparationException(
            MaxEnvironmentPreparationAttempts,
            last ?? new InvalidOperationException("Environment preparation failed without an exception."));
    }

    private static string OneLine(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static async Task SafeAwait(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { /* expected on teardown */ }
        catch { /* background loops already logged their own failures */ }
    }
}

internal sealed class RemoteEnvironmentPreparationException : Exception
{
    public RemoteEnvironmentPreparationException(int attempts, Exception innerException)
        : base($"Remote environment preparation failed after {attempts} attempts.", innerException)
    {
        Attempts = attempts;
    }

    public int Attempts { get; }
}

internal sealed record RemoteExecutionResult(
    RunOutcome Outcome,
    IReadOnlyList<string> OutputLines,
    ExecutionOutcomeDecision Decision);
