using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>The classes one shard owns and the planner's estimate for them.</summary>
public sealed record GateShardAssignment(int Index, IReadOnlyList<string> Classes, double EstimatedMs);

/// <summary>
/// A deterministic split of one suite into shards. Every host of a gate run
/// computes the same plan from the same class list and timing snapshot, and
/// reports <see cref="Digest"/> so the dispatcher can prove it.
/// </summary>
public sealed record GateShardPlan(
    string SuiteId,
    int Count,
    string CountSource,
    string Strategy,
    IReadOnlyList<GateShardAssignment> Shards,
    string Digest)
{
    /// <summary>
    /// The last shard runs the complement of every other shard's classes, so a
    /// test class the source scan did not find still runs exactly once.
    /// </summary>
    public int ComplementIndex => Count - 1;

    /// <summary>Classes the complement shard excludes: those of every other shard.</summary>
    public IReadOnlyList<string> ClassesOutsideComplement => Shards
        .Where(shard => shard.Index != ComplementIndex)
        .SelectMany(shard => shard.Classes)
        .ToArray();

    /// <summary>A shard without classes has nothing to run, except the complement shard.</summary>
    public bool Runs(GateShardAssignment shard)
        => shard.Index == ComplementIndex || shard.Classes.Count > 0;
}

/// <summary>Where a plan's shard count came from.</summary>
public static class GateShardCountSources
{
    public const string Profile = "profile";
    public const string MeasuredDuration = "measured-duration";
    public const string ExpectedDuration = "expected-duration";
    public const string Default = "default";
}

/// <summary>How a plan assigned classes to shards.</summary>
public static class GateShardStrategies
{
    /// <summary>Longest-processing-time-first over recorded per-class timings.</summary>
    public const string Timings = "timings";

    /// <summary>Recorded timings, with the median timing for classes that have none yet.</summary>
    public const string TimingsWithMedian = "timings+median";

    /// <summary>No timing recorded for the suite: stable class-name hashing.</summary>
    public const string Hash = "hash";
}

/// <summary>
/// AGT-3232: pure shard planning for a .NET test suite in the gate.
///
/// <para>
/// On 17.-19.10.2026 the full backend suite took 17 to 36 minutes per gate run
/// on two gate slots and 16 reviewed cards were parked because the gate queue
/// exceeded its budget. Splitting the suite into balanced class partitions that
/// run in parallel processes, on one host or across gate hosts, cuts the wall
/// time of one gate run.
/// </para>
///
/// <para>
/// The count comes from the suite's <c>shards</c> field in
/// <c>.agent-studio/project.yml</c>; without it, from the measured serial
/// duration of the last sharded run (or the suite's
/// <c>expectedDurationSeconds</c>) at <see cref="TargetShardSeconds"/> per
/// shard. Classes are balanced by their recorded durations (longest first onto
/// the least loaded shard); without any recorded timing they are spread by a
/// stable hash of the class name.
/// </para>
/// </summary>
public static class GateShardPlanner
{
    /// <summary>Wall time one shard should take when the count is derived.</summary>
    public const int TargetShardSeconds = 360;

    /// <summary>Upper bound for a derived count; a profile may set up to <see cref="ProjectTestSuite.MaxShards"/>.</summary>
    public const int MaxDerivedShards = 8;

    public static (int Count, string Source) ResolveCount(
        int? configured,
        GateShardSuiteTimings? timings,
        int expectedDurationSeconds,
        int classCount)
    {
        var (count, source) = configured is { } fixedCount
            ? (Math.Clamp(fixedCount, 1, ProjectTestSuite.MaxShards), GateShardCountSources.Profile)
            : timings is { SerialWallMs: > 0 }
                ? (Derived(timings.SerialWallMs / 1000d), GateShardCountSources.MeasuredDuration)
                : expectedDurationSeconds > 0
                    ? (Derived(expectedDurationSeconds), GateShardCountSources.ExpectedDuration)
                    : (1, GateShardCountSources.Default);
        return (Math.Min(count, Math.Max(1, classCount)), source);
    }

    private static int Derived(double seconds)
        => Math.Clamp((int)Math.Ceiling(seconds / TargetShardSeconds), 1, MaxDerivedShards);

    public static GateShardPlan Plan(
        string suiteId,
        IEnumerable<string> classes,
        int count,
        string countSource,
        GateShardSuiteTimings? timings)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var names = classes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var known = timings?.ClassDurationsMs
            .Where(item => double.IsFinite(item.Value) && item.Value > 0)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, double>(StringComparer.Ordinal);
        var timed = names.Where(known.ContainsKey).ToArray();

        var buckets = Enumerable.Range(0, count).Select(_ => new List<string>()).ToArray();
        var loads = new double[count];
        string strategy;
        if (timed.Length == 0)
        {
            strategy = GateShardStrategies.Hash;
            foreach (var name in names) buckets[HashIndex(name, count)].Add(name);
        }
        else
        {
            var median = Median(timed.Select(name => known[name]));
            strategy = timed.Length == names.Length
                ? GateShardStrategies.Timings
                : GateShardStrategies.TimingsWithMedian;
            var ordered = names
                .Select(name => (Name: name, Estimate: known.TryGetValue(name, out var ms) ? ms : median))
                .OrderByDescending(item => item.Estimate)
                .ThenBy(item => item.Name, StringComparer.Ordinal);
            foreach (var (name, estimate) in ordered)
            {
                var target = 0;
                for (var index = 1; index < count; index++)
                    if (loads[index] < loads[target]) target = index;
                buckets[target].Add(name);
                loads[target] += estimate;
            }
        }

        var shards = buckets
            .Select((bucket, index) => new GateShardAssignment(
                index, bucket.Order(StringComparer.Ordinal).ToArray(), loads[index]))
            .ToArray();
        return new GateShardPlan(suiteId, count, countSource, strategy, shards, Digest(suiteId, strategy, shards));
    }

    /// <summary>
    /// Lanes one host runs in parallel: never more than its runnable shards,
    /// one per gate slot it holds (its own plus every free one it could take),
    /// and no more processes than the online cores carry at the configured
    /// processors per test process. <paramref name="processorsPerShard"/> of 0
    /// means no per-process processor budget is configured.
    /// </summary>
    public static int Lanes(int runnableShards, int heldSlots, int hostCores, int processorsPerShard)
    {
        var lanes = Math.Min(runnableShards, heldSlots);
        if (processorsPerShard > 0 && hostCores > 0)
            lanes = Math.Min(lanes, hostCores / processorsPerShard);
        return Math.Max(1, lanes);
    }

    internal static int HashIndex(string name, int count)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return (int)(BinaryPrimitives.ReadUInt64BigEndian(hash) % (ulong)count);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static string Digest(string suiteId, string strategy, IReadOnlyList<GateShardAssignment> shards)
    {
        var payload = new StringBuilder()
            .Append(suiteId).Append('|').Append(strategy).Append('|').Append(shards.Count);
        foreach (var shard in shards)
            payload.Append('|').Append(shard.Index).Append(':').AppendJoin(',', shard.Classes);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())))
            .ToLowerInvariant()[..16];
    }
}
