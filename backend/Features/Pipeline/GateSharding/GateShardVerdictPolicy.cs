namespace AgentStudio.Pipeline;

/// <summary>One host's share of a gate run that the dispatcher split across hosts.</summary>
public sealed record GateShardHostResult(string Host, GateShardPart Part, BuildTestGateResult Result);

/// <summary>
/// AGT-3232: one verdict per gate run, however many shards and hosts ran it.
/// Pure: it reads shard and host outcomes and nothing else.
/// <list type="bullet">
///   <item>Every shard must pass. A shard is green when its run passed, when
///   only actively quarantined tests failed, or when its one targeted re-run
///   passed (the names stay recorded as flaky).</item>
///   <item>A red test anywhere is a code failure, even when another shard or
///   host failed for infrastructure reasons: the red is evidence about the
///   subject.</item>
///   <item>Without a red test, any infrastructure failure (timeout,
///   cancellation, launch, toolchain, transport, incomplete shard evidence)
///   makes the run an infrastructure verdict, never a code failure, even when
///   every other shard passed.</item>
/// </list>
/// </summary>
public static class GateShardVerdictPolicy
{
    /// <summary>The failure kind of one sharded suite step on one host.</summary>
    public static BuildTestGateFailureKind MergeShards(IEnumerable<GateShardEvidence> shards)
    {
        var ordered = shards.OrderBy(shard => shard.Index).ToArray();
        if (ordered.Any(shard => shard.Outcome == GateShardOutcomes.Failed))
            return BuildTestGateFailureKind.Code;
        var infrastructure = ordered.FirstOrDefault(shard => shard.Outcome == GateShardOutcomes.Infrastructure);
        if (infrastructure is null) return BuildTestGateFailureKind.None;
        return infrastructure.FailureKind is BuildTestGateFailureKind.None or BuildTestGateFailureKind.Code
            ? BuildTestGateFailureKind.Environment
            : infrastructure.FailureKind;
    }

    /// <summary>
    /// Merges the host shares of one gate run. Part 0 is the primary: it ran
    /// every unsharded command, so its selection audit and diagnosis lead.
    /// </summary>
    public static BuildTestGateResult MergeHosts(IReadOnlyList<GateShardHostResult> hosts)
    {
        ArgumentOutOfRangeException.ThrowIfZero(hosts.Count);
        var ordered = hosts.OrderBy(host => host.Part.Index).ToArray();
        var primary = ordered[0];
        var suites = MergeSuites(ordered);
        var codeRed = ordered.FirstOrDefault(host => IsCodeRed(host.Result));
        // A share stopped because a sibling failed first is a consequence, not the cause.
        var infra = ordered.FirstOrDefault(host => host.Result.IsInfrastructureFailure
                        && host.Result.FailureKind != BuildTestGateFailureKind.Cancellation)
                    ?? ordered.FirstOrDefault(host => host.Result.IsInfrastructureFailure);
        var incomplete = codeRed is null && infra is null ? CoverageGap(ordered) : null;

        GateShardHostResult lead;
        string reason;
        BuildTestGateResult merged;
        if (codeRed is not null)
        {
            lead = codeRed;
            reason = $"{codeRed.Result.Reason}{HostSuffix(codeRed)}";
            merged = codeRed.Result;
        }
        else if (infra is not null)
        {
            lead = infra;
            reason = $"partial infrastructure failure on {infra.Host} (host part {infra.Part}): " +
                     $"{infra.Result.Reason}; {PassedHosts(ordered, infra)}";
            merged = infra.Result;
        }
        else if (incomplete is not null)
        {
            lead = primary;
            reason = $"shard evidence incomplete: {incomplete}; {primary.Result.Reason}";
            merged = primary.Result with
            {
                Verdict = BuildTestGateVerdict.Fail,
                FailureKind = BuildTestGateFailureKind.Environment,
                ExitCode = null,
            };
        }
        else
        {
            lead = primary;
            reason = primary.Result.Reason;
            merged = primary.Result with
            {
                Verdict = ordered.Any(host => host.Result.Verdict == BuildTestGateVerdict.Warn)
                    ? BuildTestGateVerdict.Warn
                    : primary.Result.Verdict,
            };
        }

        var hostSummary = string.Join(", ", ordered.Select(host =>
            $"{host.Host} part {host.Part}={host.Result.Verdict}" +
            (host.Result.FailureKind == BuildTestGateFailureKind.None ? "" : $"/{host.Result.FailureKind}")));
        return merged with
        {
            Reason = $"{reason}; sharded across {ordered.Length} host(s): {hostSummary}",
            DurationMs = ordered.Max(host => host.Result.DurationMs),
            Output = string.Join('\n', ordered.Select(host =>
                $"# ===== gate host {host.Host} (host part {host.Part}): {host.Result.Verdict} =====\n{host.Result.Output}")),
            Executor = "sharded:" + string.Join(",", ordered.Select(host => host.Result.Executor ?? host.Host)),
            TestedSha = primary.Result.TestedSha,
            TestSelection = primary.Result.TestSelection,
            TestSelectionAuditDigest = primary.Result.TestSelectionAuditDigest,
            Diagnosis = lead.Result.Diagnosis ?? primary.Result.Diagnosis,
            Processes = ordered.SelectMany(host => host.Result.Processes).ToArray(),
            Findings = ordered.SelectMany(host => host.Result.Findings).ToArray(),
            RetryPerformed = ordered.Any(host => host.Result.RetryPerformed),
            FlakyQuarantinedFailures = ordered.SelectMany(host => host.Result.FlakyQuarantinedFailures)
                .Distinct(StringComparer.Ordinal).ToArray(),
            QuarantinedFailures = ordered.SelectMany(host => host.Result.QuarantinedFailures).ToArray(),
            GateQueueWaitMs = ordered.Max(host => host.Result.GateQueueWaitMs),
            GateCollisionDetected = ordered.Any(host => host.Result.GateCollisionDetected),
            TestShards = suites,
        };
    }

