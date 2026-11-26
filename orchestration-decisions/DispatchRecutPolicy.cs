namespace AgentStudio.OrchestrationDecisions;

public sealed record DispatchRecutCard(string Key, int SourceOrder, string SourceKind,
    IReadOnlyList<string> Files, IReadOnlyList<string> Modules, int FindingCount,
    double? SizePrior, string? Dossier, bool Unstarted, bool Protected = false);
public sealed record DispatchRecutFacts(string Project, string WaveId,
    IReadOnlyList<DispatchRecutCard> Cards, IReadOnlyList<string> HubFiles,
    IReadOnlyList<string> HotModules, bool LiveAuthority);
public sealed record DispatchRecutAction(string Id, string Kind, IReadOnlyList<string> Cards,
    string Reason, bool Apply, IReadOnlyList<string> Modules, IReadOnlyList<string> HotWaitsFor,
    IReadOnlyList<string>? HotParts = null);
public sealed record DispatchRecutEdge(string Card, string DependsOn, string Origin = "dispatch:packer");
public sealed record DispatchRecutPlan(IReadOnlyList<DispatchRecutAction> Actions,
    IReadOnlyList<DispatchRecutEdge> Edges);
public sealed record DispatchRecutArchiveFacts(bool UnstartedBacklog, bool WindowOpen,
    bool Applying, bool Undoing, bool OriginalMember, bool ReplacementMember,
    bool OriginalPointer, bool LiveAuthority, bool ForeignIncoming = false, bool Protected = false);

/// <summary>DS-9 wave cuts. Inputs are frozen facts; this policy performs no I/O.</summary>
public static class DispatchRecutPolicy
{
    public static bool MayArchive(DispatchRecutArchiveFacts f) => f.UnstartedBacklog && f.WindowOpen && !f.ForeignIncoming && !f.Protected
        && (f.Applying && f.OriginalMember && f.OriginalPointer && f.LiveAuthority
            || f.Undoing && f.ReplacementMember);

    public static bool MayUndo(string state, DateTimeOffset? until, DateTimeOffset now)
        => state is "applied" or "applying" or "undoing" && until > now;

    public static string SourceKind(string creationSource)
    {
        var kind = creationSource.Split(':', 2)[0].Trim().ToLowerInvariant();
        return kind switch
        {
            "playthrough" => "playthrough",
            "review-findings" or "review-finding" => "review-findings",
            "scout" => "scout",
            _ => "other",
        };
    }

    public static double? Median(IReadOnlyList<int> counts)
    {
        if (counts.Count == 0) return null;
        var sorted = counts.Order().ToArray();
        return (sorted[(sorted.Length - 1) / 2] + (double)sorted[sorted.Length / 2]) / 2;
    }

    public static DispatchRecutPlan Plan(DispatchRecutFacts facts)
    {
        var cards = facts.Cards.OrderBy(c => c.SourceOrder).ThenBy(c => c.Key, StringComparer.Ordinal).ToArray();
        var actions = new List<DispatchRecutAction>();
        var edges = new List<DispatchRecutEdge>();
        var hubs = facts.HubFiles.ToHashSet(StringComparer.Ordinal);
        string[] Files(DispatchRecutCard c) => c.Files.Except(hubs, StringComparer.Ordinal).ToArray();
        bool Overlap(DispatchRecutCard a, DispatchRecutCard b) => Files(a).Intersect(Files(b), StringComparer.Ordinal).Any();
        bool Safe(DispatchRecutCard c) => c.Unstarted && !c.Protected
            && c.SourceKind is "playthrough" or "review-findings" or "scout";
        var remaining = cards.ToList();
        var clusterNumber = 0;
        while (remaining.Count > 0)
        {
            var cluster = new List<DispatchRecutCard> { remaining[0] };
            remaining.RemoveAt(0);
            for (var changed = true; changed;)
            {
                var found = remaining.Where(c => cluster.Any(other => Overlap(c, other))).ToArray();
                changed = found.Length > 0;
                cluster.AddRange(found);
                remaining.RemoveAll(c => found.Contains(c));
            }
            cluster = cluster.OrderBy(c => c.SourceOrder).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
            var groups = new List<(string Id, DispatchRecutCard[] Cards)>();
            var pending = cluster.ToList();
            while (pending.Count > 0)
            {
                var first = pending[0];
                pending.RemoveAt(0);
                var package = new List<DispatchRecutCard> { first };
                if (!Split(first))
                    foreach (var next in pending.ToArray())
                    {
                        // Every pair must share two files. A transitive cluster alone is not a package.
                        if (Split(next) || package.Sum(c => c.FindingCount) + next.FindingCount > 3
                            || next.SourceKind != first.SourceKind || next.Dossier != first.Dossier
                            || next.SizePrior is null || package.Any(c => c.SizePrior is null)
                            || package.Sum(c => c.SizePrior!.Value) + next.SizePrior.Value > 25
                            || package.SelectMany(c => c.Modules).Concat(next.Modules).Distinct().Count() > 3
                            || !package.All(c => Files(c).Intersect(Files(next), StringComparer.Ordinal).Count() >= 2)) continue;
                        package.Add(next);
                        pending.Remove(next);
                    }
                var id = package.Count == 1 ? first.Key : $"package:{clusterNumber}:{groups.Count}";
                groups.Add((id, package.ToArray()));
                if (package.Count > 1)
                    actions.Add(new(id, "package", package.Select(c => c.Key).ToArray(), "shared-files-bounded-prior",
                        facts.LiveAuthority && package.All(Safe), package.SelectMany(c => c.Modules).Distinct().Order().ToArray(), []));
                foreach (var c in package.Where(Split))
                {
                    var hot = c.Modules.Intersect(facts.HotModules, StringComparer.Ordinal).Any();
                    actions.Add(new("split:" + c.Key, "split", [c.Key], c.Modules.Count > 3 ? "over-three-modules"
                        : c.SizePrior > 25 ? "over-size-prior" : "collection-hot-module", false,
                        c.Modules.Distinct().Order().ToArray(), hot ? cluster.Where(other => other.Key != c.Key)
                            .Select(other => other.Key).ToArray() : [],
                        c.Modules.Intersect(facts.HotModules, StringComparer.Ordinal).Order().ToArray()));
                }
            }
            if (groups.Count > 1)
            {
                var apply = facts.LiveAuthority && cluster.All(Safe)
                    && actions.Where(a => a.Kind == "package" && a.Cards.Any(k => cluster.Any(c => c.Key == k))).All(a => a.Apply);
                var chainId = $"chain:{clusterNumber}";
                actions.Add(new(chainId, "chain", cluster.Select(c => c.Key).ToArray(), "overlapping-source-order", apply, [], []));
                for (var i = 1; i < groups.Count; i++) edges.Add(new(groups[i].Id, groups[i - 1].Id));
            }
            clusterNumber++;
        }
        return new(actions, edges);

        bool Split(DispatchRecutCard c) => c.Modules.Distinct().Count() > 3 || c.SizePrior > 25
            || c.FindingCount > 1 && c.Modules.Intersect(facts.HotModules, StringComparer.Ordinal).Any();
    }
}
