namespace AgentStudio.Tasks;

/// <summary>One attributed commit's place in the card's delivery generations.</summary>
/// <param name="Sha">The attributed commit.</param>
/// <param name="Generation">
/// 1-based ordinal of the delivery generation this commit was proven to belong
/// to. Commits that carry no comparable generation marker join the generation
/// of the commits around them, so this is never null once the card has one
/// commit.
/// </param>
/// <param name="Evidence">One of <see cref="CommitIntegrationEvidence"/>.</param>
/// <param name="ReplacementSha">
/// The later, integrated commit that carries this commit's work, when the
/// deciding rule named one. Null for every other verdict.
/// </param>
public sealed record DeliveryGenerationCommit(
    string Sha,
    int Generation,
    string Evidence,
    string? ReplacementSha)
{
    public bool IsIntegrated => CommitIntegrationEvidence.IsIntegrated(Evidence);
    public bool IsSuperseded => CommitIntegrationEvidence.IsSuperseded(Evidence);
    public bool IsMissing => string.Equals(
        Evidence,
        CommitIntegrationEvidence.Missing,
        StringComparison.Ordinal);
}

/// <summary>
/// The generation-aware verdict for one card in one repository: which commits
/// are integrated, which belong to a superseded generation, and which are a
/// genuine hole in the current generation.
/// </summary>
public sealed record DeliveryGenerationVerdict(
    IReadOnlyList<DeliveryGenerationCommit> Commits,
    int GenerationCount)
{
    /// <summary>Highest generation ordinal, i.e. the card's current delivery generation. Zero when it has no commit.</summary>
    public int CurrentGeneration => GenerationCount;

    public IReadOnlyList<DeliveryGenerationCommit> Integrated
        => Commits.Where(commit => commit.IsIntegrated).ToList();

    public IReadOnlyList<DeliveryGenerationCommit> Superseded
        => Commits.Where(commit => commit.IsSuperseded).ToList();

    /// <summary>
    /// Commits of the current expectation that are not in the integration
    /// branch. A commit of a superseded generation is never in here: that is
    /// the whole point of generation-aware attribution.
    /// </summary>
    public IReadOnlyList<DeliveryGenerationCommit> Missing
        => Commits.Where(commit => commit.IsMissing).ToList();

    /// <summary>Commits the card still has to get into the branch (integrated plus missing).</summary>
    public int ExpectedCount => Commits.Count(commit => !commit.IsSuperseded);

    /// <summary>The newest integrated commit, which is the one that proves the delivery landed.</summary>
    public DeliveryGenerationCommit? Anchor
        => Commits.LastOrDefault(commit => commit.IsIntegrated);

    /// <summary>Every commit of the current generation, superseded or not.</summary>
    public IReadOnlyList<DeliveryGenerationCommit> CurrentGenerationCommits
        => Commits.Where(commit => commit.Generation == CurrentGeneration).ToList();

    /// <summary>All expected commits are in the branch and at least one proves it.</summary>
    public bool IsFullyIntegrated => Missing.Count == 0 && Integrated.Count > 0;

    /// <summary>True when only plain ancestry decided, i.e. nothing was superseded or content-matched.</summary>
    public bool IsPlainAncestry
        => Commits.Count > 0
           && Commits.All(commit => string.Equals(
               commit.Evidence,
               CommitIntegrationEvidence.Ancestor,
               StringComparison.Ordinal));
}