    private static bool IsCodeRed(BuildTestGateResult result)
        => result.FailureKind == BuildTestGateFailureKind.Code
           && result.Verdict is BuildTestGateVerdict.Fail or BuildTestGateVerdict.Warn;

    private static string HostSuffix(GateShardHostResult host) => $" (on {host.Host}, host part {host.Part})";

    private static string PassedHosts(IEnumerable<GateShardHostResult> hosts, GateShardHostResult failed)
    {
        var others = hosts.Where(host => host != failed)
            .Select(host => $"{host.Host}={host.Result.Verdict}")
            .ToArray();
        return others.Length == 0 ? "no other host" : "other hosts: " + string.Join(", ", others);
    }

    /// <summary>
    /// With every host green, the union of their shards must be exactly the
    /// plan, and every host must have computed the same plan. Anything else
    /// means some tests ran nowhere, which is not a pass.
    /// </summary>
    internal static string? CoverageGap(IReadOnlyList<GateShardHostResult> hosts)
    {
        if (hosts.Any(host => !string.Equals(host.Result.TestedSha, hosts[0].Result.TestedSha, StringComparison.OrdinalIgnoreCase)))
            return "hosts tested different commits";
        var suiteIds = hosts.SelectMany(host => host.Result.TestShards.Select(suite => suite.SuiteId))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var suiteId in suiteIds)
        {
            var reports = hosts.Select(host => (host, suite: host.Result.TestShards
                .FirstOrDefault(item => item.SuiteId == suiteId))).ToArray();
            var missing = reports.Where(item => item.suite is null).Select(item => item.host.Host).ToArray();
            if (missing.Length > 0)
                return $"suite {suiteId} was not planned on {string.Join(", ", missing)}";
            var digests = reports.Select(item => item.suite!.PlanDigest).Distinct(StringComparer.Ordinal).ToArray();
            if (digests.Length > 1)
                return $"suite {suiteId} has different shard plans ({string.Join(", ", digests)})";
            var count = reports[0].suite!.ShardCount;
            var executed = reports.SelectMany(item => item.suite!.Shards.Select(shard => shard.Index)).ToArray();
            var absent = Enumerable.Range(0, count).Except(executed).ToArray();
            if (absent.Length > 0)
                return $"suite {suiteId} shard(s) {string.Join(", ", absent.Select(index => index + 1))} of {count} ran on no host";
            var twice = executed.GroupBy(index => index).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
            if (twice.Length > 0)
                return $"suite {suiteId} shard(s) {string.Join(", ", twice.Select(index => index + 1))} ran on more than one host";
        }
        return null;
    }

    internal static IReadOnlyList<GateShardSuiteEvidence> MergeSuites(IReadOnlyList<GateShardHostResult> hosts)
        => hosts.SelectMany(host => host.Result.TestShards)
            .GroupBy(suite => suite.SuiteId, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var classDurations = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var (name, ms) in group.SelectMany(suite => suite.ClassDurationsMs))
                    classDurations[name] = classDurations.TryGetValue(name, out var existing) ? existing + ms : ms;
                return first with
                {
                    Lanes = group.Sum(suite => suite.Lanes),
                    WallMs = group.Max(suite => suite.WallMs),
                    Shards = group.SelectMany(suite => suite.Shards).OrderBy(shard => shard.Index).ToArray(),
                    Hosts = group.SelectMany(suite => suite.Hosts).Distinct(StringComparer.Ordinal).ToArray(),
                    Part = null,
                    ClassDurationsMs = classDurations,
                    SerialWallMs = group.Sum(suite => suite.SerialWallMs),
                };
            })
            .ToArray();
}
