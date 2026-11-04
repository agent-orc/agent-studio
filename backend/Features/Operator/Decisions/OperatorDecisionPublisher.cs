namespace AgentStudio.Operator;

/// <summary>
/// Publishes the decision stream (OP-2). Every sweep reads the fact sources,
/// lets <see cref="OperatorDecisionPolicy.Diff"/> pick the occurrences not yet
/// published, appends them to the log, and saves the published keys. It runs
/// on the configured interval (at most 5 seconds) and at once after a lane
/// move. It publishes facts only: it moves no card and decides no transition.
/// </summary>
public sealed class OperatorDecisionPublisher : BackgroundService
{
    private readonly OperatorDecisionLog _log;
    private readonly IReadOnlyList<IOperatorDecisionFactSource> _sources;
    private readonly OperatorDecisionOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<OperatorDecisionPublisher> _logger;
    private readonly SemaphoreSlim _sweep = new(1, 1);
    private readonly object _wakeGate = new();
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlySet<string>? _published;
    private long _intervalWaits;

    /// <summary>How often the loop started waiting for its interval; its timer exists once this is counted.</summary>
    internal long IntervalWaits => Interlocked.Read(ref _intervalWaits);

    public OperatorDecisionPublisher(OperatorDecisionLog log, IEnumerable<IOperatorDecisionFactSource> sources,
        OperatorDecisionOptions options, TimeProvider clock, ILogger<OperatorDecisionPublisher> logger)
    {
        _log = log;
        _sources = sources.ToArray();
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Starts the next sweep now instead of at the end of the interval.</summary>
    public void Wake()
    {
        lock (_wakeGate) _wake.TrySetResult();
    }

    public async Task<IReadOnlyList<OperatorDecisionEvent>> SweepOnceAsync(CancellationToken ct = default)
    {
        await _sweep.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var facts = new List<OperatorDecisionFact>();
            var observed = new HashSet<string>(StringComparer.Ordinal);
            var failed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in _sources)
            {
                try
                {
                    if (source.Collect(now) is { } collected)
                    {
                        facts.AddRange(collected);
                        observed.UnionWith(source.Types);
                    }
                    else failed.UnionWith(source.Types);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failed.UnionWith(source.Types);
                    _logger.LogWarning(exception, "operator decision source {Source} failed; its facts keep their published state",
                        source.GetType().Name);
                }
            }
            observed.ExceptWith(failed);
            _published ??= _log.LoadPublishedFacts();
            var diff = OperatorDecisionPolicy.Diff(_published, facts, observed);
            var appended = _log.Append(diff.Publish);
            if (appended.Count > 0 || !_published.SetEquals(diff.Published))
                _log.SavePublishedFacts(diff.Published);
            _published = diff.Published;
            return appended;
        }
        finally
        {
            _sweep.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Wakes before this sweep are answered by it; a wake during the
            // sweep leaves the signal set, so the next sweep starts at once.
            lock (_wakeGate)
                if (_wake.Task.IsCompleted) _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "operator decision sweep failed");
            }
            Task wake;
            lock (_wakeGate) wake = _wake.Task;
            var interval = Task.Delay(_options.SweepInterval, _clock, stoppingToken);
            Interlocked.Increment(ref _intervalWaits);
            await Task.WhenAny(wake, interval).ConfigureAwait(false);
        }
    }
}