/// <summary>
/// AGT-2871 - pure, generation-aware attribution policy for the integration
/// verdict. A card that was continued after a first delivery carries the
/// commits of every generation in <c>commits[]</c>; only the generation that
/// was actually merged is an integration expectation. Without this distinction
/// an old <c>wip(runner): salvage before teardown</c> commit of the first round
/// kept nine reviewed, merged cards reading <c>pending</c>/<c>partial</c>
/// forever, which also stopped the acceptance rail from completing them.
///
/// <para>
/// Per commit, exactly one rule decides, in this order:
/// <list type="number">
/// <item><c>ancestor</c> - it is in the integration branch;</item>
/// <item><c>content-equal</c> - a previous reconciliation proved that merging
///   it adds nothing to the branch;</item>
/// <item><c>generation-superseded</c> - an explicit replacement is recorded, or
///   it belongs to a provably earlier delivery generation;</item>
/// <item><c>path-superseded</c> - legacy fallback for records without
///   generation markers: a later, integrated attributed commit of the same card
///   covers its changed paths;</item>
/// <item><c>path-superseded</c> / <c>generation-superseded</c> - a previous
///   reconciliation recorded one of those rules;</item>
/// <item><c>content-equal</c> - the caller's live content probe proves the
///   merge adds nothing (off the hot path only);</item>
/// <item><c>missing</c> - none of the above, so the commit still blocks.</item>
/// </list>
/// The decision is recorded per commit, so the card can say which rule answered
/// for which SHA instead of only showing an aggregate.
/// </para>
/// </summary>
public static class DeliveryGenerationPolicy
{
    /// <param name="commits">The card's attributed commits for one repository, oldest to newest.</param>
    /// <param name="isIntegrated">Target-branch ancestry check for one SHA.</param>
    /// <param name="isContentIntegrated">
    /// Optional content probe (<c>git merge-tree --write-tree</c>). Null on the
    /// board hot path, where no per-card git spawn is allowed; the reconcile
    /// pass supplies it and persists what it decided.
    /// </param>
    public static DeliveryGenerationVerdict Evaluate(
        IReadOnlyList<TaskCommitInfo> commits,
        Func<string, bool> isIntegrated,
        Func<TaskCommitInfo, bool>? isContentIntegrated = null)
    {
        if (commits.Count == 0) return new DeliveryGenerationVerdict([], 0);

        var generations = AssignGenerations(commits);
        var currentGeneration = generations.Count == 0 ? 0 : generations.Max();
        var integratedByIndex = commits.Select(commit => isIntegrated(commit.Sha)).ToList();
        // The historical breadth heuristic stays an accepted path-supersession
        // rule: it is the live behaviour that already recognizes a re-delivered
        // commit whose file set the later, integrated commit covers.
        var breadthReplacements = SupersededCommitSweepPolicy.Evaluate(
                commits,
                isIntegrated,
                isCandidate: static _ => true)
            .Replacements
            .ToDictionary(
                replacement => replacement.SupersededSha,
                replacement => replacement.ReplacementSha,
                StringComparer.OrdinalIgnoreCase);

        var verdicts = new List<DeliveryGenerationCommit>(commits.Count);
        for (var index = 0; index < commits.Count; index++)
        {
            var commit = commits[index];
            var generation = generations[index];

            if (integratedByIndex[index])
            {
                verdicts.Add(new DeliveryGenerationCommit(
                    commit.Sha,
                    generation,
                    CommitIntegrationEvidence.Ancestor,
                    null));
                continue;
            }

            var persisted = CommitIntegrationEvidence.Normalize(commit.IntegrationEvidence);
            if (persisted == CommitIntegrationEvidence.ContentEqual)
            {
                verdicts.Add(new DeliveryGenerationCommit(
                    commit.Sha,
                    generation,
                    CommitIntegrationEvidence.ContentEqual,
                    null));
                continue;
            }

            // An explicitly recorded replacement is a verdict the platform
            // already wrote; the pending "next-attempt" placeholder is kept out
            // of the expectation for the same reason the attribution reader
            // drops it today.
            if (TaskCommitSupersession.IsSuperseded(commit) || generation < currentGeneration)
            {
                verdicts.Add(new DeliveryGenerationCommit(
                    commit.Sha,
                    generation,
                    CommitIntegrationEvidence.GenerationSuperseded,
                    commit.SupersededBySha ?? NewestIntegratedAfter(commits, integratedByIndex, index)));
                continue;
            }

            var covering = PathSupersession(commits, integratedByIndex, index)
                ?? (breadthReplacements.TryGetValue(commit.Sha, out var breadth) ? breadth : null);
            if (covering is not null)
            {
                verdicts.Add(new DeliveryGenerationCommit(
                    commit.Sha,
                    generation,
                    CommitIntegrationEvidence.PathSuperseded,
                    covering));
                continue;
            }

            if (persisted == CommitIntegrationEvidence.PathSuperseded
                || persisted == CommitIntegrationEvidence.GenerationSuperseded)
            {
                verdicts.Add(new DeliveryGenerationCommit(
                    commit.Sha,
                    generation,
                    persisted,
                    commit.SupersededBySha));
                continue;
            }

            if (isContentIntegrated?.Invoke(commit) == true)
            {
                verdicts.Add(new DeliveryGenerationCommit(
                    commit.Sha,
                    generation,
                    CommitIntegrationEvidence.ContentEqual,
                    null));
                continue;
            }

            verdicts.Add(new DeliveryGenerationCommit(
                commit.Sha,
                generation,
                CommitIntegrationEvidence.Missing,
                null));
        }

        return new DeliveryGenerationVerdict(verdicts, currentGeneration);
    }

