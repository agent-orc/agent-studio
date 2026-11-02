namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Pure review package formation (AGT-3400 PK3 A, PK5 A). A package holds the
/// cards of one Dossier whose card check passed within the formation window
/// of the first, up to the member cap and within the union material budget.
/// It waits only for cards whose card check is still running, never for a
/// card being coded, and a lone member is a package of one.
/// </summary>
public static class ReviewPackageFormationPolicy
{
    public const string MemberCapReason = "member cap reached";
    public const string BudgetReason = "material budget reached";
    public const string WindowReason = "formation window elapsed";
    public const string LoneReason = "lone member: package of one";
    public const string NoneInReviewReason = "no other member in review";
    public const string OverBudgetReason = "member alone exceeds the material budget";

    public static IReadOnlyList<ReviewPackageFormationDecision> Plan(ReviewPackageFormationInput input)
    {
        var decisions = new List<ReviewPackageFormationDecision>();
        var limits = input.Limits;
        var waves = input.Candidates
            .Where(card => !string.IsNullOrWhiteSpace(card.DossierPath))
            .GroupBy(card => NormalizePath(card.DossierPath), StringComparer.OrdinalIgnoreCase)
            .OrderBy(wave => wave.Key, StringComparer.Ordinal);
        foreach (var wave in waves)
        {
            var ready = wave.Where(card => card.Stage == ReviewPackageCandidateStages.CardCheckPassed
                    && card.CardCheckPassedAtUtc is not null)
                .OrderBy(card => card.CardCheckPassedAtUtc)
                .ThenBy(card => card.TaskKey, StringComparer.Ordinal)
                .ToList();
            var inReview = wave.Where(card => card.Stage == ReviewPackageCandidateStages.InReview)
                .Select(card => card.TaskKey).Order(StringComparer.Ordinal).ToArray();
            var considered = ready.Select(card => card.TaskKey).ToArray();
            while (ready.Count > 0)
            {
                var first = ready[0];
                if (!Fits([first], limits))
                {
                    decisions.Add(Decision(wave.Key, ReviewPackageFormationDecisions.PerCard, OverBudgetReason,
                        [first], null, [], considered, input.NowUtc));
                    ready.RemoveAt(0);
                    continue;
                }

                var closeAt = first.CardCheckPassedAtUtc!.Value.AddMinutes(limits.WindowMinutes);
                var members = new List<ReviewPackageCandidate> { first };
                string? stop = null;
                foreach (var next in ready.Skip(1))
                {
                    if (next.CardCheckPassedAtUtc > closeAt) { stop = WindowReason; break; }
                    if (members.Count >= limits.MaximumMembers) { stop = MemberCapReason; break; }
                    if (!Fits([.. members, next], limits)) { stop = BudgetReason; break; }
                    members.Add(next);
                }

                var reason = members.Count >= limits.MaximumMembers ? MemberCapReason
                    : stop ?? (input.NowUtc >= closeAt ? WindowReason : null);
                if (reason is null && inReview.Length > 0)
                {
                    decisions.Add(Decision(wave.Key, ReviewPackageFormationDecisions.Wait,
                        $"waiting for {inReview.Length} member(s) in review", members, closeAt,
                        inReview, considered, input.NowUtc));
                    break;
                }
                decisions.Add(Decision(wave.Key, ReviewPackageFormationDecisions.Form,
                    reason ?? (members.Count == 1 ? LoneReason : NoneInReviewReason),
                    members, closeAt, [], considered, input.NowUtc));
                ready.RemoveAll(card => members.Contains(card));
            }
        }
        return decisions;
    }

    /// <summary>
    /// Stack order: a member follows every member it depends on
    /// (<c>references.dependsOn</c>), then the Dossier implementation index,
    /// then card-check order. A dependency cycle falls back to index order.
    /// </summary>
    public static IReadOnlyList<ReviewPackageCandidate> StackOrder(IReadOnlyList<ReviewPackageCandidate> members)
    {
        var keys = members.Select(member => member.TaskKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = members.OrderBy(Rank).ToList();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ReviewPackageCandidate>();
        while (pending.Count > 0)
        {
            var next = pending.FirstOrDefault(member => (member.DependsOn ?? [])
                .Where(keys.Contains).All(placed.Contains)) ?? pending[0];
            ordered.Add(next);
            placed.Add(next.TaskKey);
            pending.Remove(next);
        }
        return ordered;
    }

    private static (int, DateTimeOffset, string) Rank(ReviewPackageCandidate member)
        => (member.ImplementationIndex ?? int.MaxValue,
            member.CardCheckPassedAtUtc ?? DateTimeOffset.MaxValue, member.TaskKey);

    private static bool Fits(IReadOnlyList<ReviewPackageCandidate> members, ReviewPackageLimits limits)
        => UnionFiles(members) <= limits.MaximumFiles && members.Sum(member => member.ChangedLines) <= limits.MaximumLines;

    private static int UnionFiles(IEnumerable<ReviewPackageCandidate> members)
        => members.SelectMany(member => member.Paths).Distinct(StringComparer.Ordinal).Count();

    private static ReviewPackageFormationDecision Decision(
        string dossier, string decision, string reason, IReadOnlyList<ReviewPackageCandidate> members,
        DateTimeOffset? closeAt, IReadOnlyList<string> waiting, IReadOnlyList<string> considered,
        DateTimeOffset now)
    {
        var stacked = decision == ReviewPackageFormationDecisions.PerCard ? members : StackOrder(members);
        return new ReviewPackageFormationDecision(dossier, decision, reason,
            stacked.Select(member => member.TaskKey).ToArray(),
            decision == ReviewPackageFormationDecisions.Wait ? closeAt : null,
            UnionFiles(members), members.Sum(member => member.ChangedLines),
            members.Any(member => !member.SizeKnown), waiting, considered, now);
    }

    public static string NormalizePath(string path) => path.Trim().Replace('\\', '/').TrimStart('/');
}
