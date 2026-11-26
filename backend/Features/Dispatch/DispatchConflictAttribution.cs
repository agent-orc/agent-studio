using System.Collections.Frozen;

namespace AgentStudio.Dispatch;

/// <summary>Immutable ownership facts shared by conflict records from one task publication.</summary>
internal sealed class DispatchConflictAttribution
{
    private readonly FrozenDictionary<string, ProjectFacts> _projects;
    private sealed record ProjectFacts(FrozenDictionary<string, string> Cards,
        FrozenDictionary<string, string[]> Commits);

    private DispatchConflictAttribution(FrozenDictionary<string, ProjectFacts> projects) => _projects = projects;

    public string Card(string project, string identity)
        => _projects.TryGetValue(project, out var facts) && facts.Cards.TryGetValue(identity, out var card)
            ? card : identity;

    public IReadOnlyList<string>? Owners(string project, string sha)
        => _projects.TryGetValue(project, out var facts) && facts.Commits.TryGetValue(sha, out var owners)
            ? owners : null;

    public static DispatchConflictAttribution Build(IEnumerable<TaskInfo> cards)
        => new(cards.GroupBy(card => card.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(group => group.Key, group =>
            {
                var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var commits = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var card in group)
                {
                    var owner = card.Key ?? card.Id;
                    foreach (var identity in new[] { card.TaskKey, card.Id, card.Key }.OfType<string>())
                        identities.TryAdd(identity, owner);
                    var shas = card.Commits.SelectMany(commit => new[] { commit.Sha, commit.SupersededBySha })
                        .Append(card.Provenance?.Merge?.MergeCommit).OfType<string>();
                    foreach (var sha in shas)
                    {
                        if (!commits.TryGetValue(sha, out var owners))
                            commits[sha] = owners = new(StringComparer.OrdinalIgnoreCase);
                        owners.Add(owner);
                    }
                }
                return new ProjectFacts(identities.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
                    commits.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
            }, StringComparer.OrdinalIgnoreCase));
}
