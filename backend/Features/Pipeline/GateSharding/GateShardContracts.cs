using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3232: the share of one gate run a host executes when the dispatcher
/// splits the shards of a suite across gate hosts. Host part
/// <see cref="Index"/> of <see cref="Count"/> runs the shards whose index
/// modulo <see cref="Count"/> equals <see cref="Index"/>. Part 0 is the
/// primary host: it also runs every command that is not sharded. A request
/// without a part is the whole gate on one host.
/// </summary>
public sealed record GateShardPart(int Index, int Count)
{
    /// <summary>Upper bound for the number of hosts one gate run spans.</summary>
    public const int MaxHosts = 8;

    public bool IsPrimary => Index == 0;

    public bool Owns(int shardIndex) => shardIndex % Count == Index;

    public bool IsValid => Count is >= 1 and <= MaxHosts && Index >= 0 && Index < Count;

    public override string ToString() => $"{Index + 1}/{Count}";
}

/// <summary>
/// Recorded timings of one sharded suite, keyed by the namespace-qualified test class name.
/// <see cref="SerialWallMs"/> is the sum of the shard process wall times of the
/// run that produced the snapshot, i.e. what the suite costs in one process.
/// </summary>
public sealed record GateShardSuiteTimings(
    IReadOnlyDictionary<string, double> ClassDurationsMs,
    long SerialWallMs);

/// <summary>
/// The timing input of a gate run, keyed by suite id. The dispatcher passes
/// one snapshot to every host of a run so that all hosts compute the same plan.
/// </summary>
public sealed record GateShardTimingSnapshot(
    IReadOnlyDictionary<string, GateShardSuiteTimings> Suites)
{
    /// <summary>Bounds a snapshot must stay within to cross the gate transport.</summary>
    public const int MaxSuites = 16;
    public const int MaxClassesPerSuite = 8192;

    /// <summary>
    /// Serialized size limit. The remote worker refuses a request file over
    /// 2 MiB, and the snapshot shares that file with the rest of the request.
    /// </summary>
    public const int MaxSerializedBytes = 1024 * 1024;

    public static GateShardTimingSnapshot Empty { get; } =
        new(new Dictionary<string, GateShardSuiteTimings>(StringComparer.Ordinal));

    public GateShardSuiteTimings? For(string suiteId)
        => Suites.TryGetValue(suiteId, out var timings) ? timings : null;

    /// <summary>Computed on demand; never part of the serialized snapshot it measures.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsWithinBounds => Suites.Count <= MaxSuites && Suites.All(suite =>
        suite.Value is not null
        && suite.Value.SerialWallMs >= 0
        && suite.Value.ClassDurationsMs.Count <= MaxClassesPerSuite
        && suite.Value.ClassDurationsMs.Values.All(value => double.IsFinite(value) && value >= 0))
        && JsonSerializer.SerializeToUtf8Bytes(this).Length <= MaxSerializedBytes;

    /// <summary>
    /// A stable digest of the snapshot. Hosts that plan from the same snapshot
    /// compute the same shard plans, so a cached host part is only reused for
    /// a run that hands out the same snapshot.
    /// </summary>
    public string Digest()
    {
        var payload = new StringBuilder();
        foreach (var (suiteId, timings) in Suites.OrderBy(suite => suite.Key, StringComparer.Ordinal))
        {
            payload.Append(suiteId).Append('|').Append(timings.SerialWallMs.ToString(CultureInfo.InvariantCulture));
            foreach (var (name, ms) in timings.ClassDurationsMs.OrderBy(item => item.Key, StringComparer.Ordinal))
                payload.Append('|').Append(name).Append('=').Append(ms.ToString("R", CultureInfo.InvariantCulture));
            payload.Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())))[..16];
    }
}

/// <summary>Test counts read from one test process's TRX report.</summary>
public sealed record GateTestCounts(int Total, int Passed, int Failed, int Skipped)
{
    public static GateTestCounts None { get; } = new(0, 0, 0, 0);

    public GateTestCounts Add(GateTestCounts other)
        => new(Total + other.Total, Passed + other.Passed, Failed + other.Failed, Skipped + other.Skipped);
}

/// <summary>Outcome tokens of one shard in the gate evidence.</summary>
public static class GateShardOutcomes
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    /// <summary>Red in the shard run, green on the targeted re-run; recorded as flaky.</summary>
    public const string FlakyPassed = "flaky-passed";
    /// <summary>Only actively quarantined tests failed.</summary>
    public const string QuarantinePassed = "quarantine-passed";
    /// <summary>Timeout, cancellation, launch failure or toolchain crash; not a product verdict.</summary>
    public const string Infrastructure = "infrastructure";
    /// <summary>The planner assigned no class to this shard; no process ran.</summary>
    public const string Empty = "empty";

    /// <summary>Not started because another shard of the suite was already red.</summary>
    public const string NotRun = "not-run";
}

/// <summary>The one targeted re-run of a red shard, recorded next to the first outcome.</summary>
public sealed record GateShardRerunEvidence(
    string Reason,
    IReadOnlyList<string> Tests,
    bool Performed,
    string? Outcome,
    int? ExitCode,
    long DurationMs);

/// <summary>One shard of one suite, as executed on one host.</summary>
public sealed record GateShardEvidence(
    int Index,
    string Host,
    int Lane,
    int Classes,
    double EstimatedMs,
    long DurationMs,
    int? ExitCode,
    string Outcome,
    GateTestCounts Tests,
    IReadOnlyList<string> FailedTests)
{
    public BuildTestGateFailureKind FailureKind { get; init; } = BuildTestGateFailureKind.None;
    public GateShardRerunEvidence? Rerun { get; init; }

    /// <summary>A <see cref="GateShardDetails"/> value for an infrastructure shard; null otherwise.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// The sharded execution of one suite command in one gate run. A run that
/// spans hosts carries one entry per host; <see cref="GateShardVerdictPolicy"/>
/// merges them into one entry with every shard.
/// </summary>
public sealed record GateShardSuiteEvidence(
    string SuiteId,
    string Command,
    int ShardCount,
    string CountSource,
    string Strategy,
    string PlanDigest,
    int Lanes,
    long WallMs,
    IReadOnlyList<GateShardEvidence> Shards)
{
    /// <summary>Host part that produced this entry; null for a whole-gate run.</summary>
    public GateShardPart? Part { get; init; }

    /// <summary>Hosts that ran shards of this suite.</summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];

    public GateTestCounts Tests => Shards.Aggregate(GateTestCounts.None, (sum, shard) => sum.Add(shard.Tests));

    /// <summary>
    /// Measured per-class durations for the next plan. Carried over the gate
    /// transport so the dispatcher can record them; not part of the
    /// human-readable evidence.
    /// </summary>
    public IReadOnlyDictionary<string, double> ClassDurationsMs { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>Sum of the shard process wall times.</summary>
    public long SerialWallMs { get; init; }
}

/// <summary>Shard request inputs carried on <see cref="BuildTestGateRequest"/>.</summary>
public sealed record GateShardRequest
{
    public GateShardPart? Part { get; init; }

    /// <summary>Null: the executing host plans with class-name hashing and the expected durations.</summary>
    public GateShardTimingSnapshot? Timings { get; init; }
}
