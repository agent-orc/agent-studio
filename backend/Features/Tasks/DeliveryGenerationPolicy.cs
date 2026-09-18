namespace AgentStudio.Tasks;

/// <summary>
/// One attributed commit with the generation it belongs to and the rule that
/// decided its integration verdict.
/// </summary>
/// <param name="Generation">1-based delivery generation, oldest first.</param>
/// <param name="GenerationIdentity">
/// Run attempt or result SHA that fenced the generation, or null for a legacy
/// record that carries no generation marker at all.
/// </param>
/// <param name="Rule">One of <see cref="CommitIntegrationRules"/>.</param>
/// <param name="ReplacementSha">
/// The later commit that carries this commit's work, when the rule is a
/// supersession. Null otherwise.
/// </param>
public sealed record CommitIntegrationEvidence(
    string Sha,
    int Generation,
    string? GenerationIdentity,
    bool IsCurrentGeneration,
    string Rule,
    string? ReplacementSha)
{
    public bool IsIntegrated => CommitIntegrationRules.IsIntegrated(Rule);
    public bool IsSuperseded => CommitIntegrationRules.IsSuperseded(Rule);
    public bool IsMissing => string.Equals(Rule, CommitIntegrationRules.Missing, StringComparison.Ordinal);
}

/// <summary>
/// The card's delivery expectation split into the current generation and the
/// generations it replaced. <see cref="Missing"/> is the only set that may
/// block acceptance.
/// </summary>
public sealed record DeliveryGenerationVerdict(
    IReadOnlyList<CommitIntegrationEvidence> Commits,
    int CurrentGeneration,
    bool CurrentGenerationIsIdentified)
{
    public static readonly DeliveryGenerationVerdict Empty = new([], 0, false);

    /// <summary>Commits that landed, by ancestry or by proven content equality.</summary>
    public IReadOnlyList<CommitIntegrationEvidence> Integrated
        => Commits.Where(commit => commit.IsIntegrated).ToList();

    /// <summary>Earlier-generation history that is no longer a delivery expectation.</summary>
    public IReadOnlyList<CommitIntegrationEvidence> Superseded
        => Commits.Where(commit => commit.IsSuperseded).ToList();

    /// <summary>Current expectations that are not in the integration branch.</summary>
    public IReadOnlyList<CommitIntegrationEvidence> Missing
        => Commits.Where(commit => commit.IsMissing).ToList();

    /// <summary>
    /// The commit that proves the delivery landed: the newest integrated commit
    /// of the current generation, or the newest integrated commit of any
    /// generation when the current one contributed none. Null when nothing
    /// landed.
    /// </summary>
    public CommitIntegrationEvidence? Anchor
        => Commits.LastOrDefault(commit => commit.IsIntegrated && commit.IsCurrentGeneration)
            ?? Commits.LastOrDefault(commit => commit.IsIntegrated);
}

