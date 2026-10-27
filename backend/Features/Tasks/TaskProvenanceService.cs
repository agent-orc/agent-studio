using SourceFactDemandRequest = AgentStudio.TaskServer.Contracts.SourceFactDemandRequest;
using SourceFactKinds = AgentStudio.TaskServer.Contracts.SourceFactKinds;
using SourceReachabilityView = AgentStudio.TaskServer.Contracts.SourceReachabilityView;


namespace AgentStudio.Tasks;

/// <summary>
/// Owns the task commit-provenance record (ASS-1724): the
/// append-only <c>provenance</c> block on <c>task.json</c> plus the read-time
/// projection (<see cref="TaskProvenanceView"/>) that the board uses to answer
/// "where does this work live" across the worktree -&gt; develop -&gt; main path.
///
/// <para>
/// Two halves, deliberately split so the write side stays cheap and the read
/// side stays graph-fresh:
/// <list type="bullet">
/// <item><see cref="RecordTransition"/> is the ONE recording hook. It is called
///   from <see cref="TaskTransitionService"/> after a lane move lands and appends
///   a single anchor (branch tip + integration head at that instant). Nothing
///   else writes transitions, so the ladder can be reconstructed historically
///   from one ordered list.</item>
/// <item><see cref="BuildView"/> recomputes the derived facts live off the graph
///   - <see cref="DeriveLandedState"/> and the per-commit merge-set - because
///   those go stale the moment develop / main move. The derived
///   <c>landedState</c> is intentionally never persisted (see
///   <see cref="TaskProvenance"/>).</item>
/// </list>
/// </para>
///
/// <para>
/// The graph queries reuse the existing <see cref="GitService"/> primitives
/// (<see cref="GitService.GetBranchTip"/>, <see cref="GitService.GetMergeBase"/>,
/// <see cref="GitService.IsAncestor"/>,
/// <see cref="GitService.GetCommitsInRangeAtRoot"/>). Recording is best-effort
/// and fully guarded: a git or disk failure must never block the lane
/// transition that already completed.
/// </para>
/// </summary>
public sealed class TaskProvenanceService
{
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly TaskMutationService _mutations;
    private readonly ILogger<TaskProvenanceService> _logger;
    private readonly AgentStudio.Git.ISourceFactsReader? _sourceFacts;

    public const string ReleaseBranch = "main";

    public TaskProvenanceService(
        GitService git,
        ProjectSettingsService settings,
        TaskMutationService mutations,
        ILogger<TaskProvenanceService> logger,
        AgentStudio.Git.ISourceFactsReader? sourceFacts = null)
    {
        _git = git;
        _settings = settings;
        _mutations = mutations;
        _logger = logger;
        _sourceFacts = sourceFacts;
    }

    /// <summary>
    /// Card-open read of <c>GET /api/tasks/{id}/provenance</c>. Under derived
    /// source facts (AGT-3318) the landed state, ladder heads and per-commit
    /// membership come from the reachability facts the runner-host producer
    /// published; the request starts no Git process. A missing fact is shown
    /// as not yet landed with <c>sourceFacts=warming</c> and is requested from
    /// the producer. The legacy mode keeps <see cref="BuildView"/>.
    /// </summary>
    public async Task<TaskProvenanceView> BuildViewAsync(TaskInfo info, CancellationToken ct)
    {
        if (_sourceFacts is not { Mode: AgentStudio.Git.SourceFactsReadMode.Derived } facts)
            return BuildView(info);

        using var _t = GitProcessTelemetry.BeginRequest("tasks/provenance", _logger);
        var integrationBranch = _settings.Get(info.ProjectName).IntegrationBranch;
        var shas = AttributedChain(info).Select(commit => commit.Sha).ToArray();
        string[] targets = [integrationBranch, ReleaseBranch];
        var read = shas.Length == 0
            ? AgentStudio.Git.SourceFactRead<SourceReachabilityView>.Ready(
                new SourceReachabilityView(info.ProjectName, [], [], []))
            : await facts.GetReachabilityAsync(info.ProjectName, shas, targets, ct);
        var view = BuildDerivedView(info, integrationBranch, read);
        if (read.Value is { Missing.Count: > 0 } missing)
            await facts.RequestAsync(info.ProjectName, new SourceFactDemandRequest(
                SourceFactKinds.Reachability,
                "card-open-provenance",
                Shas: missing.Missing.Select(pair => pair.Sha).Distinct(StringComparer.Ordinal).ToArray(),
                TargetRefs: missing.Missing.Select(pair => pair.TargetRef).Distinct(StringComparer.Ordinal).ToArray()), ct);
        else if (read.State == AgentStudio.Git.SourceFactStates.Warming && shas.Length > 0)
            await facts.RequestAsync(info.ProjectName, new SourceFactDemandRequest(
                SourceFactKinds.Reachability, "card-open-provenance", Shas: shas, TargetRefs: targets), ct);
        return view;
    }

