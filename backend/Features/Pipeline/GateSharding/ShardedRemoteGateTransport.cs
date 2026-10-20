namespace AgentStudio.Pipeline;

/// <summary>One gate host the dispatcher can send a host part to.</summary>
internal sealed record GateShardHost(string Name, IRemoteGateTransport Transport, GateSlotUsageTracker Slots);

/// <summary>
/// AGT-3232: decides how many gate hosts share one gate run. Pure.
/// A run spans hosts only when more than one gate slot is free: one on the
/// primary host, which runs every unsharded command, and one on each
/// additional host that takes a share of the shards. A host whose last
/// transport attempt failed is not used.
/// </summary>
public static class GateShardPlacementPolicy
{
    public static IReadOnlyList<int> SelectAdditionalHosts(
        GateSlotUsage primary,
        IReadOnlyList<(GateSlotUsage Slots, string Reachability)> additional)
    {
        if (primary.Busy >= primary.Total) return [];
        return additional
            .Select((host, index) => (host, index))
            .Where(item => item.host.Slots.Busy < item.host.Slots.Total
                           && !string.Equals(item.host.Reachability, "unreachable", StringComparison.Ordinal))
            .Select(item => item.index)
            .Take(GateShardPart.MaxHosts - 1)
            .ToArray();
    }
}

/// <summary>
/// AGT-3232: the dispatcher side of gate sharding. It hands every gate run the
/// project's recorded shard timings, splits the run across the primary gate
/// host and the configured <c>RemoteGate:ShardHosts</c> that have a free slot,
/// merges the host shares into one verdict, and records the measured timings
/// for the next run. Without additional hosts, or without a free slot on one,
/// the run goes to the primary host whole and shards there in parallel lanes.
/// </summary>
public sealed class ShardedRemoteGateTransport : IRemoteGateTransport
{
    private readonly GateShardHost _primary;
    private readonly IReadOnlyList<GateShardHost> _additional;
    private readonly GateShardTimingStore _timings;
    private readonly ILogger<ShardedRemoteGateTransport> _logger;

    public ShardedRemoteGateTransport(
        RemoteGateTransport primary,
        GateSlotUsageTracker primarySlots,
        IConfiguration configuration,
        ILoggerFactory loggers,
        GateShardTimingStore timings,
        TimeProvider? clock = null)
        : this(
            new GateShardHost(configuration["RemoteGate:SshHost"] ?? "primary", primary, primarySlots),
            AdditionalHosts(configuration, loggers, clock),
            timings,
            loggers.CreateLogger<ShardedRemoteGateTransport>())
    {
    }

    internal ShardedRemoteGateTransport(
        GateShardHost primary,
        IReadOnlyList<GateShardHost> additional,
        GateShardTimingStore timings,
        ILogger<ShardedRemoteGateTransport> logger)
    {
        _primary = primary;
        _additional = additional;
        _timings = timings;
        _logger = logger;
    }

    public async Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
        IReadOnlyList<string>? changedFiles, BuildProfile? profile,
        PostStepMode mode, TimeSpan timeout, CancellationToken ct)
    {
        var projectKey = request.Project ?? Path.GetFullPath(request.RepositoryPath);
        var snapshot = _timings.Read(projectKey);
        var selected = request.Sharding?.Part is null
            ? GateShardPlacementPolicy.SelectAdditionalHosts(
                _primary.Slots.Snapshot(),
                _additional.Select(host => (host.Slots.Snapshot(), host.Slots.Reachability)).ToArray())
            : [];

        BuildTestGateResult result;
        if (selected.Count == 0)
        {
            result = await _primary.Transport.RunAsync(
                request with { Sharding = new GateShardRequest { Part = request.Sharding?.Part, Timings = snapshot } },
                changedFiles, profile, mode, timeout, ct).ConfigureAwait(false);
        }
        else
        {
            var hosts = new[] { _primary }.Concat(selected.Select(index => _additional[index])).ToArray();
            _logger.LogInformation(
                "gate_shard_hosts_selected gate={GateId} expected_sha={ExpectedSha} hosts={Hosts}",
                request.GateId, request.ExpectedSha, string.Join(",", hosts.Select(host => host.Name)));
            using var siblings = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var shares = await Task.WhenAll(hosts.Select((host, index) =>
                RunShareAsync(host, new GateShardPart(index, hosts.Length), snapshot,
                    request, changedFiles, profile, mode, timeout, siblings, ct))).ConfigureAwait(false);
            result = GateShardVerdictPolicy.MergeHosts(shares);
        }

        if (result.VerdictSource == GateVerdictSource.Executed && result.TestShards.Count > 0)
        {
            try { _timings.Record(projectKey, result.TestShards); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "gate_shard_timings_not_recorded project={Project}", projectKey);
            }
        }
        return result;
    }

    /// <summary>
    /// One host's share. A transport exception becomes that share's
    /// infrastructure result so the merge, not the first failing host, decides
    /// the run's verdict. A share that is not green stops its siblings: the run
    /// cannot pass any more, and the stopped shares are recorded as cancelled.
    /// </summary>
    private async Task<GateShardHostResult> RunShareAsync(
        GateShardHost host, GateShardPart part, GateShardTimingSnapshot snapshot,
        BuildTestGateRequest request, IReadOnlyList<string>? changedFiles, BuildProfile? profile,
        PostStepMode mode, TimeSpan timeout, CancellationTokenSource siblings, CancellationToken ct)
    {
        BuildTestGateResult result;
        try
        {
            result = await host.Transport.RunAsync(
                request with { Sharding = new GateShardRequest { Part = part, Timings = snapshot } },
                changedFiles, profile, mode, timeout, siblings.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = RemoteBuildTestGateRunner.Failure(request, BuildTestGateFailureKind.Cancellation,
                $"Shard host {host.Name} stopped because another shard host of this gate run did not pass.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception,
                "gate_shard_host_failed host={Host} part={Part} gate={GateId} expected_sha={ExpectedSha}",
                host.Name, part, request.GateId, request.ExpectedSha);
            result = RemoteBuildTestGateRunner.Failure(request, BuildTestGateFailureKind.Environment,
                $"Remote gate transport failure on shard host {host.Name}: {exception.Message}");
        }
        if (result.Verdict is not (BuildTestGateVerdict.Ok or BuildTestGateVerdict.Warn
                or BuildTestGateVerdict.NotApplicable or BuildTestGateVerdict.Skipped)
            || result.FailureKind != BuildTestGateFailureKind.None)
            await siblings.CancelAsync().ConfigureAwait(false);
        return new GateShardHostResult(host.Name, part, result);
    }

    private static IReadOnlyList<GateShardHost> AdditionalHosts(
        IConfiguration configuration, ILoggerFactory loggers, TimeProvider? clock)
        => configuration.GetSection("RemoteGate:ShardHosts").GetChildren()
            .Where(section => !string.IsNullOrWhiteSpace(section["SshHost"]))
            .OrderBy(section => section.Key, StringComparer.Ordinal)
            .Select(section =>
            {
                var slots = new GateSlotUsageTracker(clock: clock);
                var transport = new RemoteGateTransport(configuration, loggers.CreateLogger<RemoteGateTransport>(),
                    section.Path, slots, clock);
                return new GateShardHost(section["SshHost"]!.Trim(), transport, slots);
            })
            .ToArray();
}
