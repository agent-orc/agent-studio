using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3232: shard assignment is deterministic, balanced by recorded class
/// timings, and falls back to class-name hashing without them.
/// </summary>
public sealed class GateShardPlannerTests
{
    private static readonly string[] Classes = Enumerable.Range(1, 60).Select(i => $"Class{i:00}Tests").ToArray();

    private static GateShardSuiteTimings Timings(IEnumerable<string> classes, Func<int, double> durationMs, long serialWallMs = 0)
        => new(classes.Select((name, index) => (name, durationMs(index)))
            .ToDictionary(item => item.name, item => item.Item2, StringComparer.Ordinal), serialWallMs);

    [Fact]
    public void Same_classes_and_timings_give_the_same_plan_in_any_input_order()
    {
        var timings = Timings(Classes, index => (index * 7919 % 1000) + 10);
        var shuffled = Classes.Reverse().ToArray();

        var first = GateShardPlanner.Plan("backend", Classes, 4, GateShardCountSources.Profile, timings);
        var second = GateShardPlanner.Plan("backend", shuffled.Concat(Classes.Take(3)), 4, GateShardCountSources.Profile, timings);

        Assert.Equal(first.Digest, second.Digest);
        Assert.Equal(first.Shards.Select(shard => shard.Classes), second.Shards.Select(shard => shard.Classes));
    }