    /// <summary>
    /// Pure projection of the persisted provenance facts and one reachability
    /// read. A pair without a current fact counts as not contained, so a card
    /// never reads as landed on evidence that is not there. The branch tip is
    /// the last recorded transition anchor; the live task branch is not a
    /// derived fact.
    /// </summary>
    public static TaskProvenanceView BuildDerivedView(
        TaskInfo info,
        string integrationBranch,
        AgentStudio.Git.SourceFactRead<SourceReachabilityView> read)
    {
        var branch = WorktreeTaskLifecycle.BranchFor(info.Id);
        var prov = info.Provenance;
        var reach = read.Value;
        var contained = new HashSet<(string Sha, string Ref)>(
            (reach?.Facts ?? []).Where(fact => fact.Contained)
                .Select(fact => (fact.Sha.ToLowerInvariant(), fact.TargetRef)));
        bool In(string sha, string target) => contained.Contains((sha.ToLowerInvariant(), target));
        var commits = AttributedChain(info).Select(commit => new TaskCommitMembership
        {
            Sha = commit.Sha,
            ShortSha = string.IsNullOrWhiteSpace(commit.ShortSha) ? Short(commit.Sha) : commit.ShortSha,
            Message = commit.Message,
            OnTaskBranch = true,
            AlsoOnIntegration = In(commit.Sha, integrationBranch),
            AlsoOnRelease = In(commit.Sha, ReleaseBranch),
        }).ToList();
        var mergedToIntegration = commits.Count > 0 && commits.All(commit => commit.AlsoOnIntegration);
        var releasedToRelease = commits.Count > 0 && commits.All(commit => commit.AlsoOnRelease);
        string? Head(string target) => reach?.Heads.FirstOrDefault(head => head.Ref == target)?.Sha;
        var state = read.State != AgentStudio.Git.SourceFactStates.Ready
            ? read.State
            : reach is { Missing.Count: > 0 }
                ? AgentStudio.Git.SourceFactStates.Warming
                : AgentStudio.Git.SourceFactStates.Ready;
        return new TaskProvenanceView
        {
            Branch = prov?.Branch ?? branch,
            Base = prov?.Base,
            Transitions = prov?.Transitions ?? [],
            Merge = prov?.Merge,
            LandedState = releasedToRelease
                ? LandedStates.ReleasedToMain
                : mergedToIntegration ? LandedStates.MergedToDevelop : LandedStates.OnBranchOnly,
            Ladder = new TaskLandedLadder
            {
                Branch = branch,
                BranchTip = prov?.Transitions.LastOrDefault(transition => transition.BranchTip is not null)?.BranchTip,
                IntegrationBranch = integrationBranch,
                IntegrationHead = Head(integrationBranch),
                MergedToIntegration = mergedToIntegration,
                ReleaseBranch = ReleaseBranch,
                ReleaseHead = Head(ReleaseBranch),
                ReleasedToRelease = releasedToRelease,
            },
            Commits = commits,
            SourceFacts = state,
        };
    }

    /// <summary>The persisted attributed commit chain, the same set integration status uses.</summary>
    private static List<TaskCommitInfo> AttributedChain(TaskInfo info)
    {
        var chain = info.Commits.Count > 0
            ? info.Commits
            : info.Commit is null ? [] : new List<TaskCommitInfo> { info.Commit };
        var attributed = new HashSet<string>(
            TaskIntegrationStatusService.AttributedCommits(info),
            StringComparer.OrdinalIgnoreCase);
        return chain.Where(c => !string.IsNullOrWhiteSpace(c.Sha) && attributed.Contains(c.Sha)).ToList();
    }

