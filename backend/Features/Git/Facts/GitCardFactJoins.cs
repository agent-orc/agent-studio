namespace AgentStudio.Git;

/// <summary>
/// The card reads of Git facts S3 as pure joins of the current task with
/// published facts: commit aggregate, aggregate file list, provenance and the
/// diff object keys a card's default diff uses. A fact the join needs but the
/// store does not hold yet is added to the caller's miss list as work for the
/// producer; the join never falls back to Git.
/// </summary>
internal static class GitCardFactJoins
{
    /// <summary>
    /// The job-level commit aggregate of <c>GET /commits</c>: run SHA ranges,
    /// the run trailer index and the persisted chain, as
    /// <see cref="TaskCommitsAggregator"/> folds them.
    /// </summary>
    internal static TaskCommitsAggregate Commits(TaskInfo info, IReadOnlyList<RunRecord> runs,
        GitPublishedFacts facts, ICollection<GitFactWork> misses)
    {
        var trailer = Infos(facts.RunCommits.GetValueOrDefault(info.Id) ?? [], facts, misses);
        return TaskCommitsAggregator.Aggregate(info, runs,
            (before, after) => RangeCommits(before, after, facts, misses), trailer);
    }

    /// <summary>The commits of <c>before..after</c> from the stored range, or a miss.</summary>
    internal static List<GitCommitInfo> RangeCommits(string before, string after, GitPublishedFacts facts,
        ICollection<GitFactWork> misses)
    {
        if (!IsSha(before) || !IsSha(after) || string.Equals(before, after, StringComparison.OrdinalIgnoreCase)) return [];
        var shas = facts.Catalog.Range(GitCommitRangeKeys.Sha(before, after));
        if (shas is null)
        {
            misses.Add(new GitRangeWork(before, after));
            return [];
        }
        return Infos(shas, facts, misses);
    }

    /// <summary>
    /// The commits of a run window without SHAs, or a miss. An open window
    /// (a run in flight) read at another HEAD still answers, and asks for a
    /// fresh read.
    /// </summary>
    internal static List<GitCommitInfo> WindowCommits(DateTime fromUtc, DateTime? toUtc, GitPublishedFacts facts,
        ICollection<GitFactWork> misses)
    {
        var key = GitCommitRangeKeys.Window(fromUtc, toUtc);
        if (toUtc is null)
        {
            var open = facts.Catalog.OpenWindow(key);
            if (open is null || !string.Equals(open.Value.Head, facts.Refs.GetValueOrDefault("HEAD"), StringComparison.OrdinalIgnoreCase))
                misses.Add(new GitWindowWork(fromUtc, null));
            return open is null ? [] : Infos(open.Value.Shas, facts, misses);
        }
        var shas = facts.Catalog.Range(key);
        if (shas is null)
        {
            misses.Add(new GitWindowWork(fromUtc, toUtc));
            return [];
        }
        return Infos(shas, facts, misses);
    }

