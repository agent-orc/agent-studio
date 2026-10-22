using AgentStudio.TaskServer.Contracts;
using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>One gate host the dispatcher can send a host part to.</summary>
internal sealed record GateShardHost(string Name, IRemoteGateTransport Transport, GateSlotUsageTracker Slots);

/// <summary>What the dispatcher knows about one additional shard host when it places a run.</summary>
public sealed record GateShardHostState(GateSlotUsage Slots, string Reachability, DateTimeOffset? LastFailureAt);

/// <summary>
/// AGT-3232: decides how many gate hosts share one gate run. Pure.
/// A run spans hosts only when the subject declares a suite that plans to two
/// or more shards and more than one gate slot is free: one on the primary
/// host, which runs every unsharded command, and one on each additional host
/// that takes a share of the shards. A run never uses more hosts than its
/// shard count. A host whose last transport attempt failed waits out
/// <see cref="UnreachableCooldown"/> before it is tried again.
/// </summary>
public static class GateShardPlacementPolicy
{
    public static readonly TimeSpan UnreachableCooldown = TimeSpan.FromMinutes(10);

    public static IReadOnlyList<int> SelectAdditionalHosts(
        GateSlotUsage primary,
        IReadOnlyList<GateShardHostState> additional,
        int shardCount,
        DateTimeOffset now)
    {
        if (shardCount < 2 || primary.Busy >= primary.Total) return [];
        return additional
            .Select((host, index) => (host, index))
            .Where(item => item.host.Slots.Busy < item.host.Slots.Total && IsAdmitted(item.host, now))
            .Select(item => item.index)
            .Take(Math.Min(shardCount, GateShardPart.MaxHosts) - 1)
            .ToArray();
    }

    public static bool IsAdmitted(GateShardHostState host, DateTimeOffset now)
        => !string.Equals(host.Reachability, "unreachable", StringComparison.Ordinal)
           || host.LastFailureAt is not { } failedAt
           || now - failedAt >= UnreachableCooldown;

    /// <summary>
    /// The most shards any declared suite of the subject plans to, as far as
    /// the dispatcher can tell without a checkout: a suite with a
    /// <c>shards</c> value other than 1 and a shardable command. The gate host
    /// still plans the exact split from the test sources.
    /// </summary>
    public static int PlannedShardCount(IReadOnlyList<ProjectTestSuite> suites, GateShardTimingSnapshot timings)
        => suites
            .Where(suite => suite.Shards is not (null or 1) && GateShardCommand.TryParse(suite.Command) is not null)
            .Select(suite => GateShardPlanner.ResolveCount(
                suite.Shards == ProjectTestSuite.AutoShards ? null : suite.Shards,
                timings.For(suite.Id), suite.ExpectedDurationSeconds, int.MaxValue).Count)
            .DefaultIfEmpty(0)
            .Max();
}

/// <summary>
/// AGT-3232: the dispatcher side of gate sharding. It hands every gate run the
/// project's recorded shard timings and, when the subject's
/// <c>.agent-studio/project.yml</c> at <c>ExpectedSha</c> declares a suite that
/// plans to two or more shards, splits the run across the primary gate host
/// and the configured <c>RemoteGate:ShardHosts</c> that have a free slot. It
/// merges the host shares into one verdict and records the measured timings
/// for the next run. Otherwise the run goes to the primary whole and shards
/// there in parallel lanes when the subject asks for it.
/// <para>
/// The primary is the conventional transport (pull worker when one is
/// healthy, SSH otherwise); shard hosts are always reached over SSH.
/// </para>
/// </summary>
public sealed class ShardedRemoteGateTransport : IRemoteGateTransport
{
    private static readonly TimeSpan SubjectReadBudget = TimeSpan.FromSeconds(30);

    private readonly GateShardHost _primary;
    private readonly IReadOnlyList<GateShardHost> _additional;
    private readonly GateShardTimingStore _timings;
    private readonly ILogger<ShardedRemoteGateTransport> _logger;
    private readonly TimeProvider _clock;
    private readonly Func<BuildTestGateRequest, CancellationToken, Task<IReadOnlyList<ProjectTestSuite>>> _subjectSuites;