    /// <summary>
    /// 1-based generation ordinal per commit. A new generation starts only
    /// where two commits carry comparable markers that prove they came from
    /// different delivery attempts (<see cref="ProvenDifferentGeneration"/>).
    /// Absence of a marker never starts one: a legacy record without markers
    /// cannot prove it belongs to an earlier round, so it keeps counting
    /// towards the current expectation until the content or path rule resolves
    /// it. That conservative reading is what deliverable 2's fallback exists
    /// for.
    /// </summary>
    internal static List<int> AssignGenerations(IReadOnlyList<TaskCommitInfo> commits)
    {
        var generations = new List<int>(commits.Count);
        if (commits.Count == 0) return generations;

        var generation = 1;
        var representative = commits[0];
        generations.Add(generation);
        for (var index = 1; index < commits.Count; index++)
        {
            if (ProvenDifferentGeneration(representative, commits[index]))
            {
                generation++;
                representative = commits[index];
            }
            else if (GenerationKey(representative) is null && GenerationKey(commits[index]) is not null)
            {
                // The generation's first commit was unmarked and this one is
                // not: adopt the marker as the generation's identity so a
                // later, genuinely different attempt is still recognized.
                representative = commits[index];
            }
            generations.Add(generation);
        }
        return generations;
    }

    /// <summary>
    /// Whether two commits are proven to come from different delivery
    /// generations. Only comparable markers decide - attempt against attempt,
    /// result against result, branch against branch - which is the same rule
    /// the historical supersession migration uses.
    /// </summary>
    internal static bool ProvenDifferentGeneration(TaskCommitInfo older, TaskCommitInfo newer)
    {
        if (Differs(older.RunAttemptId, newer.RunAttemptId) is { } byAttempt) return byAttempt;
        if (Differs(older.ResultSha, newer.ResultSha) is { } byResult) return byResult;
        return Differs(older.Branch, newer.Branch) ?? false;
    }

    /// <summary>The marker that identifies a commit's delivery generation, or null when it carries none.</summary>
    internal static string? GenerationKey(TaskCommitInfo commit)
    {
        foreach (var candidate in new[] { commit.RunAttemptId, commit.ResultSha, commit.Branch })
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate!.Trim();
        return null;
    }

