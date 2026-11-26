namespace AgentStudio.Pipeline;

/// <summary>One waiting delivery handed to <see cref="MergeIntoDevelopRunner.PlanPostMergeTrainAsync"/>.</summary>
public sealed record PostMergeTrainRequest(string JobId, string JobFolderPath, string? ReviewedResultSha);

public sealed partial class MergeIntoDevelopRunner
{
    /// <summary>
    /// AGT-4020: in strategy fast, compiles the merged tree of every reviewed
    /// delivery that waits for integration in one pass before the first of them
    /// lands. Each member is merged in queue order with its own merge commit on
    /// the current integration tip; the tip is compiled once at the compile-only
    /// level (no lint, no test command). A red combined compile is searched one
    /// card at a time (<see cref="PostMergeTrainSearch"/>). Each member's
    /// decision is written beside the card; the member's own integration then
    /// reuses the compile when its merge reproduces the planned tree, or runs its
    /// own compile-only gate when it does not. Returns null when nothing was
    /// planned: another strategy, a single waiting card, or a head the current
    /// train already decided.
    /// </summary>
    public async Task<PostMergeTrainPlan?> PlanPostMergeTrainAsync(
        string project,
        string? watchPath,
        string integrationBranch,
        IReadOnlyList<PostMergeTrainRequest> queue,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (queue.Count < 2 || _preDevelopBuildGate is not { } compileGate) return null;
        var settings = ProjectSettingsFor(project);
        if (TestingStrategies.Normalize(TestingStrategies.Resolve(settings)) != TestingStrategies.Fast
            || PostMergeModeService.Effective(_postMergeMode, project, settings) != IntegrationTestModes.PostMerge)
            return null;
        var developerRoot = _git.ResolveRepoRootForWatchPath(watchPath)
            ?? (string.IsNullOrWhiteSpace(watchPath) ? null : watchPath);
        if (string.IsNullOrWhiteSpace(developerRoot)) return null;
        var branch = _git.ResolveIntegrationBranch(developerRoot, integrationBranch);
        if (IsReleaseBranch(branch)) return null;

        var members = new List<PostMergeTrainMember>();
        foreach (var item in queue)
        {
            var sha = ResolveTrainDelivery(developerRoot, item, ct);
            // A delivery the train cannot lay out still merges in its turn, so
            // every tree planned behind it would be wrong: the train stops there.
            if (sha is null) break;
            members.Add(new PostMergeTrainMember(item.JobId, item.JobFolderPath, sha));
        }
        if (members.Count < 2) return null;
        var head = PostMergeTrainEntryStore.Read(members[0].JobFolderPath);
        if (head is not null
            && string.Equals(head.DeliverySha, members[0].DeliverySha, StringComparison.OrdinalIgnoreCase)
            && head.Status is PostMergeTrainStatuses.Covered or PostMergeTrainStatuses.CompileRed)
            return null;

        var key = BranchLockKey(project, branch);
        var mergeLock = _mergeGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await mergeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _mergeHolders[key] = project;
        try
        {
            var baseSha = _git.GetBranchTip(developerRoot, _git.IntegrationBaseRef(developerRoot, branch));
            if (string.IsNullOrWhiteSpace(baseSha)) return null;
            var profile = BuildProfileFor(project);
            var trainId = "train-" + Guid.NewGuid().ToString("N")[..12];
            var startedAt = _gateTime.GetUtcNow();
            var plan = await PostMergeTrainSearch.RunAsync(
                trainId,
                baseSha,
                members,
                (tip, member) =>
                {
                    var merged = _git.VirtualMerge(developerRoot, tip, member.DeliverySha,
                        $"Post-merge train {trainId}: {member.JobId}");
                    return (merged.Sha, merged.TreeSha, merged.Conflict, merged.Error);
                },
                async (sha, jobIds) =>
                {
                    _git.PinPostMergeTrain(developerRoot, branch, sha);
                    var (timeout, timeoutSource) = ResolveGateTimeout(project, _preDevelopTimeout);
                    var changed = _git.ChangedPathsAgainstMergeBase(developerRoot, baseSha, sha) ?? [];
                    var gate = await compileGate.RunAsync(
                        new BuildTestGateRequest(developerRoot, sha, "merge-into-develop-train-compile")
                        {
                            Project = project,
                            JobId = members[0].JobId,
                            Lane = TaskStates.Completed,
                            TestExecution = TestExecutionFor(project),
                            SubjectRef = branch,
                            TimeoutBudgetSource = timeoutSource,
                            InfrastructureTimeout = ResolveGateInfrastructureTimeout(),
                        },
                        changed,
                        profile,
                        timeout,
                        CancellationToken.None,
                        false,
                        TestExecutionLevels.CompileOnly).ConfigureAwait(false);
                    _logger.LogInformation(
                        "post-merge-train compile project={Project} train={TrainId} sha={Sha} members={Members} verdict={Verdict} reason={Reason}",
                        project, trainId, sha, string.Join(",", jobIds), gate.Verdict, gate.Reason);
                    return (PreDevelopBuildGate.IsGreen(gate), gate.Reason);
                },
                startedAt).ConfigureAwait(false);
            foreach (var entry in plan.Entries)
            {
                var member = members.First(item => item.JobId == entry.JobId);
                PostMergeTrainEntryStore.Write(member.JobFolderPath, entry);
            }
            _logger.LogInformation(
                "post-merge-train planned project={Project} train={TrainId} base={Base} members={Members} compiles={Compiles} decisions={Decisions}",
                project, trainId, baseSha, plan.Entries.Count, plan.Compiles.Count,
                string.Join(",", plan.Entries.Select(entry => $"{entry.JobId}:{entry.Status}")));
            return plan;
        }
        finally
        {
            _mergeHolders.TryRemove(key, out _);
            mergeLock.Release();
        }
    }