/// <summary>
/// AGT-2871 - pure delivery-generation policy. A card that was continued after
/// a first delivery carries the commits of every round in <c>commits[]</c>.
/// Only the current round is a delivery expectation; the rounds it replaced are
/// readable history. Treating them as missing is what left nine integrated
/// cards sitting in Human Review with a <c>pending</c> / <c>partial</c> badge
/// while their final delivery was long on <c>develop</c>.
///
/// <para>
/// Generation identity is the fenced run attempt (<see cref="TaskCommitInfo.RunAttemptId"/>)
/// or, failing that, the verified result tip (<see cref="TaskCommitInfo.ResultSha"/>).
/// The delivery branch is deliberately NOT an identity: the canonical runner
/// ref <c>runner/&lt;host&gt;/&lt;KEY&gt;</c> is reused across rounds, so equal
/// branches prove nothing about generations. A generation boundary is a change
/// of that identity between two adjacent commits; an unmarked legacy commit is
/// its own generation, because an identified generation stamps every commit it
/// produced and an unstamped commit therefore cannot belong to it.
/// </para>
///
/// <para>
/// Generation-based supersession only fires when the CURRENT generation is
/// identified. A wholly legacy card carries no generation evidence at all, so
/// its older commits are decided by the content fallbacks instead - never by
/// the mere fact that a newer commit exists. That gate is what keeps
/// "a missing commit of the current generation blocks acceptance" true.
/// </para>
/// </summary>
public static class DeliveryGenerationPolicy
{
    /// <summary>
    /// Assigns 1-based generations to <paramref name="commits"/> (oldest to
    /// newest). Pure and total: every commit gets a generation, even when the
    /// card carries no markers at all.
    /// </summary>
    public static IReadOnlyList<CommitGenerationAssignment> AssignGenerations(
        IReadOnlyList<TaskCommitInfo> commits)
    {
        var assignments = new List<CommitGenerationAssignment>(commits.Count);
        var generation = 0;
        string? previousIdentity = null;
        foreach (var commit in commits)
        {
            var identity = Identity(commit);
            // Unmarked commits never share a generation - not with each other
            // and not with a marked round. "Unknown" is not an identity.
            if (identity is null || !string.Equals(identity, previousIdentity, StringComparison.OrdinalIgnoreCase))
                generation++;
            previousIdentity = identity;
            assignments.Add(new CommitGenerationAssignment(commit.Sha, generation, identity));
        }
        return assignments;
    }

    /// <param name="commits">The card's attributed commits for one repository, oldest to newest.</param>
    /// <param name="isIntegrationAncestor">Integration-branch ancestry for one SHA.</param>
    /// <param name="isContentEqual">
    /// Optional, git-backed check that merging a commit into the integration
    /// branch yields the branch's own tree. The hot-path projection passes null
    /// (no per-card git spawn) and reads the persisted
    /// <see cref="CommitIntegrationRules.ContentEqual"/> verdict instead; the
    /// reconcile pass passes the real check and records what it found.
    /// </param>
    public static DeliveryGenerationVerdict Evaluate(
        IReadOnlyList<TaskCommitInfo> commits,
        Func<string, bool> isIntegrationAncestor,
        Func<TaskCommitInfo, bool>? isContentEqual = null)
    {
        if (commits.Count == 0) return DeliveryGenerationVerdict.Empty;

        var generations = AssignGenerations(commits);
        var currentGeneration = generations[^1].Generation;
        var currentIsIdentified = generations[^1].Identity is not null;

        // Landed-ness first, for every commit, so the supersession rules below
        // can ask "is a LATER commit already integrated?" without recursion.
        // Ancestry is asked once per commit: on the board this runs for every
        // delivered card, and an abbreviated persisted SHA costs a set scan.
        var ancestor = new bool[commits.Count];
        var landed = new bool[commits.Count];
        for (var index = 0; index < commits.Count; index++)
        {
            var commit = commits[index];
            ancestor[index] = isIntegrationAncestor(commit.Sha);
            landed[index] = ancestor[index]
                || string.Equals(
                    CommitIntegrationRules.Normalize(commit.IntegrationRule),
                    CommitIntegrationRules.ContentEqual,
                    StringComparison.Ordinal)
                || isContentEqual?.Invoke(commit) == true;
        }

        // Last resort for legacy records the path rules cannot decide: the
        // shipped, conservative changed-file breadth heuristic.
        var landedBySha = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < commits.Count; index++)
            landedBySha[commits[index].Sha] = landed[index];
        var breadthReplacements = SupersededCommitSweepPolicy.Evaluate(
                commits,
                sha => landedBySha.GetValueOrDefault(sha),
                isCandidate: static _ => true)
            .Replacements
            .ToDictionary(
                replacement => replacement.SupersededSha,
                replacement => replacement.ReplacementSha,
                StringComparer.OrdinalIgnoreCase);

