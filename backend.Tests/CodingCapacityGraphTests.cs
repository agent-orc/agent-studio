using System.Diagnostics;
using System.Numerics;
using AgentStudio.Operator;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

public sealed class CodingCapacityGraphTests(ITestOutputHelper output)
{
    [Fact]
    public void Width_matches_exhaustive_antichains_for_all_forward_edge_five_card_dags()
    {
        const int count = 5;
        for (var graph = 0; graph < 1 << (count * (count - 1) / 2); graph++)
        {
            var ancestors = new int[count];
            var cards = new List<TaskInfo>();
            var edge = 0;
            for (var i = 0; i < count; i++)
            {
                var parents = new List<TaskDependencyReference>();
                for (var j = 0; j < i; j++, edge++)
                    if ((graph & (1 << edge)) != 0)
                    {
                        parents.Add(new($"CARD-{j}"));
                        ancestors[i] |= (1 << j) | ancestors[j];
                    }
                cards.Add(new() { Id = $"CARD-{i}", Key = $"CARD-{i}", State = TaskStates.Ready,
                    References = new() { DependsOn = parents } });
            }
            var expected = Enumerable.Range(0, 1 << count)
                .Where(subset => Enumerable.Range(0, count).All(i =>
                    (subset & (1 << i)) == 0 || (ancestors[i] & subset) == 0))
                .Max(subset => BitOperations.PopCount((uint)subset));
            if ((graph & 1) != 0) cards.Reverse();
            var fact = CodingCapacityProjection.Project(null, cards, TaskReferenceIndex.Build(cards), 12, 0, _ => []);
            Assert.True(expected == fact.DependencyWidth,
                $"Graph {graph}: expected width {expected}, got {fact.DependencyWidth}");
        }
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void Three_long_chains_projection_measurement()
    {
        var cards = Enumerable.Range(0, 3).SelectMany(chain => Enumerable.Range(0, 120)
            .Select(i => new TaskInfo { Id = $"CHAIN-{chain}-{i}", Key = $"CHAIN-{chain}-{i}", State = TaskStates.Ready,
                References = new() { DependsOn = i == 0 ? [] : [new($"CHAIN-{chain}-{i - 1}")] } })).ToArray();
        var references = TaskReferenceIndex.Build(cards);
        WaitsOnStatus Waits(TaskInfo task) => new()
        {
            Blocked = task.References.DependsOn.Count > 0,
            Items = task.References.DependsOn.Select(edge => new WaitsOnItem { Key = edge.Key }).ToList(),
        };
        var timer = Stopwatch.StartNew();
        var fact = CodingCapacityProjection.Project(null, cards, references, 12, 0, _ => [], Waits);
        timer.Stop();
        output.WriteLine($"360 Ready cards in three chains: width={fact.DependencyWidth}; projection={timer.Elapsed.TotalMilliseconds:F2} ms");
        Assert.Equal(3, fact.DependencyWidth);
    }
}
