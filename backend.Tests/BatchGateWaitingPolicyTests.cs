using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateWaitingPolicyTests
{
    // clock-independent: fixed dates only order manifests in the pure policy.
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

    [Fact]
    public void Older_formed_batch_starts_before_a_newer_larger_one()
    {
        var at = new DateTimeOffset(2026, 11, 4, 1, 0, 0, TimeSpan.Zero);
        BatchGateManifest Manifest(string id, int size, DateTimeOffset closed)
            => new(id, null!, "", "", closed,
                Enumerable.Repeat<BatchGateSubject>(null!, size).ToArray(), [], []);
        var older = Manifest("older", 2, at);
        var larger = Manifest("larger", 5, at.AddMinutes(10));

        Assert.Equal(["older", "larger"], BatchGateWaitingPolicy
            .OrderForStart([older, larger]).Select(manifest => manifest.BatchId));
    }

    [Fact]
    public void Stacked_successor_starts_before_an_ordinary_formed_batch()
    {
        var at = new DateTimeOffset(2026, 11, 4, 1, 0, 0, TimeSpan.Zero);
        BatchGateManifest Manifest(string id, int size, string? predecessor = null)
            => new(id, null!, "", "", at,
                Enumerable.Repeat<BatchGateSubject>(null!, size).ToArray(), [], [],
                StackedOnBatchId: predecessor);
        var ordinary = Manifest("ordinary", 5);
        var stacked = Manifest("stacked", 2, "predecessor");

        Assert.Equal(["stacked", "ordinary"], BatchGateWaitingPolicy
            .OrderForStart([ordinary, stacked]).Select(manifest => manifest.BatchId));
    }
}
