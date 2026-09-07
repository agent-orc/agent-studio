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
/// upload the results/ evidence, post a fenced runner completion so the result
/// enters the normal review pipeline, and always release the lease. Before removing
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
        RunSpecDto? runSpec = null)
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
            fencingToken: lease.FencingToken);
        var slot = _state.Create(
            taskKey, lease, workspace.RepoPath, runId, leaseInstanceId,
            projectId, repositoryUrl, defaultBranch, taskKind, runSpec);
        return await RunPersistedAsync(slot, workspace, shutdown, reattach: false);
    }

    /// <summary>Continue a positively verified detached process from durable host state.</summary>
    public async Task<int> ReattachAsync(PersistedRunnerSlot slot, CancellationToken stopRun)
    {
        _log($"reattaching task '{slot.TaskKey}' attempt {slot.AttemptId} pid={slot.ProcessId} worktree={slot.WorktreePath}");
        // Restore the recorded base SHA: this process never prepared the worktree,
        // and without it the completion would be assembled with no envelope trio
        // after every daemon restart.
        var workspace = new GitWorkspace(
            _options, slot.TaskKey, _log, slot.ProjectId, slot.RepositoryUrl, slot.DefaultBranch,
            restoredBaseSha: slot.BaseSha,
            sourceRunAttemptId: slot.RunId ?? slot.Lease.AttemptId ?? slot.AttemptId,
            fencingToken: slot.Lease.FencingToken);
        return await RunPersistedAsync(slot, workspace, stopRun, reattach: true);
    }

    public async Task<bool> ReleaseDeadAsync(PersistedRunnerSlot slot, string reason)
    {
        _log($"releasing dead persisted attempt task={slot.TaskKey} attempt={slot.AttemptId}: {reason}");
        var outcome = string.Equals(
            slot.Phase,
            "authority-deadline-exhausted",
            StringComparison.Ordinal)
            ? "authority-deadline-exhausted"
            : "runner-process-missing";
        if (await ReleaseWithRetryAsync(slot.Lease, outcome))
        {
            _state.Delete(slot);
            return true;
        }

        _log($"dead attempt state retained for release retry: {slot.TaskKey}");
        return false;
    }

    private async Task<int> RunPersistedAsync(
        PersistedRunnerSlot slot,
        GitWorkspace workspace,
        CancellationToken shutdown,
        bool reattach)
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
        var heartbeatTask = heartbeat.RunAsync(stopRun, shutdown);

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
        var resultTransferAcknowledged = false;
        var releaseOnly = false;
        DurableArtifactManifest? artifactManifest = null;
        try
        {
            var execution = reattach
                ? await AwaitDetachedAsync(slot, workspace, shipper, outbox, stopRun.Token)
                : await ExecuteAsync(slot, workspace, shipper, outbox, stopRun, shutdown, epicPlanning);
            outcome = execution.Outcome;
            outcomeDecision = execution.Decision;
            outputLines = execution.OutputLines;
            await shipper.FlushAsync(stopRun.Token);
            artifactManifest = await UploadResultsAsync(
                taskKey,
                lease,
                outbox,
                stopRun.Token);
            resultTransferAcknowledged = true;

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
                teardown = await SecureForHandoffWithRetryAsync(
                    taskKey,
                    workspace,
                    outcome,
                    outbox,
                    stopRun.Token);
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
                teardown = await workspace.TeardownAsync(
                    outcome.Kind.ToString(),
                    lease.AttemptId,
                    CancellationToken.None);
            }
            outcomeDecision = WithDurableOutput(outcomeDecision, teardown);
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
                var completion = outbox.Enqueue(
                    "completion",
                    JsonSerializer.Serialize(
                        new DurableCompletionPayload(
                            outcomeDecision.Outcome.ToString(),
                            outcome.Reason,
                            envelopeDigest,
                            outcomeDecision),
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
            _log($"task '{taskKey}' handed back to the local board: {outcome.Kind}");
            return outcome.Kind is RunOutcomeKind.Done or RunOutcomeKind.NoOp ? 0 : 1;
        }
        catch (DetachedWorkerLostException ex)
        {
            releaseOnly = true;
            _log($"detached worker lost; attempt will be released to Ready: {ex.Message}");
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
            if (heartbeat.LeaseLost
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
            if (outbox is null && !teardownAttempted && Directory.Exists(workspace.RepoPath))
            {
                if (!resultTransferAcknowledged)
                {
                    _log(
                        $"result transfer was not acknowledged; retaining worktree before teardown " +
                        $"task={taskKey} path={workspace.RepoPath}");
                }
                else
                {
                    try
                    {
                        teardownAttempted = true;
                        var teardown = epicPlanning
                            ? WorktreeTeardownResult.NoWork
                            : await workspace.TeardownAsync(
                                outcome.Kind.ToString(),
                                lease.AttemptId,
                                CancellationToken.None);
                        if (epicPlanning)
                            sourceMutated = await workspace.TeardownReadOnlyAsync(CancellationToken.None);
                        if (!handedBack && !heartbeat.LeaseLost && !releaseOnly)
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
            }

            // Completion is fenced by the live lease, so release only after the
            // normal or fail-closed handoff has finished.
            if (outbox is null || handedBack)
            {
                var released = releaseOnly
                    ? await ReleaseWithRetryAsync(lease, "runner-process-missing")
                    : await ReleaseAsync(lease, CancellationToken.None);
                if (released)
                    _state.Delete(slot);
            }
        }
    }

    private async Task<RemoteExecutionResult> ExecuteAsync(
        PersistedRunnerSlot slot, GitWorkspace workspace, LogShipper shipper,
        DurableRunOutbox? outbox, CancellationTokenSource stopRun,
        CancellationToken shutdown, bool epicPlanning)
    {
        var taskKey = slot.TaskKey;
        var lease = slot.Lease;
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
            prompt = RemoteRunPrompt.Build(taskPrompt, runSpec?.ModeFraming, ResultsDir(taskKey));
            shipper.Add("system", string.IsNullOrWhiteSpace(runSpec?.ModeFraming)
                ? "[runner] results-dir context + remote-completion-protocol appended to task prompt"
                : "[runner] server-composed mode framing + results-dir context + remote-completion-protocol appended to task prompt");
        }

        var resultsDir = ResultsDir(taskKey);
        if (Directory.Exists(resultsDir)) Directory.Delete(resultsDir, recursive: true);
        Directory.CreateDirectory(resultsDir);

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
                cleanContextKey: taskKey);
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
        return await AwaitDetachedAsync(slot, workspace, shipper, outbox, stopRun.Token);
    }

    private async Task<RemoteExecutionResult> AwaitDetachedAsync(
        PersistedRunnerSlot slot,
        GitWorkspace workspace,
        LogShipper shipper,
        DurableRunOutbox? outbox,
        CancellationToken stopRun,
        int sameSessionResumeAttempts = 0)
    {
        var process = DurableAgentProcess.Attach(slot);
        var sequence = slot.LastOutputSequence;
        try
        {
            while (true)
            {
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
                    _state.Save(slot with { Phase = "finalizing", LastOutputSequence = sequence });
                    var processResult = new ProcessResult(result.ExitCode, result.StdOut, result.StdErr);
                    var invocation = AgentCliProcess.Resolve(_options, slot.RunSpec);
                    var providerAccess = ProviderAccessClassifier.Classify(
                        processResult.ExitCode,
                        processResult.StdOut,
                        processResult.StdErr);
                    var providerAuth = ProviderAuthProbe.Shared.RecordProcessResult(
                        invocation.FileName,
                        processResult);
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
                    else if (providerAccess.Kind == ProviderAccessEvidenceKind.RateLimited)
                    {
                        shipper.Add(
                            "system",
                            $"[runner] provider-auth state=limited provider={invocation.CliType} until={providerAuth.LimitedUntil:o}; claims wait for provider recovery, not sign-in");
                    }
                    else if (providerAccess.Kind == ProviderAccessEvidenceKind.TransientFailure)
                    {
                        shipper.Add(
                            "system",
                            $"[runner] provider-auth state=retrying provider={invocation.CliType}; last-good capability retained");
                    }
                    var classified = result.TimedOut
                        ? ClassifyTimedOutResult(slot.Lease, workspace, result, sameSessionResumeAttempts)
                        : ClassifyProcessResult(
                            slot.Lease,
                            workspace,
                            processResult,
                            result.LaunchFailed,
                            sameSessionResumeAttempts);
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
                            cleanContextKey: resumeSlot.TaskKey);
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
                            sameSessionResumeAttempts + 1);
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
                await Task.Delay(TimeSpan.FromMilliseconds(250), stopRun);
            }
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
        int sameSessionResumeAttempts)
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
        var factsAfterExit = Facts(
            lease,
            workspace,
            ProviderTerminalEvent: provider.TerminalEvent,
            FinalAssistantOutput: provider.FinalAssistantOutput,
            StdOut: result.StdOut,
            StdErr: result.StdErr,
            ExitCode: result.ExitCode,
            Signal: SignalFromExitCode(result.ExitCode),
            LaunchFailed: launchFailed,
            SessionState: sessionState,
            SessionId: provider.SessionId,
            SameSessionResumeAttempts: sameSessionResumeAttempts);
        var typed = ExecutionOutcomeAdapter.Classify(factsAfterExit);
        var sentinelOutcome = SentinelScanner.Scan(result.StdOut);
        var outcome = BuildRunOutcome(typed, provider, sentinelOutcome, result.StdErr);
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

    private async Task<DurableArtifactManifest> UploadResultsAsync(
        string taskKey,
        RunLeaseInfoDto lease,
        DurableRunOutbox? outbox,
        CancellationToken ct)
    {
        var resultsDir = ResultsDir(taskKey);
        var manifest = new List<ArtifactManifestEntry>();
        var files = Directory.Exists(resultsDir)
            ? Directory.EnumerateFiles(
                resultsDir,
                "*",
                SearchOption.AllDirectories).ToList()
            : [];

        var uploads = new List<RunnerArtifactUpload>();
        foreach (var file in files)
        {
            var rel = "results/" + Path.GetRelativePath(resultsDir, file).Replace('\\', '/');
            var bytes = await File.ReadAllBytesAsync(file, ct);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            manifest.Add(new ArtifactManifestEntry(rel, sha, bytes.LongLength));
            var content = Convert.ToBase64String(bytes);
            if (outbox is not null)
            {
                outbox.Enqueue(
                    "artifact",
                    JsonSerializer.Serialize(
                        new DurableArtifactPayload(
                            rel,
                            TaskServerClient.MediaTypeForPath(rel),
                            content,
                            sha),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            else
            {
                uploads.Add(new RunnerArtifactUpload(rel, content));
            }
        }

        var artifactManifest = BuildArtifactManifest(manifest);
        if (outbox is not null)
        {
            outbox.Enqueue("artifact-manifest", artifactManifest.Json);
            _log(
                $"durably journaled {manifest.Count} artifact(s) for fenced outbox replay");
        }
        else
        {
            var digestInput = string.Join("\n", uploads.OrderBy(x => x.Path, StringComparer.Ordinal)
                .Select(x => $"{x.Path}:{WireDigest.Hash(x.ContentBase64)}"));
            var resp = await _client.UploadArtifactsAsync(new ArtifactIngestRequest(
                taskKey,
                uploads,
                RunnerId: lease.RunnerId,
                LeaseId: lease.LeaseId,
                FencingToken: lease.FencingToken,
                AttemptId: lease.AttemptId,
                Fence: lease.FencingToken,
                AuthorityEpoch: lease.AuthorityEpoch,
                IdempotencyKey: $"artifacts:{lease.AttemptId}:{WireDigest.Hash(digestInput)}",
                FinalizeResult: true), ct);
            ValidateArtifactAcknowledgement(taskKey, uploads, resp);
            _log(
                $"uploaded {resp!.Uploaded} artifact(s); commit {resp.CommitStatus ?? "n/a"}; " +
                $"result-document={resp.ResultDocumentStatus ?? "not-reported"}");
        }
        return artifactManifest;
    }

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
        var acknowledged = response.Files
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
        CancellationToken ct)
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
            IntegrationBranch: integrationBranch), ct);
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
        var external = BuildVerifiedOutOfBandRequest(
            outcome,
            outcomeDecision,
            teardown,
            baseSha,
            _options.RunnerName);
        if (external is not null)
        {
            var response = await _client.CompleteAsync(taskKey, external, ct);
            _log(
                $"remote-runner-verified-out-of-band recorded: state {response?.TargetState}, " +
                $"ref {teardown.DeliveryProof!.Ref}, sha {teardown.DeliveryProof.CommitSha}");
            return;
        }

        await CompleteAsync(
            taskKey,
            lease,
            outcome,
            outcomeDecision,
            teardown,
            repository,
            baseSha,
            integrationBranch,
            artifactManifestDigest,
            outputLines,
            sourceMutated,
            ct);
    }

    internal static ExternalCompletionRequest? BuildVerifiedOutOfBandRequest(
        RunOutcome outcome,
        ExecutionOutcomeDecision outcomeDecision,
        WorktreeTeardownResult teardown,
        string? baseSha,
        string source)
    {
        var proof = teardown.DeliveryProof;
        if (outcome.Kind != RunOutcomeKind.Unknown
            || outcomeDecision.Outcome != ExecutionOutcomeKind.ProtocolInconclusive
            || !teardown.SecuredWork
            || proof is null
            || !IsCommitSha(baseSha)
            || string.IsNullOrWhiteSpace(teardown.ResultSha)
            || string.Equals(baseSha, teardown.ResultSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                proof.CommitSha,
                teardown.ResultSha,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var summary =
            $"Remote work completed without a terminal sentinel. " +
            $"The registered project repository was verified at {proof.Ref} " +
            $"with commit {proof.CommitSha}.";
        return new ExternalCompletionRequest(
            Summary: summary,
            Deliverables:
            [
                new ExternalDeliverable(
                    Path: $"{proof.Ref}@{proof.CommitSha}",
                    Note: "Verified by ls-remote against the project registration.")
            ],
            Source: source,
            TargetState: "5-human-review",
            // AGT-2220: hand the proof over as data, not only as prose. The
            // sentence above used to BE the evidence - the server stamped on a
            // string it never re-checked. These two fields are what the server
            // now independently verifies against the target repository.
            ResultSha: proof.CommitSha,
            ResultRef: proof.Ref,
            BaseSha: baseSha);
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
        string outcome = "runner-process-missing")
    {
        try
        {
            var resp = await _client.ReleaseLeaseAsync(new RunLeaseReleaseRequest(
                lease.TaskKey, lease.LeaseId, lease.FencingToken, _options.RunnerId,
                lease.AttemptId, lease.AuthorityEpoch,
                $"release:{lease.AttemptId}:{lease.LeaseId}",
                outcome), ct);
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
        string outcome)
    {
        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            using var requestDeadline = new CancellationTokenSource(
                TimeSpan.FromSeconds(_options.ServerRequestTimeoutSeconds));
            if (await ReleaseAsync(lease, requestDeadline.Token, outcome))
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
        return ExecutionOutcomeAdapter.Classify(decision.RawFacts with
        {
            DurableOutputState = state,
            DurableOutputReference = reference ?? decision.RawFacts.DurableOutputReference,
        });
    }

    private static int? SignalFromExitCode(int exitCode)
        => !OperatingSystem.IsWindows() && exitCode is >= 129 and <= 255
            ? exitCode - 128
            : null;

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
        int FreshSalvageAttempts = 0)
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
            FreshSalvageAttempts);

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
