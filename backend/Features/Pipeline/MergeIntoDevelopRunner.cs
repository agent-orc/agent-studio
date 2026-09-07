using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>
/// Runs the common "Merge into Develop" post-step
/// (<see cref="PipelineCatalogue.MergeIntoDevelopStepId"/>). Green fenced Remote
/// deliveries invoke it immediately before Human Review. Human acceptance invokes
/// it again when immediate integration failed or a legacy delivery is still
/// pending. The catalogue remains <see cref="PipelineStep.Deferred"/> because the
/// ordinary local post-bracket does not execute this step by itself.
///
/// <para>
/// It performs the real, scoped git merge from the resolved delivery ref into
/// the configured integration branch via
/// <see cref="GitService.MergeBranchIntoIntegration"/> and records the outcome
/// into the job's <c>pipeline-execution.json</c> so the deferred step flips from
/// pending to passed / failed / skipped in place. A merge conflict is recorded
/// <see cref="PipelineStepStatus.Failed"/> with the conflicted files in the
/// verdict summary - made visible, never silently resolved - while the working
/// tree is left clean (the merge is aborted). Only a successful result lets the
/// acceptance worker move the task to Completed. Immediate Remote failures remain
/// visible in Human Review and do not claim integration.
/// </para>
/// </summary>
public sealed class MergeIntoDevelopRunner
{
    private readonly GitService _git;
    private readonly PipelineExecutionLog _pipelineLog;
    private readonly ILogger<MergeIntoDevelopRunner> _logger;
    private readonly IntegrationPushQueue? _pushQueue;
    private readonly ProjectSettingsService? _projectSettings;
    private readonly PreMainTestGate? _preMainTestGate;
    private readonly PreDevelopBuildGate? _preDevelopBuildGate;
    private readonly AttemptAuthorityService? _attemptAuthority;
    private readonly TaskMutationService? _taskMutations;
    private readonly TimeSpan _preMainTimeout;
    private readonly TimeSpan _preDevelopTimeout;
    private readonly Func<int, TimeSpan> _environmentalBackoff;
    private readonly SemaphoreSlim _mergeGate = new(1, 1);
    private readonly SemaphoreSlim _pushGate = new(1, 1);
    private int _mergeGateUsers;

    public MergeIntoDevelopRunner(
        GitService git,
        PipelineExecutionLog pipelineLog,
        ILogger<MergeIntoDevelopRunner> logger,
        IntegrationPushQueue? pushQueue = null,
        ProjectSettingsService? projectSettings = null,
        PreMainTestGate? preMainTestGate = null,
        TimeSpan? preMainTimeout = null,
        Func<int, TimeSpan>? environmentalBackoff = null,
        PreDevelopBuildGate? preDevelopBuildGate = null,
        TimeSpan? preDevelopTimeout = null,
        AttemptAuthorityService? attemptAuthority = null,
        TaskMutationService? taskMutations = null)
    {
        _git = git;
        _pipelineLog = pipelineLog;
        _logger = logger;
        _pushQueue = pushQueue;
        _projectSettings = projectSettings;
        _preMainTestGate = preMainTestGate;
        _preDevelopBuildGate = preDevelopBuildGate;
        _attemptAuthority = attemptAuthority;
        _taskMutations = taskMutations;
        _preMainTimeout = preMainTimeout is { } configured && configured > TimeSpan.Zero
            ? configured
            : TimeSpan.FromHours(1);
        _preDevelopTimeout = preDevelopTimeout is { } configuredDevelop && configuredDevelop > TimeSpan.Zero
            ? configuredDevelop
            : TimeSpan.FromMinutes(30);
        // Default to the AGT-1944 environmental backoff (30s, 120s, cap 5min); a
        // test injects a zero backoff so it does not sleep between retries.
        _environmentalBackoff = environmentalBackoff ?? PostProcessingOutcomeTaxonomy.RetryBackoff;
    }

    /// <summary>
    /// Triggers the merge of the task branch into <paramref name="integrationBranch"/>
    /// and records the post-step outcome. Returns the underlying
    /// <see cref="MergeIntoIntegrationResult"/> for callers / tests; the lane
    /// transition does not depend on it. Never throws.
    /// </summary>
    public MergeIntoIntegrationResult Run(
        string project,
        string jobId,
        string jobFolderPath,
        string? watchPath,
        string integrationBranch,
        string integrationStrategy = IntegrationStrategies.DirectMerge,
        string pipelineType = PipelineTypes.Task)
        => RunAsync(
            project,
            jobId,
            jobFolderPath,
            watchPath,
            integrationBranch,
            CancellationToken.None,
            integrationStrategy,
            pipelineType).GetAwaiter().GetResult();

