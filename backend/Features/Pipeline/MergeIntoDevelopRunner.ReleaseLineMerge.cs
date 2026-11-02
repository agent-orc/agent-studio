namespace AgentStudio.Pipeline;

public sealed partial class MergeIntoDevelopRunner
{
    /// <summary>
    /// Integrates a delivery that is behind the release line (AGT-3435). The
    /// rebased-onto-main rule of AGT-2224 only existed so the tested SHA could
    /// become main by fast-forward; it sent clean deliveries back to the agent
    /// whenever another card moved main during a fix round. The release
    /// invariant is kept by testing the merge result instead:
    ///
    /// <list type="number">
    /// <item>merge the delivery onto the release lane with the same
    ///   <c>--no-ff</c> merge the develop path uses, so every delivery SHA stays
    ///   an ancestor or, after a mechanical rebase, is mapped one-to-one;</item>
    /// <item>a real conflict returns <see cref="MergeIntoIntegrationOutcome.AgentRoundRequired"/>
    ///   with the conflicted files, which starts one bounded automatic round;</item>
    /// <item>run the mandatory full suite on the exact merge SHA; a red verdict
    ///   rolls the lane back and nothing is published;</item>
    /// <item>a green verdict stands only while the merge base still contains the
    ///   published release line; otherwise merge again, at most
    ///   <see cref="ReleaseLineMergePolicy.MaxAttempts"/> times.</item>
    /// </list>
    ///
    /// <para>The caller holds the merge lock and the ref-mutation lease, so only
    /// the published line can move here. The gate journal covers the window in
    /// which the lane carries a merge without a verdict, as on the develop path.</para>
    /// </summary>
    private async Task<(MergeIntoIntegrationResult Merge, BuildTestGateResult? Gate)> MergeBehindReleaseLineAsync(
        string project,
        string jobId,
        string jobFolderPath,
        string developerRoot,
        string repoRoot,
        DeliveryRefResolution delivery,
        string releaseBranch,
        CancellationToken ct)
    {
        var settings = _projectSettings!.Get(project);
        BuildTestGateResult? gate = null;
        for (var attempt = 1; ; attempt++)
        {
            var journalOwner = Guid.NewGuid().ToString("N");
            IntegrationGateJournal.Open(jobFolderPath, new IntegrationGateJournalEntry
            {
                Owner = journalOwner,
                Project = project,
                JobId = jobId,
                RepoRoot = developerRoot,
                IntegrationBranch = releaseBranch,
                PreMergeTip = _git.GetBranchTip(repoRoot, _git.IntegrationBaseRef(repoRoot, releaseBranch)),
                StartedAt = DateTimeOffset.UtcNow,
                Step = PreMainTestGateStep,
            });
            var merge = delivery.IsRemote
                ? _git.MergeRemoteDeliveryIntoIntegration(
                    repoRoot,
                    delivery.Ref,
                    delivery.ExpectedResultSha ?? string.Empty,
                    releaseBranch,
                    ct)
                : _git.MergeBranchIntoIntegration(repoRoot, delivery.Ref, releaseBranch, ct);
            if (!merge.Outcome.IsFreshMerge())
            {
                // Conflict, error, or a release line that already carries the
                // delivery: nothing of this invocation is on the lane, and the
                // caller verifies a contained delivery.
                IntegrationGateJournal.ClearOwned(jobFolderPath, journalOwner);
                return (merge, gate);
            }

            var mergedSha = merge.MergedSha;
            var preMergeTip = string.IsNullOrWhiteSpace(mergedSha) ? null : _git.GetFirstParent(repoRoot, mergedSha);
            if (string.IsNullOrWhiteSpace(mergedSha) || string.IsNullOrWhiteSpace(preMergeTip))
            {
                // The journal keeps the anchor for restart recovery.
                return (
                    MergeIntoIntegrationResult.Failed(
                        AcceptedIntegrationFailureCodes.IntegrationRefUnresolved,
                        $"The pre-main gate could not resolve the exact merge result and base on '{releaseBranch}'; "
                        + "the unverified merge needs manual repair.",
                        mergedSha: mergedSha),
                    gate);
            }
            IntegrationGateJournal.RecordMergeResult(jobFolderPath, mergedSha, preMergeTip);

            var (preMainTimeout, preMainTimeoutSource) = ResolveGateTimeout(project, _preMainTimeout);
            gate = await _preMainTestGate!.RunAsync(
                new BuildTestGateRequest(repoRoot, mergedSha, "merge-into-main")
                {
                    Project = project,
                    JobId = jobId,
                    Lane = TaskStates.Completed,
                    TestExecution = settings.TestExecution,
                    JobFolderPath = jobFolderPath,
                    SubjectRef = releaseBranch,
                    TimeoutBudgetSource = preMainTimeoutSource,
                    InfrastructureTimeout = ResolveGateInfrastructureTimeout(),
                },
                settings.BuildProfile,
                preMainTimeout,
                ct).ConfigureAwait(false);
            if (gate.FailureKind != BuildTestGateFailureKind.Cancellation)
                IntegrationGateReceipts.Record(jobFolderPath, PreMainTestGateStep, gate, _timeline);

            var gateGreen = gate.Verdict == BuildTestGateVerdict.Ok;
            var published = gateGreen ? PublishedReleaseTip(repoRoot, releaseBranch, ct) : null;
            var moved = published is not null && !_git.IsAncestor(repoRoot, published, preMergeTip);
            var decision = ReleaseLineMergePolicy.Decide(gateGreen, moved, attempt);
            _logger.LogInformation(
                "merge-into-main behind-release-line project={Project} job={JobId} attempt={Attempt}/{MaxAttempts} merged={MergedSha} base={Base} published={Published} verdict={Verdict} decision={Decision} reason={Reason}",
                project, jobId, attempt, ReleaseLineMergePolicy.MaxAttempts, Short(mergedSha), Short(preMergeTip),
                published is null ? "not-observed" : Short(published), gate.Verdict, decision.Action, decision.Reason);

            if (decision.Action == ReleaseLineMergeAction.Publish)
            {
                IntegrationGateJournal.ClearOwned(jobFolderPath, journalOwner);
                return (merge, gate);
            }

            // Every other decision leaves the merge unpublished.
            var reset = _git.ResetIntegrationBranch(repoRoot, releaseBranch, preMergeTip);
            IntegrationGateJournal.ClearOwned(jobFolderPath, journalOwner);
            if (!reset.Success)
            {
                return (
                    MergeIntoIntegrationResult.Failed(
                        AcceptedIntegrationFailureCodes.IntegrationError,
                        $"The merge onto '{releaseBranch}' was not published ({decision.Reason}), and rolling the lane "
                        + $"back to {Short(preMergeTip)} FAILED ({reset.Error ?? "unknown error"}); "
                        + "the unverified merge needs manual repair."),
                    gate);
            }
            switch (decision.Action)
            {
                case ReleaseLineMergeAction.Retry:
                    continue;
                case ReleaseLineMergeAction.StopMoving:
                    return (
                        MergeIntoIntegrationResult.Failed(
                            AcceptedIntegrationFailureCodes.ReleaseLineMoving,
                            $"Merge onto '{releaseBranch}' not published: {decision.Reason}. "
                            + $"The lane was rolled back to {Short(preMergeTip)}; the delivery needs no change and "
                            + "the next integration attempt merges it again."),
                        gate);
                default:
                    return (
                        MergeIntoIntegrationResult.Failed(
                            gate.FailureKind == BuildTestGateFailureKind.Environment
                                ? AcceptedIntegrationFailureCodes.GateEnvironmentFailure
                                : AcceptedIntegrationFailureCodes.BuildGateFailed,
                            $"Pre-main full suite blocked the merge result {Short(mergedSha)}: {gate.Reason}. "
                            + $"'{releaseBranch}' was rolled back to {Short(preMergeTip)} and nothing was pushed."),
                        gate);
            }
        }
    }

    /// <summary>
    /// The freshly fetched published release tip, or null when the repository
    /// has no origin or the fetch failed. Null means "not observed to move": the
    /// push still refuses a non-fast-forward, so an unknown tip publishes nothing
    /// that origin does not accept.
    /// </summary>
    private string? PublishedReleaseTip(string repoRoot, string releaseBranch, CancellationToken ct)
    {
        var fetched = _git.FetchIntegrationBranch(repoRoot, releaseBranch, ct);
        if (!fetched.Success || fetched.Outcome == IntegrationBranchSyncOutcome.NoRemote) return null;
        return _git.GetBranchTip(repoRoot, $"refs/remotes/origin/{releaseBranch}");
    }
}
