using System.Collections.Concurrent;

namespace AgentStudio.Git;

/// <summary>
/// Answers verification ancestry against historical trees below one published
/// tip. A single rev-list read replaces one merge-base process per card and
/// the completed graph remains usable when an index run later times out.
/// </summary>
internal sealed class GitAncestryGraph
{
    private readonly IReadOnlyDictionary<string, string[]> _parents;
    private readonly IReadOnlyDictionary<string, int> _generation;
    private readonly ConcurrentDictionary<(string Ancestor, string Tree), bool> _answers = new();
    private readonly ConcurrentDictionary<string, int> _treeRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HashSet<string>> _treeAncestors =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _publications = new(StringComparer.OrdinalIgnoreCase);

    private GitAncestryGraph(string tip, IReadOnlyList<(string Sha, string[] Parents)> rows)
    {
        _parents = rows.ToDictionary(row => row.Sha, row => row.Parents, StringComparer.OrdinalIgnoreCase);
        // rev-list --topo-order emits children before parents. Reverse that
        // order to assign a monotonic generation, which prunes old history
        // before a pairwise walk reaches it.
        var generations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sha, ancestors) in rows.Reverse())
            generations[sha] = ancestors.Length == 0 ? 1
                : 1 + ancestors.Max(parent => generations.GetValueOrDefault(parent));
        _generation = generations;
        var commit = tip;
        while (_parents.TryGetValue(commit, out var parents) && _publications.Add(commit) && parents.Length > 0)
            commit = parents[0];
    }

    internal static GitAncestryGraph Read(string root, string tip)
        => new(tip, GitFactCommands.RevListParents(root, tip));

    internal bool IsPublication(string sha) => _publications.Contains(sha);

    internal string? SingleParent(string sha)
        => _parents.TryGetValue(sha, out var parents) && parents.Length == 1 ? parents[0] : null;

    internal bool IsAncestor(string ancestor, string tree)
    {
        GitProcessBudget.Token.ThrowIfCancellationRequested();
        ancestor = Resolve(ancestor);
        tree = Resolve(tree);
        if (!_parents.ContainsKey(ancestor) || !_parents.ContainsKey(tree)) return false;
        if (string.Equals(ancestor, tree, StringComparison.OrdinalIgnoreCase)) return true;
        var targetGeneration = _generation[ancestor];
        if (_generation[tree] < targetGeneration) return false;
        if (_treeAncestors.TryGetValue(tree, out var known)) return known.Contains(ancestor);
        if (_treeRequests.AddOrUpdate(tree, 1, (_, count) => count + 1) >= 2
            && _treeAncestors.Count < 4)
            return _treeAncestors.GetOrAdd(tree, BuildAncestorSet).Contains(ancestor);
        return _answers.GetOrAdd((ancestor, tree), pair =>
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(pair.Tree);
            while (pending.TryPop(out var sha))
            {
                if (!seen.Add(sha)) continue;
                if (string.Equals(sha, pair.Ancestor, StringComparison.OrdinalIgnoreCase)) return true;
                if (_parents.TryGetValue(sha, out var parents))
                    foreach (var parent in parents)
                        if (_generation[parent] >= targetGeneration) pending.Push(parent);
            }
            return false;
        });
    }

    private HashSet<string> BuildAncestorSet(string tree)
    {
        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(tree);
        while (pending.TryPop(out var sha))
        {
            GitProcessBudget.Token.ThrowIfCancellationRequested();
            if (!ancestors.Add(sha)) continue;
            if (_parents.TryGetValue(sha, out var parents))
                foreach (var parent in parents) pending.Push(parent);
        }
        return ancestors;
    }

    private string Resolve(string sha)
    {
        if (_parents.ContainsKey(sha)) return sha;
        if (sha.Length < 4 || sha.Length >= 64) return sha;
        string? match = null;
        foreach (var candidate in _parents.Keys)
        {
            if (!candidate.StartsWith(sha, StringComparison.OrdinalIgnoreCase)) continue;
            if (match is not null) return sha;
            match = candidate;
        }
        return match ?? sha;
    }
}

/// <summary>Retains completed protected-tip graphs across index attempts.</summary>
internal sealed class GitAncestryGraphCache
{
    private readonly ConcurrentDictionary<string, RootCache> _roots =
        new(FileSystemPathComparer.Instance);

    internal GitAncestryGraph Get(string root, string tip)
    {
        GitProcessBudget.Token.ThrowIfCancellationRequested();
        var cache = _roots.GetOrAdd(root, _ => new RootCache());
        Lazy<GitAncestryGraph> graph;
        lock (cache.Gate)
        {
            if (cache.Graphs.TryGetValue(tip, out var existing))
            {
                cache.Order.Remove(existing.Node);
                cache.Order.AddLast(existing.Node);
                graph = existing.Graph;
            }
            else
            {
                graph = new Lazy<GitAncestryGraph>(() => GitAncestryGraph.Read(root, tip),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                var node = cache.Order.AddLast(tip);
                cache.Graphs[tip] = (graph, node);
                if (cache.Graphs.Count > 2)
                {
                    var oldest = cache.Order.First!;
                    cache.Order.RemoveFirst();
                    cache.Graphs.Remove(oldest.Value);
                }
            }
        }
        try { return graph.Value; }
        catch
        {
            // A cancelled or failed read must be retryable. Completed graphs
            // remain cached even when later index work reaches its deadline.
            lock (cache.Gate)
            {
                if (cache.Graphs.TryGetValue(tip, out var current)
                    && ReferenceEquals(current.Graph, graph))
                {
                    cache.Order.Remove(current.Node);
                    cache.Graphs.Remove(tip);
                }
            }
            throw;
        }
    }

    private sealed class RootCache
    {
        internal readonly object Gate = new();
        internal readonly Dictionary<string, (Lazy<GitAncestryGraph> Graph, LinkedListNode<string> Node)> Graphs =
            new(StringComparer.OrdinalIgnoreCase);
        internal readonly LinkedList<string> Order = new();
    }
}