    private string? ResolveTrainDelivery(string developerRoot, PostMergeTrainRequest item, CancellationToken ct)
    {
        try
        {
            var delivery = DeliveryRefResolver.Resolve(item.JobId, item.JobFolderPath);
            var sha = item.ReviewedResultSha ?? delivery.ExpectedResultSha
                ?? _git.GetBranchTip(developerRoot, delivery.Ref);
            if (!ReviewSubjectStore.IsValidResultSha(sha)) return null;
            if (!_git.HasCommitObject(developerRoot, sha!) && delivery.IsRemote)
                _git.FetchFencedDeliveryForInspection(developerRoot, delivery.Ref, sha!, ct);
            return _git.HasCommitObject(developerRoot, sha!) ? sha : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "MergeIntoDevelopRunner: an unresolvable delivery ends the post-merge train there");
            return null;
        }
    }

    /// <summary>
    /// The card's covered train entry when its own merge result has exactly the
    /// tree the train compiled and merges the delivery the train planned.
    /// </summary>
    private PostMergeTrainEntry? TrainCompileFor(string repoRoot, string jobFolderPath, string gatedSha)
    {
        var entry = PostMergeTrainEntryStore.Read(jobFolderPath);
        if (entry is not { Status: PostMergeTrainStatuses.Covered, MergedTreeSha: { } plannedTree, CompiledSha: not null })
            return null;
        var tree = _git.GetCommitTree(repoRoot, gatedSha);
        var delivery = _git.GetSecondParent(repoRoot, gatedSha);
        return string.Equals(tree, plannedTree, StringComparison.OrdinalIgnoreCase)
            && string.Equals(delivery, entry.DeliverySha, StringComparison.OrdinalIgnoreCase)
            ? entry : null;
    }

    /// <summary>
    /// Names the merge-window run that tests this card after integration
    /// (<see cref="PostMergeWindowCoverage"/>). Evidence only; never blocks.
    /// </summary>
    private void RecordWindowCoverage(string project, string jobId, string jobFolderPath, string mergedSha,
        string testingStrategy, string preMergeLevel, PostMergeTrainEntry? train)
    {
        var suites = TestingStrategies.WindowSuites(testingStrategy) ?? PostMergeWindowSuites.Affected;
        var state = _postMergeMode?.ReadState(project);
        var windowFrom = state?.LastWindowTipSha;
        var coverage = new PostMergeWindowCoverage
        {
            JobId = jobId,
            MergedSha = mergedSha,
            Strategy = TestingStrategies.Normalize(testingStrategy) ?? TestingStrategies.Default,
            WindowSuites = suites,
            WindowFromSha = windowFrom,
            PreMergeLevel = preMergeLevel,
            TrainId = train?.TrainId,
            CompiledSha = train?.CompiledSha,
            CoveredBy = PostMergeWindowCoverage.Describe(windowFrom, suites),
            RecordedAtUtc = _gateTime.GetUtcNow(),
        };
        PostMergeWindowCoverage.Write(jobFolderPath, coverage);
        IntegrationLaneEvents.Append(jobFolderPath, project, jobId, "post-merge-window",
            level: preMergeLevel, reason: coverage.CoveredBy, receipt: mergedSha);
    }
}