    /// <summary>
    /// Async merge entry point used by immediate Remote integration and by the
    /// compatibility worker/backstop for durable legacy transactions. A configured
    /// <c>main</c> target is a release mutation, so it is fail-closed behind the
    /// mandatory full-suite gate and advances only to the exact tested SHA.
    /// </summary>
    public async Task<MergeIntoIntegrationResult> RunAsync(
        string project,
        string jobId,
        string jobFolderPath,
        string? watchPath,
        string integrationBranch,
        CancellationToken ct,
        string integrationStrategy = IntegrationStrategies.DirectMerge,
        string pipelineType = PipelineTypes.Task)
    {
        // Count both the active operation and serialized waiters. The external
        // stable watchdog uses this drain signal to avoid cutting the process
        // between merge and gate/rollback. The accepted lane and pipeline facts
        // still recover queued work that has not entered this boundary.
        Interlocked.Increment(ref _mergeGateUsers);
        try
        {
            // Deliberately NOT the caller's token. Merge + gate + rollback form
            // one consistency boundary after acceptance is already durable.
            await _mergeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                return await RunSerializedAsync(
                    project, jobId, jobFolderPath, watchPath,
                    integrationBranch, integrationStrategy, pipelineType, ct).ConfigureAwait(false);
            }
            finally
            {
                _mergeGate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _mergeGateUsers);
        }
    }

    /// <summary>
    /// True while a delivery integration is inside, or waiting to enter, the
    /// serialized merge + build-gate + rollback consistency boundary.
    /// </summary>
    public bool IsMergeGateBusy => Volatile.Read(ref _mergeGateUsers) > 0;

    private async Task<MergeIntoIntegrationResult> RunSerializedAsync(
        string project,
        string jobId,
        string jobFolderPath,
        string? watchPath,
        string integrationBranch,
        string integrationStrategy,
        string pipelineType,
        CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        try
        {
            var repoRoot = _git.ResolveRepoRootForWatchPath(watchPath)
                ?? (string.IsNullOrWhiteSpace(watchPath) ? null : watchPath);
            if (string.IsNullOrWhiteSpace(repoRoot))
            {
                var unresolved = MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error, error: "Could not resolve repository root for the project.");
                Record(
                    jobFolderPath,
                    project,
                    jobId,
                    integrationBranch,
                    unresolved,
                    preMainResult: null,
                    preDevelopResult: null,
                    startedAt);
                return unresolved;
            }

            var reviewSubject = ReviewSubjectStore.Read(jobFolderPath);
            if (reviewSubject is not null
                && _attemptAuthority is not null
                && !ReviewSubjectStore.TryValidateCurrentAttempt(
                    jobFolderPath,
                    reviewSubject,
                    _attemptAuthority,
                    out var subjectError))
            {
                var stale = MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: subjectError);
                Record(
                    jobFolderPath,
                    project,
                    jobId,
                    integrationBranch,
                    stale,
                    preMainResult: null,
                    preDevelopResult: null,
                    startedAt);
                return stale;
            }
            var delivery = DeliveryRefResolver.Resolve(jobId, jobFolderPath);
            // The caller supplies current project/repository truth. A review
            // subject stores only the branch observed when the run was prepared
            // and must not retarget acceptance after project settings or
            // origin/HEAD change.
            var branch = _git.ResolveIntegrationBranch(repoRoot, integrationBranch);
            var taskBranch = delivery.Ref;
            var strategy = IntegrationStrategies.Normalize(integrationStrategy);
            var isPullRequest = string.Equals(
                strategy,
                IntegrationStrategies.PullRequest,
                StringComparison.Ordinal);
            BuildTestGateResult? preMainResult = null;
            BuildTestGateResult? preDevelopResult = null;
            string? pushBranch = null;
            string? approvedPushSha = null;
            var mechanicalAttributionHandled = false;
            MergeIntoIntegrationResult result;
            ImmediateIntegrationLineageDecision? lineage = null;
            IntegrationBranchSyncResult synchronized;
            if (!isPullRequest && IsReleaseBranch(branch) && HasDevelopLine(repoRoot))
            {
                synchronized = _git.SynchronizeIntegrationBranch(repoRoot, "develop", ct);
                if (synchronized.Success)
                    synchronized = _git.SynchronizeIntegrationBranch(repoRoot, branch, ct);

                if (synchronized.Success)
                {
                    lineage = ImmediateIntegrationLineagePolicy.Decide(
                        branch,
                        developAvailable: true,
                        mainIsAncestorOfDevelop: _git.IsAncestor(repoRoot, branch, "develop"));
                }
            }
            else
            {
                synchronized = _git.SynchronizeIntegrationBranch(repoRoot, branch, ct);
                lineage = ImmediateIntegrationLineagePolicy.Decide(
                    branch,
                    developAvailable: false,
                    mainIsAncestorOfDevelop: false);
            }

            if (!synchronized.Success)
            {
                result = MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: synchronized.Error);
            }
            else if (lineage?.Mode == ImmediateIntegrationLineageMode.Blocked)
            {
                result = MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: lineage.Reason);
            }
            else if (isPullRequest)
            {
                result = MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.PushedForReview,
                    error: $"Delivery '{taskBranch}' remains outside {branch} because the project uses the pull-request integration strategy.");
            }
            else if (lineage?.Mode == ImmediateIntegrationLineageMode.DevelopThenMain)
            {
                var advance = await MergeIntoDevelopThenMainAsync(
                    project,
                    jobId,
                    jobFolderPath,
                    repoRoot,
                    delivery,
                    branch,
                    ct).ConfigureAwait(false);
                result = advance.Merge;
                preMainResult = advance.PreMainGate;
                preDevelopResult = advance.PreDevelopGate;
                pushBranch = advance.PushBranch;
                approvedPushSha = advance.ApprovedPushSha;
                mechanicalAttributionHandled = advance.MechanicalAttributionHandled;
            }
            else if (IsReleaseBranch(branch))
            {
                (result, preMainResult) = await MergeIntoMainAsync(
                    project,
                    jobId,
                    jobFolderPath,
                    repoRoot,
                    delivery,
                    branch,
                    ct).ConfigureAwait(false);
            }
            else if (delivery.IsRemote)
            {
                (result, preDevelopResult) = await MergeIntoIntegrationGatedAsync(
                    project,
                    jobId,
                    jobFolderPath,
                    repoRoot,
                    branch,
                    () => _git.MergeRemoteDeliveryIntoIntegration(
                        repoRoot,
                        delivery.Ref,
                        delivery.ExpectedResultSha ?? string.Empty,
                        branch,
                        ct)).ConfigureAwait(false);
            }
            else
            {
                (result, preDevelopResult) = await MergeIntoIntegrationGatedAsync(
                    project,
                    jobId,
                    jobFolderPath,
                    repoRoot,
                    branch,
                    () => _git.MergeBranchIntoIntegration(repoRoot, taskBranch, branch, ct)).ConfigureAwait(false);
            }

            if (result.Outcome == MergeIntoIntegrationOutcome.NoTaskBranch
                && TryResolveDirectDelivery(
                    jobFolderPath,
                    repoRoot,
                    branch,
                    out var directEvidence))
            {
                result = MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.AlreadyOnIntegrationBranch,
                    mergedSha: _git.GetBranchTip(repoRoot, branch) ?? directEvidence[^1]) with
                {
                    EvidenceShas = directEvidence,
                };
            }

            if (!mechanicalAttributionHandled
                && result.Outcome == MergeIntoIntegrationOutcome.MergedAfterRebase
                && result.RebasedCommits.Count > 0
                && _taskMutations is not null
                && !_taskMutations.RecordMechanicalRebaseOnFolder(
                    jobFolderPath,
                    result.RebasedCommits))
            {
                var rollback = string.IsNullOrWhiteSpace(result.PreviousIntegrationSha)
                    ? null
                    : _git.ResetHard(repoRoot, result.PreviousIntegrationSha);
                var detail = rollback?.Success == true
                    ? $"Mechanical rebase attribution could not be persisted; {branch} was rolled back and nothing was pushed."
                    : $"Mechanical rebase attribution could not be persisted and rollback failed ({rollback?.Error ?? "missing rollback anchor"}); manual repair is required.";
                result = MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: detail);
            }
            _logger.LogInformation(
                "merge-into-develop project={Project} job={JobId} delivery={Delivery} integration={Integration} strategy={Strategy} outcome={Outcome}",
                project, jobId, taskBranch, branch, strategy, result.Outcome);
            Record(jobFolderPath, project, jobId, branch, result, preMainResult, preDevelopResult, startedAt);

            // AGT-1999: once the accepted task is folded into the integration
            // branch, push that branch to origin so integration is never only
            // local. Offloaded to the background worker (the same "not on the
            // request path" strategy as the completed-job workspace push), so the
            // accept transition never awaits the network round-trip.
            if (pushBranch is null && result.Outcome.IsSuccessfulIntegration())
            {
                // Pin the object the push may publish: the merge result this card's
                // gate released, or, for AlreadyMerged, the exact SHA recovered
                // from the gate receipt. A project without an applicable gate uses
                // the branch tip as it stands at release time. Reading it here and
                // not in the worker closes the gate window: by the time the queued
                // push runs, the tip may carry a merge no gate approved.
                approvedPushSha = !string.IsNullOrWhiteSpace(result.MergedSha)
                    ? result.MergedSha
                    : _git.GetBranchTip(repoRoot, branch);
                pushBranch = branch;
            }
            if (pushBranch is not null)
            {
                MaybeEnqueueIntegrationPush(
                    project,
                    jobId,
                    jobFolderPath,
                    watchPath,
                    pushBranch,
                    approvedPushSha,
                    pipelineType);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "merge-into-develop post-step failed for {JobId}", jobId);
            var errored = MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Error, error: ex.Message);
            try
            {
                Record(
                    jobFolderPath,
                    project,
                    jobId,
                    integrationBranch,
                    errored,
                    preMainResult: null,
                    preDevelopResult: null,
                    startedAt);
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "MergeIntoDevelopRunner: recording is best-effort");
            }
            return errored;
        }
    }

    /// <summary>
    /// Integrates a delivery into the work line and advances the release line
    /// only from the exact resulting <c>develop</c> commit. Both mutations run
    /// under the caller's merge gate, so another immediate integration cannot
    /// interleave between them.
    /// </summary>
    private async Task<LineageAdvanceResult> MergeIntoDevelopThenMainAsync(
        string project,
        string jobId,
        string jobFolderPath,
        string repoRoot,
        DeliveryRefResolution delivery,
        string releaseBranch,
        CancellationToken ct)
    {
        const string workBranch = "develop";
        var (developMerge, preDevelopGate) = delivery.IsRemote
            ? await MergeIntoIntegrationGatedAsync(
                project,
                jobId,
                jobFolderPath,
                repoRoot,
                workBranch,
                () => _git.MergeRemoteDeliveryIntoIntegration(
                    repoRoot,
                    delivery.Ref,
                    delivery.ExpectedResultSha ?? string.Empty,
                    workBranch,
                    ct)).ConfigureAwait(false)
            : await MergeIntoIntegrationGatedAsync(
                project,
                jobId,
                jobFolderPath,
                repoRoot,
                workBranch,
                () => _git.MergeBranchIntoIntegration(
                    repoRoot,
                    delivery.Ref,
                    workBranch,
                    ct)).ConfigureAwait(false);

        if (!developMerge.Outcome.IsSuccessfulIntegration())
        {
            return new(
                developMerge,
                null,
                preDevelopGate,
                null,
                null,
                MechanicalAttributionHandled: false);
        }

        var developSha = !string.IsNullOrWhiteSpace(developMerge.MergedSha)
            ? developMerge.MergedSha
            : _git.GetBranchTip(repoRoot, workBranch);
        if (string.IsNullOrWhiteSpace(developSha))
        {
            return new(
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: "Immediate integration could not resolve the exact develop merge commit."),
                null,
                preDevelopGate,
                null,
                null,
                MechanicalAttributionHandled: false);
        }

        var attributionHandled = false;
        if (developMerge.Outcome == MergeIntoIntegrationOutcome.MergedAfterRebase
            && developMerge.RebasedCommits.Count > 0
            && _taskMutations is not null)
        {
            attributionHandled = true;
            if (!_taskMutations.RecordMechanicalRebaseOnFolder(
                    jobFolderPath,
                    developMerge.RebasedCommits))
            {
                var rollback = string.IsNullOrWhiteSpace(developMerge.PreviousIntegrationSha)
                    ? null
                    : _git.ResetHard(repoRoot, developMerge.PreviousIntegrationSha);
                var detail = rollback?.Success == true
                    ? "Mechanical rebase attribution could not be persisted; develop was rolled back and main remained unchanged."
                    : $"Mechanical rebase attribution could not be persisted and develop rollback failed ({rollback?.Error ?? "missing rollback anchor"}); manual repair is required.";
                return new(
                    MergeIntoIntegrationResult.Of(
                        MergeIntoIntegrationOutcome.Error,
                        error: detail),
                    null,
                    preDevelopGate,
                    null,
                    null,
                    MechanicalAttributionHandled: true);
            }
        }

        var (mainAdvance, preMainGate) = await PromoteDevelopToMainAsync(
            project,
            jobId,
            jobFolderPath,
            repoRoot,
            workBranch,
            releaseBranch,
            ct).ConfigureAwait(false);
        if (!mainAdvance.Outcome.IsSuccessfulIntegration())
        {
            return new(
                mainAdvance,
                preMainGate,
                preDevelopGate,
                workBranch,
                developSha,
                attributionHandled);
        }

        var result = developMerge.Outcome == MergeIntoIntegrationOutcome.MergedAfterRebase
            ? mainAdvance with
            {
                Outcome = MergeIntoIntegrationOutcome.MergedAfterRebase,
                MergedSha = developSha,
                RebasedCommits = developMerge.RebasedCommits,
                PreviousIntegrationSha = developMerge.PreviousIntegrationSha,
            }
            : mainAdvance with { MergedSha = developSha };
        return new(
            result,
            preMainGate,
            preDevelopGate,
            releaseBranch,
            developSha,
            attributionHandled);
    }

    /// <summary>
    /// Performs a non-release integration merge behind the pre-develop build gate
    /// (<see cref="PreDevelopBuildGate"/>). The gated subject is the MERGE RESULT,
    /// not the delivery: only after the merge commit exists is there something to
    /// build that nobody built before.
    ///
    /// <list type="number">
    /// <item>capture the exact pre-merge tip of the integration branch;</item>
    /// <item>merge exactly as before (<paramref name="merge"/>);</item>
    /// <item>build the merged SHA in an isolated worktree - the live checkout is
    ///   only an object store, never a command workspace;</item>
    /// <item>red gate -&gt; hard-reset the integration branch back to the captured
    ///   tip and report <see cref="MergeIntoIntegrationOutcome.GateFailed"/>, so
    ///   nothing is pushed and the card is honestly not integrated;</item>
    /// <item>green gate -&gt; the merge stands and the push is enqueued as before.</item>
    /// </list>
    ///
    /// <para>The whole sequence runs inside the caller's <c>_mergeGate</c>, so the
    /// merge, the gate, and the rollback are one atomic step against the shared
    /// integration checkout.</para>
    /// </summary>
    private async Task<(MergeIntoIntegrationResult Merge, BuildTestGateResult? Gate)> MergeIntoIntegrationGatedAsync(
        string project,
        string jobId,
        string jobFolderPath,
        string repoRoot,
        string integrationBranch,
        Func<MergeIntoIntegrationResult> merge)
    {
        var profile = BuildProfileFor(project);
        var result = merge();
        if (!result.Outcome.IsSuccessfulIntegration())
            return (result, null);

        var gatedSha = result.Outcome.IsFreshMerge()
            ? result.MergedSha
            : _git.GetBranchTip(repoRoot, integrationBranch);
        if (string.IsNullOrWhiteSpace(gatedSha))
        {
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: $"The build gate could not resolve the exact {integrationBranch} SHA."),
                null);
        }

        // The remote-delivery merge may create the configured local integration
        // branch from origin. In that case there was no local tip to capture
        // before the merge. The first parent of the new --no-ff merge commit is
        // the exact synchronized integration tip and therefore the authoritative
        // rollback anchor.
        var preMergeTip = _git.GetFirstParent(repoRoot, gatedSha);
        var changedPaths = string.IsNullOrWhiteSpace(preMergeTip)
            ? null
            : _git.ChangedPathsAgainstMergeBase(repoRoot, preMergeTip, gatedSha);
        var gateApplies = PreDevelopBuildGate.AppliesTo(profile, changedPaths);
        if (!gateApplies && changedPaths is not null)
        {
            const string skipReason =
                "the merge does not touch frontend/ and the project declares no build-profile build commands";
            _logger.LogInformation(
                "merge-into-develop build gate skipped for project={Project} job={JobId} integration={Integration}: {Reason}",
                project, jobId, integrationBranch, skipReason);
            return (result, null);
        }
        if (result.Outcome.IsFreshMerge()
            && string.IsNullOrWhiteSpace(preMergeTip))
        {
            var missingAnchorReason =
                $"The build gate could not determine the pre-merge tip of {integrationBranch}; " +
                "the merged branch requires manual verification and repair.";
            var missingAnchor = new BuildTestGateResult(
                BuildTestGateVerdict.Fail,
                null,
                0,
                string.Empty,
                missingAnchorReason,
                false,
                false)
            {
                ExpectedSha = gatedSha,
                FailureKind = BuildTestGateFailureKind.MissingSource,
            };
            RecordGateEvidence(jobFolderPath, "pre-develop-build-gate", missingAnchor);
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    mergedSha: gatedSha,
                    error: missingAnchor.Reason),
                missingAnchor);
        }

        // BP-02: ancestry proves only that the delivery is present. It does not
        // prove that the merge result passed its gate before a process died.
        // Recovery may reuse only a durable verdict whose expected and tested
        // SHA match the exact branch object it is about to release.
        var gate = result.Outcome == MergeIntoIntegrationOutcome.AlreadyMerged
            ? ReadExactGateVerdict(jobFolderPath, "pre-develop-build-gate", gatedSha)
            : null;
        if (gate is null)
        {
            if (changedPaths is null)
            {
                gate = new BuildTestGateResult(
                    BuildTestGateVerdict.Fail,
                    null,
                    0,
                    string.Empty,
                    "The pre-develop gate could not derive the exact merge-result changed-file set.",
                    false,
                    false)
                {
                    ExpectedSha = gatedSha,
                    FailureKind = BuildTestGateFailureKind.MissingSource,
                };
            }
            else if (_preDevelopBuildGate is null)
            {
                gate = new BuildTestGateResult(
                    BuildTestGateVerdict.Fail,
                    null,
                    0,
                    string.Empty,
                    "The applicable pre-develop build gate is not available.",
                    false,
                    false)
                {
                    ExpectedSha = gatedSha,
                    FailureKind = BuildTestGateFailureKind.MissingSource,
                };
            }
            else
            {
                gate = await _preDevelopBuildGate.RunAsync(
                    new BuildTestGateRequest(repoRoot, gatedSha, "merge-into-develop-build-gate")
                    {
                        Project = project,
                        JobId = jobId,
                        Lane = TaskStates.Completed,
                        TestExecution = TestExecutionFor(project),
                        JobFolderPath = jobFolderPath,
                        SubjectRef = integrationBranch,
                    },
                    changedPaths,
                    profile,
                    _preDevelopTimeout,
                    // Deliberately NOT the caller's token: once the background worker
                    // starts a merge, its gate and possible rollback must reach a
                    // consistent terminal state. The gate stays bounded by its timeout.
                    CancellationToken.None).ConfigureAwait(false);
            }
            RecordGateEvidence(jobFolderPath, "pre-develop-build-gate", gate);
        }
        else
        {
            _logger.LogInformation(
                "merge-into-develop recovered exact build-gate verdict for project={Project} job={JobId} integration={Integration} sha={Sha} verdict={Verdict}",
                project, jobId, integrationBranch, gatedSha, gate.Verdict);
        }

        if (PreDevelopBuildGate.IsGreen(gate))
        {
            _logger.LogInformation(
                "merge-into-develop build gate passed for project={Project} job={JobId} integration={Integration} merged={MergedSha} verdict={Verdict}",
                project, jobId, integrationBranch, gatedSha, gate.Verdict);
            if (result.Outcome == MergeIntoIntegrationOutcome.AlreadyMerged)
            {
                result = MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.AlreadyMerged,
                    mergedSha: gatedSha);
            }
            return (result, gate);
        }

        // A merge created by this invocation owns an exact rollback anchor. An
        // AlreadyMerged recovery does not know who created the existing graph,
        // so it fails closed without rewriting that branch. In both cases the
        // failed outcome prevents Passed and prevents a push.
        var reset = result.Outcome.IsFreshMerge()
            ? _git.ResetHard(repoRoot, preMergeTip!)
            : null;
        _logger.LogWarning(
            "merge-into-develop build gate FAILED for project={Project} job={JobId} integration={Integration} merged={MergedSha} verdict={Verdict} reason={Reason} rollback={Rollback}",
            project, jobId, integrationBranch, gatedSha, gate.Verdict, gate.Reason,
            reset is null
                ? "not-attempted-existing-history"
                : reset.Success
                    ? "reset-to-pre-merge-tip"
                    : "FAILED: " + (reset.Error ?? "unknown"));

        var error = result.Outcome == MergeIntoIntegrationOutcome.AlreadyMerged
            ? $"The build gate blocked recovery of the existing {integrationBranch} commit {Short(gatedSha)}: {gate.Reason}. " +
              "The integration history was left unchanged, no push was released, and the delivery needs manual repair."
            : reset!.Success
            ? $"The build gate blocked the merge into {integrationBranch}: {gate.Reason}. " +
              $"{integrationBranch} was rolled back to {Short(preMergeTip!)} and nothing was pushed; " +
              "start a steer round so the delivery builds on top of the current integration branch."
            : $"The build gate blocked the merge into {integrationBranch}: {gate.Reason}. " +
              $"Rolling {integrationBranch} back to {Short(preMergeTip!)} FAILED ({reset.Error ?? "unknown error"}); " +
              "the unverified merge is still on the local integration branch and needs manual repair.";
        return (MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.GateFailed, error: error), gate);
    }

    /// <summary>
    /// The project's declared build profile, or null when no settings service is
    /// wired (legacy fixtures) or the read fails. A null profile still permits
    /// the convention-derived frontend work-package gate.
    /// </summary>
    private BuildProfile? BuildProfileFor(string project)
    {
        if (_projectSettings == null) return null;
        try { return _projectSettings.Get(project).BuildProfile; }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "MergeIntoDevelopRunner: build-profile read is best-effort");
            return null;
        }
    }

    private TestExecutionPolicy? TestExecutionFor(string project)
    {
        if (_projectSettings == null) return null;
        try { return _projectSettings.Get(project).TestExecution; }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "MergeIntoDevelopRunner: test-execution read is best-effort");
            return null;
        }
    }

    private async Task<(MergeIntoIntegrationResult Merge, BuildTestGateResult? Gate)> PromoteDevelopToMainAsync(
        string project,
        string jobId,
        string jobFolderPath,
        string repoRoot,
        string workBranch,
        string releaseBranch,
        CancellationToken ct)
    {
        var sourceSha = _git.GetBranchTip(repoRoot, workBranch);
        var targetSha = _git.GetBranchTip(repoRoot, releaseBranch);
        if (string.IsNullOrWhiteSpace(sourceSha) || string.IsNullOrWhiteSpace(targetSha))
        {
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: "Could not resolve the exact develop and main SHAs for immediate release integration."),
                null);
        }
        if (!_git.IsAncestor(repoRoot, releaseBranch, workBranch))
        {
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: "Immediate integration refused to advance main because the develop candidate is not a descendant of main."),
                null);
        }
        if (string.Equals(sourceSha, targetSha, StringComparison.OrdinalIgnoreCase))
        {
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.AlreadyMerged,
                    mergedSha: sourceSha),
                null);
        }
        if (_preMainTestGate is null || _projectSettings is null)
        {
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: "Pre-main test gate is unavailable; refusing to advance main."),
                null);
        }

        var changedPaths = _git.ChangedPathsAgainstMergeBase(repoRoot, releaseBranch, workBranch);
        BuildTestGateResult gate;
        if (DocsOnlyDeliveryPolicy.IsDocsOnly(changedPaths))
        {
            gate = new BuildTestGateResult(
                BuildTestGateVerdict.Skipped,
                null,
                0,
                string.Empty,
                $"Docs-only develop candidate ({changedPaths!.Count} changed path(s), all documentation/evidence); "
                + "the full-suite release gate is not applicable and main may fast-forward to the exact develop commit.",
                false,
                false)
            {
                ExpectedSha = sourceSha,
                TestedSha = sourceSha,
            };
            RecordGateEvidence(jobFolderPath, "pre-main-test-gate", gate);
            _logger.LogInformation(
                "merge-into-main develop-candidate docs-only light gate for project={Project} job={JobId} changedPaths={Count}",
                project,
                jobId,
                changedPaths.Count);
        }
        else
        {
            var settings = _projectSettings.Get(project);
            gate = await _preMainTestGate.RunAsync(
                new BuildTestGateRequest(repoRoot, sourceSha, "merge-into-main")
                {
                    Project = project,
                    JobId = jobId,
                    Lane = TaskStates.Completed,
                    TestExecution = settings.TestExecution,
                    JobFolderPath = jobFolderPath,
                    SubjectRef = workBranch,
                },
                settings.BuildProfile,
                _preMainTimeout,
                ct).ConfigureAwait(false);
            RecordGateEvidence(jobFolderPath, "pre-main-test-gate", gate);
            if (gate.Verdict != BuildTestGateVerdict.Ok)
            {
                return (
                    MergeIntoIntegrationResult.Of(
                        MergeIntoIntegrationOutcome.Error,
                        error: $"Pre-main full suite blocked the develop-to-main fast-forward: {gate.Reason}"),
                    gate);
            }
        }

        return (
            _git.MergeBranchFastForward(
                repoRoot,
                workBranch,
                releaseBranch,
                sourceSha,
                targetSha),
            gate);
    }

    private async Task<(MergeIntoIntegrationResult Merge, BuildTestGateResult? Gate)> MergeIntoMainAsync(
        string project,
        string jobId,
        string jobFolderPath,
        string repoRoot,
        DeliveryRefResolution delivery,
        string releaseBranch,
        CancellationToken ct)
    {
        var taskBranch = delivery.Ref;
        if (delivery.IsRemote)
        {
            if (!ReviewSubjectStore.IsValidResultSha(delivery.ExpectedResultSha))
            {
                return (
                    MergeIntoIntegrationResult.Of(
                        MergeIntoIntegrationOutcome.Error,
                        error: $"Remote delivery '{taskBranch}' has no valid fenced result SHA."),
                    null);
            }
            var inspected = _git.InspectRemoteDeliveryCommitRange(
                repoRoot,
                taskBranch,
                delivery.ExpectedResultSha!,
                releaseBranch,
                ct);
            if (!inspected.Success)
            {
                return (
                    MergeIntoIntegrationResult.Of(
                        MergeIntoIntegrationOutcome.NoTaskBranch,
                        error: inspected.Warning),
                    null);
            }
            taskBranch = "origin/" + taskBranch;
        }
        else if (!_git.BranchExists(repoRoot, taskBranch))
        {
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.NoTaskBranch,
                    error: $"Task branch '{taskBranch}' does not exist."),
                null);
        }
        if (!_git.BranchExists(repoRoot, releaseBranch))
        {
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: $"Release branch '{releaseBranch}' does not exist."),
                null);
        }
        if (_git.IsAncestor(repoRoot, taskBranch, releaseBranch))
        {
            return (
                MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.AlreadyMerged),
                null);
        }
        if (_preMainTestGate is null || _projectSettings is null)
        {
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: "Pre-main test gate is unavailable; refusing to advance main."),
                null);
        }

        var sourceSha = _git.GetBranchTip(repoRoot, taskBranch);
        var targetSha = _git.GetBranchTip(repoRoot, releaseBranch);
        if (string.IsNullOrWhiteSpace(sourceSha) || string.IsNullOrWhiteSpace(targetSha))
        {
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: "Could not resolve the exact source and main SHAs for the pre-main gate."),
                null);
        }

        // AGT-2417 docs rule: a delivery whose whole diff (against the merge
        // base) is documentation / task evidence cannot change any build or
        // test signal. It skips the full-suite release gate AND the
        // rebased-onto-main requirement and integrates through the same
        // conflict-checked merge the non-release path uses; a real conflict
        // still surfaces honestly. Unknown diffs stay on the strict path.
        var changedPaths = _git.ChangedPathsAgainstMergeBase(repoRoot, releaseBranch, taskBranch);
        if (DocsOnlyDeliveryPolicy.IsDocsOnly(changedPaths))
        {
            var lightGate = new BuildTestGateResult(
                BuildTestGateVerdict.Skipped,
                null,
                0,
                string.Empty,
                $"Docs-only delivery ({changedPaths!.Count} changed path(s), all documentation/evidence); " +
                "the full-suite release gate is not applicable and a conflict-checked merge probe integrates directly.",
                false,
                false)
            {
                ExpectedSha = sourceSha,
            };
            RecordGateEvidence(jobFolderPath, "pre-main-test-gate", lightGate);
            _logger.LogInformation(
                "merge-into-main docs-only light gate for project={Project} job={JobId} changedPaths={Count}",
                project, jobId, changedPaths.Count);
            var docsMerge = delivery.IsRemote
                ? _git.MergeRemoteDeliveryIntoIntegration(
                    repoRoot,
                    delivery.Ref,
                    delivery.ExpectedResultSha ?? string.Empty,
                    releaseBranch,
                    ct)
                : _git.MergeBranchIntoIntegration(repoRoot, taskBranch, releaseBranch, ct);
            return (docsMerge, lightGate);
        }

        if (!_git.IsAncestor(repoRoot, releaseBranch, taskBranch))
        {
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: $"Release source '{taskBranch}' must be rebased onto '{releaseBranch}' before the full-suite gate."),
                null);
        }

        var settings = _projectSettings.Get(project);
        var gate = await _preMainTestGate.RunAsync(
            new BuildTestGateRequest(repoRoot, sourceSha, "merge-into-main")
            {
                Project = project,
                JobId = jobId,
                Lane = TaskStates.Completed,
                TestExecution = settings.TestExecution,
                JobFolderPath = jobFolderPath,
                SubjectRef = taskBranch,
            },
            settings.BuildProfile,
            _preMainTimeout,
            ct).ConfigureAwait(false);
        RecordGateEvidence(jobFolderPath, "pre-main-test-gate", gate);

        if (gate.Verdict != BuildTestGateVerdict.Ok)
        {
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: $"Pre-main full suite blocked the merge: {gate.Reason}"),
                gate);
        }

        var merge = _git.MergeBranchFastForward(
            repoRoot,
            taskBranch,
            releaseBranch,
            sourceSha,
            targetSha);
        return (merge, gate);
    }

    private static bool IsReleaseBranch(string branch)
        => string.Equals(branch, "main", StringComparison.OrdinalIgnoreCase);

    private bool HasDevelopLine(string repoRoot)
        => string.Equals(
            _git.ResolveIntegrationBranch(repoRoot, "develop"),
            "develop",
            StringComparison.OrdinalIgnoreCase);

    private sealed record LineageAdvanceResult(
        MergeIntoIntegrationResult Merge,
        BuildTestGateResult? PreMainGate,
        BuildTestGateResult? PreDevelopGate,
        string? PushBranch,
        string? ApprovedPushSha,
        bool MechanicalAttributionHandled);

    /// <summary>
    /// Enqueues the integration-branch push onto the background
    /// <see cref="IntegrationPushQueue"/> when one is wired (production) and the
    /// push step is enabled for the project. No queue means no offload wiring
    /// (the unit-test fixtures) - <see cref="Run"/> then stays merge-only and a
    /// test drives <see cref="PushIntegrationBranchAsync"/> directly. Never
    /// throws: the merge has already landed and the push is best-effort.
    /// </summary>
    private void MaybeEnqueueIntegrationPush(
        string project, string jobId, string jobFolderPath, string? watchPath, string integrationBranch,
        string? approvedSha,
        string pipelineType)
    {
        if (_pushQueue == null) return;
        if (!IntegrationPushEnabled(project, pipelineType))
        {
            _logger.LogInformation(
                "merge-into-develop push disabled for project={Project} job={JobId}; leaving origin unchanged",
                project, jobId);
            return;
        }

        var enqueued = _pushQueue.Enqueue(new IntegrationPushRequest(
            project, jobId, jobFolderPath, watchPath, integrationBranch, approvedSha));
        if (enqueued)
            _logger.LogInformation(
                "merge-into-develop push enqueued for project={Project} job={JobId} branch={Branch} approved={ApprovedSha}",
                project, jobId, integrationBranch, approvedSha ?? "branch-tip");
        else
            _logger.LogWarning(
                "merge-into-develop push enqueue failed (queue closed) for project={Project} job={JobId}",
                project, jobId);
    }

    /// <summary>
    /// True unless the operator disabled the deferred push step for this project
    /// (<see cref="PipelineCatalogue.MergeIntoDevelopPushStepId"/>, default on).
    /// A missing settings service (legacy fixtures) defaults to on.
    /// </summary>
    private bool IntegrationPushEnabled(string project, string pipelineType)
    {
        if (_projectSettings == null) return true;
        try
        {
            var settings = PipelineTypeSettings.ForType(_projectSettings.Get(project), pipelineType);
            return PipelineStepConfigResolver.IsEnabled(settings, PipelineCatalogue.MergeIntoDevelopPushStepId);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "MergeIntoDevelopRunner: push-enabled read is best-effort; default on");
            return true;
        }
    }

    /// <summary>
    /// Pushes the integration branch to <c>origin</c> and records the deferred
    /// <see cref="PipelineCatalogue.MergeIntoDevelopPushStepId"/> step outcome.
    /// This is the offloaded body the <see cref="IntegrationPushWorker"/> runs
    /// (and tests drive directly). A transient failure is classified environmental
    /// and retried with backoff per the AGT-1944 taxonomy; once the retry budget
    /// is spent the step is recorded <see cref="PipelineStepStatus.Failed"/>
    /// flagged <c>environmental</c> so a reviewer does not read an infra blip as a
    /// failed change. Never throws (except on cooperative cancellation).
    /// <para>
    /// <paramref name="approvedSha"/> is the merge result the gate released. It is
    /// what gets pushed, so nothing that landed on the integration branch after
    /// the approval rides along. Omitting it keeps the historical branch-tip push
    /// and is reserved for callers that have no approval record (the durable
    /// restart backstop).
    /// </para>
    /// </summary>
    public async Task<GitPushResult> PushIntegrationBranchAsync(
        string project,
        string jobId,
        string jobFolderPath,
        string? watchPath,
        string integrationBranch,
        CancellationToken ct = default,
        string? approvedSha = null)
    {
        await _pushGate.WaitAsync(ct);
        try
        {
            var repoRoot = _git.ResolveRepoRootForWatchPath(watchPath)
                ?? (string.IsNullOrWhiteSpace(watchPath) ? null : watchPath);
            if (!string.IsNullOrWhiteSpace(repoRoot)
                && IsReleaseBranch(integrationBranch)
                && HasDevelopLine(repoRoot))
            {
                // AGT-2688: publish develop first and unconditionally. The
                // lineage guard below only ever decides whether MAIN may
                // follow; it must never gate develop's own push, or origin/develop
                // starves on an unrelated main/develop divergence while the
                // delivery sits merged-but-invisible and acceptance loops
                // forever reading it as still pending.
                var developPush = await PushIntegrationBranchSerializedAsync(
                    project,
                    jobId,
                    jobFolderPath,
                    watchPath,
                    "develop",
                    approvedSha,
                    ct,
                    recordSuccessfulStep: false);
                if (!developPush.Success)
                    return developPush;

                var decision = ImmediateIntegrationLineagePolicy.Decide(
                    integrationBranch,
                    developAvailable: true,
                    mainIsAncestorOfDevelop: _git.IsAncestor(repoRoot, integrationBranch, "develop"));
                if (decision.Mode == ImmediateIntegrationLineageMode.Blocked)
                {
                    var blocked = new GitPushResult(
                        false,
                        approvedSha ?? developPush.Sha,
                        "lineage-blocked",
                        decision.Reason);
                    RecordPushStep(jobFolderPath, project, jobId, blocked, DateTime.UtcNow, environmentalRetries: 0);
                    _logger.LogWarning(
                        "merge-into-develop push: develop published to origin but {Branch} is blocked project={Project} job={JobId}; {Reason}",
                        integrationBranch, project, jobId, decision.Reason);
                    return blocked;
                }
            }

            return await PushIntegrationBranchSerializedAsync(
                project,
                jobId,
                jobFolderPath,
                watchPath,
                integrationBranch,
                approvedSha,
                ct);
        }
        finally
        {
            _pushGate.Release();
        }
    }

    private async Task<GitPushResult> PushIntegrationBranchSerializedAsync(
        string project,
        string jobId,
        string jobFolderPath,
        string? watchPath,
        string integrationBranch,
        string? approvedSha,
        CancellationToken ct,
        bool recordSuccessfulStep = true)
    {
        var startedAt = DateTime.UtcNow;
        GitPushResult result;
        var environmentalRetries = 0;
        try
        {
            var repoRoot = _git.ResolveRepoRootForWatchPath(watchPath)
                ?? (string.IsNullOrWhiteSpace(watchPath) ? null : watchPath);
            if (string.IsNullOrWhiteSpace(repoRoot))
            {
                result = new GitPushResult(false, string.Empty, "repo-missing", "Could not resolve repository root for the integration push.");
            }
            else
            {
                var branch = _git.ResolveIntegrationBranch(repoRoot, integrationBranch);
                while (true)
                {
                    result = await _git.PushIntegrationBranchAsync(repoRoot, branch, ct, approvedSha);
                    if (result.Success) break;

                    var issue = ClassifyPushFailure(result.Status);
                    if (!PostProcessingOutcomeTaxonomy.IsRetryableEnvironmental(issue)) break;

                    var decision = PostProcessingOutcomeTaxonomy.DecideEnvironmentalRetry(issue, environmentalRetries);
                    if (decision.Action != EnvironmentalRetryAction.RetryWithBackoff) break;

                    environmentalRetries = decision.Attempt;
                    var backoff = _environmentalBackoff(environmentalRetries);
                    _logger.LogWarning(
                        "merge-into-develop push failed (environmental {Status}) project={Project} job={JobId} branch={Branch}; {Reason} (backoff {Backoff})",
                        result.Status, project, jobId, branch, decision.Reason, backoff);
                    if (backoff > TimeSpan.Zero)
                    {
                        try { await Task.Delay(backoff, ct); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "merge-into-develop push threw for project={Project} job={JobId}", project, jobId);
            result = new GitPushResult(false, string.Empty, "error", ex.Message);
        }

        _logger.LogInformation(
            "merge-into-develop-push project={Project} job={JobId} branch={Branch} status={Status} retries={Retries}",
            project, jobId, integrationBranch, result.Status, environmentalRetries);
        try
        {
            if (!result.Success || recordSuccessfulStep)
                RecordPushStep(jobFolderPath, project, jobId, result, startedAt, environmentalRetries);
        }
        catch (Exception ex) { SilentCatch.Note(ex, "MergeIntoDevelopRunner: push-step recording is best-effort"); }
        return result;
    }

    /// <summary>
    /// Maps a failed push status to the AGT-1944 issue kind that drives the
    /// retry / escalation decision. A generic push failure is treated as a
    /// transient environmental fault (network / remote availability) that retries;
    /// a non-fast-forward rejection is a diverged remote - an environmental
    /// blocker a blind retry cannot clear, so it escalates immediately (visible).
    /// Anything else is a non-environmental hard error.
    /// </summary>
    private static RunIssueKind ClassifyPushFailure(string status) => status switch
    {
        "failed" => RunIssueKind.EnvironmentalTransient,
        "remote-rejected" => RunIssueKind.EnvironmentBlocker,
        _ => RunIssueKind.None,
    };

    private void RecordPushStep(
        string jobFolderPath,
        string project,
        string jobId,
        GitPushResult result,
        DateTime startedAt,
        int environmentalRetries)
    {
        var pipelineRecord = _pipelineLog.Read(jobFolderPath)
            ?? _pipelineLog.EnsureRun(jobFolderPath, PipelineCatalogue.Standard, project, jobId);
        using var pipelineAttempt = _pipelineLog.EnterAttempt(jobFolderPath, pipelineRecord.Attempt);

        var completedAt = DateTime.UtcNow;
        var (status, verdict, reason, summary) = ProjectPush(result, environmentalRetries);
        var failure = AcceptedIntegrationFailurePolicy.Classify(status, verdict, reason, summary);

        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopPushStepId,
            Kind = StepKind.Tool,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationMs = (long)(completedAt - startedAt).TotalMilliseconds,
            Verdict = verdict,
            VerdictSummary = summary,
            Reason = reason,
            FailureCode = failure?.Code,
        });
    }

    private static (PipelineStepStatus Status, string? Verdict, string? Reason, string? Summary) ProjectPush(
        GitPushResult result, int environmentalRetries)
    {
        if (result.Success)
        {
            return result.Status switch
            {
                "pushed" => (PipelineStepStatus.Passed, "pushed", $"Pushed integration branch to origin{ShaSuffix(result.Sha)}.", null),
                "already-remote" => (PipelineStepStatus.Passed, "already-remote", "Integration branch already up to date on origin; nothing to push.", null),
                "no-remote" => (PipelineStepStatus.Skipped, "no-remote", "No origin remote configured; nothing to push.", null),
                _ => (PipelineStepStatus.Passed, result.Status, "Integration branch push completed.", null),
            };
        }

        if (string.Equals(result.Status, "lineage-blocked", StringComparison.Ordinal))
        {
            // AGT-2688: honest, distinct terminal state. develop already reached
            // origin by the time this is recorded (see PushIntegrationBranchAsync);
            // only main is withheld pending real lineage convergence. This must
            // not be confused with a plain in-flight push (which would read as
            // "pending" and loop the accepted-integration backstop forever).
            return (
                PipelineStepStatus.Failed,
                "lineage-blocked",
                $"Integration push blocked: {result.Error ?? "main is not an ancestor of develop yet."}",
                null);
        }

        var issue = ClassifyPushFailure(result.Status);
        if (issue == RunIssueKind.EnvironmentBlocker)
        {
            // A non-fast-forward rejection is a genuinely diverged remote, not a
            // transient blip a retry can clear. Distinct verdict from
            // "environmental" so it surfaces as integration-push-blocked instead
            // of reading as an ordinary pending push (AGT-2688).
            return (
                PipelineStepStatus.Failed,
                "push-blocked",
                $"Push of the integration branch to origin was rejected ({result.Status}); the remote has diverged and needs reconciliation.",
                result.Error);
        }

        if (PostProcessingOutcomeTaxonomy.IsEnvironmental(issue))
        {
            // AGT-1944: flag the failure environmental so a reviewer does not read
            // an infra blip as a failed change. Retryable transients reach here
            // only after their retry budget is spent.
            var retried = environmentalRetries > 0
                ? $" after {environmentalRetries} retr{(environmentalRetries == 1 ? "y" : "ies")}"
                : string.Empty;
            return (
                PipelineStepStatus.Failed,
                "environmental",
                $"Push of the integration branch to origin failed{retried} ({result.Status}); flagged environmental (AGT-1944).",
                result.Error);
        }

        return (PipelineStepStatus.Failed, "error", $"Push of the integration branch to origin failed ({result.Status}).", result.Error);
    }

    private static string ShaSuffix(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? string.Empty : $" ({Short(sha!)})";

    private void Record(
        string jobFolderPath,
        string project,
        string jobId,
        string integrationBranch,
        MergeIntoIntegrationResult result,
        BuildTestGateResult? preMainResult,
        BuildTestGateResult? preDevelopResult,
        DateTime startedAt)
    {
        // Record into the existing run when one is present (the deferred merge
        // step already sits in it as "pending"); only begin a fresh baseline when
        // none exists yet, so RecordStep is never a silent no-op. The merge step
        // only lives in the standard pipeline (the read-only variant drops every
        // git step), so the baseline uses the standard catalogue.
        var pipelineRecord = _pipelineLog.Read(jobFolderPath)
            ?? _pipelineLog.EnsureRun(jobFolderPath, PipelineCatalogue.Standard, project, jobId);
        using var pipelineAttempt = _pipelineLog.EnterAttempt(jobFolderPath, pipelineRecord.Attempt);

        var completedAt = DateTime.UtcNow;
        var (status, verdict, reason, summary) = Project(
            result, integrationBranch, preMainResult, preDevelopResult);
        var failure = AcceptedIntegrationFailurePolicy.Classify(
            status,
            verdict,
            reason,
            summary);

        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationMs = (long)(completedAt - startedAt).TotalMilliseconds,
            Verdict = verdict,
            VerdictSummary = summary,
            Reason = reason,
            FailureCode = failure?.Code,
        });
    }

    /// <summary>
    /// Reads the newest durable gate receipt for one exact subject. A receipt is
    /// applicable only when both the expected and tested SHAs match; malformed
    /// or partial crash debris is ignored and forces a fresh gate.
    /// </summary>
    private static BuildTestGateResult? ReadExactGateVerdict(
        string jobFolderPath,
        string prefix,
        string expectedSha)
    {
        var dir = Path.Combine(jobFolderPath, "post-steps");
        if (!Directory.Exists(dir)) return null;

        foreach (var path in Directory.GetFiles(dir, $"{prefix}-*.log")
                     .OrderByDescending(GateEvidenceIndex))
        {
            try
            {
                using var reader = new StreamReader(path);
                var verdictLine = reader.ReadLine();
                var shaLine = reader.ReadLine();
                var reasonLine = reader.ReadLine();
                if (verdictLine is null || shaLine is null || reasonLine is null) continue;

                var verdictValue = HeaderValue(verdictLine, "verdict=");
                var recordedExpected = HeaderValue(shaLine, "expectedSha=");
                var recordedTested = HeaderValue(shaLine, "testedSha=");
                if (!Enum.TryParse<BuildTestGateVerdict>(verdictValue, ignoreCase: true, out var verdict)
                    || !string.Equals(recordedExpected, expectedSha, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(recordedTested, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var exitValue = HeaderValue(verdictLine, "exit=");
                int? exitCode = int.TryParse(exitValue, out var parsedExitCode)
                    ? parsedExitCode
                    : null;
                _ = long.TryParse(HeaderValue(verdictLine, "durationMs="), out var durationMs);
                var reason = reasonLine.StartsWith("reason=", StringComparison.Ordinal)
                    ? reasonLine["reason=".Length..]
                    : "Recovered durable gate verdict.";
                return new BuildTestGateResult(
                    verdict,
                    exitCode,
                    durationMs,
                    string.Empty,
                    reason,
                    false,
                    false)
                {
                    ExpectedSha = recordedExpected,
                    TestedSha = recordedTested == "n/a" ? null : recordedTested,
                };
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "MergeIntoDevelopRunner: corrupt gate evidence is ignored");
            }
        }

        return null;
    }

    private static int GateEvidenceIndex(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var separator = name.LastIndexOf('-');
        return separator >= 0 && int.TryParse(name[(separator + 1)..], out var index)
            ? index
            : 0;
    }

    private static string? HeaderValue(string line, string key)
    {
        var start = line.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return null;
        start += key.Length;
        var end = line.IndexOf(' ', start);
        return end < 0 ? line[start..] : line[start..end];
    }

    /// <summary>
    /// Writes one numbered gate-evidence log into the job's <c>post-steps</c>
    /// folder (<c>&lt;prefix&gt;-N.log</c>): verdict, exact expected / tested SHA,
    /// the test-selection audit, and the tail of the command output. Same shape
    /// for both merge gates, so the evidence reads identically whether main or
    /// develop was the target.
    /// </summary>
    private static void RecordGateEvidence(
        string jobFolderPath,
        string prefix,
        BuildTestGateResult result)
    {
        var dir = Path.Combine(jobFolderPath, "post-steps");
        Directory.CreateDirectory(dir);
        var index = Directory.GetFiles(dir, $"{prefix}-*.log").Length + 1;
        var selection = System.Text.Json.JsonSerializer.Serialize(
            result.TestSelection,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var dependencyCache = System.Text.Json.JsonSerializer.Serialize(
            result.DependencyCache,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var budget = result.ViolatedBudget is null
            ? "budget=none"
            : $"budget={result.ViolatedBudget.Name} limitMs={result.ViolatedBudget.LimitMs} " +
              $"consumedMs={result.ViolatedBudget.ConsumedMs} phase={result.ViolatedBudget.Phase}";
        var body =
            $"verdict={result.Verdict} exit={result.ExitCode?.ToString() ?? "n/a"} durationMs={result.DurationMs}\n" +
            $"expectedSha={result.ExpectedSha ?? "n/a"} testedSha={result.TestedSha ?? "n/a"}\n" +
            $"reason={result.Reason}\n" +
            budget + "\n" +
            "--- dependency-cache.json ---\n" +
            dependencyCache + "\n" +
            "--- test-selection.json ---\n" +
            selection + "\n" +
            "--- last-300-lines ---\n" +
            result.Output;
        File.WriteAllText(
            Path.Combine(dir, $"{prefix}-{index}.log"),
            body);
    }

    private static (PipelineStepStatus Status, string? Verdict, string? Reason, string? Summary) Project(
        MergeIntoIntegrationResult result,
        string integrationBranch,
        BuildTestGateResult? preMainResult,
        BuildTestGateResult? preDevelopResult)
    {
        switch (result.Outcome)
        {
            case MergeIntoIntegrationOutcome.Merged:
                var sha = string.IsNullOrWhiteSpace(result.MergedSha)
                    ? string.Empty
                    : $" ({Short(result.MergedSha!)})";
                var fullSuite = preMainResult is null
                    ? string.Empty
                    : " after the mandatory full suite passed";
                var buildGate = preDevelopResult is null
                    ? string.Empty
                    : " after the build gate passed on the merge result";
                return (
                    PipelineStepStatus.Passed,
                    "merged",
                    $"Merged into {integrationBranch}{sha}{fullSuite}{buildGate}.",
                    preMainResult?.Reason ?? preDevelopResult?.Reason);
            case MergeIntoIntegrationOutcome.GateFailed:
                return (
                    PipelineStepStatus.Failed,
                    "gate-failed",
                    result.Error ?? $"The build gate blocked the merge into {integrationBranch}.",
                    preDevelopResult?.Reason);
            case MergeIntoIntegrationOutcome.MergedAfterRebase:
                var replacementCount = result.RebasedCommits.Count;
                var rebaseGate = preDevelopResult is null
                    ? string.Empty
                    : $" Build gate {preDevelopResult.Verdict} checked the rebased merge result.";
                return (
                    PipelineStepStatus.Passed,
                    "merged-after-rebase",
                    $"Delivery replayed cleanly onto {integrationBranch} and the rebased result was merged.",
                    $"{replacementCount} commit SHA(s) were superseded.{rebaseGate}");
            case MergeIntoIntegrationOutcome.AlreadyMerged:
                var exactGate = preDevelopResult is null
                    ? string.Empty
                    : $" Exact build-gate verdict {preDevelopResult.Verdict} exists for {Short(result.MergedSha!)}.";
                return (
                    PipelineStepStatus.Passed,
                    "already-merged",
                    $"Task branch already contained in {integrationBranch}; no merge needed.{exactGate}",
                    preDevelopResult?.Reason);
            case MergeIntoIntegrationOutcome.AlreadyOnIntegrationBranch:
                var evidence = result.EvidenceShas.Count == 0
                    ? "attributed commits"
                    : string.Join(", ", result.EvidenceShas.Select(Short));
                return (
                    PipelineStepStatus.Passed,
                    "already-on-integration-branch",
                    $"No task branch exists; attributed commits are already on {integrationBranch}.",
                    $"Evidence: {evidence}.");
            case MergeIntoIntegrationOutcome.NoTaskBranch:
                return (PipelineStepStatus.Skipped, "no-branch", result.Error ?? "No task branch to merge.", null);
            case MergeIntoIntegrationOutcome.PushedForReview:
                return (
                    PipelineStepStatus.Skipped,
                    "pushed-for-review",
                    result.Error ?? "The configured pull-request strategy did not merge the delivery.",
                    null);
            case MergeIntoIntegrationOutcome.Conflict:
                var files = result.ConflictedFiles is { Count: > 0 }
                    ? string.Join(", ", result.ConflictedFiles)
                    : "unknown files";
                return (
                    PipelineStepStatus.Failed,
                    "conflict",
                    $"Merge conflict in {result.ConflictedFiles?.Count ?? 0} file(s); merge aborted, working tree left clean. Start a steer round to rebase the delivery onto the current integration branch.",
                    $"Conflicted: {files}. Recovery: rebase the delivery onto the current integration branch, resolve the conflicts, and accept again.");
            case MergeIntoIntegrationOutcome.AgentRoundRequired:
                var ambiguousFiles = result.ConflictedFiles is { Count: > 0 }
                    ? string.Join(", ", result.ConflictedFiles)
                    : "none recorded";
                return (
                    PipelineStepStatus.Failed,
                    "agent-round-required",
                    result.Error ?? "Automatic merge and cardinality-preserving rebase paths could not retain unambiguous delivery SHA attribution.",
                    $"A bounded automatic steer round is required. Conflicted files: {ambiguousFiles}.");
            default:
                return (PipelineStepStatus.Failed, "error", result.Error ?? "Merge failed.", null);
        }
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private bool TryResolveDirectDelivery(
        string jobFolderPath,
        string repositoryRoot,
        string integrationBranch,
        out IReadOnlyList<string> evidenceShas)
    {
        evidenceShas = [];
        try
        {
            var path = Path.Combine(jobFolderPath, "task.json");
            if (!File.Exists(path)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("commits", out var commitsElement)
                || commitsElement.ValueKind != JsonValueKind.Array)
                return false;

            var origin = _git.ReadOriginUrlAt(repositoryRoot);
            var repositoryLabel = RepositoryLabel(origin ?? repositoryRoot);
            var commits = new List<TaskCommitInfo>();
            foreach (var element in commitsElement.EnumerateArray())
            {
                var commit = JsonSerializer.Deserialize<TaskCommitInfo>(
                    element.GetRawText(),
                    TaskJsonFile.ReadOpts);
                if (commit is null
                    || string.IsNullOrWhiteSpace(commit.Sha)
                    || TaskCommitSupersession.IsSuperseded(commit))
                    continue;
                commit = TaskCommitRepository.NormalizeLegacy(commit);
                if (TaskIntegrationStatusService.IsZeroFileLifecycleMarker(commit)
                    && !_git.IsAncestor(repositoryRoot, commit.Sha, integrationBranch))
                    continue;
                if (!string.IsNullOrWhiteSpace(commit.Repository)
                    && !SameRepository(commit.Repository, origin, repositoryLabel))
                    continue;
                commits.Add(commit);
            }

            if (commits.Count == 0) return false;
            var landed = commits.All(commit =>
                _git.IsAncestor(repositoryRoot, commit.Sha, integrationBranch));
            if (!landed) return false;
            evidenceShas = commits.Select(commit => commit.Sha).ToList();
            return true;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "MergeIntoDevelopRunner: direct-delivery attribution is best-effort");
            return false;
        }
    }

    private static bool SameRepository(string candidate, string? origin, string repositoryLabel)
        => string.Equals(candidate.Trim().TrimEnd('/', '\\'), origin?.Trim().TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)
            || string.Equals(RepositoryLabel(candidate), repositoryLabel, StringComparison.OrdinalIgnoreCase);

    private static string RepositoryLabel(string value)
    {
        var normalized = value.Trim().TrimEnd('/', '\\');
        var separator = Math.Max(normalized.LastIndexOf('/'), Math.Max(normalized.LastIndexOf('\\'), normalized.LastIndexOf(':')));
        var label = separator < 0 ? normalized : normalized[(separator + 1)..];
        return label.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? label[..^4] : label;
    }
}
