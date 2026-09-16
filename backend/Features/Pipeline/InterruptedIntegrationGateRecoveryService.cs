namespace AgentStudio.Pipeline;

/// <param name="Branches">Integration branches that carried an in-flight gate record.</param>
/// <param name="RolledBack">Branches returned to their exact pre-merge tip.</param>
/// <param name="Requeued">Cards whose integration was handed back to the retry ladder.</param>
/// <param name="Escalated">Branches that could not be repaired without destroying work.</param>
public sealed record InterruptedIntegrationGateRecoveryReport(
    int Branches,
    int RolledBack,
    int Requeued,
    int Escalated);

/// <summary>
/// Startup recovery for a pre-develop build gate that never reached a verdict
/// (AGT-2849).
///
/// <para>
/// The gate publishes before it verifies: the merge commit is created first, and
/// only the verdict decides whether it stays. A process that dies inside that
/// window leaves an un-gated merge on the integration branch with nobody left to
/// judge it, and the next delivery merges on top of it - so one later gate ends
/// up testing, and on success publishing, a stack of merges nobody ever gated,
/// while a failure rolls back only to the previous un-gated tip.
/// </para>
/// <para>
/// This service closes that window at the only moment it can be closed safely:
/// before the merge gate opens for new work. It reads the durable journal
/// entries left behind, asks <see cref="InterruptedIntegrationGatePolicy"/> what
/// the branch needs, and performs exactly that - reset to the oldest unverified
/// pre-merge tip, or nothing when every interrupted merge turns out to carry a
/// durable verdict for its exact SHA. Either way the affected cards are handed
/// to the bounded retry ladder, which replays the integration for the unchanged
/// delivery SHA and never starts a new review.
/// </para>
/// <para>
/// Boundary validation, application coordination, pure decision, bounded side
/// effects: the git reads build the facts, the policy decides, and only then is
/// a ref moved.
/// </para>
/// </summary>
public sealed class InterruptedIntegrationGateRecoveryService
{
    private readonly TaskScannerService _scanner;
    private readonly GitService _git;
    private readonly IntegrationWorktreeProvider _worktrees;
    private readonly PipelineExecutionLog _pipelineLog;
    private readonly TimelineLog _timeline;
    private readonly ILogger<InterruptedIntegrationGateRecoveryService> _logger;

    public InterruptedIntegrationGateRecoveryService(
        TaskScannerService scanner,
        GitService git,
        IntegrationWorktreeProvider worktrees,
        PipelineExecutionLog pipelineLog,
        TimelineLog timeline,
        ILogger<InterruptedIntegrationGateRecoveryService> logger)
    {
        _scanner = scanner;
        _git = git;
        _worktrees = worktrees;
        _pipelineLog = pipelineLog;
        _timeline = timeline;
        _logger = logger;
    }

    /// <summary>
    /// One recovery pass over every card that left an in-flight gate record
    /// behind. Never throws: one unrepairable branch must not stop the others.
    /// </summary>
    public InterruptedIntegrationGateRecoveryReport RunOnce(CancellationToken ct = default)
    {
        var open = ReadOpenJournals();
        if (open.Count == 0)
            return new InterruptedIntegrationGateRecoveryReport(0, 0, 0, 0);

        var branches = 0;
        var rolledBack = 0;
        var requeued = 0;
        var escalated = 0;
        foreach (var group in open.GroupBy(
                     item => (item.Journal.RepoRoot, item.Journal.IntegrationBranch),
                     TupleComparer))
        {
            ct.ThrowIfCancellationRequested();
            branches++;
            try
            {
                var outcome = RecoverBranch(group.Key.RepoRoot, group.Key.IntegrationBranch, [.. group], ct);
                if (outcome.Action == InterruptedGateRecoveryAction.RollBack) rolledBack++;
                if (outcome.Action == InterruptedGateRecoveryAction.Escalate) escalated++;
                requeued += outcome.Requeued;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "interrupted-integration-gate recovery failed for repo={RepoRoot} branch={Branch}",
                    group.Key.RepoRoot,
                    group.Key.IntegrationBranch);
            }
        }