    /// <summary>
    /// The ONE recording hook. Appends a single <see cref="TaskProvenanceTransition"/>
    /// anchor for the lane the task just entered, pinning the
    /// <c>task/&lt;id&gt;</c> tip and the integration-branch head at that instant.
    /// The <see cref="TaskProvenance.Base"/> fork point is captured once, on the
    /// first anchor that can see the branch. Best-effort: any failure is logged
    /// and swallowed so the move that already landed is never undone.
    /// </summary>
    public void RecordTransition(TaskInfo info, string targetLane)
    {
        try
        {
            var repoRoot = _git.ResolveRepoRootForWatchPath(info.WatchPath);
            if (string.IsNullOrWhiteSpace(repoRoot)) return;

            var branch = WorktreeTaskLifecycle.BranchFor(info.Id);
            var integrationBranch = ResolveIntegrationBranch(info, repoRoot);

            var branchTip = _git.GetBranchTip(repoRoot, branch);
            var workBranchHead = HeadOf(repoRoot, integrationBranch);

            var existing = info.Provenance;
            // Capture the fork point once, the first time the branch is visible.
            var baseSha = existing?.Base;
            if (string.IsNullOrWhiteSpace(baseSha) && branchTip != null)
                baseSha = _git.GetMergeBase(repoRoot, branch, integrationBranch)
                          ?? _git.GetMergeBase(repoRoot, branch, "origin/" + integrationBranch);

            var transition = new TaskProvenanceTransition
            {
                Lane = targetLane,
                AtUtc = DateTime.UtcNow,
                BranchTip = branchTip,
                WorkBranchHead = workBranchHead,
            };

            var merged = AppendTransition(existing, branch, baseSha, transition);
            _mutations.SetProvenanceOnFolder(info.FolderPath, merged);

            _logger.LogInformation(
                "provenance-recorded job={JobId} lane={Lane} branch={Branch} branchTip={BranchTip} base={Base}",
                info.Id, targetLane, branch,
                branchTip == null ? "(none)" : Short(branchTip),
                baseSha == null ? "(none)" : Short(baseSha));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "provenance-record failed for {JobId} entering {Lane}", info.Id, targetLane);
        }
    }

    /// <summary>
    /// Pure append: returns a new <see cref="TaskProvenance"/> with
    /// <paramref name="transition"/> added to the end of the transition list. The
    /// <see cref="TaskProvenance.Base"/> fork point is write-once (an existing base
    /// is never overwritten); <see cref="TaskProvenance.Merge"/> is carried through
    /// untouched (it is owned by sibling slice ASS-1721). Side-effect-free so it is
    /// trivially unit-testable.
    /// </summary>
    public static TaskProvenance AppendTransition(
        TaskProvenance? existing,
        string branch,
        string? baseSha,
        TaskProvenanceTransition transition)
    {
        var transitions = existing?.Transitions is { Count: > 0 }
            ? new List<TaskProvenanceTransition>(existing.Transitions)
            : new List<TaskProvenanceTransition>();
        transitions.Add(transition);

        return new TaskProvenance
        {
            Branch = string.IsNullOrWhiteSpace(existing?.Branch) ? branch : existing!.Branch,
            Base = string.IsNullOrWhiteSpace(existing?.Base) ? baseSha : existing!.Base,
            Transitions = transitions,
            Merge = existing?.Merge,
        };
    }

    /// <summary>
    /// Records the develop-merge fact (ASS-1721 / ASS-1752): writes the
    /// <see cref="TaskProvenanceMerge"/> block onto the task's provenance the first
    /// time the <c>task/&lt;id&gt;</c> branch is folded into the integration branch.
    /// This is historical attempt evidence, not current target-branch truth.
    /// Accepted-card status is recomputed from attributed commits. The write still
    /// requires a fresh <see cref="TaskInfo"/> because the on-disk provenance write
    /// is replace-all. Write-once like <see cref="TaskProvenance.Base"/> and
    /// best-effort: a failure here can never undo the merge that already landed.
    /// </summary>
    public void RecordMerge(TaskInfo info, string? mergeSha, string? beforeSha = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(mergeSha)) return;
            if (info.Provenance?.Merge?.MergeCommit is { Length: > 0 }) return; // write-once

            var merge = new TaskProvenanceMerge
            {
                MergeCommit = mergeSha,
                WorkBranchHeadBefore = beforeSha,
                WorkBranchHeadAfter = mergeSha,
                AtUtc = DateTime.UtcNow,
            };

            var merged = WithMerge(info.Provenance, WorktreeTaskLifecycle.BranchFor(info.Id), merge);
            _mutations.SetProvenanceOnFolder(info.FolderPath, merged);

            _logger.LogInformation(
                "provenance-merge-recorded job={JobId} mergeCommit={Merge} before={Before}",
                info.Id, Short(mergeSha), beforeSha == null ? "(none)" : Short(beforeSha));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "provenance-merge-record failed for {JobId}", info.Id);
        }
    }

    /// <summary>
    /// Pure write-once merge anchor: returns a new <see cref="TaskProvenance"/> with
    /// <paramref name="merge"/> set, carrying the branch, base, and transitions
    /// through untouched. An existing <see cref="TaskProvenance.Merge"/> is never
    /// overwritten (append-only fact). Side-effect-free so it is trivially
    /// unit-testable, matching <see cref="AppendTransition"/>.
    /// </summary>
    public static TaskProvenance WithMerge(
        TaskProvenance? existing,
        string branch,
        TaskProvenanceMerge merge)
    {
        var transitions = existing?.Transitions is { Count: > 0 }
            ? new List<TaskProvenanceTransition>(existing.Transitions)
            : new List<TaskProvenanceTransition>();

        return new TaskProvenance
        {
            Branch = string.IsNullOrWhiteSpace(existing?.Branch) ? branch : existing!.Branch,
            Base = existing?.Base,
            Transitions = transitions,
            Merge = existing?.Merge ?? merge,
        };
    }

    /// <summary>
    /// Graph-based landed-state for an anchor SHA. Returns
    /// <see cref="LandedStates.ReleasedToMain"/> when the anchor is an ancestor of
    /// the release branch, <see cref="LandedStates.MergedToDevelop"/> when it is an
    /// ancestor of the integration branch, otherwise
    /// <see cref="LandedStates.OnBranchOnly"/> (including when there is no anchor at
    /// all). Both local and <c>origin/</c>-prefixed refs are checked, so a fresh
    /// clone with only remote-tracking branches still resolves. Static + takes the
    /// <see cref="GitService"/> so tests can drive it against a throwaway repo
    /// without constructing the full service.
    /// </summary>
    public static string DeriveLandedState(
        GitService git,
        string repoRoot,
        string? anchorSha,
        string integrationBranch,
        string releaseBranch = ReleaseBranch)
    {
        if (string.IsNullOrWhiteSpace(anchorSha)) return LandedStates.OnBranchOnly;
        if (IsContainedIn(git, repoRoot, anchorSha, releaseBranch)) return LandedStates.ReleasedToMain;
        if (IsContainedIn(git, repoRoot, anchorSha, integrationBranch)) return LandedStates.MergedToDevelop;
        return LandedStates.OnBranchOnly;
    }

    /// <summary>
    /// Read-time projection for <c>GET /api/tasks/{id}/provenance</c>: the
    /// persisted facts plus the live-derived landed-state, landed ladder, and
    /// per-commit branch membership. Never persisted; recomputed on every read so
    /// it never lies about where develop / main currently are.
    /// </summary>
    public TaskProvenanceView BuildView(TaskInfo info)
    {
        // Measure the provenance endpoint's git spawns (AGT-2007). This was the
        // heaviest git-info request: it forked a fixed handful of ref reads plus
        // TWO `merge-base --is-ancestor` spawns PER attributed commit, all
        // serial, so a multi-commit task's landed-state view cost seconds. The
        // work below is now fanned out in parallel waves and the per-commit
        // membership is batched into a fixed set of `rev-list` reads.
        using var _t = GitProcessTelemetry.BeginRequest("tasks/provenance", _logger);

        var branch = WorktreeTaskLifecycle.BranchFor(info.Id);
        var prov = info.Provenance;
        var repoRoot = _git.ResolveRepoRootForWatchPath(info.WatchPath);
        var integrationBranch = ResolveIntegrationBranch(info, repoRoot);

        // No resolvable repo (e.g. project not configured for git): surface the
        // persisted facts with an empty derived view rather than throwing.
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return new TaskProvenanceView
            {
                Branch = prov?.Branch ?? branch,
                Base = prov?.Base,
                Transitions = prov?.Transitions ?? [],
                Merge = prov?.Merge,
                LandedState = LandedStates.OnBranchOnly,
                Ladder = new TaskLandedLadder { Branch = prov?.Branch ?? branch, IntegrationBranch = integrationBranch, ReleaseBranch = ReleaseBranch },
                Commits = FallbackMembership(info, null, integrationBranch),
            };
        }

        var root = repoRoot!;

        // Wave 1: the branch tip and the two integration/release heads are
        // independent ref reads. Fetch them concurrently instead of paying three
        // (up to five, counting origin/ fallbacks) serial spawns in a row.
        var tBranchTip = Task.Run(() => _git.GetBranchTip(root, branch));
        var tIntegrationHead = Task.Run(() => HeadOf(root, integrationBranch));
        var tReleaseHead = Task.Run(() => HeadOf(root, ReleaseBranch));
        Task.WaitAll(tBranchTip, tIntegrationHead, tReleaseHead);
        var liveBranchTip = tBranchTip.Result;
        var integrationHead = tIntegrationHead.Result;
        var releaseHead = tReleaseHead.Result;

        // The ladder uses the same attributed commit set as integration.status.
        // Every attributed commit must be present; branch tips and remembered
        // merge attempts never substitute for target-branch membership.
        var commits = BuildCommitMembership(info, root, integrationBranch);
        var mergedToIntegration = commits.Count > 0
            && commits.All(commit => commit.AlsoOnIntegration);
        var releasedToRelease = commits.Count > 0
            && commits.All(commit => commit.AlsoOnRelease);

        var landedState = releasedToRelease
            ? LandedStates.ReleasedToMain
            : mergedToIntegration ? LandedStates.MergedToDevelop : LandedStates.OnBranchOnly;

        var ladder = new TaskLandedLadder
        {
            Branch = branch,
            BranchTip = liveBranchTip,
            IntegrationBranch = integrationBranch,
            IntegrationHead = integrationHead,
            MergedToIntegration = mergedToIntegration,
            ReleaseBranch = ReleaseBranch,
            ReleaseHead = releaseHead,
            ReleasedToRelease = releasedToRelease,
        };

        return new TaskProvenanceView
        {
            Branch = prov?.Branch ?? branch,
            Base = prov?.Base,
            Transitions = prov?.Transitions ?? [],
            Merge = prov?.Merge,
            LandedState = landedState,
            Ladder = ladder,
            Commits = commits,
        };
    }

    /// <summary>
    /// Per-commit branch membership for the persisted attributed commit set.
    /// This is the same set used by <see cref="TaskIntegrationStatusService"/>;
    /// live branch-only WIP does not affect integration fields.
    /// </summary>
    private List<TaskCommitMembership> BuildCommitMembership(
        TaskInfo info,
        string repoRoot,
        string integrationBranch)
        => FallbackMembership(info, repoRoot, integrationBranch);

    /// <summary>
    /// Fallback merge-set from the persisted attributed commit chain when there is
    /// no live branch to walk. Membership is still graph-checked when a repo root
    /// is available so a sequential-run task whose commits landed on develop still
    /// reads correctly.
    /// </summary>
    private List<TaskCommitMembership> FallbackMembership(TaskInfo info, string? repoRoot, string integrationBranch)
    {
        var chain = info.Commits.Count > 0
            ? info.Commits
            : info.Commit is null ? [] : new List<TaskCommitInfo> { info.Commit };

        var attributed = new HashSet<string>(
            TaskIntegrationStatusService.AttributedCommits(info),
            StringComparer.OrdinalIgnoreCase);
        var commits = chain
            .Where(c => !string.IsNullOrWhiteSpace(c.Sha) && attributed.Contains(c.Sha))
            .ToList();

        // No repo to graph-check against: the chain is task-only by construction.
        if (repoRoot == null)
        {
            return commits.Select(c => new TaskCommitMembership
            {
                Sha = c.Sha,
                ShortSha = string.IsNullOrWhiteSpace(c.ShortSha) ? Short(c.Sha) : c.ShortSha,
                Message = c.Message,
                OnTaskBranch = true,
                AlsoOnIntegration = false,
                AlsoOnRelease = false,
            }).ToList();
        }

        // Each commit needs two ancestry checks; the chain is small (persisted
        // attributed commits), so fan the checks out per commit rather than
        // paying 2*N serial spawns (AGT-2007). Order is preserved.
        var tasks = commits.Select(c => Task.Run(() => new TaskCommitMembership
        {
            Sha = c.Sha,
            ShortSha = string.IsNullOrWhiteSpace(c.ShortSha) ? Short(c.Sha) : c.ShortSha,
            Message = c.Message,
            OnTaskBranch = true,
            AlsoOnIntegration = IsContainedIn(_git, repoRoot, c.Sha, integrationBranch),
            AlsoOnRelease = IsContainedIn(_git, repoRoot, c.Sha, ReleaseBranch),
        })).ToArray();
        Task.WaitAll(tasks);
        return tasks.Select(t => t.Result).ToList();
    }

    private string ResolveIntegrationBranch(TaskInfo task, string? repoRoot)
    {
        var configured = _settings.Get(task.ProjectName).IntegrationBranch;
        if (!string.IsNullOrWhiteSpace(repoRoot))
            return _git.ResolveIntegrationBranch(repoRoot, configured);
        return configured;
    }

    /// <summary>Live head of a branch, checking the local ref then its <c>origin/</c> mirror.</summary>
    private string? HeadOf(string repoRoot, string branch)
        => _git.GetBranchTip(repoRoot, branch) ?? _git.GetBranchTip(repoRoot, "origin/" + branch);

    private static bool IsContainedIn(GitService git, string repoRoot, string sha, string branch)
        => git.IsAncestor(repoRoot, sha, branch) || git.IsAncestor(repoRoot, sha, "origin/" + branch);

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