    public ShardedRemoteGateTransport(
        ConventionalGateTransport primary,
        GateSlotUsageTracker primarySlots,
        IConfiguration configuration,
        ILoggerFactory loggers,
        GateShardTimingStore timings,
        TimeProvider? clock = null,
        OrchestratorLog? activity = null, AgentStudio.Tasks.TaskScannerService? scanner = null)
        : this(
            new GateShardHost(configuration["RemoteGate:SshHost"] ?? "primary", primary, primarySlots),
            AdditionalHosts(configuration, loggers, clock, activity, scanner),
            timings,
            loggers.CreateLogger<ShardedRemoteGateTransport>(),
            clock)
    {
    }

    internal ShardedRemoteGateTransport(
        GateShardHost primary,
        IReadOnlyList<GateShardHost> additional,
        GateShardTimingStore timings,
        ILogger<ShardedRemoteGateTransport> logger,
        TimeProvider? clock = null,
        Func<BuildTestGateRequest, CancellationToken, Task<IReadOnlyList<ProjectTestSuite>>>? subjectSuites = null)
    {
        _primary = primary;
        _additional = additional;
        _timings = timings;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _subjectSuites = subjectSuites ?? SubjectSuitesAsync;
    }

    public async Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
        IReadOnlyList<string>? changedFiles, BuildProfile? profile,
        PostStepMode mode, TimeSpan timeout, CancellationToken ct)
    {
        var projectKey = request.Project ?? Path.GetFullPath(request.RepositoryPath);
        var snapshot = _timings.Read(projectKey);
        if (!snapshot.IsWithinBounds)
        {
            _logger.LogWarning("gate_shard_timings_out_of_bounds project={Project}", projectKey);
            snapshot = GateShardTimingSnapshot.Empty;
        }
        IReadOnlyList<int> selected = [];
        if (request.Sharding?.Part is null && _additional.Count > 0)
        {
            var shardCount = GateShardPlacementPolicy.PlannedShardCount(
                await _subjectSuites(request, ct).ConfigureAwait(false), snapshot);
            selected = GateShardPlacementPolicy.SelectAdditionalHosts(
                _primary.Slots.Snapshot(),
                _additional.Select(host => new GateShardHostState(
                    host.Slots.Snapshot(), host.Slots.Reachability, host.Slots.LastFailureAt)).ToArray(),
                shardCount, _clock.GetUtcNow());
        }

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

    internal async Task CancelOrphanedRunsAsync(CancellationToken ct)
    {
        foreach (var host in _additional)
            if (host.Transport is RemoteGateTransport transport)
                await transport.CancelOrphanedRunsAsync(ct).ConfigureAwait(false);
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

    /// <summary>
    /// The test suites the subject declares at <c>ExpectedSha</c>, read from
    /// the dispatcher's repository. Any failure to read them keeps the run on
    /// the primary host whole, which is the unsharded behaviour.
    /// </summary>
    private async Task<IReadOnlyList<ProjectTestSuite>> SubjectSuitesAsync(BuildTestGateRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ExpectedSha)) return [];
        var repository = Path.GetFullPath(request.RepositoryPath);
        var lines = new List<string>();
        try
        {
            await RemoteGateProcess.RunAsync("git",
                ["-C", repository, "show", $"{request.ExpectedSha}:{ProjectPreparationPaths.Definition}"],
                repository, SubjectReadBudget, ct, lines.Add).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException
                                              or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogInformation(exception,
                "gate_shard_subject_unread gate={GateId} expected_sha={ExpectedSha}; the run stays on the primary host",
                request.GateId, request.ExpectedSha);
            return [];
        }
        var read = ProjectDefinitionReader.Parse(string.Join('\n', lines));
        return read.IsValid ? read.Definition!.TestSuites : [];
    }

    private static IReadOnlyList<GateShardHost> AdditionalHosts(
        IConfiguration configuration, ILoggerFactory loggers, TimeProvider? clock,
        OrchestratorLog? activity, AgentStudio.Tasks.TaskScannerService? scanner)
        => configuration.GetSection("RemoteGate:ShardHosts").GetChildren()
            .Where(section => !string.IsNullOrWhiteSpace(section["SshHost"]))
            .OrderBy(section => section.Key, StringComparer.Ordinal)
            .Select(section =>
            {
                var slots = new GateSlotUsageTracker(clock: clock);
                var transport = new RemoteGateTransport(configuration, loggers.CreateLogger<RemoteGateTransport>(),
                    section.Path, slots, clock, activity, scanner);
                return new GateShardHost(section["SshHost"]!.Trim(), transport, slots);
            })
            .ToArray();
}
