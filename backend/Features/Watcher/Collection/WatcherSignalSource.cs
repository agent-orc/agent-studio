namespace AgentStudio.Watcher;

/// <summary>
/// One producer of Watcher signals. Sources are the only part of the sweep that
/// touches the world, which is what keeps detection pure and the dossier
/// fixtures replayable.
/// </summary>
/// <remarks>
/// A source must be cheap, bounded, and read-only. It never throws for a
/// missing dependency: an unavailable source returns an empty input and the
/// sweep records that it collected nothing, so a blind sweep cannot be mistaken
/// for a healthy one.
/// </remarks>
public interface IWatcherSignalSource
{
    /// <summary>Stable name used in the sweep log and in the snapshot.</summary>
    string Name { get; }

    Task<WatcherSweepInput> CollectAsync(DateTime nowUtc, CancellationToken ct = default);
}

/// <summary>
/// Runs every registered source and folds the results into one sweep input.
/// A source that fails is logged and skipped; the others still run.
/// </summary>
public sealed class WatcherSignalCollector
{
    private readonly IReadOnlyList<IWatcherSignalSource> _sources;
    private readonly ILogger<WatcherSignalCollector> _logger;

    public WatcherSignalCollector(
        IEnumerable<IWatcherSignalSource> sources,
        ILogger<WatcherSignalCollector> logger)
    {
        _sources = sources.OrderBy(source => source.Name, StringComparer.Ordinal).ToList();
        _logger = logger;
    }

    public IReadOnlyList<string> SourceNames => _sources.Select(source => source.Name).ToList();

    public async Task<WatcherSweepInput> CollectAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var combined = new WatcherSweepInput { NowUtc = nowUtc };
        foreach (var source in _sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var input = await source.CollectAsync(nowUtc, ct);
                combined = combined.Concat(input);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "watcher-source-failed source={Source}", source.Name);
            }
        }
        return combined with { NowUtc = nowUtc };
    }
}