    /// <summary>
    /// The SHA set of the combined files and diff views: every aggregate
    /// commit except superseded chain entries, in aggregate order.
    /// </summary>
    internal static IReadOnlyList<string> ActiveShas(TaskInfo info, TaskCommitsAggregate aggregate)
    {
        var superseded = info.Commits
            .Where(TaskCommitSupersession.IsSuperseded)
            .Select(commit => commit.Sha)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return aggregate.Commits
            .Where(commit => !superseded.Contains(commit.Sha))
            .Select(commit => commit.Sha)
            .Where(sha => !string.IsNullOrWhiteSpace(sha) && IsShaOrRef(sha))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The facts of <paramref name="shas"/>, recording a miss for each one not stored yet.</summary>
    internal static List<GitCommitFact> Facts(IEnumerable<string> shas, GitPublishedFacts facts,
        ICollection<GitFactWork> misses)
    {
        var found = new List<GitCommitFact>();
        foreach (var sha in shas)
        {
            if (facts.Catalog.Commit(sha) is { } fact) found.Add(fact);
            else if (!facts.Catalog.IsMissing(sha)) misses.Add(new GitCommitWork(sha));
        }
        return found;
    }

    /// <summary>
    /// The aggregated file list of a curated commit set, with the status
    /// combination and line sums of <see cref="GitService.GetAggregateCommitFiles"/>.
    /// </summary>
    internal static List<GitFileChange> AggregateFiles(IEnumerable<GitCommitFact> commits)
    {
        var byPath = new Dictionary<string, (string Status, int Added, int Removed)>(StringComparer.Ordinal);
        foreach (var commit in commits)
        {
            foreach (var file in commit.ToFileChanges())
            {
                var status = SimplifyStatus(file.Status);
                byPath[file.Path] = byPath.TryGetValue(file.Path, out var existing)
                    ? (existing.Status == status ? status : "M", existing.Added + file.Added, existing.Removed + file.Removed)
                    : (status, file.Added, file.Removed);
            }
        }
        return byPath
            .Select(pair => new GitFileChange(pair.Value.Status, pair.Key, pair.Value.Added, pair.Value.Removed))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// <c>GET /provenance</c> from the published generation: ladder heads from
    /// its ref object IDs, per-commit membership from the reachability sets of
    /// the integration and release refs. Same shape and rules as
    /// <see cref="TaskProvenanceService.BuildView"/>.
    /// </summary>
    internal static TaskProvenanceView Provenance(TaskInfo info, GitPublishedFacts facts)
    {
        var branch = WorktreeTaskLifecycle.BranchFor(info.Id);
        var prov = info.Provenance;
        var integrationBranch = facts.IntegrationBranch;
        const string release = TaskProvenanceService.ReleaseBranch;
        if (facts.State == GitPublishedFacts.UnavailableState)
        {
            return new TaskProvenanceView
            {
                Branch = prov?.Branch ?? branch,
                Base = prov?.Base,
                Transitions = prov?.Transitions ?? [],
                Merge = prov?.Merge,
                LandedState = LandedStates.OnBranchOnly,
                Ladder = new TaskLandedLadder
                {
                    Branch = prov?.Branch ?? branch, IntegrationBranch = integrationBranch, ReleaseBranch = release,
                },
                Commits = Membership(info, null),
            };
        }

        var commits = Membership(info, facts);
        var merged = commits.Count > 0 && commits.All(commit => commit.AlsoOnIntegration);
        var released = commits.Count > 0 && commits.All(commit => commit.AlsoOnRelease);
        return new TaskProvenanceView
        {
            Branch = prov?.Branch ?? branch,
            Base = prov?.Base,
            Transitions = prov?.Transitions ?? [],
            Merge = prov?.Merge,
            LandedState = released ? LandedStates.ReleasedToMain
                : merged ? LandedStates.MergedToDevelop : LandedStates.OnBranchOnly,
            Ladder = new TaskLandedLadder
            {
                Branch = branch,
                BranchTip = facts.Tip(branch),
                IntegrationBranch = integrationBranch,
                IntegrationHead = facts.Tip(integrationBranch) ?? facts.Tip("origin/" + integrationBranch),
                MergedToIntegration = merged,
                ReleaseBranch = release,
                ReleaseHead = facts.Tip(release) ?? facts.Tip("origin/" + release),
                ReleasedToRelease = released,
            },
            Commits = commits,
        };
    }

    /// <summary>The persisted attributed chain with branch membership; task-only without facts.</summary>
    private static List<TaskCommitMembership> Membership(TaskInfo info, GitPublishedFacts? facts)
    {
        var chain = info.Commits.Count > 0
            ? info.Commits
            : info.Commit is null ? [] : new List<TaskCommitInfo> { info.Commit };
        var attributed = new HashSet<string>(TaskIntegrationStatusService.AttributedCommits(info),
            StringComparer.OrdinalIgnoreCase);
        return chain
            .Where(commit => !string.IsNullOrWhiteSpace(commit.Sha) && attributed.Contains(commit.Sha))
            .Select(commit => new TaskCommitMembership
            {
                Sha = commit.Sha,
                ShortSha = string.IsNullOrWhiteSpace(commit.ShortSha) ? Short(commit.Sha) : commit.ShortSha,
                Message = commit.Message,
                OnTaskBranch = true,
                AlsoOnIntegration = facts?.IsContainedIn(commit.Sha, facts.IntegrationBranch) == true,
                AlsoOnRelease = facts?.IsContainedIn(commit.Sha, TaskProvenanceService.ReleaseBranch) == true,
            })
            .ToList();
    }

    /// <summary>
    /// The diff objects behind a card's default diff, for the producer to write
    /// at publication: the selected commit of a one-commit chain, or every
    /// active commit plus the aggregate when the chain has several, matching
    /// <c>applyCommitDefault</c> of the Studio's Git pane.
    /// </summary>
    internal static IReadOnlyList<GitFactWork> DefaultDiffWork(TaskInfo info, IReadOnlyList<string> activeShas)
    {
        var chain = info.Commits.Count > 0
            ? info.Commits
            : info.Commit is null ? [] : new List<TaskCommitInfo> { info.Commit };
        if (chain.Count == 0) return [];
        var active = chain.Where(commit => !TaskCommitSupersession.IsSuperseded(commit)).ToList();
        if (active.Count > 1)
        {
            var work = activeShas.Select(sha => (GitFactWork)new GitCommitDiffWork(sha)).ToList();
            if (activeShas.Count > 0) work.Add(new GitAggregateDiffWork(activeShas));
            return work;
        }
        var selected = active.FirstOrDefault() ?? chain[^1];
        var single = new List<GitFactWork>();
        if (IsShaOrRef(selected.Sha)) single.Add(new GitCommitDiffWork(selected.Sha));
        if (info.Commit is { Sha: { Length: > 0 } recorded } && IsShaOrRef(recorded)
            && !string.Equals(recorded, selected.Sha, StringComparison.OrdinalIgnoreCase))
            single.Add(new GitCommitDiffWork(recorded));
        return single;
    }

    private static List<GitCommitInfo> Infos(IEnumerable<string> shas, GitPublishedFacts facts,
        ICollection<GitFactWork> misses)
        => Facts(shas, facts, misses).Select(fact => fact.ToCommitInfo()).ToList();

    private static string SimplifyStatus(string status)
        => string.IsNullOrWhiteSpace(status) ? "?" : status[0] is 'A' or 'D' or 'M' ? status[..1] : "M";

    internal static bool IsSha(string? value)
        => value is { Length: >= 7 and <= 64 } && value.All(Uri.IsHexDigit);

    /// <summary>The SHA guard of the old Git helpers: letters, digits and <c>_-.^</c> only.</summary>
    internal static bool IsShaOrRef(string value)
        => value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '^');

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
