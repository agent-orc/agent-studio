using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateWaitingPolicyTests
{
    [Fact]
    public void Chooses_the_largest_compatible_set_even_when_the_first_card_blocks_two()
    {
        BatchGateWaitingPolicy.Card[] cards =
        [
            new("A", ["one.cs", "two.cs"]),
            new("B", ["one.cs"]),
            new("C", ["two.cs"]),
        ];

        var selected = BatchGateWaitingPolicy.LargestCompatible(cards, 8, "separate");

        Assert.Equal(["B", "C"], selected.Select(card => card.TaskKey));
        Assert.Equal(["one.cs"], BatchGateWaitingPolicy.ConflictingFiles(cards[0], cards[1]));
    }

    [Fact]
    public void Adjacent_overlap_allows_shared_files_and_maximum_size_still_applies()
    {
        var cards = Enumerable.Range(0, 3)
            .Select(index => new BatchGateWaitingPolicy.Card($"C{index}", ["same.cs"]))
            .ToArray();

        Assert.Equal(["C0", "C1"], BatchGateWaitingPolicy
            .LargestCompatible(cards, 2, "adjacent").Select(card => card.TaskKey));
    }
}