        _logger.LogInformation(
            "interrupted-integration-gate recovery branches={Branches} rolledBack={RolledBack} requeued={Requeued} escalated={Escalated}",
            branches, rolledBack, requeued, escalated);
        return new InterruptedIntegrationGateRecoveryReport(branches, rolledBack, requeued, escalated);
    }

    private (InterruptedGateRecoveryAction Action, int Requeued) RecoverBranch(
        string repoRoot,
        string integrationBranch,
        IReadOnlyList<OpenJournal> journals,
        CancellationToken ct)
    {
        if (!Directory.Exists(repoRoot))
        {
            // The project was moved or unregistered while the record survived.
            // Nothing can be proven about that branch, so the record is dropped
            // rather than acted on against an unknown repository.
            foreach (var item in journals) IntegrationGateJournal.Clear(item.JobFolderPath);
            return (InterruptedGateRecoveryAction.None, 0);
        }

        var branchTip = _git.GetBranchTip(repoRoot, integrationBranch);
        var publishedTip = _git.GetBranchTip(repoRoot, "origin/" + integrationBranch);
        var entries = journals
            .Select(item => Facts(repoRoot, integrationBranch, branchTip, publishedTip, item))
            .ToList();
        var decision = InterruptedIntegrationGatePolicy.Decide(entries);

        if (decision.Action == InterruptedGateRecoveryAction.Escalate)
        {
            foreach (var item in journals)
            {
                RecordEscalation(item, integrationBranch, decision);
                IntegrationGateJournal.Clear(item.JobFolderPath);
            }
            return (decision.Action, 0);
        }

        string? rollbackError = null;
        if (decision.Action == InterruptedGateRecoveryAction.RollBack)
        {
            rollbackError = RollBack(repoRoot, integrationBranch, decision.RollbackSha!, ct);
            if (rollbackError is not null)
            {
                foreach (var item in journals)
                {
                    RecordEscalation(item, integrationBranch, decision, rollbackError);
                    IntegrationGateJournal.Clear(item.JobFolderPath);
                }
                return (InterruptedGateRecoveryAction.Escalate, 0);
            }
        }

        var byJob = journals.ToDictionary(item => item.JobFolderPath, StringComparer.OrdinalIgnoreCase);
        var requeued = 0;
        foreach (var entry in decision.Requeue)
        {
            if (!byJob.TryGetValue(entry.JobFolderPath, out var item)) continue;
            RecordRequeue(item, integrationBranch, decision, entry);
            IntegrationGateJournal.Clear(item.JobFolderPath);
            requeued++;
        }
        return (decision.Action, requeued);
    }

    /// <summary>
    /// Git facts for one in-flight record, read against the branch as it stands
    /// now. Ancestry is asked of the branch tip, so a record whose merge is not
    /// in the graph any more describes an already repaired branch.
    /// </summary>
    private InterruptedGateEntry Facts(
        string repoRoot,
        string integrationBranch,
        string? branchTip,
        string? publishedTip,
        OpenJournal item)
    {
        var journal = item.Journal;
        var anchor = journal.PreMergeTip;
        var verdict = InterruptedGateVerdict.None;
        if (!string.IsNullOrWhiteSpace(journal.GatedSha))
        {
            var receipt = IntegrationGateReceipts.ReadExact(
                item.JobFolderPath,
                journal.Step,
                journal.GatedSha!);
            if (receipt is not null)
            {
                verdict = PreDevelopBuildGate.IsGreen(receipt)
                    ? InterruptedGateVerdict.Green
                    : InterruptedGateVerdict.Red;
            }
        }

        return new InterruptedGateEntry(
            journal.JobId,
            item.JobFolderPath,
            journal.GatedSha,
            anchor,
            journal.StartedAt,
            verdict,
            GatedShaOnBranch: !string.IsNullOrWhiteSpace(journal.GatedSha)
                              && !string.IsNullOrWhiteSpace(branchTip)
                              && _git.IsAncestor(repoRoot, journal.GatedSha!, branchTip!),
            AnchorOnBranch: !string.IsNullOrWhiteSpace(anchor)
                            && !string.IsNullOrWhiteSpace(branchTip)
                            && _git.IsAncestor(repoRoot, anchor!, branchTip!),
            AnchorIsBranchTip: !string.IsNullOrWhiteSpace(anchor)
                               && string.Equals(anchor, branchTip, StringComparison.OrdinalIgnoreCase),
            // No published branch at all means the local branch IS the
            // publication, so no reset can un-publish anything.
            AnchorContainsPublishedTip: string.IsNullOrWhiteSpace(publishedTip)
                                        || !string.IsNullOrWhiteSpace(anchor)
                                        && _git.IsAncestor(repoRoot, publishedTip!, anchor!));
    }

    /// <summary>
    /// Moves the integration branch back to the pre-merge anchor. The reset runs
    /// in the Studio-owned integration worktree (AGT-2832); the developer
    /// checkout is never a command workspace, and a checkout that was
    /// fast-forwarded along with the branch is returned by the same primitive
    /// the live gate rollback uses.
    /// </summary>
    private string? RollBack(
        string repoRoot,
        string integrationBranch,
        string anchor,
        CancellationToken ct)
    {
        var workspace = _worktrees.Resolve(repoRoot, integrationBranch, ct);
        if (!workspace.Success)
            return workspace.Error ?? "The integration worktree is unavailable.";

        var reset = _git.ResetIntegrationBranch(workspace.Path!, integrationBranch, anchor);
        if (!reset.Success)
            return reset.Error ?? "The integration branch could not be reset.";

        _logger.LogWarning(
            "interrupted-integration-gate rolled {Branch} back to {Anchor} in {RepoRoot}",
            integrationBranch,
            Short(anchor),
            repoRoot);
        return null;
    }

    private void RecordRequeue(
        OpenJournal item,
        string integrationBranch,
        InterruptedGateDecision decision,
        InterruptedGateEntry entry)
    {
        var rolledBack = decision.Action == InterruptedGateRecoveryAction.RollBack;
        var reason = rolledBack
            ? $"The {integrationBranch} build gate was interrupted before it reached a verdict. "
              + $"{integrationBranch} was rolled back to {Short(decision.RollbackSha!)}, nothing was pushed, "
              + "and the integration is replayed for the same reviewed delivery."
            : $"The {integrationBranch} build gate was interrupted before its result was recorded. "
              + "The merge it had already verified is unchanged and the integration is replayed "
              + "to publish it for the same reviewed delivery.";

        var now = DateTime.UtcNow;
        _pipelineLog.RecordStep(item.JobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            StartedAt = item.Journal.StartedAt.UtcDateTime,
            CompletedAt = now,
            Verdict = "gate-interrupted",
            VerdictSummary = $"Integration gate interrupted on {integrationBranch}.",
            Reason = reason,
            FailureCode = AcceptedIntegrationFailureCodes.GateInterrupted,
        });

        Append(item, integrationBranch, decision, entry, reason);
    }

    private void RecordEscalation(
        OpenJournal item,
        string integrationBranch,
        InterruptedGateDecision decision,
        string? rollbackError = null)
    {
        var reason = rollbackError is not null
            ? $"The interrupted {integrationBranch} build gate could not be rolled back to "
              + $"{Short(decision.RollbackSha ?? "unknown")} ({rollbackError}); the unverified merge is still on "
              + "the local integration branch and needs manual repair."
            : decision.Reason == InterruptedIntegrationGatePolicy.Reasons.AnchorBehindPublished
                ? $"An interrupted {integrationBranch} build gate left an unverified merge that cannot be rolled "
                  + $"back: the pre-merge tip no longer contains origin/{integrationBranch}, so the reset would "
                  + "un-publish commits that are already on origin. Manual repair is required."
                : $"An interrupted {integrationBranch} build gate left an unverified merge whose pre-merge tip is "
                  + "no longer reachable from the branch, so the exact rollback anchor is gone. Manual repair is "
                  + "required.";

        var now = DateTime.UtcNow;
        _pipelineLog.RecordStep(item.JobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            StartedAt = item.Journal.StartedAt.UtcDateTime,
            CompletedAt = now,
            Verdict = "error",
            VerdictSummary = $"Interrupted integration gate on {integrationBranch} needs manual repair.",
            Reason = reason,
            FailureCode = AcceptedIntegrationFailureCodes.IntegrationError,
        });

        _logger.LogError(
            "interrupted-integration-gate escalated job={JobId} branch={Branch} reason={Reason}",
            item.Journal.JobId,
            integrationBranch,
            decision.Reason);
        Append(item, integrationBranch, decision, entry: null, reason);
    }

    private void Append(
        OpenJournal item,
        string integrationBranch,
        InterruptedGateDecision decision,
        InterruptedGateEntry? entry,
        string reason)
    {
        try
        {
            _timeline.Append(
                item.JobFolderPath,
                TimelineEventKinds.IntegrationGateInterrupted,
                TimelineActors.System,
                reason,
                details: new Dictionary<string, string>
                {
                    ["action"] = decision.Action.ToString(),
                    ["policyReason"] = decision.Reason,
                    ["integrationBranch"] = integrationBranch,
                    ["gatedSha"] = entry?.GatedSha ?? item.Journal.GatedSha ?? string.Empty,
                    ["rollbackSha"] = decision.RollbackSha ?? string.Empty,
                });
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "InterruptedIntegrationGateRecoveryService: timeline append is best-effort");
        }
    }

    private List<OpenJournal> ReadOpenJournals()
    {
        var open = new List<OpenJournal>();
        foreach (var job in _scanner.ScanAllJobsWithArchive())
        {
            if (string.IsNullOrWhiteSpace(job.FolderPath)) continue;
            if (IntegrationGateJournal.Read(job.FolderPath) is not { } journal) continue;
            if (string.IsNullOrWhiteSpace(journal.IntegrationBranch)) continue;
            open.Add(new OpenJournal(job.FolderPath, journal));
        }
        return open;
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static readonly IEqualityComparer<(string RepoRoot, string IntegrationBranch)> TupleComparer =
        new RepoBranchComparer();

    private sealed record OpenJournal(string JobFolderPath, IntegrationGateJournalEntry Journal);

    private sealed class RepoBranchComparer : IEqualityComparer<(string RepoRoot, string IntegrationBranch)>
    {
        public bool Equals((string RepoRoot, string IntegrationBranch) x, (string RepoRoot, string IntegrationBranch) y)
            => string.Equals(x.RepoRoot, y.RepoRoot, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.IntegrationBranch, y.IntegrationBranch, StringComparison.Ordinal);

        public int GetHashCode((string RepoRoot, string IntegrationBranch) obj)
            => HashCode.Combine(
                obj.RepoRoot.ToLowerInvariant(),
                obj.IntegrationBranch);
    }
}