        var evidence = new List<CommitIntegrationEvidence>(commits.Count);
        for (var index = 0; index < commits.Count; index++)
        {
            var commit = commits[index];
            var assignment = generations[index];
            var isCurrent = assignment.Generation == currentGeneration;

            if (landed[index])
            {
                var rule = ancestor[index]
                    ? CommitIntegrationRules.Ancestor
                    : CommitIntegrationRules.ContentEqual;
                evidence.Add(Evidence(assignment, isCurrent, rule, replacement: null));
                continue;
            }

            // An earlier generation of an identified current round is history,
            // not a hole: the card was continued and re-delivered.
            if (!isCurrent && currentIsIdentified)
            {
                evidence.Add(Evidence(
                    assignment,
                    isCurrent,
                    CommitIntegrationRules.SupersededGeneration,
                    CurrentGenerationAnchor(commits, generations, landed, currentGeneration)));
                continue;
            }

            var contentReplacement = ContentReplacement(commits, generations, landed, index);
            if (contentReplacement is not null)
            {
                evidence.Add(Evidence(
                    assignment,
                    isCurrent,
                    CommitIntegrationRules.SupersededContent,
                    contentReplacement));
                continue;
            }

            if (breadthReplacements.TryGetValue(commit.Sha, out var breadthReplacement))
            {
                evidence.Add(Evidence(
                    assignment,
                    isCurrent,
                    CommitIntegrationRules.SupersededBreadth,
                    breadthReplacement));
                continue;
            }

            evidence.Add(Evidence(assignment, isCurrent, CommitIntegrationRules.Missing, replacement: null));
        }

        return new DeliveryGenerationVerdict(evidence, currentGeneration, currentIsIdentified);
    }

    /// <summary>
    /// The later, integrated commit of a DIFFERENT generation whose changed
    /// files cover every path the commit at <paramref name="index"/> touched.
    /// Null when no such commit exists, or when the commit carries no path
    /// metadata to compare - an unknown breadth is never proof of replacement.
    /// </summary>
    private static string? ContentReplacement(
        IReadOnlyList<TaskCommitInfo> commits,
        IReadOnlyList<CommitGenerationAssignment> generations,
        IReadOnlyList<bool> landed,
        int index)
    {
        var paths = NormalizePaths(commits[index].Files);
        if (paths.Count == 0) return null;

        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? newest = null;
        for (var later = index + 1; later < commits.Count; later++)
        {
            if (!landed[later]
                || generations[later].Generation == generations[index].Generation)
            {
                continue;
            }
            var laterPaths = NormalizePaths(commits[later].Files);
            if (!laterPaths.Overlaps(paths)) continue;
            covered.UnionWith(laterPaths);
            newest = commits[later].Sha;
        }

        return newest is not null && paths.IsSubsetOf(covered) ? newest : null;
    }

    /// <summary>The newest landed commit of the current generation, for supersession wording.</summary>
    private static string? CurrentGenerationAnchor(
        IReadOnlyList<TaskCommitInfo> commits,
        IReadOnlyList<CommitGenerationAssignment> generations,
        IReadOnlyList<bool> landed,
        int currentGeneration)
    {
        string? fallback = null;
        for (var index = commits.Count - 1; index >= 0; index--)
        {
            if (generations[index].Generation != currentGeneration) continue;
            if (landed[index]) return commits[index].Sha;
            fallback ??= commits[index].Sha;
        }
        return fallback;
    }

    private static CommitIntegrationEvidence Evidence(
        CommitGenerationAssignment assignment,
        bool isCurrent,
        string rule,
        string? replacement)
        => new(assignment.Sha, assignment.Generation, assignment.Identity, isCurrent, rule, replacement);

    private static string? Identity(TaskCommitInfo commit)
    {
        if (!string.IsNullOrWhiteSpace(commit.RunAttemptId)) return commit.RunAttemptId!.Trim();
        return string.IsNullOrWhiteSpace(commit.ResultSha) ? null : commit.ResultSha!.Trim();
    }

    private static HashSet<string> NormalizePaths(IReadOnlyCollection<string> files)
        => files.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/').TrimStart('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <param name="Identity">Run attempt or result SHA that fenced the generation; null on an unmarked legacy record.</param>
public sealed record CommitGenerationAssignment(string Sha, int Generation, string? Identity);
