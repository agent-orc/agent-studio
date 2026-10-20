using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3232: the recorded per-class timings the dispatcher hands to the next
/// gate run of a project, one small JSON file per project. Only a suite whose
/// shards all ran replaces its entry, so a red or interrupted run never leaves
/// a partial class map behind. A missing or unreadable file plans by hashing.
/// </summary>
public sealed class GateShardTimingStore
{
    private sealed record StoredTimings(
        DateTimeOffset UpdatedAtUtc,
        Dictionary<string, GateShardSuiteTimings> Suites);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private readonly string _root;
    private readonly TimeProvider _clock;

    public GateShardTimingStore(TimeProvider? clock = null)
        : this(Path.Combine(Path.GetTempPath(), "agentstudio-gate-shard-timings"), clock)
    {
    }

    internal GateShardTimingStore(string root, TimeProvider? clock = null)
    {
        _root = root;
        _clock = clock ?? TimeProvider.System;
    }

    public GateShardTimingSnapshot Read(string projectKey)
    {
        lock (_sync)
        {
            var stored = Load(PathFor(projectKey));
            return stored is null
                ? GateShardTimingSnapshot.Empty
                : new GateShardTimingSnapshot(stored.Suites);
        }
    }

    /// <summary>Records every complete suite of <paramref name="suites"/>; returns how many it stored.</summary>
    public int Record(string projectKey, IReadOnlyList<GateShardSuiteEvidence> suites)
    {
        var complete = suites.Where(IsComplete).ToArray();
        if (complete.Length == 0) return 0;
        lock (_sync)
        {
            var path = PathFor(projectKey);
            var merged = new Dictionary<string, GateShardSuiteTimings>(
                Load(path)?.Suites ?? [], StringComparer.Ordinal);
            foreach (var suite in complete)
                merged[suite.SuiteId] = new GateShardSuiteTimings(
                    new Dictionary<string, double>(suite.ClassDurationsMs, StringComparer.Ordinal), suite.SerialWallMs);
            var snapshot = new GateShardTimingSnapshot(merged);
            if (!snapshot.IsWithinBounds) return 0;
            Directory.CreateDirectory(_root);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new StoredTimings(_clock.GetUtcNow(), merged), Json));
            File.Move(temporary, path, overwrite: true);
            return complete.Length;
        }
    }

    /// <summary>
    /// A suite feeds the next plan only when every shard ran to a test verdict
    /// and the run reported class timings.
    /// </summary>
    internal static bool IsComplete(GateShardSuiteEvidence suite)
        => suite.ClassDurationsMs.Count > 0
           && suite.Shards.Count == suite.ShardCount
           && suite.Shards.All(shard => shard.Outcome is GateShardOutcomes.Passed or GateShardOutcomes.FlakyPassed
               or GateShardOutcomes.QuarantinePassed or GateShardOutcomes.Empty);

    private string PathFor(string projectKey)
        => Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectKey)))
            .ToLowerInvariant()[..24] + ".json");

    private static StoredTimings? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var stored = JsonSerializer.Deserialize<StoredTimings>(File.ReadAllText(path), Json);
            return stored?.Suites is null ? null : stored with
            {
                Suites = new Dictionary<string, GateShardSuiteTimings>(stored.Suites, StringComparer.Ordinal),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SilentCatch.Note(ex, "GateShardTimingStore: unreadable timings; the next plan uses class-name hashing");
            return null;
        }
    }
}