    [Fact]
    public void Every_class_lands_in_exactly_one_shard()
    {
        var plan = GateShardPlanner.Plan("backend", Classes, 5, GateShardCountSources.Profile,
            Timings(Classes, index => index + 1));

        var assigned = plan.Shards.SelectMany(shard => shard.Classes).ToArray();
        Assert.Equal(Classes.Length, assigned.Length);
        Assert.Equal(Classes.Order(StringComparer.Ordinal), assigned.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Recorded_timings_balance_the_shards_within_one_class_of_the_ideal()
    {
        // A skewed suite: a few host-booting classes dominate, like the backend suite.
        var timings = Timings(Classes, index => index < 4 ? 60_000 : 500 + index * 37 % 900);
        var plan = GateShardPlanner.Plan("backend", Classes, 4, GateShardCountSources.Profile, timings);

        var loads = plan.Shards.Select(shard => shard.Classes.Sum(name => timings.ClassDurationsMs[name])).ToArray();
        var ideal = loads.Sum() / 4;
        var largestClass = timings.ClassDurationsMs.Values.Max();
        Assert.Equal(GateShardStrategies.Timings, plan.Strategy);
        Assert.All(loads, load => Assert.InRange(load, ideal - largestClass, ideal + largestClass));
        Assert.True(loads.Max() / loads.Min() < 1.05, $"loads {string.Join(", ", loads)}");
        Assert.Equal(loads, plan.Shards.Select(shard => shard.EstimatedMs));
    }

    [Fact]
    public void Without_timings_the_plan_hashes_class_names_stably()
    {
        var plan = GateShardPlanner.Plan("backend", Classes, 3, GateShardCountSources.ExpectedDuration, timings: null);
        var again = GateShardPlanner.Plan("backend", Classes, 3, GateShardCountSources.ExpectedDuration,
            new GateShardSuiteTimings(new Dictionary<string, double>(), 0));

        Assert.Equal(GateShardStrategies.Hash, plan.Strategy);
        Assert.Equal(plan.Digest, again.Digest);
        Assert.All(plan.Shards, shard => Assert.All(shard.Classes,
            name => Assert.Equal(shard.Index, GateShardPlanner.HashIndex(name, 3))));
        Assert.All(plan.Shards, shard => Assert.NotEmpty(shard.Classes));
    }

    [Fact]
    public void Adding_an_untimed_class_keeps_the_hash_assignment_of_every_other_class()
    {
        var before = GateShardPlanner.Plan("backend", Classes, 4, GateShardCountSources.Default, null);
        var after = GateShardPlanner.Plan("backend", Classes.Append("BrandNewTests"), 4, GateShardCountSources.Default, null);

        foreach (var shard in before.Shards)
            Assert.All(shard.Classes, name => Assert.Contains(name, after.Shards[shard.Index].Classes));
    }

    [Fact]
    public void Classes_without_a_timing_are_estimated_at_the_median()
    {
        var timed = new Dictionary<string, double> { ["ATests"] = 100, ["BTests"] = 300, ["CTests"] = 900 };
        var plan = GateShardPlanner.Plan("backend", ["ATests", "BTests", "CTests", "NewTests"], 2,
            GateShardCountSources.Profile, new GateShardSuiteTimings(timed, 0));

        Assert.Equal(GateShardStrategies.TimingsWithMedian, plan.Strategy);
        // CTests (900) alone; BTests (300) + NewTests (median 300) + ATests (100) on the other.
        Assert.Equal(["CTests"], plan.Shards[0].Classes);
        Assert.Equal(["ATests", "BTests", "NewTests"], plan.Shards[1].Classes);
        Assert.Equal(700, plan.Shards[1].EstimatedMs);
    }

    [Fact]
    public void A_different_timing_snapshot_changes_the_digest()
    {
        var a = GateShardPlanner.Plan("backend", Classes, 4, GateShardCountSources.Profile, Timings(Classes, index => index + 1));
        var b = GateShardPlanner.Plan("backend", Classes, 4, GateShardCountSources.Profile, Timings(Classes, index => 100 - index));

        Assert.NotEqual(a.Digest, b.Digest);
    }

    [Theory]
    [InlineData(5, 0L, 1440, 60, 5, GateShardCountSources.Profile)]
    [InlineData(null, 1_500_000L, 1440, 60, 5, GateShardCountSources.MeasuredDuration)]
    [InlineData(null, 0L, 1440, 60, 4, GateShardCountSources.ExpectedDuration)]
    [InlineData(null, 0L, 90, 60, 1, GateShardCountSources.ExpectedDuration)]
    [InlineData(null, 0L, 0, 60, 1, GateShardCountSources.Default)]
    [InlineData(null, 0L, 86_400, 60, GateShardPlanner.MaxDerivedShards, GateShardCountSources.ExpectedDuration)]
    [InlineData(12, 0L, 1440, 3, 3, GateShardCountSources.Profile)]
    public void Shard_count_comes_from_the_profile_then_the_measured_then_the_expected_duration(
        int? configured, long serialWallMs, int expectedSeconds, int classCount, int count, string source)
    {
        var timings = serialWallMs > 0 ? new GateShardSuiteTimings(new Dictionary<string, double>(), serialWallMs) : null;

        Assert.Equal((count, source), GateShardPlanner.ResolveCount(configured, timings, expectedSeconds, classCount));
    }

    [Theory]
    [InlineData(4, 3, 24, 6, 3)]
    [InlineData(4, 3, 12, 6, 2)]
    [InlineData(2, 3, 24, 6, 2)]
    [InlineData(4, 1, 24, 6, 1)]
    [InlineData(4, 3, 24, 0, 3)]
    [InlineData(4, 3, 4, 6, 1)]
    public void Lanes_stay_within_shards_slots_and_cores(int runnable, int slots, int cores, int processors, int lanes)
        => Assert.Equal(lanes, GateShardPlanner.Lanes(runnable, slots, cores, processors));

    [Fact]
    public void The_last_shard_is_the_complement_and_always_runs()
    {
        var plan = GateShardPlanner.Plan("backend", ["OnlyTests"], 3, GateShardCountSources.Profile, null);

        Assert.Equal(2, plan.ComplementIndex);
        Assert.True(plan.Runs(plan.Shards[plan.ComplementIndex]));
        Assert.Equal(plan.Shards.Where(shard => shard.Index != 2).SelectMany(shard => shard.Classes),
            plan.ClassesOutsideComplement);
        Assert.Equal(1, plan.Shards.Count(shard => shard.Classes.Count > 0));
    }
}
