namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3002 - verification of a delivery the integration branch already
/// contains. On 2026-09-28 an operator script pushed a developer checkout
/// whose <c>develop</c> carried merges whose gate later failed. The lane then
/// found those deliveries "already merged" and completed their cards although
/// no gate ever passed on the merged tree. From here on such a card completes
/// only on gate evidence for the exact tree it claims, and the lane produces
/// that evidence at most once by running the gate on the current branch tip.
/// </summary>
public sealed partial class MergeIntoDevelopRunner
{
    internal const string IntegrationUnverifiedFailureCode = "IntegrationUnverified";

    private const string PreMainTestGateStep = "pre-main-test-gate";

    private sealed record ContainedDeliveryVerification(
        MergeIntoIntegrationResult Result,
        IntegrationVerificationDecision Decision,
        BuildTestGateResult? Gate,
        bool ReleaseGate,
        string Branch);

    /// <summary>
    /// Boundary of the rule: gathers the facts the pure
    /// <see cref="IntegrationVerificationPolicy"/> needs, runs the gate once
    /// when the policy asks for it, and records the answer on the card. The
    /// branch history is never rewritten here: this call did not create it.
    /// </summary>
    private async Task<ContainedDeliveryVerification> VerifyContainedDeliveryAsync(
        string project,
        string jobId,
        string jobFolderPath,
        string? watchPath,
        string repoRoot,
        string branch,
        MergeIntoIntegrationResult result)
    {
        var releaseGate = IsReleaseBranch(branch);
        // The Studio-owned lane is authoritative. The developer checkout may
        // still hold a stale local branch after origin advanced out of band.
        var sha = _git.GetBranchTip(repoRoot, _git.IntegrationLineRef(repoRoot, branch));
        var tree = ReviewSubjectStore.IsValidResultSha(sha) ? _git.GetCommitTree(repoRoot, sha!) : null;
        bool SameTree(string candidate)
            => string.Equals(candidate, sha, StringComparison.OrdinalIgnoreCase)
               || (tree is not null
                   && string.Equals(_git.GetCommitTree(repoRoot, candidate), tree, StringComparison.Ordinal));

        var facts = new IntegrationVerificationFacts(
            result.Outcome,
            sha,
            ExactReceipt: tree is null
                ? null
                : IntegrationGateReceipts.ReadNewest(
                    jobFolderPath,
                    // A work-line receipt cannot satisfy the release line's
                    // mandatory full suite, even when both branches point to
                    // the same tree. Only reuse a receipt from this target's gate.
                    [releaseGate ? PreMainTestGateStep : IntegrationGateJournal.PreDevelopBuildGateStep],
                    SameTree),
            HasVerifiedIntegrationRecord: tree is not null
                && ReadIntegrationRecords(jobId, watchPath).Any(record =>
                    string.Equals(record.Classification, IntegrationRecordClasses.IntegratedVerified, StringComparison.Ordinal)
                    && IntegrationVerificationProjection.SameBranch(record.IntegrationBranch, branch)
                    && ReviewSubjectStore.IsValidResultSha(record.IntegrationSha)
                    && SameTree(record.IntegrationSha!)),
            GateRun: null);
        var decision = IntegrationVerificationPolicy.Decide(facts);
        var gate = facts.ExactReceipt;

        if (decision.Action == IntegrationVerificationAction.RunGate)
        {
            // The card says integrated-unverified while the gate runs, so a
            // crash mid-gate never leaves a completed-looking card behind.
            RecordVerification(jobFolderPath, repoRoot, branch, sha, decision, gate: null);
            gate = await RunVerificationGateAsync(
                project, jobId, jobFolderPath, repoRoot, branch, sha!, releaseGate).ConfigureAwait(false);
            IntegrationGateReceipts.Record(
                jobFolderPath,
                releaseGate ? PreMainTestGateStep : IntegrationGateJournal.PreDevelopBuildGateStep,
                gate,
                _timeline);
            decision = IntegrationVerificationPolicy.Decide(facts with { GateRun = gate });
        }

        RecordVerification(jobFolderPath, repoRoot, branch, sha, decision, gate, result.EvidenceShas);
        _logger.LogInformation(
            "merge-into-develop contained-delivery verification project={Project} job={JobId} integration={Integration} sha={Sha} state={State} evidence={Evidence} action={Action}",
            project, jobId, branch, sha, decision.State, decision.Evidence, decision.Action);

        var shortSha = sha is null ? "(unresolved)" : Short(sha);
        return decision.Action switch
        {
            IntegrationVerificationAction.CompleteVerified => new(
                result with { MergedSha = sha }, decision, gate, releaseGate, branch),
            IntegrationVerificationAction.GateUnresolved when gate?.FailureKind == BuildTestGateFailureKind.Environment => new(
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.GateEnvironmentFailure,
                    error: $"{branch} already contains this delivery at {shortSha}, but it is integrated-unverified: {decision.Reason} "
                           + "The integration history was left unchanged and no push was released; "
                           + "GateEnvironment: the same delivery is verified again without a new review."),
                decision, gate, releaseGate, branch),
            _ => new(
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.GateFailed,
                    error: $"{branch} already contains this delivery at {shortSha}, but it is integrated-unverified: {decision.Reason} "
                           + "The integration history was left unchanged, no push was released, and the card stays in Human Review"
                           + (decision.GateFailed
                               ? " until the branch is repaired and a gate passes; a cause card tracks the branch."
                               : " until a gate reaches a verdict on the tree.")),
                decision, gate, releaseGate, branch),
        };
    }

    /// <summary>
    /// The single gate run on the current tip. The release line keeps its
    /// mandatory full suite; the work line runs the pre-develop gate over the
    /// delivery's own diff plus the tip's last commit. When the review subject
    /// is absent, the containing merge supplies the delivery range; existing
    /// stack entry points keep a mutable ref or later docs-only tip from
    /// shrinking the gate scope to nothing.
    /// </summary>
    private async Task<BuildTestGateResult> RunVerificationGateAsync(
        string project,
        string jobId,
        string jobFolderPath,
        string repoRoot,
        string branch,
        string sha,
        bool releaseGate)
    {
        if (releaseGate)
        {
            if (_preMainTestGate is null || _projectSettings is null)
                return UnwiredGate("pre-main test gate", sha);
            var settings = _projectSettings.Get(project);
            var (timeout, timeoutSource) = ResolveGateTimeout(project, _preMainTimeout);
            return await _preMainTestGate.RunAsync(
                new BuildTestGateRequest(repoRoot, sha, "merge-into-main")
                {
                    Project = project,
                    JobId = jobId,
                    Lane = TaskStates.Completed,
                    TestExecution = settings.TestExecution,
                    JobFolderPath = jobFolderPath,
                    SubjectRef = branch,
                    TimeoutBudgetSource = timeoutSource,
                    InfrastructureTimeout = ResolveGateInfrastructureTimeout(),
                },
                settings.BuildProfile,
                timeout,
                CancellationToken.None).ConfigureAwait(false);
        }

        var profile = BuildProfileFor(project);
        var changedPaths = VerificationChangedPaths(repoRoot, jobId, jobFolderPath, sha);
        // An exact delivery range is unavailable for some fast-forwarded
        // histories. Declared build commands still provide a real gate on the
        // current tip; only a history with no gate scope must fail unresolved.
        if (changedPaths is null && VerifyCommandPlanner.HasProfileBuildCommands(profile))
            changedPaths = [];
        if (changedPaths is null)
        {
            return new BuildTestGateResult(
                BuildTestGateVerdict.Fail,
                null,
                0,
                string.Empty,
                "The verification gate could not derive the changed-file set of the contained delivery.",
                false,
                false)
            {
                ExpectedSha = sha,
                TestedSha = sha,
                FailureKind = BuildTestGateFailureKind.MissingSource,
            };
        }

        if (!PreDevelopBuildGate.AppliesTo(profile, changedPaths))
        {
            // Written as an explicit receipt: "no gate applies" is itself a
            // gate verdict, and the card must be able to name it.
            return new BuildTestGateResult(
                BuildTestGateVerdict.NotApplicable,
                null,
                0,
                string.Empty,
                "No gate applies: the contained delivery touches neither frontend/ nor managed sources "
                + "and the project declares no build-profile build commands.",
                false,
                false)
            {
                ExpectedSha = sha,
                TestedSha = sha,
            };
        }

        if (_preDevelopBuildGate is null)
            return UnwiredGate("pre-develop build gate", sha);

        var (preDevelopTimeout, preDevelopTimeoutSource) = ResolveGateTimeout(project, _preDevelopTimeout);
        return await _preDevelopBuildGate.RunAsync(
            new BuildTestGateRequest(repoRoot, sha, "merge-into-develop-build-gate")
            {
                Project = project,
                JobId = jobId,
                Lane = TaskStates.Completed,
                TestExecution = TestExecutionFor(project),
                JobFolderPath = jobFolderPath,
                SubjectRef = branch,
                TimeoutBudgetSource = preDevelopTimeoutSource,
                InfrastructureTimeout = ResolveGateInfrastructureTimeout(),
            },
            changedPaths,
            profile,
            preDevelopTimeout,
            CancellationToken.None).ConfigureAwait(false);
    }

    private IReadOnlyList<string>? VerificationChangedPaths(
        string repoRoot, string jobId, string jobFolderPath, string sha)
    {
        var firstParent = _git.GetFirstParent(repoRoot, sha);
        var tipPaths = firstParent is null
            ? null
            : _git.ChangedPathsAgainstMergeBase(repoRoot, firstParent, sha);
        var subject = ReviewSubjectStore.Read(jobFolderPath);
        IReadOnlyList<string>? deliveryPaths = subject is not null
                            && ReviewSubjectStore.IsValidResultSha(subject.BaseSha)
                            && ReviewSubjectStore.IsValidResultSha(subject.ResultSha)
            ? _git.ChangedPathsAgainstMergeBase(repoRoot, subject.BaseSha!, subject.ResultSha!)
            : null;
        if (deliveryPaths is null)
        {
            var delivery = DeliveryRefResolver.Resolve(jobId, jobFolderPath);
            var deliverySha = _git.GetBranchTip(repoRoot, delivery.Ref)
                ?? _git.GetBranchTip(repoRoot, "origin/" + delivery.Ref)
                ?? delivery.ExpectedResultSha;
            if (ReviewSubjectStore.IsValidResultSha(deliverySha))
                deliveryPaths = _git.ChangedPathsForContainedDelivery(repoRoot, sha, deliverySha!);
        }
        // The card can name a commit chain even when its source ref has gone
        // or advanced. Derive every attributed commit's paths from Git, never
        // from the card's cached file list or only its newest commit.
        var attributed = DeliveryRefResolver.AttributedCommitShas(jobFolderPath);
        if (attributed.Count > 0)
        {
            var commitPaths = new List<string>();
            var complete = true;
            foreach (var commitSha in attributed)
            {
                var parent = _git.GetFirstParent(repoRoot, commitSha);
                var paths = parent is not null && _git.IsAncestor(repoRoot, commitSha, sha)
                    ? _git.ChangedPathsAgainstMergeBase(repoRoot, parent, commitSha)
                    : null;
                if (paths is null)
                {
                    complete = false;
                    break;
                }
                commitPaths.AddRange(paths);
            }
            if (complete)
                deliveryPaths = (deliveryPaths ?? []).Concat(commitPaths)
                    .Distinct(StringComparer.Ordinal).ToList();
        }
        if (subject is null || deliveryPaths is null)
        {
            // Without a fenced subject, even a readable delivery ref may have
            // advanced since this card's delivery. Include existing stack
            // entry points so a later docs-only ref cannot write a false
            // NotApplicable receipt for earlier code.
            var anchors = Directory.EnumerateFiles(repoRoot, "*.sln", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(repoRoot, "*.slnx", SearchOption.TopDirectoryOnly))
                .Select(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'))
                .ToList();
            if (File.Exists(Path.Combine(repoRoot, "frontend", "package.json")))
                anchors.Add("frontend/package.json");
            if (deliveryPaths is null && anchors.Count == 0) return null;
            deliveryPaths = (deliveryPaths ?? []).Concat(anchors).ToList();
        }
        if (tipPaths is null) return deliveryPaths;
        return (tipPaths ?? []).Concat(deliveryPaths ?? [])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static BuildTestGateResult UnwiredGate(string gateName, string sha)
        => new(
            BuildTestGateVerdict.Fail,
            null,
            0,
            string.Empty,
            $"The applicable {gateName} is not available.",
            false,
            false)
        {
            ExpectedSha = sha,
            FailureKind = BuildTestGateFailureKind.MissingSource,
        };

    private IReadOnlyList<TaskIntegrationRecord> ReadIntegrationRecords(string jobId, string? watchPath)
    {
        try
        {
            return _taskScanner?.FindJob(jobId, watchPath)?.IntegrationRecords ?? [];
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "MergeIntoDevelopRunner: integration records are optional verification evidence");
            return [];
        }
    }

    /// <summary>
    /// A fresh merge created by this lane went through its own gate (or none
    /// applies to its diff), so it is verified by construction; the record
    /// makes that visible next to the contained-delivery case.
    /// </summary>
    private void RecordFreshMergeVerification(
        string jobFolderPath,
        string repoRoot,
        string branch,
        MergeIntoIntegrationResult result,
        BuildTestGateResult? gate)
    {
        var sha = result.MergedSha;
        var decision = new IntegrationVerificationDecision(
            IntegrationVerificationAction.CompleteVerified,
            IntegrationVerificationStates.Verified,
            gate is null ? IntegrationVerificationEvidence.GateNotApplicable : IntegrationVerificationEvidence.MergeGate,
            gate is null
                ? "The integration lane created this merge; no gate applies to its diff."
                : $"The integration lane created this merge and its gate returned {gate.Verdict}.");
        RecordVerification(jobFolderPath, repoRoot, branch, sha, decision, gate, result.EvidenceShas);
    }

    private void RecordVerification(
        string jobFolderPath,
        string repoRoot,
        string branch,
        string? sha,
        IntegrationVerificationDecision decision,
        BuildTestGateResult? gate,
        IReadOnlyList<string>? evidenceShas = null)
    {
        try
        {
            IntegrationVerificationStore.Write(jobFolderPath, new IntegrationVerificationRecord
            {
                State = decision.State,
                Sha = sha,
                IntegrationBranch = branch,
                DeliveryShas = decision.State == IntegrationVerificationStates.Verified
                    ? DeliveryShasContainedIn(jobFolderPath, repoRoot, sha, evidenceShas)
                    : [],
                Evidence = decision.Evidence,
                GateVerdict = gate?.Verdict.ToString(),
                GateFailed = decision.GateFailed,
                Reason = decision.Reason,
                RecordedAtUtc = DateTimeOffset.UtcNow,
            });
            _timeline?.Append(
                jobFolderPath,
                TimelineEventKinds.IntegrationVerificationRecorded,
                TimelineActors.System,
                $"{decision.State} on {branch}{ShaSuffix(sha)}: {decision.Reason}",
                details: new Dictionary<string, string>
                {
                    ["state"] = decision.State,
                    ["sha"] = sha ?? string.Empty,
                    ["integrationBranch"] = branch,
                    ["evidence"] = decision.Evidence,
                    ["gateVerdict"] = gate?.Verdict.ToString() ?? string.Empty,
                });
        }
        catch (Exception ex)
        {
            // The merge step outcome stays authoritative for completion; this
            // record is the card's explanation of it.
            _logger.LogWarning(ex, "merge-into-develop could not record integration verification for {JobFolder}", jobFolderPath);
        }
    }

    /// <summary>
    /// The card's delivery SHAs the verified tree contains: the review-subject
    /// result, the merge evidence, and every attributed commit. Ancestry is
    /// checked here, once per verdict, so the board projection can keep the
    /// verdict after the tip advances without a Git call per card.
    /// </summary>
    private List<string> DeliveryShasContainedIn(
        string jobFolderPath,
        string repoRoot,
        string? sha,
        IReadOnlyList<string>? evidenceShas)
    {
        if (!ReviewSubjectStore.IsValidResultSha(sha)) return [];
        var candidates = new List<string>();
        var subjectSha = ReviewSubjectStore.Read(jobFolderPath)?.ResultSha;
        if (subjectSha is not null) candidates.Add(subjectSha);
        candidates.AddRange(evidenceShas ?? []);
        candidates.AddRange(DeliveryRefResolver.AttributedCommitShas(jobFolderPath));
        return candidates
            .Where(ReviewSubjectStore.IsValidResultSha)
            .Select(candidate => candidate.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Where(candidate => _git.IsAncestor(repoRoot, candidate, sha!))
            .ToList();
    }

    /// <summary>
    /// Opens (or joins) the cause card for a branch that carries a delivery
    /// whose tree failed its gate. Raised whenever the service is wired: the
    /// branch itself is defective, which is not a per-project opt-in question.
    /// The signature names the branch only, so every card stuck on the same
    /// defect lands on one cause card.
    /// </summary>
    private async Task RaiseUnverifiedBranchCauseAsync(
        string jobId,
        string? watchPath,
        ContainedDeliveryVerification verification,
        DateTime startedAt,
        CancellationToken ct)
    {
        if (_failureInterventions is null || _taskScanner is null) return;
        try
        {
            var task = _taskScanner.FindJob(jobId, watchPath);
            if (task is null) return;
            await _failureInterventions.RaiseAsync(task, new FailureCommandEvidence(
                IntegrationUnverifiedFailureCode,
                verification.Result.Outcome.ToString(),
                verification.Gate?.ExitCode,
                (long)(DateTime.UtcNow - startedAt).TotalMilliseconds,
                $"unverified integration branch {verification.Branch}",
                verification.Decision.Reason,
                PipelineCatalogue.MergeIntoDevelopStepId,
                [PipelineExecutionLog.FileName, IntegrationVerificationStore.FileName, "post-steps/"],
                startedAt), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "merge-into-develop could not open the cause card for unverified branch {Branch}", verification.Branch);
        }
    }
}
