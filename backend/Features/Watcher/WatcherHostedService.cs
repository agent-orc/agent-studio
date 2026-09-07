namespace AgentStudio.Watcher;

/// <summary>
/// Live state of the Watcher for the status endpoint and the shell strip:
/// what it last did, what is open, and what the contingent has left.
/// </summary>
public sealed record WatcherSnapshot
{
    public bool Enabled { get; init; }
    public DateTime? LastRunAtUtc { get; init; }
    public DateTime? LastHeartbeatAtUtc { get; init; }
    public int OpenCases { get; init; }
    public int DecisionsRequired { get; init; }
    public int Backlog { get; init; }
    public int ActiveSuppressions { get; init; }
    public WatcherSweepResult? LastSweep { get; init; }
    public WatcherContingentSnapshot? Contingent { get; init; }
}

/// <summary>
/// Hosts the five-minute reconciliation sweep in the server process. It owns no
/// authority of its own: it collects, detects, and asks the sweep coordinator to
/// act inside the bounds of the contingent and the review mode.
/// </summary>
/// <remarks>
/// Modelled on the acceptance rail: a public <see cref="RunOnceAsync"/> seam for
/// tests, options re-read at every tick so <c>Watcher:Enabled</c> is a hot kill
/// switch, a published snapshot behind a read endpoint, and one
/// <c>watcher-run</c> structured log line per sweep.
/// </remarks>
public sealed class WatcherHostedService : BackgroundService
{
    private readonly IWatcherEvidenceCollector _collector;
    private readonly WatcherSweepCoordinator _coordinator;
    private readonly WatcherStore _store;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WatcherHostedService> _logger;
    private readonly object _snapshotGate = new();
    private WatcherSnapshot _current = new() { Enabled = WatcherDefaults.Enabled };

    public WatcherHostedService(
        IWatcherEvidenceCollector collector,
        WatcherSweepCoordinator coordinator,
        WatcherStore store,
        IConfiguration configuration,
        ILogger<WatcherHostedService> logger)
    {
        _collector = collector;
        _coordinator = coordinator;
        _store = store;
        _configuration = configuration;
        _logger = logger;
    }

    public WatcherSnapshot Current
    {
        get { lock (_snapshotGate) return _current; }
    }

    public async Task<WatcherSnapshot> RunOnceAsync(CancellationToken ct = default)
    {
        var options = WatcherOptions.FromConfiguration(_configuration);
        var nowUtc = DateTime.UtcNow;

        if (!options.Enabled)
        {
            // The kill switch stops detection and every write, but the service
            // keeps heartbeating so an operator can tell "off" from "stuck".
            return Publish(new WatcherSnapshot
            {
                Enabled = false,
                LastHeartbeatAtUtc = nowUtc,
                LastRunAtUtc = Current.LastRunAtUtc,
            });
        }

        var input = _collector.Collect(nowUtc);
        var sweep = await _coordinator.SweepAsync(input, options, ct);
        var snapshot = Publish(Describe(sweep, options, nowUtc));

        _logger.LogInformation(
            "watcher-run findings={Findings} casesOpened={CasesOpened} casesUpdated={CasesUpdated} "
            + "casesClosed={CasesClosed} proposals={Proposals} comments={Comments} suppressed={Suppressed} "
            + "backlog={Backlog} modelCalls={ModelCalls} failed={Failed} openCases={OpenCases} "
            + "decisionsRequired={DecisionsRequired} lastRunAtUtc={LastRunAtUtc}",
            sweep.Findings,
            sweep.CasesOpened,
            sweep.CasesUpdated,
            sweep.CasesClosed,
            sweep.ProposalsCreated,
            sweep.CommentsAppended,
            sweep.SuppressedFindings,
            sweep.Backlog,
            sweep.ModelCalls,
            sweep.Failed,
            snapshot.OpenCases,
            snapshot.DecisionsRequired,
            snapshot.LastRunAtUtc);

        return snapshot;
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
                _logger.LogError(ex, "watcher-run-failed");
            }

            try
            {
                await Task.Delay(options.SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private WatcherSnapshot Describe(WatcherSweepResult sweep, WatcherOptions options, DateTime nowUtc)
    {
        var cases = _store.Cases();
        var backlog = cases.Count(row =>
            row.State == WatcherCaseStates.Open && row.BacklogReason is not null);

        return new WatcherSnapshot
        {
            Enabled = true,
            LastRunAtUtc = nowUtc,
            LastHeartbeatAtUtc = nowUtc,
            OpenCases = cases.Count(row => row.State == WatcherCaseStates.Open),
            DecisionsRequired = cases.Count(row => row.State == WatcherCaseStates.DecisionRequired),
            Backlog = backlog,
            ActiveSuppressions = _store.Suppressions().Count(row => row.IsActive(nowUtc)),
            LastSweep = sweep,
            Contingent = WatcherContingentPolicy.Describe(
                options.Contingent, _store.Snapshot().Spend, nowUtc, backlog),
        };
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