    private static bool? Differs(string? older, string? newer)
        => string.IsNullOrWhiteSpace(older) || string.IsNullOrWhiteSpace(newer)
            ? null
            : !string.Equals(older.Trim(), newer.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The newest later, integrated commit whose changed paths cover this
    /// commit's changed paths ("a later attributed commit is an ancestor and
    /// touches the same paths"). Null when the commit has no path metadata or
    /// when a path of it was never touched again - that is a genuine hole, not
    /// a supersession.
    /// </summary>
    private static string? PathSupersession(
        IReadOnlyList<TaskCommitInfo> commits,
        IReadOnlyList<bool> integrated,
        int index)
    {
        var paths = NormalizePaths(commits[index].Files);
        if (paths.Count == 0) return null;

        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? newestCovering = null;
        for (var later = index + 1; later < commits.Count; later++)
        {
            if (!integrated[later]) continue;
            var laterPaths = NormalizePaths(commits[later].Files);
            if (!laterPaths.Overlaps(paths)) continue;
            covered.UnionWith(laterPaths);
            newestCovering = commits[later].Sha;
        }
        return newestCovering is not null && paths.IsSubsetOf(covered) ? newestCovering : null;
    }

    private static string? NewestIntegratedAfter(
        IReadOnlyList<TaskCommitInfo> commits,
        IReadOnlyList<bool> integrated,
        int index)
    {
        for (var later = commits.Count - 1; later > index; later--)
            if (integrated[later]) return commits[later].Sha;
        return null;
    }

    private static HashSet<string> NormalizePaths(IReadOnlyCollection<string> files)
        => files.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/').TrimStart('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// AGT-2871 - the operator-facing sentence for a generation-aware verdict.
/// Pure text, so the wording is asserted directly instead of through a git
/// fixture. A card whose delivery landed says which commit proves it and what
/// happened to the earlier generations, rather than naming a superseded SHA as
/// if the delivery were incomplete.
/// </summary>
public static class DeliveryGenerationDetail
{
    /// <summary>
    /// Evidence text for a fully integrated card. Plain ancestry keeps the
    /// historical <c>anchor-ancestor</c> token, so nothing that reads it has to
    /// change for the ordinary single-generation delivery.
    /// </summary>
    public static string Integrated(DeliveryGenerationVerdict verdict)
    {
        if (verdict.IsPlainAncestry) return "anchor-ancestor";

        var anchor = verdict.Anchor;
        if (anchor is null) return "anchor-ancestor";

        var parts = new List<string>(3)
        {
            verdict.GenerationCount > 1
                ? $"integrated via {Short(anchor.Sha)} (generation {anchor.Generation})"
                : $"integrated via {Short(anchor.Sha)}",
        };

        var byContent = verdict.Commits
            .Where(commit => string.Equals(
                commit.Evidence,
                CommitIntegrationEvidence.ContentEqual,
                StringComparison.Ordinal))
            .ToList();
        if (byContent.Count > 0)
        {
            parts.Add($"{Count(byContent.Count, "commit")} integrated by content: "
                      + string.Join(", ", byContent.Select(commit => Short(commit.Sha))));
        }

        var superseded = verdict.Superseded;
        if (superseded.Count > 0)
        {
            parts.Add($"{Count(superseded.Count, "earlier generation commit")} superseded: "
                      + string.Join(", ", superseded.Select(Describe)));
        }

        return string.Join("; ", parts);
    }

    /// <summary>Evidence text for a card whose current generation is still incomplete.</summary>
    public static string Partial(DeliveryGenerationVerdict verdict)
    {
        var missing = string.Join(", ", verdict.Missing.Select(commit => Short(commit.Sha)));
        var detail = $"{verdict.Integrated.Count}/{verdict.ExpectedCount} attributed commits integrated; "
                     + $"missing: {missing}";
        return verdict.Superseded.Count == 0
            ? detail
            : detail + "; superseded: " + string.Join(", ", verdict.Superseded.Select(Describe));
    }

    /// <summary>The superseded-commit notes a repository line appends to its own count sentence.</summary>
    public static string SupersessionSuffix(DeliveryGenerationVerdict verdict)
        => verdict.Superseded.Count == 0
            ? string.Empty
            : " (" + string.Join(", ", verdict.Superseded.Select(Describe)) + ")";

    private static string Describe(DeliveryGenerationCommit commit)
        => commit.ReplacementSha is null
            ? $"{Short(commit.Sha)} ({commit.Evidence})"
            : $"{Short(commit.Sha)} superseded by {Short(commit.ReplacementSha)}";

    private static string Count(int value, string noun)
        => value == 1 ? $"1 {noun}" : $"{value} {noun}s";

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
