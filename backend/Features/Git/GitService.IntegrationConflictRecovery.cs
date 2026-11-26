namespace AgentStudio.Git;

public sealed record IntegrationConflictContext(string BaseSha, IReadOnlyList<string> Cards, string Changes);

public partial class GitService
{
    public bool TargetTouchedPaths(string repoRoot, string oldTip, string? tip, IReadOnlyList<string> paths)
    {
        if (string.IsNullOrWhiteSpace(tip) || paths.Count == 0) return false;
        var (log, _, code) = RunGitArgs(repoRoot, ["log", "-1", "--format=%H", $"{oldTip}..{tip}", "--", .. paths]);
        if (code == 0 && !string.IsNullOrWhiteSpace(log)) return true;
        var (_, _, diffCode) = RunGitArgs(repoRoot, ["diff", "--quiet", oldTip, tip, "--", .. paths]);
        return diffCode == 1;
    }

    public IReadOnlyList<string>? DeliveryChangedPaths(string repoRoot, string deliverySha, string target)
    {
        var (baseSha, _, baseCode) = RunGitArgs(repoRoot, "merge-base", deliverySha, IntegrationBaseRef(repoRoot, target));
        if (baseCode != 0 || string.IsNullOrWhiteSpace(baseSha)) return null;
        var (paths, _, code) = RunGitArgs(repoRoot, "diff", "--name-only", "-z", baseSha.Trim(), deliverySha, "--");
        return code == 0 ? paths.Split('\0', StringSplitOptions.RemoveEmptyEntries) : null;
    }

    /// <summary>The delivery's leased base is the attribution boundary, including earlier sync merges.</summary>
    public IntegrationConflictContext ConflictChanges(string repoRoot, string deliverySha, string? deliveryBase,
        string tip, IReadOnlyList<string> paths, string ownKey, string? project = null)
    {
        var baseSha = deliveryBase;
        if (string.IsNullOrWhiteSpace(baseSha))
        {
            var (mergeBase, _, baseCode) = RunGitArgs(repoRoot, "merge-base", deliverySha, tip);
            if (baseCode != 0) return new("unavailable", [], "Target changes could not be read.");
            baseSha = mergeBase.Trim();
        }
        var (log, _, code) = RunGitArgs(repoRoot,
            ["log", "--full-history", "--format=%H %s", "--name-status", $"{baseSha}..{tip}", "--", .. paths]);
        if (code != 0) return new(baseSha, [], "Target changes could not be read.");
        var attribution = project is null ? null : ReadDispatchConflictAttribution();
        var cards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attributedLog = new List<string>();
        foreach (var line in log.Split('\n'))
        {
            var separator = line.IndexOf(' ');
            if (separator < 0 || !ReviewSubjectStore.IsValidResultSha(line[..separator]))
            {
                attributedLog.Add(line);
                continue;
            }
            var sha = line[..separator];
            var owners = attribution?.Owners(project!, sha);
            if (owners is null && attribution is not null && NetChangeSource(repoRoot, sha) is { } source)
                owners = attribution.Owners(project!, source);
            owners ??= PrimaryConflictCard(line[(separator + 1)..]) is { } primary ? [primary] : [];
            foreach (var owner in owners)
                if (!owner.Equals(ownKey, StringComparison.OrdinalIgnoreCase)) cards.Add(owner);
            attributedLog.Add(line + (owners.Count == 0 ? " [card not recorded]" : $" [cards: {string.Join(", ", owners)}]"));
        }
        var patchBase = baseSha;
        if (!IsAncestor(repoRoot, baseSha, tip))
        {
            var (common, _, commonCode) = RunGitArgs(repoRoot, "merge-base", baseSha, tip);
            if (commonCode == 0 && !string.IsNullOrWhiteSpace(common)) patchBase = common.Trim();
        }
        var (diff, _, diffCode) = RunGitArgs(repoRoot,
            ["diff", "--no-ext-diff", "--no-renames", "--unified=3", patchBase, tip, "--", .. paths]);
        var changes = "Target commits and changed paths:\n" + string.Join("\n", attributedLog).TrimEnd()
            + $"\nTarget patch from common ancestor {patchBase}:\n" + (diffCode == 0 ? diff.TrimEnd() : "unavailable");
        const int limit = 48_000;
        if (changes.Length > limit) changes = changes[..limit]
            + $"\n[Context truncated at {limit} characters; inspect git log {baseSha}..{tip} and git diff {patchBase} {tip} on the listed files.]";
        return new(baseSha, cards.Order(StringComparer.Ordinal).ToArray(), changes);
    }
}
