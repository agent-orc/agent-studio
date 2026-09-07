namespace AgentStudio.Watcher;

/// <summary>
/// The global Watcher as a hosted service beside the task API, with its own
/// cadence, kill switch, heartbeat, and structured run log.
/// </summary>
/// <remarks>
/// <para>
/// Configuration is re-read at every tick, so <c>Watcher:Enabled</c> can be
/// flipped without restarting the backend. A disabled Watcher stays resident and
/// idle: it publishes a disabled snapshot rather than disappearing, because an
/// absent monitor and a switched-off monitor must not look the same.
/// </para>
/// <para>
/// The service performs no task or Git mutation of its own. Every board write
/// comes from the proposal service through the task API.
/// </para>
/// </remarks>
public sealed class WatcherHostedService : BackgroundService
{
    private readonly WatcherSweepService _sweep;
    private readonly WatcherStore _store;
    private readonly WatcherBusPublisher _publisher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WatcherHostedService> _logger;
    private readonly object _snapshotGate = new();
    private WatcherSnapshot _current = new();
    private long _sweeps;

    public WatcherHostedService(
        WatcherSweepService sweep,
        WatcherStore store,
        WatcherBusPublisher publisher,
        IConfiguration configuration,
        ILogger<WatcherHostedService> logger)
    {
        _sweep = sweep;
        _store = store;
        _publisher = publisher;
        _configuration = configuration;
        _logger = logger;
    }

    public WatcherSnapshot Current
    {
        get { lock (_snapshotGate) return _current; }
    }

    /// <summary>
    /// One cycle, callable outside the loop. Returns null when the kill switch
    /// is off, so a caller can tell "did nothing because disabled" from "did
    /// nothing because there was nothing to find".
    /// </summary>
    public async Task<WatcherSweepResult?> RunOnceAsync(CancellationToken ct = default)
    {
        var options = WatcherOptions.FromConfiguration(_configuration);
        if (!options.Enabled)
        {
            Publish(_current with { Enabled = false, LastRunAtUtc = DateTime.UtcNow });
            return null;
        }

        var result = await _sweep.SweepAsync(ct);
        var contingent = _sweep.Contingent(result.AtUtc);
        var cases = _store.Cases();
        var snapshot = Publish(new WatcherSnapshot
        {
            Enabled = true,
            LastRunAtUtc = result.AtUtc,
            Sweeps = Interlocked.Increment(ref _sweeps),
            SignalsCollected = result.SignalsCollected,
            FindingsDetected = result.FindingsDetected,
            OpenCases = cases.Count(item => !WatcherCaseStates.IsTerminal(item.State)),
            PendingProposals = _store.Proposals()
                .Count(item => item.Decision.State == WatcherProposalDecisions.Pending),
            BacklogCases = contingent.BacklogCases,
            ProposalsCreatedLastRun = result.ProposalsCreated,
            CommentsAppendedLastRun = result.CommentsAppended,
            ModelCallsLastRun = result.ModelCalls,
            ResolvedLastRun = result.CasesClosed,
            LastRunElapsedMs = result.ElapsedMs,
        });

        await _publisher.HeartbeatAsync(snapshot, ct);
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = WatcherOptions.FromConfiguration(_configuration);
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed sweep is visible, not silent: the snapshot keeps the
                // error until a clean sweep replaces it.
                Publish(_current with
                {
                    Enabled = options.Enabled,
                    LastRunFailedAtUtc = DateTime.UtcNow,
                    LastError = ex.Message,
                });
                _logger.LogError(ex, "watcher-run-failed");
            }

            await Task.Delay(options.Interval, stoppingToken);
        }
    }

    private WatcherSnapshot Publish(WatcherSnapshot snapshot)
    {
        lock (_snapshotGate)
        {
            _current = snapshot;
            return _current;
        }
    }
}
